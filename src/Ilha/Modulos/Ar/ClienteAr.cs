using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ilha.Ar;

internal enum TipoResposta { Ok, Chave, Recusado, Erro, Rede }

/// <summary>
/// Um pedido ao servidor do ar. Ok: 2xx com JSON. Chave: 401. Recusado: 400 (o servidor diz o porquê, em português).
/// Erro: outro código. Rede: o mini PC nem respondeu (fora da rede, desligado, prazo).
/// </summary>
internal sealed record RespostaAr(TipoResposta Tipo, int Status, string Corpo, string ErroServidor)
{
    /// <summary>A frase para a cena quando não deu certo ("" quando deu).</summary>
    public string Problema => Tipo switch
    {
        TipoResposta.Ok => "",
        TipoResposta.Chave => ContasAr.ProblemaChave,
        TipoResposta.Recusado => ErroServidor.Length > 0 ? ErroServidor : "O servidor do ar recusou o pedido.",
        TipoResposta.Erro => ErroServidor.Length > 0 ? ErroServidor : ContasAr.TextoErroHttp(Status),
        _ => ContasAr.ProblemaRede,
    };

    public static RespostaAr Classificar(int status, string corpo)
    {
        if (status == 401) return new RespostaAr(TipoResposta.Chave, 401, "", "");
        if (status is >= 200 and < 300)
        {
            string t = corpo.TrimStart();
            return t.StartsWith('{')
                ? new RespostaAr(TipoResposta.Ok, status, corpo, "")
                : new RespostaAr(TipoResposta.Erro, status, "", ContasAr.ProblemaResposta);
        }
        string erro = ContasAr.ErroDoCorpo(corpo);
        // 500 do servidor vem com "Erro interno do servidor."; o resto (404, 405, 413...) é defeito daqui: mostra o código
        if (status == 400) return new RespostaAr(TipoResposta.Recusado, 400, "", erro);
        return new RespostaAr(TipoResposta.Erro, status, "", status >= 500 && erro.Length > 0 ? erro : "");
    }
}

/// <summary>
/// HTTP com o servidor do projeto ar-do-quarto (contrato em ar-do-quarto/docs/contrato.md). A chave vai só no
/// cabeçalho Authorization, nunca no endereço, em log ou em mensagem. Só as rotas que a ilha usa passam: o
/// /api/atualizar (que força leitura da LG) nunca sai daqui, e o /api/simulador só existe para o autoteste, no
/// simulador local.
/// </summary>
internal static class ClienteAr
{
    // o servidor fecha conexão parada em 30 s; a ilha larga antes para nunca reaproveitar uma já fechada
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(4),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
        AllowAutoRedirect = false,
    })
    { Timeout = TimeSpan.FromSeconds(8) };

    public static bool RotaPermitida(HttpMethod metodo, string rota, string endereco) => (metodo.Method, rota) switch
    {
        ("GET", "/api/estado") or ("GET", "/api/historico") or ("GET", "/api/ping") or ("POST", "/api/comando") => true,
        ("POST", "/api/simulador") => EhLocal(endereco),
        _ => false,
    };

    public static bool EhLocal(string endereco) =>
        Uri.TryCreate(endereco, UriKind.Absolute, out var u) && (u.Host == "127.0.0.1" || u.Host == "localhost" || u.Host == "[::1]");

    /// <summary>Nunca lança. `endereco` é a origem (http://ip:porta); `chave` vazia manda sem Authorization (simulador sem chave).</summary>
    public static async Task<RespostaAr> Pedir(string endereco, string? chave, HttpMethod metodo, string rota, string? corpoJson,
        CancellationToken ct = default)
    {
        if (ContasAr.NormalizarEndereco(endereco) is not string origem || !RotaPermitida(metodo, rota, origem))
            return new RespostaAr(TipoResposta.Erro, 0, "", "Pedido não permitido pela ilha.");
        try
        {
            using var req = new HttpRequestMessage(metodo, origem + rota);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            if (!string.IsNullOrEmpty(chave)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", chave);
            if (corpoJson != null) req.Content = new StringContent(corpoJson, Encoding.UTF8, "application/json");
            using var r = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            string corpo = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return RespostaAr.Classificar((int)r.StatusCode, corpo);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or System.IO.IOException)
        {
            return new RespostaAr(TipoResposta.Rede, 0, "", "");
        }
        catch (Exception e)
        {
            // a mensagem não leva a chave (ela só existe no cabeçalho), mas também não leva o endereço
            Log.Escrever("ar: pedido falhou: " + e.GetType().Name);
            return new RespostaAr(TipoResposta.Rede, 0, "", "");
        }
    }
}
