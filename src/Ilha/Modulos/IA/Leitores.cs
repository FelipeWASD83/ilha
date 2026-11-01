using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ilha.IA;

/// <summary>Erro que já sai em português para a tela. Nunca leva a chave (passa por <see cref="Segredo.Limpar"/>).</summary>
internal class ErroIA : Exception
{
    public ErroIA(string mensagem) : base(Segredo.Limpar(mensagem)) { }
}

/// <summary>O crédito (ou o teto de gasto) de um lado acabou ANTES de qualquer texto: é o gatilho da reserva.</summary>
internal sealed class SemCreditoErro : ErroIA
{
    public string Lado { get; }
    public SemCreditoErro(string mensagem, string lado) : base(mensagem) => Lado = lado;
}

internal static class Segredo
{
    // chaves da OpenAI (sk-..., sk-proj-...) e da Anthropic (sk-ant-...): a OpenAI devolve parte da chave no 401
    private static readonly Regex Chave = new(@"(?<![A-Za-z0-9])sk-[A-Za-z0-9_\-]{3,}[A-Za-z0-9_\-\*\.]*", RegexOptions.Compiled);

    public static string Limpar(string? texto) => Chave.Replace(texto ?? "", Lang.T("(chave)", "(key)"));
}

/// <summary>O que um stream terminado devolve, igual para os dois lados.</summary>
internal sealed record ResultadoStream(string Texto, string? Incompleta, JsonObject? Uso, string Parada);

/// <summary>
/// Lê os eventos do stream de um lado. Cada `Processar` recebe o `data:` de um evento (JSON) e devolve o pedaço
/// de texto novo (ou null). Erro no meio do stream vira exceção na hora; falta de crédito só vira
/// <see cref="SemCreditoErro"/> se nenhum texto chegou ainda (depois disso, trocar de lado reescreveria uma
/// resposta pela metade).
/// </summary>
internal interface ILeitorStream
{
    string? Processar(string dados);
    ResultadoStream Fim();
    bool TemTexto { get; }
}

