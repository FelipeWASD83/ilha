using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Ilha.Claude;

/// <summary>
/// O protocolo entre o hook PermissionRequest (ferramentas/hook_permissao.py) e a ilha, um JSON por linha em UTF-8:
///   hook  -> ilha: {"v":1,"sessao":"...","ferramenta":"Bash","resumo":"git push","cwd":"C:\\..."}
///   ilha  -> hook: {"ok":true,"espera":110}                   (recebido; o hook espera até "espera" segundos)
///   ilha  -> hook: {"decisao":"allow"} | {"decisao":"deny","mensagem":"..."} | {"decisao":null}
/// Decisão nula, conexão fechada ou tempo esgotado: o hook sai sem saída e o diálogo normal do Claude Code segue.
/// Nunca "allow" por tempo esgotado.
/// </summary>
internal static class ProtocoloPermissao
{
    public const string PIPE = "ilha-permissao";
    public const int MAIOR_LINHA = 64 * 1024;

    public sealed record Pedido(string Sessao, string Ferramenta, string Resumo, string Cwd);

    public static Pedido? LerPedido(string linha)
    {
        try
        {
            if (JsonNode.Parse(linha) is not JsonObject o) return null;
            string Txt(string k) => o[k] is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";
            var p = new Pedido(Txt("sessao"), Txt("ferramenta"), Txt("resumo"), Txt("cwd"));
            return p.Ferramenta.Length == 0 ? null : p;
        }
        catch (JsonException) { return null; }
    }

    public static string Recebido(int esperaSegundos) =>
        new JsonObject { ["ok"] = true, ["espera"] = esperaSegundos }.ToJsonString();

    /// <summary>decisao: "allow", "deny" ou null (sem decisão).</summary>
    public static string Decisao(string? decisao, string? mensagem = null)
    {
        var o = new JsonObject { ["decisao"] = decisao };
        if (decisao == "deny") o["mensagem"] = mensagem ?? "Negado pela ilha.";
        return o.ToJsonString();
    }

    /// <summary>O lado do hook, para o autoteste: lê a decisão de uma linha do servidor (null = sem decisão).</summary>
    public static (string? Decisao, string? Mensagem, int? Espera) LerResposta(string linha)
    {
        try
        {
            if (JsonNode.Parse(linha) is not JsonObject o) return (null, null, null);
            string? d = o["decisao"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
            string? m = o["mensagem"] is JsonValue vm && vm.TryGetValue(out string? sm) ? sm : null;
            int? e = o["espera"] is JsonValue ve && ve.TryGetValue(out int ie) ? ie : null;
            if (d is not ("allow" or "deny")) d = null;
            return (d, m, e);
        }
        catch (JsonException) { return (null, null, null); }
    }

    /// <summary>Lê uma linha (até \n) com limite de tamanho; null se a conexão fechou antes.</summary>
    public static async Task<string?> LerLinha(Stream s, CancellationToken ct)
    {
        var buf = new MemoryStream();
        var um = new byte[1];
        while (buf.Length < MAIOR_LINHA)
        {
            int n = await s.ReadAsync(um.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) return buf.Length > 0 ? Encoding.UTF8.GetString(buf.ToArray()) : null;
            if (um[0] == (byte)'\n') return Encoding.UTF8.GetString(buf.ToArray()).TrimEnd('\r');
            buf.WriteByte(um[0]);
        }
        return null;
    }

    public static async Task Escrever(Stream s, string linha, CancellationToken ct)
    {
        var b = Encoding.UTF8.GetBytes(linha + "\n");
        await s.WriteAsync(b, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Servidor do pipe nomeado \\.\pipe\ilha-permissao, numa thread própria. ACL só do usuário atual. Cada conexão é um
/// pedido: chega, a ilha mostra, e quem decide é o clique (ou nada, e o hook cai no diálogo normal).
/// </summary>
internal sealed class ServidorPermissao : IDisposable
{
    private readonly string _nome;
    private readonly Func<int> _espera;
    /// <summary>Chamado numa thread do pool com o pedido e a função de responder; devolve a ação que tira o pedido da tela.</summary>
    private readonly Func<ProtocoloPermissao.Pedido, Action<string?>, Action> _chegou;
    private readonly CancellationTokenSource _parar = new();
    private readonly Thread _thread;
    private bool _primeira = true;

    public ServidorPermissao(string nome, Func<int> esperaSegundos, Func<ProtocoloPermissao.Pedido, Action<string?>, Action> chegou)
    {
        _nome = nome;
        _espera = esperaSegundos;
        _chegou = chegou;
        _thread = new Thread(() => Laco().GetAwaiter().GetResult()) { IsBackground = true, Name = "ilha: pipe de permissão" };
        _thread.Start();
    }

    private NamedPipeServerStream Criar()
    {
        var eu = WindowsIdentity.GetCurrent().User!;
        var seg = new PipeSecurity();
        seg.SetOwner(eu);
        seg.AddAccessRule(new PipeAccessRule(eu, PipeAccessRights.FullControl, AccessControlType.Allow));
        // a primeira instância garante que ninguém criou o nome antes da ilha
        var op = PipeOptions.Asynchronous | (_primeira ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        var s = NamedPipeServerStreamAcl.Create(_nome, PipeDirection.InOut, 16, PipeTransmissionMode.Byte, op, 4096, 4096, seg);
        _primeira = false;
        return s;
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
                Log.Escrever($"claude: pipe de permissão não abriu ({e.GetType().Name}: {e.Message})");
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
            _ = Task.Run(() => Atender(s));
        }
    }

    private async Task Atender(NamedPipeServerStream s)
    {
        Action? tirar = null;
        try
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(_parar.Token);
            limite.CancelAfter(5000);
            var linha = await ProtocoloPermissao.LerLinha(s, limite.Token).ConfigureAwait(false);
            var pedido = linha == null ? null : ProtocoloPermissao.LerPedido(linha);
            if (pedido == null) return;
            int espera = Math.Clamp(_espera(), 5, 590);
            await ProtocoloPermissao.Escrever(s, ProtocoloPermissao.Recebido(espera), _parar.Token).ConfigureAwait(false);

            var resposta = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            tirar = _chegou(pedido, d => resposta.TrySetResult(d));
            // a conexão fechando (o hook morreu, estourou o tempo ou o Claude Code cancelou) também encerra
            var fechou = ProtocoloPermissao.LerLinha(s, _parar.Token);
            var prazo = Task.Delay(TimeSpan.FromSeconds(espera + 5), _parar.Token);
            var primeiro = await Task.WhenAny(resposta.Task, fechou, prazo).ConfigureAwait(false);
            if (primeiro == resposta.Task && s.IsConnected)
            {
                string? d = resposta.Task.Result;
                await ProtocoloPermissao.Escrever(s, ProtocoloPermissao.Decisao(d), CancellationToken.None).ConfigureAwait(false);
                try { s.WaitForPipeDrain(); } catch (IOException) { }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception e) { Log.Escrever("claude: pedido de permissão: " + e); }
        finally
        {
            try { tirar?.Invoke(); } catch (Exception e) { Log.Escrever("claude: tirar pedido: " + e); }
            s.Dispose();
        }
    }

    public void Dispose()
    {
        _parar.Cancel();
        _thread.Join(1500);
    }
}
