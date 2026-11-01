using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Ilha.Mascote;

/// <summary>
/// O protocolo entre a ilha e um app acompanhante: um mascote que vive na própria janela e pode morar dentro da ilha.
/// Um JSON por linha em UTF-8, sempre com "tipo". O app é dono de tudo (desenho, cenário, comportamento, config); a
/// ilha só mostra o quadro que recebe e conta onde está e o que o mouse fez.
///   app  -> ilha: ola {versao, nome} | quadro {largura, altura, png_base64, pixelado} | entrar | sair
///                 | pairando {dentro} | visivel {sim} | recusou {motivo}
///   ilha -> app : palco {x, y, largura, altura, pode_mostrar} (pixels da tela) | arrasto_saida {x, y}
///                 | arrasto {x, y} | soltou {x, y} | pedir_entrar | pedir_sair
/// </summary>
internal static class ProtocoloAcompanhante
{
    public const string PIPE = "ilha-acompanhante";
    public const int VERSAO = 1;
    public const int MAIOR_LINHA = 4 * 1024 * 1024;
    public const int MAIOR_LADO = 512;
    public const int MAIOR_NOME = 40;

    /// <summary>Uma mensagem do app já conferida. Quadro traz o PNG decodificado de base64 (ainda não a imagem).</summary>
    public sealed record Recebida(string Tipo, string? Nome = null, int Versao = 0, int Largura = 0, int Altura = 0,
        byte[]? Png = null, bool Pixelado = true, bool Sim = false, string? Motivo = null);

    public static Recebida? Ler(string linha)
    {
        try
        {
            if (JsonNode.Parse(linha) is not JsonObject o) return null;
            string? Txt(string k) => o[k] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
            int Int(string k) => o[k] is JsonValue v && v.TryGetValue(out int i) ? i : 0;
            bool? Bool(string k) => o[k] is JsonValue v && v.TryGetValue(out bool b) ? b : null;
            switch (Txt("tipo"))
            {
                case "ola":
                    {
                        string nome = (Txt("nome") ?? "").Trim();
                        if (nome.Length > MAIOR_NOME) nome = nome[..MAIOR_NOME];
                        return new Recebida("ola", Nome: nome.Length > 0 ? nome : null, Versao: Int("versao"));
                    }
                case "quadro":
                    {
                        int l = Int("largura"), a = Int("altura");
                        if (l is < 1 or > MAIOR_LADO || a is < 1 or > MAIOR_LADO) return null;
                        byte[] png;
                        try { png = Convert.FromBase64String(Txt("png_base64") ?? ""); }
                        catch (FormatException) { return null; }
                        if (png.Length < 8) return null;
                        return new Recebida("quadro", Largura: l, Altura: a, Png: png, Pixelado: Bool("pixelado") ?? true);
                    }
                case "entrar": return new Recebida("entrar");
                case "sair": return new Recebida("sair");
                case "pairando": return new Recebida("pairando", Sim: Bool("dentro") ?? false);
                case "visivel": return new Recebida("visivel", Sim: Bool("sim") ?? true);
                case "recusou": return new Recebida("recusou", Motivo: Txt("motivo"));
                default: return null;
            }
        }
        catch (JsonException) { return null; }
    }

    public static string Palco(System.Drawing.Rectangle r, bool podeMostrar) => new JsonObject
    {
        ["tipo"] = "palco", ["x"] = r.X, ["y"] = r.Y, ["largura"] = r.Width, ["altura"] = r.Height, ["pode_mostrar"] = podeMostrar,
    }.ToJsonString();

    /// <summary>arrasto_saida, arrasto ou soltou, com o cursor em pixels da tela.</summary>
    public static string Ponto(string tipo, int x, int y) => new JsonObject { ["tipo"] = tipo, ["x"] = x, ["y"] = y }.ToJsonString();

    public static string Simples(string tipo) => new JsonObject { ["tipo"] = tipo }.ToJsonString();

    /// <summary>O PNG do quadro em BGRA pré-multiplicado (o formato da superfície). null = não é imagem ou é grande demais.</summary>
    public static byte[]? Pixels(byte[] png, out int largura, out int altura)
    {
        largura = altura = 0;
        try
        {
            using var ms = new MemoryStream(png, writable: false);
            using var bmp = new System.Drawing.Bitmap(ms);
            int l = bmp.Width, a = bmp.Height;
            if (l is < 1 or > MAIOR_LADO || a is < 1 or > MAIOR_LADO) return null;
            var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, l, a), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                var px = new byte[l * a * 4];
                for (int y = 0; y < a; y++)
                    System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, px, y * l * 4, l * 4);
                largura = l;
                altura = a;
                return px;
            }
            finally { bmp.UnlockBits(bd); }
        }
        catch (Exception e) when (e is ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException or InvalidOperationException) { return null; }
    }
}

/// <summary>Junta os pedaços que chegam do pipe em linhas (\n), com limite de tamanho por linha.</summary>
internal sealed class JuntadorDeLinhas
{
    private readonly int _limite;
    private readonly MemoryStream _resto = new();
    /// <summary>Uma linha passou do limite: a conexão deve fechar (do outro lado não é quem a ilha espera).</summary>
    public bool Estourou { get; private set; }

    public JuntadorDeLinhas(int limite) => _limite = limite;

