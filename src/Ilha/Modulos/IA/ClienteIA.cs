using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Ilha.IA;

/// <summary>Um pedido ao chat ou ao tradutor, no formato da OpenAI (Responses): o lado do Claude é convertido.</summary>
internal sealed class PedidoIA
{
    public required string Modelo;
    public string Esforco = "low";
    public string Reserva = "auto";
    public string? Instrucoes;
    /// <summary>`input` da Responses: mensagens com role e content (input_text, output_text, input_image).</summary>
    public required JsonArray Itens;
    public int MaxSaida = 8192;
}

internal sealed record Troca(string De, string Para, string Motivo)
{
    /// <summary>The reason when the main side has no key (compared by the chat to word its note differently).</summary>
    public static string MotivoSemChave => Lang.T("sem chave", "no key");
}

internal sealed record RespostaIA(string Texto, string Modelo, string? Incompleta, double? Custo, Troca? Trocou);

/// <summary>
/// Fala com a OpenAI (Responses) e com o Claude (Messages) por HttpClient, com streaming SSE
/// (System.Net.ServerSentEvents, do próprio .NET). Troca de lado sozinha só por falta de crédito, e só antes do
/// primeiro texto: erro de servidor no meio do stream continua sendo erro. A chave é lida do Gerenciador de
/// Credenciais na hora de cada pedido e só vai no cabeçalho.
/// </summary>
internal sealed class ClienteIA
{
    public const string BaseOpenAI = "https://api.openai.com/v1";
    public const string BaseAnthropic = "https://api.anthropic.com/v1";

    private readonly HttpClient _http;
    private readonly Func<string, string?> _chave;
    private readonly string _baseOpenAI, _baseAnthropic;
    public Disjuntor Disjuntor { get; }