internal static class Json
{
    public static JsonObject? Objeto(string texto)
    {
        try { return JsonNode.Parse(texto) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    public static string Curto(string s, int max = 160) => s.Length > max ? s[..(max - 1)] + "…" : s;
}

/// <summary>Responses API da OpenAI: response.output_text.delta, response.completed/incomplete/failed e error.</summary>
internal sealed class LeitorOpenAI : ILeitorStream
{
    private readonly StringBuilder _texto = new();
    private JsonObject? _final;
    public bool TemTexto => _texto.Length > 0;

    public string? Processar(string dados)
    {
        var ev = Json.Objeto(dados);
        if (ev == null) return null;
        switch (Json.Str(ev["type"]))
        {
            case "response.output_text.delta":
            {
                string d = Json.Str(ev["delta"]);
                if (d.Length == 0) return null;
                _texto.Append(d);
                return d;
            }
            case "response.completed":
            case "response.incomplete":
                _final = ev["response"] as JsonObject;
                return null;
            case "response.failed":
            {
                var erro = (ev["response"] as JsonObject)?["error"] as JsonObject;
                Falhar(erro, Json.Str(erro?["message"]) is { Length: > 0 } m ? m : Lang.T("a OpenAI marcou a resposta como falha", "OpenAI marked the reply as failed"));
                return null;
            }
            case "error":
            {
                var erro = ev["error"] as JsonObject ?? new JsonObject { ["code"] = Json.Str(ev["code"]), ["message"] = Json.Str(ev["message"]) };
                string m = Json.Str(ev["message"]) is { Length: > 0 } a ? a : Json.Str(erro["message"]) is { Length: > 0 } b ? b : Lang.T("erro no stream", "stream error");
                Falhar(erro, m);
                return null;
            }
            default:
                return null;
        }
    }

    private void Falhar(JsonObject? erro, string mensagem)
    {
        var dados = new JsonObject { ["error"] = erro?.DeepClone() };
        if (!TemTexto && Provedor.FaltaDeCredito(429, dados))
            throw new SemCreditoErro(Lang.T("A OpenAI ficou sem crédito (", "OpenAI ran out of credit (") + Json.Curto(mensagem, 140) + ").", "openai");
        throw new ErroIA("OpenAI: " + Json.Curto(mensagem));
    }

    public ResultadoStream Fim()
    {
        if (_final == null) throw new ErroIA(Lang.T("A conexão com a OpenAI caiu no meio da resposta.", "The connection to OpenAI dropped mid-reply."));
        // o texto oficial é o do response.completed; os deltas são para a tela
        var saida = _final["output"] as JsonArray;
        string oficial = saida == null ? "" : string.Concat(saida.OfType<JsonObject>()
            .Where(i => Json.Str(i["type"]) == "message")
            .SelectMany(i => (i["content"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            .Where(c => Json.Str(c["type"]) == "output_text")
            .Select(c => Json.Str(c["text"])));
        string texto = oficial.Length > 0 ? oficial : _texto.ToString();
        string? incompleta = Json.Str(_final["status"]) == "incomplete"
            ? (Json.Str((_final["incomplete_details"] as JsonObject)?["reason"]) is { Length: > 0 } r ? r : "incompleta")
            : null;
        var uso = _final["usage"] as JsonObject;
        return new ResultadoStream(texto, incompleta, uso?.DeepClone() as JsonObject, Json.Str(_final["status"]));
    }
}

/// <summary>Messages API do Claude: message_start, blocos por índice, message_delta, message_stop e error.</summary>
internal sealed class LeitorClaude : ILeitorStream
{
    private readonly Dictionary<int, (string Tipo, StringBuilder Texto)> _blocos = new();
    private readonly JsonObject _uso = new();
    private string? _parada;
    private bool _terminou;
    public bool TemTexto { get; private set; }

    public string? Processar(string dados)
    {
        var ev = Json.Objeto(dados);
        if (ev == null) return null;
        switch (Json.Str(ev["type"]))
        {
            case "message_start":
                if ((ev["message"] as JsonObject)?["usage"] is JsonObject ui)
                    foreach (var (k, v) in ui) _uso[k] = v?.DeepClone();
                return null;
            case "content_block_start":
            {
                var b = ev["content_block"] as JsonObject;
                int i = (int)(ev["index"]?.GetValue<double>() ?? 0);
                string tipo = Json.Str(b?["type"]);
                string inicial = tipo == "text" ? Json.Str(b?["text"]) : "";
                _blocos[i] = (tipo, new StringBuilder(inicial));
                if (inicial.Length > 0) { TemTexto = true; return inicial; }
                return null;
            }
            case "content_block_delta":
            {
                int i = (int)(ev["index"]?.GetValue<double>() ?? 0);
                var d = ev["delta"] as JsonObject;
                if (!_blocos.TryGetValue(i, out var b) || Json.Str(d?["type"]) != "text_delta") return null;
                string t = Json.Str(d?["text"]);
                if (t.Length == 0) return null;
                b.Texto.Append(t);
                TemTexto = true;
                return t;
            }
            case "message_delta":
            {
                if ((ev["delta"] as JsonObject)?["stop_reason"] is JsonNode p && Json.Str(p).Length > 0) _parada = Json.Str(p);
                if (ev["usage"] is JsonObject u)
                    foreach (var (k, v) in u) if (v != null) _uso[k] = v.DeepClone();
                return null;
            }
            case "message_stop":
                _terminou = true;
                return null;
            case "error":
            {
                var erro = ev["error"] as JsonObject ?? new JsonObject();
                var dadosErro = new JsonObject { ["error"] = erro.DeepClone() };
                string m = Json.Str(erro["message"]);
                if (!TemTexto && Provedor.FaltaDeCredito(400, dadosErro))
                    throw new SemCreditoErro(Lang.T("O Claude ficou sem crédito (", "Claude ran out of credit (") + Json.Curto(m, 140) + ").", "claude");
                // overloaded_error e companhia: erro de servidor, nunca troca de lado no meio
                int status = Json.Str(erro["type"]) switch { "overloaded_error" => 529, "rate_limit_error" => 429, "api_error" => 500, "authentication_error" => 401, _ => 0 };
                throw new ErroIA(status > 0 ? Mensagens.DeErro("claude", status, dadosErro) : "Claude: " + Json.Curto(m.Length > 0 ? m : Lang.T("erro no stream", "stream error")));
            }
            default:
                return null;
        }
    }

    public ResultadoStream Fim()
    {
        if (!_terminou && _parada == null) throw new ErroIA(Lang.T("A conexão com o Claude caiu no meio da resposta.", "The connection to Claude dropped mid-reply."));
        string texto = string.Concat(_blocos.OrderBy(b => b.Key).Where(b => b.Value.Tipo == "text").Select(b => b.Value.Texto.ToString())).Trim();
        string? incompleta = _parada switch
        {
            "max_tokens" => "max_output_tokens",
            "refusal" => "content_filter",
            null or "" or "end_turn" or "stop_sequence" or "tool_use" => null,
            var outra => outra,
        };
        return new ResultadoStream(texto, incompleta, Provedor.UsoNormalizado(_uso), _parada ?? "");
    }
}

/// <summary>Mensagens de erro HTTP em português. Nunca repete a mensagem do servidor no 401 (a OpenAI põe parte da chave nela).</summary>
internal static class Mensagens
{
    public static string Nome(string lado) => lado == "claude" ? "a Anthropic" : "a OpenAI";
    private static string NomeMaiusculo(string lado) => lado == "claude" ? "A Anthropic" : "A OpenAI";
    /// <summary>The provider name for English texts (no article).</summary>
    public static string Name(string lado) => lado == "claude" ? "Anthropic" : "OpenAI";

    public static string DeErro(string lado, int status, JsonNode? dados)
    {
        string msg = Json.Str(((dados as JsonObject)?["error"] as JsonObject)?["message"]);
        if (Lang.English) return InEnglish(lado, status, msg);
        string q = NomeMaiusculo(lado);
        return status switch
        {
            401 => q + " recusou a chave (401). Confira na tela de chaves se ela foi colada inteira.",
            403 => q + " negou o acesso (403): a chave não tem permissão para este modelo ou para conversar.",
            404 => q + " não achou o modelo (404). Ele existe nesta conta?",
            429 => q + " está limitando os pedidos (429). Espere um pouco.",
            402 => q + " está sem saldo (402).",
            529 => q + " está sobrecarregada (529). Tente de novo daqui a pouco.",
            >= 500 => q + " está com problema (" + status + "). Tente de novo daqui a pouco.",
            _ => q + " (" + status + "): " + Json.Curto(Segredo.Limpar(msg.Length > 0 ? msg : "erro sem descrição")),
        };
    }

    private static string InEnglish(string lado, int status, string msg)
    {
        string q = Name(lado);
        return status switch
        {
            401 => q + " rejected the key (401). On the keys screen, check that it was pasted whole.",
            403 => q + " denied access (403): the key isn't allowed to use this model or to chat.",
            404 => q + " didn't find the model (404). Does it exist on this account?",
            429 => q + " is rate limiting requests (429). Wait a bit.",
            402 => q + " has no balance left (402).",
            529 => q + " is overloaded (529). Try again in a moment.",
            >= 500 => q + " is having trouble (" + status + "). Try again in a moment.",
            _ => q + " (" + status + "): " + Json.Curto(Segredo.Limpar(msg.Length > 0 ? msg : "error without a description")),
        };
    }
}