    public List<string> Receber(ReadOnlySpan<byte> dados)
    {
        var linhas = new List<string>();
        while (!Estourou && dados.Length > 0)
        {
            int fim = dados.IndexOf((byte)'\n');
            var pedaco = fim < 0 ? dados : dados[..fim];
            if (_resto.Length + pedaco.Length > _limite) { Estourou = true; break; }
            _resto.Write(pedaco);
            if (fim < 0) break;
            var linha = Encoding.UTF8.GetString(_resto.GetBuffer(), 0, (int)_resto.Length).TrimEnd('\r');
            _resto.SetLength(0);
            if (linha.Length > 0) linhas.Add(linha);
            dados = dados[(fim + 1)..];
        }
        return linhas;
    }
}

/// <summary>
/// Servidor do pipe \\.\pipe\ilha-acompanhante, numa thread própria, com a ACL só do usuário atual (o mesmo padrão
/// do pipe de permissão do Claude). Uma conexão por vez: o app acompanhante é um só. Os avisos chegam na thread do
/// pipe; quem recebe leva para a interface.
/// </summary>
internal sealed class ServidorAcompanhante : IDisposable
{
    private readonly string _nome;
    private readonly Action _conectou;
    private readonly Action<ProtocoloAcompanhante.Recebida> _chegou;
    private readonly Action _caiu;
    private readonly CancellationTokenSource _parar = new();
    private readonly Thread _thread;
    private Channel<string>? _saida;

    public ServidorAcompanhante(string nome, Action conectou, Action<ProtocoloAcompanhante.Recebida> chegou, Action caiu)
    {
        _nome = nome;
        _conectou = conectou;
        _chegou = chegou;
        _caiu = caiu;
        _thread = new Thread(() => Laco().GetAwaiter().GetResult()) { IsBackground = true, Name = "ilha: pipe do acompanhante" };
        _thread.Start();
    }

    /// <summary>Manda uma linha para o app conectado (sem conexão, nada). Não bloqueia: a escrita é numa fila.</summary>
    public void Enviar(string linha) => Volatile.Read(ref _saida)?.Writer.TryWrite(linha);

    private NamedPipeServerStream Criar()
    {
        var eu = WindowsIdentity.GetCurrent().User!;
        var seg = new PipeSecurity();
        seg.SetOwner(eu);
        seg.AddAccessRule(new PipeAccessRule(eu, PipeAccessRights.FullControl, AccessControlType.Allow));
        // uma instância por vez e sempre a primeira: ninguém mais pode ter criado o nome
        return NamedPipeServerStreamAcl.Create(_nome, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 64 * 1024, 64 * 1024, seg);
    }

    private async Task Laco()
    {
        int falhas = 0;
        while (!_parar.IsCancellationRequested)
        {
            NamedPipeServerStream s;
            try { s = Criar(); }
            catch (Exception e)
            {
                Log.Escrever($"mascote: pipe do acompanhante não abriu ({e.GetType().Name}: {e.Message})");
                if (++falhas > 5 || e is UnauthorizedAccessException && falhas > 1) return;
                try { await Task.Delay(2000, _parar.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }
            try
            {
                await s.WaitForConnectionAsync(_parar.Token).ConfigureAwait(false);
                falhas = 0;
            }
            catch (Exception)
            {
                s.Dispose();
                if (_parar.IsCancellationRequested) return;
                continue;
            }
            await Atender(s).ConfigureAwait(false);
        }
    }

    private async Task Atender(NamedPipeServerStream s)
    {
        var saida = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        using var fim = CancellationTokenSource.CreateLinkedTokenSource(_parar.Token);
        Volatile.Write(ref _saida, saida);
        Seguro(_conectou, "conectou");
        var escritor = Escrever(s, saida.Reader, fim.Token);
        try
        {
            var juntador = new JuntadorDeLinhas(ProtocoloAcompanhante.MAIOR_LINHA);
            var buf = new byte[64 * 1024];
            while (!fim.IsCancellationRequested)
            {
                int n = await s.ReadAsync(buf.AsMemory(), fim.Token).ConfigureAwait(false);
                if (n == 0) break;
                foreach (var linha in juntador.Receber(buf.AsSpan(0, n)))
                    if (ProtocoloAcompanhante.Ler(linha) is { } m) Seguro(() => _chegou(m), m.Tipo);
                if (juntador.Estourou) { Log.Escrever("mascote: linha grande demais no pipe, conexão fechada"); break; }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception e) { Log.Escrever("mascote: pipe do acompanhante: " + e); }
        finally
        {
            Volatile.Write(ref _saida, null);
            saida.Writer.TryComplete();
            fim.Cancel();
            try { await escritor.ConfigureAwait(false); } catch (Exception) { }
            s.Dispose();
            Seguro(_caiu, "caiu");
        }
    }

    private static async Task Escrever(Stream s, ChannelReader<string> fila, CancellationToken ct)
    {
        try
        {
            await foreach (var linha in fila.ReadAllAsync(ct).ConfigureAwait(false))
                await s.WriteAsync(Encoding.UTF8.GetBytes(linha + "\n"), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static void Seguro(Action a, string onde)
    {
        try { a(); }
        catch (Exception e) { Log.Escrever($"mascote: aviso do pipe ({onde}): {e}"); }
    }

    public void Dispose()
    {
        _parar.Cancel();
        _thread.Join(1500);
    }
}