    /// <summary>Sem cabeçalhos por quanto tempo antes de desistir, e quanto o stream pode ficar parado.</summary>
    public TimeSpan EsperaCabecalho { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan EsperaParado { get; init; } = TimeSpan.FromSeconds(90);

    /// <param name="chave">"openai" ou "anthropic" → a chave (ou null). O teste passa uma de mentira.</param>
    public ClienteIA(Func<string, string?> chave, HttpMessageHandler? transporte = null, Disjuntor? disjuntor = null,
        string baseOpenAI = BaseOpenAI, string baseAnthropic = BaseAnthropic)
    {
        _chave = chave;
        _http = new HttpClient(transporte ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Ilha/1.0");
        Disjuntor = disjuntor ?? new Disjuntor();
        _baseOpenAI = baseOpenAI.TrimEnd('/');
        _baseAnthropic = baseAnthropic.TrimEnd('/');
    }

    public static string NomeDaCredencial(string lado) => lado == "claude" ? "anthropic" : "openai";

    private string? ChaveDe(string lado)
    {
        var c = _chave(NomeDaCredencial(lado))?.Trim();
        return string.IsNullOrEmpty(c) ? null : c;
    }

    public bool TemChave(string lado) => ChaveDe(lado) != null;

    /// <summary>
    /// Uma resposta, em streaming. `aoTexto` recebe cada pedaço (em qualquer thread); `aoTrocar` avisa quando a
    /// reserva assume (antes do primeiro texto). Cancelar o token para o stream na hora.
    /// </summary>
    public async Task<RespostaIA> Responder(PedidoIA p, Action<string>? aoTexto, Action<string>? aoTrocar, CancellationToken ct)
    {
        string principal = p.Modelo;
        string ladoP = Provedor.ProvedorDe(principal);
        string? reserva = Provedor.ReservaDe(principal, p.Reserva);
        string? ladoR = reserva == null ? null : Provedor.ProvedorDe(reserva);
        bool reservaUsavel() => reserva != null && ladoR != null && TemChave(ladoR) && !Disjuntor.Aberto(ladoR);

        string modelo = principal;
        Troca? trocou = null;
        if (!TemChave(ladoP))
        {
            if (!reservaUsavel())
                throw new ErroIA(reserva != null && ladoR != null && !TemChave(ladoR)
                    ? Lang.T("Sem chave da OpenAI nem da Anthropic. Abra a tela de chaves e cole uma.", "No OpenAI or Anthropic key. Open the keys screen and paste one.")
                    : Lang.T($"Sem chave d{Mensagens.Nome(ladoP)}. Abra a tela de chaves.", $"No {Mensagens.Name(ladoP)} key. Open the keys screen."));
            modelo = reserva!;
            trocou = new Troca(principal, reserva!, Troca.MotivoSemChave);
        }
        else if (Disjuntor.Aberto(ladoP) && reservaUsavel())
        {
            modelo = reserva!;
            trocou = new Troca(principal, reserva!, Lang.T($"sem crédito, de molho por mais {Disjuntor.MinutosRestantes(ladoP)} min", $"out of credit, benched for {Disjuntor.MinutosRestantes(ladoP)} more min"));
        }
        if (trocou != null) aoTrocar?.Invoke(modelo);

        try
        {
            var r = await Chamar(modelo, p, aoTexto, ct).ConfigureAwait(false);
            return r with { Trocou = trocou };
        }
        catch (SemCreditoErro e)
        {
            Disjuntor.Abrir(e.Lado);
            if (trocou != null || !reservaUsavel())
            {
                string extra = reserva == null ? Lang.T(" A reserva está desligada.", " The fallback is off.")
                    : ladoR != null && !TemChave(ladoR) ? Lang.T(" Não tem chave do outro lado para trocar.", " There's no key on the other side to switch to.")
                    : trocou != null ? Lang.T(" A reserva também ficou sem crédito.", " The fallback ran out of credit too.") : "";
                throw new ErroIA(e.Message + extra);
            }
            aoTrocar?.Invoke(reserva!);
            try
            {
                var r = await Chamar(reserva!, p, aoTexto, ct).ConfigureAwait(false);
                return r with { Trocou = new Troca(modelo, reserva!, Lang.T("sem crédito", "out of credit")) };
            }
            catch (SemCreditoErro e2)
            {
                Disjuntor.Abrir(e2.Lado);
                throw new ErroIA(e.Message + Lang.T(" A reserva também ficou sem crédito.", " The fallback ran out of credit too."));
            }
        }
    }

    private async Task<RespostaIA> Chamar(string modelo, PedidoIA p, Action<string>? aoTexto, CancellationToken ct)
    {
        string lado = Provedor.ProvedorDe(modelo);
        string chave = ChaveDe(lado) ?? throw new ErroIA(Lang.T($"Sem chave d{Mensagens.Nome(lado)}.", $"No {Mensagens.Name(lado)} key."));
        HttpRequestMessage req;
        ILeitorStream leitor;
        if (lado == "claude")
        {
            var (sistema, mensagens) = Provedor.ConverterEntrada(p.Instrucoes, p.Itens);
            var corpo = Provedor.CorpoClaude(modelo, sistema, mensagens, p.Esforco, p.MaxSaida, stream: true);
            req = new HttpRequestMessage(HttpMethod.Post, _baseAnthropic + "/messages") { Content = JsonCorpo(corpo) };
            CabecalhosClaude(req, chave);
            leitor = new LeitorClaude();
        }
        else
        {
            var corpo = Provedor.CorpoOpenAI(modelo, p.Instrucoes, (JsonArray)p.Itens.DeepClone(), p.Esforco, p.MaxSaida, stream: true);
            req = new HttpRequestMessage(HttpMethod.Post, _baseOpenAI + "/responses") { Content = JsonCorpo(corpo) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", chave);
            leitor = new LeitorOpenAI();
        }
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var vigia = CancellationTokenSource.CreateLinkedTokenSource(ct);
        vigia.CancelAfter(EsperaCabecalho);
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, vigia.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ErroIA(Lang.T($"{Mensagens.Nome(lado)} não respondeu a tempo.", $"{Mensagens.Name(lado)} didn't respond in time.")); }
        catch (HttpRequestException) { throw new ErroIA(Lang.T($"Não consegui falar com {Mensagens.Nome(lado)}. Confira a internet.", $"Couldn't reach {Mensagens.Name(lado)}. Check the internet connection.")); }
        catch (FormatException) { throw new ErroIA(Lang.T("A chave tem um caractere inválido. Cole de novo na tela de chaves.", "The key has an invalid character. Paste it again on the keys screen.")); }
        finally { req.Dispose(); }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                string corpoErro = "";
                try { corpoErro = await resp.Content.ReadAsStringAsync(vigia.Token).ConfigureAwait(false); } catch { }
                var dados = Json.Objeto(corpoErro);
                int st = (int)resp.StatusCode;
                if (Provedor.FaltaDeCredito(st, dados))
                    throw new SemCreditoErro(Lang.T((lado == "claude" ? "O Claude" : "A OpenAI") + " ficou sem crédito (", (lado == "claude" ? "Claude" : "OpenAI") + " ran out of credit (") +
                        Json.Curto(Json.Str((dados?["error"] as JsonObject)?["message"]) is { Length: > 0 } m ? m : "HTTP " + st, 140) + ").", lado);
                throw new ErroIA(Mensagens.DeErro(lado, st, dados));
            }

            try
            {
                vigia.CancelAfter(EsperaParado);
                await using var corpo = await resp.Content.ReadAsStreamAsync(vigia.Token).ConfigureAwait(false);
                await foreach (var item in SseParser.Create(corpo).EnumerateAsync(vigia.Token).ConfigureAwait(false))
                {
                    vigia.CancelAfter(EsperaParado);
                    if (item.Data is not { Length: > 0 } d || d == "[DONE]") continue;
                    var pedaco = leitor.Processar(d);
                    if (pedaco != null) aoTexto?.Invoke(pedaco);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ErroIA(Lang.T($"{Mensagens.Nome(lado)} parou de mandar a resposta no meio.", $"{Mensagens.Name(lado)} stopped sending the reply midway.")); }
            catch (IOException) when (!ct.IsCancellationRequested) { throw new ErroIA(Lang.T($"A conexão com {Mensagens.Nome(lado)} caiu no meio da resposta.", $"The connection to {Mensagens.Name(lado)} dropped mid-reply.")); }
            catch (HttpRequestException) when (!ct.IsCancellationRequested) { throw new ErroIA(Lang.T($"A conexão com {Mensagens.Nome(lado)} caiu no meio da resposta.", $"The connection to {Mensagens.Name(lado)} dropped mid-reply.")); }
            ct.ThrowIfCancellationRequested();
            var fim = leitor.Fim();
            return new RespostaIA(fim.Texto, modelo, fim.Incompleta, Provedor.Custo(modelo, fim.Uso), null);
        }
    }

    private static StringContent JsonCorpo(JsonObject corpo) => new(corpo.ToJsonString(), Encoding.UTF8, "application/json");

    private static void CabecalhosClaude(HttpRequestMessage req, string chave)
    {
        req.Headers.TryAddWithoutValidation("x-api-key", chave);
        req.Headers.TryAddWithoutValidation("anthropic-version", Provedor.AnthropicVersao);
    }

    /// <summary>
    /// Confere a chave de um lado sem gastar geração: lista os modelos da conta (GET /models é de graça nos dois).
    /// Devolve a frase para a tela; a chave nunca aparece nela.
    /// </summary>
    public async Task<(bool Ok, string Mensagem)> TestarChave(string lado, CancellationToken ct)
    {
        string? chave = ChaveDe(lado);
        if (chave == null) return (false, Lang.T("Nenhuma chave salva.", "No key saved."));
        using var req = new HttpRequestMessage(HttpMethod.Get, lado == "claude" ? _baseAnthropic + "/models?limit=1000" : _baseOpenAI + "/models");
        try
        {
            if (lado == "claude") CabecalhosClaude(req, chave);
            else req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", chave);
        }
        catch (FormatException) { return (false, Lang.T("A chave tem um caractere inválido. Cole de novo.", "The key has an invalid character. Paste it again.")); }
        using var vigia = CancellationTokenSource.CreateLinkedTokenSource(ct);
        vigia.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var resp = await _http.SendAsync(req, vigia.Token).ConfigureAwait(false);
            string corpo = await resp.Content.ReadAsStringAsync(vigia.Token).ConfigureAwait(false);
            var dados = Json.Objeto(corpo);
            if (!resp.IsSuccessStatusCode) return (false, Segredo.Limpar(Mensagens.DeErro(lado, (int)resp.StatusCode, dados)));
            int n = (dados?["data"] as JsonArray)?.Count ?? 0;
            return (true, Lang.T($"Chave válida: {n} modelo(s) nesta conta.", $"Valid key: {n} model(s) on this account."));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (false, Lang.T($"{Mensagens.Nome(lado)} não respondeu a tempo.", $"{Mensagens.Name(lado)} didn't respond in time.")); }
        catch (HttpRequestException) { return (false, Lang.T($"Não consegui falar com {Mensagens.Nome(lado)}. Confira a internet.", $"Couldn't reach {Mensagens.Name(lado)}. Check the internet connection.")); }
    }
}
