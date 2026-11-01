using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ilha.Gravando;

/// <summary>Como está a conversa com o OBS.</summary>
internal enum EstadoObs { Desligado, Fechado, Conectando, SemServidor, PedeSenha, SenhaRecusada, Conectado, Caiu }

/// <summary>
/// Cliente do obs-websocket v5 no 127.0.0.1: conecta, faz o Hello/Identify (com senha, se o OBS pedir), assina só
/// os eventos de saída e pede um GetRecordStatus; depois só escuta. Tudo roda em tarefas do pool e nenhuma exceção
/// escapa: o estado e as mensagens saem pelos eventos (de outra thread; quem usa volta para a interface).
/// A senha nunca é guardada aqui nem escrita em log.
/// </summary>
internal sealed class ClienteObs : IDisposable
{
    private const int MAXIMO_MENSAGEM = 1 << 20, CONECTAR_MS = 3000, HELLO_MS = 5000;

    private readonly object _trava = new();
    private readonly SemaphoreSlim _envio = new(1, 1);
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cancelar;
    private int _pedidos;
    private bool _parado;

    public EstadoObs Estado { get; private set; } = EstadoObs.Fechado;
    /// <summary>Estado novo (thread do pool).</summary>
    public event Action<EstadoObs>? Mudou;
    /// <summary>Evento ou resposta do OBS já lido (thread do pool).</summary>
    public event Action<MensagemObs>? Mensagem;

    public bool Ocupado => Estado is EstadoObs.Conectando or EstadoObs.Conectado;

    /// <summary>Começa a conectar (se já não estiver). `senha` null = sem senha guardada.</summary>
    public void Conectar(int porta, string? senha)
    {
        CancellationTokenSource cts;
        lock (_trava)
        {
            if (_parado || Ocupado) return;
            _cancelar = cts = new CancellationTokenSource();
            Definir(EstadoObs.Conectando);
        }
        _ = Task.Run(() => Rodar(porta, senha, cts.Token));
    }

    public void Desconectar(EstadoObs depois = EstadoObs.Fechado)
    {
        lock (_trava)
        {
            try { _cancelar?.Cancel(); } catch (ObjectDisposedException) { }
            try { _ws?.Abort(); } catch { }
            Definir(depois);
        }
    }

    /// <summary>Manda um pedido sem dados (PauseRecord, ResumeRecord, StopRecord, GetRecordStatus). false = sem conexão.</summary>
    public bool Pedir(string tipo)
    {
        ClientWebSocket? ws;
        CancellationToken ct;
        lock (_trava)
        {
            if (Estado != EstadoObs.Conectado || _ws == null || _cancelar == null) return false;
            ws = _ws;
            ct = _cancelar.Token;
        }
        string id = "ilha-" + Interlocked.Increment(ref _pedidos);
        _ = Enviar(ws, ProtocoloObs.Pedido(tipo, id), ct);
        return true;
    }

    private void Definir(EstadoObs e)
    {
        if (Estado == e) return;
        Estado = e;
        try { Mudou?.Invoke(e); } catch (Exception ex) { Log.Escrever("gravando: estado do OBS: " + ex.Message); }
    }

    private void Avisar(MensagemObs m)
    {
        try { Mensagem?.Invoke(m); } catch (Exception ex) { Log.Escrever("gravando: mensagem do OBS: " + ex.Message); }
    }

    private async Task Rodar(int porta, string? senha, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        ws.Options.AddSubProtocol("obswebsocket.json");
        lock (_trava) _ws = ws;
        EstadoObs fim = EstadoObs.Caiu;
        try
        {
            using (var limite = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limite.CancelAfter(CONECTAR_MS);
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{porta}"), limite.Token).ConfigureAwait(false);
            }
            MensagemObs? hello;
            using (var limite = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limite.CancelAfter(HELLO_MS);
                hello = ProtocoloObs.Ler(await Receber(ws, limite.Token).ConfigureAwait(false) ?? "");
            }
            if (hello?.Op != ProtocoloObs.OP_HELLO) { Log.Escrever("gravando: o OBS não mandou o Hello"); fim = EstadoObs.SemServidor; return; }
            if (hello.PedeSenha && string.IsNullOrEmpty(senha))
            {
                fim = EstadoObs.PedeSenha;
                await Fechar(ws).ConfigureAwait(false);
                return;
            }
            await Enviar(ws, ProtocoloObs.Identify(hello, senha), ct).ConfigureAwait(false);
            senha = null;
            while (!ct.IsCancellationRequested)
            {
                string? texto = await Receber(ws, ct).ConfigureAwait(false);
                if (texto == null) break;
                var m = ProtocoloObs.Ler(texto);
                if (m == null) continue;
                if (m.Op == ProtocoloObs.OP_IDENTIFIED)
                {
                    lock (_trava) if (!ct.IsCancellationRequested) Definir(EstadoObs.Conectado);
                    Pedir("GetRecordStatus");
                    continue;
                }
                if (m.Op is ProtocoloObs.OP_EVENT or ProtocoloObs.OP_RESPONSE) Avisar(m);
            }
            if ((int?)ws.CloseStatus == ProtocoloObs.FECHOU_SENHA_ERRADA) fim = EstadoObs.SenhaRecusada;
            // o OBS fechando manda 1000/1001: fechou, não caiu
            else if (ws.CloseStatus is WebSocketCloseStatus.NormalClosure or WebSocketCloseStatus.EndpointUnavailable) fim = EstadoObs.Fechado;
            else if (Estado != EstadoObs.Conectado) Log.Escrever($"gravando: o OBS fechou a conexão ({(int?)ws.CloseStatus} {ws.CloseStatusDescription})");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (OperationCanceledException) { fim = EstadoObs.SemServidor; Log.Escrever("gravando: o OBS não respondeu a tempo"); }
        catch (Exception e)
        {
            if ((int?)ws.CloseStatus == ProtocoloObs.FECHOU_SENHA_ERRADA) fim = EstadoObs.SenhaRecusada;
            else if (Estado == EstadoObs.Conectado) { fim = EstadoObs.Caiu; Log.Escrever($"gravando: a conexão com o OBS caiu: {e.GetType().Name}: {e.Message}"); }
            else
            {
                // porta recusada = OBS aberto com o servidor WebSocket desligado (ou noutra porta): normal, sem log
                fim = EstadoObs.SemServidor;
                if (!Recusada(e)) Log.Escrever($"gravando: não conectou ao OBS: {e.GetType().Name}: {e.Message}");
            }
        }
        finally
        {
            lock (_trava)
            {
                if (_ws == ws) _ws = null;
                // cancelado = quem cancelou já definiu o estado
                if (!ct.IsCancellationRequested && !_parado) Definir(fim);
            }
            ws.Dispose();
        }
    }

    private static bool Recusada(Exception e)
    {
        for (var x = e; x != null; x = x.InnerException)
            if (x is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) return true;
        return false;
    }

    private static async Task Fechar(ClientWebSocket ws)
    {
        try
        {
            using var limite = new CancellationTokenSource(1000);
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", limite.Token).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task Enviar(ClientWebSocket ws, string texto, CancellationToken ct)
    {
        try
        {
            await _envio.WaitAsync(ct).ConfigureAwait(false);
            try { await ws.SendAsync(Encoding.UTF8.GetBytes(texto), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            finally { _envio.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception e) { Log.Escrever($"gravando: envio ao OBS falhou: {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>Uma mensagem de texto inteira (junta os pedaços). null = o servidor fechou.</summary>
    private static async Task<string?> Receber(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > MAXIMO_MENSAGEM) throw new InvalidDataException("mensagem grande demais");
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
    }

    public void Dispose()
    {
        lock (_trava)
        {
            _parado = true;
            try { _cancelar?.Cancel(); } catch (ObjectDisposedException) { }
            try { _ws?.Abort(); } catch { }
        }
        Mudou = null;
        Mensagem = null;
    }
}
