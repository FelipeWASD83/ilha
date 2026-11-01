using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Não é Ilha.Modulos.IA: o núcleo tem a classe estática Ilha.Modulos, e o namespace com o mesmo nome dá CS0101.
namespace Ilha.IA;

/// <summary>
/// O que a OpenAI (Responses) e o Claude (Messages) têm em comum, sem rede: porte para C# do provedor.py dos
/// projetos em Python (lado do modelo, pares de reserva, preço, falta de crédito, disjuntor, raciocínio e a
/// conversão do pedido do formato da OpenAI para o do Claude). As tabelas são as mesmas; mudou lá, muda aqui.
/// A conversão de esquema e de ferramentas do original não veio porque o chat e o tradutor não usam.
/// </summary>
internal static class Provedor
{
    public const string AnthropicVersao = "2023-06-01";
    public const string Abertura = "[A conversa começa aqui.]";
    public const string Continue = "[Continue a partir daqui.]";
    public const double SegundosDoDisjuntor = 30 * 60;

    public static JsonObject Cache() => new() { ["type"] = "ephemeral" };

    // ── lado ──────────────────────────────────────────────────────────────────

    public static bool EhClaude(string? modelo) => (modelo ?? "").Trim().ToLowerInvariant().StartsWith("claude");

    public static string ProvedorDe(string? modelo) => EhClaude(modelo) ? "claude" : "openai";

    /// <summary>Modelo de raciocínio da OpenAI (série o e GPT-5 em diante): só esses aceitam `reasoning`.</summary>
    public static bool EhModeloDeRaciocinio(string? modelo) => Regex.IsMatch(modelo ?? "", @"^(o\d|gpt-[5-9])", RegexOptions.IgnoreCase);

    // Par de cada modelo do outro lado: mesma faixa de preço e de inteligência.
    public static readonly IReadOnlyDictionary<string, string> Pares = new Dictionary<string, string>
    {
        ["gpt-6-luna"] = "claude-haiku-5-5",
        ["gpt-6.1-sol"] = "claude-sonnet-5-5",
        ["gpt-6-sol"] = "claude-sonnet-5-5",
        ["gpt-6-astra"] = "claude-opus-5-5",
        ["claude-haiku-5-5"] = "gpt-6-luna",
        ["claude-sonnet-5-5"] = "gpt-6.1-sol",
        ["claude-opus-5-5"] = "gpt-6-astra",
        ["claude-fable-5-1"] = "gpt-6-astra",
        ["claude-fable-5"] = "gpt-6-astra",
        ["claude-opus-5"] = "gpt-6-astra",
        ["claude-opus-4-8"] = "gpt-6-astra",
        ["claude-sonnet-5"] = "gpt-6.1-sol",
        ["claude-sonnet-4-6"] = "gpt-6.1-sol",
        ["claude-haiku-4-5"] = "gpt-6-luna",
        ["gpt-5.6-luna"] = "claude-haiku-5-5",
        ["gpt-5.6-terra"] = "claude-sonnet-5-5",
        ["gpt-5.6-sol"] = "claude-sonnet-5-5",
    };

    public static readonly IReadOnlyDictionary<string, string> ReservaPadrao = new Dictionary<string, string>
    {
        ["openai"] = "claude-haiku-5-5",
        ["claude"] = "gpt-6-luna",
    };

    private static readonly string[] SemReserva = { "nenhuma", "nenhum", "off", "desligada", "desligado", "não", "nao", "false" };

    private static string SemData(string modelo) => Regex.Replace((modelo ?? "").Trim().ToLowerInvariant(), @"-\d{8}$", "");

    /// <summary>
    /// O modelo do outro lado que assume quando o principal fica sem crédito. `configurada`: "auto" (o par da
    /// tabela), um id do OUTRO lado, ou "nenhuma". Id do mesmo lado não serve (acabou o crédito de um lado, o do
    /// mesmo lado acabou junto): cai no par da tabela.
    /// </summary>
    public static string? ReservaDe(string modelo, string? configurada = "auto")
    {
        string escolha = string.IsNullOrWhiteSpace(configurada) ? "auto" : configurada.Trim();
        if (SemReserva.Contains(escolha.ToLowerInvariant())) return null;
        if (escolha.ToLowerInvariant() != "auto" && ProvedorDe(escolha) != ProvedorDe(modelo)) return escolha;
        if (Pares.TryGetValue(modelo ?? "", out var par)) return par;
        if (Pares.TryGetValue(SemData(modelo ?? ""), out par)) return par;
        return ReservaPadrao[ProvedorDe(modelo)];
    }

    // ── preço (US$ por 1 milhão de tokens): entrada, leitura do cache, escrita do cache, saída ──

    public static readonly IReadOnlyDictionary<string, (double Entrada, double Lido, double Escrita, double Saida)> Precos =
        new Dictionary<string, (double, double, double, double)>
        {
            ["gpt-6-luna"] = (0.10, 0.01, 0.125, 0.50),
            ["gpt-6.1-sol"] = (2.00, 0.10, 2.50, 10.00),
            ["gpt-6-sol"] = (2.00, 0.20, 2.50, 10.00),
            ["gpt-6-astra"] = (10.00, 1.00, 12.50, 50.00),
            ["gpt-5.6-luna"] = (0.20, 0.02, 0.25, 1.20),
            ["gpt-5.6-terra"] = (2.00, 0.20, 2.50, 12.00),
            ["gpt-5.6-sol"] = (4.00, 0.40, 5.00, 20.00),
            ["gpt-5-mini"] = (0.25, 0.025, 0.25, 2.00),
            ["gpt-5-nano"] = (0.05, 0.005, 0.05, 0.40),
            ["gpt-4o-mini"] = (0.15, 0.075, 0.15, 0.60),
            ["gpt-4o"] = (2.50, 1.25, 2.50, 10.00),
            ["claude-haiku-5-5"] = (0.10, 0.01, 0.125, 0.50),
            ["claude-sonnet-5-5"] = (2.00, 0.10, 2.50, 10.00),
            ["claude-opus-5-5"] = (4.00, 0.20, 5.00, 20.00),
            ["claude-fable-5-1"] = (10.00, 0.25, 12.50, 50.00),
            ["claude-fable-5"] = (10.00, 1.00, 12.50, 50.00),
            ["claude-sonnet-5"] = (2.00, 0.20, 2.50, 10.00),
            ["claude-opus-5"] = (5.00, 0.50, 6.25, 25.00),
            ["claude-opus-4-8"] = (5.00, 0.50, 6.25, 25.00),
            ["claude-sonnet-4-6"] = (3.00, 0.30, 3.75, 15.00),
            ["claude-haiku-4-5"] = (1.00, 0.10, 1.25, 5.00),
        };

    /// <summary>Preço do modelo; aceita id com data no fim (claude-haiku-4-5-20251001).</summary>
    public static (double Entrada, double Lido, double Escrita, double Saida)? PrecoDe(string? modelo)
    {
        string m = (modelo ?? "").Trim().ToLowerInvariant();
        if (Precos.TryGetValue(m, out var p)) return p;
        if (Precos.TryGetValue(SemData(m), out p)) return p;
        return null;
    }

    private static long Inteiro(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        if (v.TryGetValue<string>(out var s) && long.TryParse(s, out l)) return l;
        return 0;
    }

    /// <summary>Custo em dólares de um `uso` no formato da OpenAI (ver UsoNormalizado). Null se o modelo não tem preço.</summary>
    public static double? Custo(string? modelo, JsonObject? uso)
    {
        var preco = PrecoDe(modelo);
        if (preco == null || uso == null || uso.Count == 0) return null;
        var (entrada, lido, escrita, saida) = preco.Value;
        long total = Inteiro(uso["input_tokens"]);
        var det = uso["input_tokens_details"] as JsonObject;
        long cache = Math.Min(Inteiro(det?["cached_tokens"]), total);
        long gravado = Math.Min(Inteiro(det?["cache_write_tokens"]), total - cache);
        long cheio = total - cache - gravado;
        return (cheio * entrada + cache * lido + gravado * escrita + Inteiro(uso["output_tokens"]) * saida) / 1_000_000;
    }

    // ── sem crédito ───────────────────────────────────────────────────────────

    private static readonly string[] SinaisDeSemCredito =
    {
        "insufficient_quota",            // OpenAI: 429 com code/type insufficient_quota
        "exceeded your current quota",   // OpenAI: mesma situação, na mensagem
        "billing_hard_limit",            // OpenAI: teto de gasto da conta
        "billing_not_active",            // OpenAI: conta sem forma de pagamento
        "billing_error",                 // Anthropic: type do erro 402
        "credit balance is too low",     // Anthropic: 400 invalid_request_error
        "credit balance too low",
        "specified api usage limits",    // Anthropic: teto de gasto do workspace
        "out of credits",
        "insufficient credit",
    };

    private static string Texto(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "";

    /// <summary>
    /// A resposta de erro diz que o crédito ou o teto de gasto da conta acabou? 402 sempre; 400/401/403/429 só com
    /// um dos sinais no texto. Limite por minuto, servidor ocupado (5xx, 529) e chave errada continuam sendo erro.
    /// </summary>
    public static bool FaltaDeCredito(int status, JsonNode? dados)
    {
        if (status == 402) return true;
        if (status is not (400 or 401 or 403 or 429)) return false;
        var erro = (dados as JsonObject)?["error"];
        string texto = erro is JsonObject o
            ? $"{Texto(o["code"])} {Texto(o["type"])} {Texto(o["message"])}"
            : erro == null ? "" : Texto(erro);
        texto = texto.ToLowerInvariant();
        return SinaisDeSemCredito.Any(s => texto.Contains(s));
    }

    // ── raciocínio: o esforço da OpenAI traduzido para o Claude ──────────────

    public static readonly string[] Esforcos = { "none", "low", "medium", "high", "xhigh", "max" };

    private static string FamiliaClaude(string modelo)
    {
        string m = SemData(modelo);
        if (m.StartsWith("claude-haiku-5")) return "haiku5";
        if (m.StartsWith("claude-sonnet-5-5")) return "sonnet55";
        if (m.StartsWith("claude-opus-5-5") || m.StartsWith("claude-fable") || m.StartsWith("claude-mythos")) return "sempre_pensa";
        if (m.StartsWith("claude-sonnet-5") || m.StartsWith("claude-opus-5") || m.StartsWith("claude-opus-4-8") || m.StartsWith("claude-opus-4-7")) return "desliga";
        if (m.StartsWith("claude-opus-4-6") || m.StartsWith("claude-sonnet-4-6")) return "adaptativo";
        return "antigo";
    }

    /// <summary>
    /// Pedaços do corpo do Claude (`thinking` e `output_config.effort`). "none" quer dizer não pensar: o Haiku 5.5 e
    /// os que desligam usam `disabled`; o Sonnet 5.5 recusa o disabled e usa `between_tools`; o Opus 5.5 e o Fable
    /// não desligam e vão no esforço mais baixo. Modelo antigo não recebe nada. Nunca vai temperature.
    /// </summary>
    public static JsonObject ParametrosDeRaciocinio(string modelo, string esforco)
    {
        string familia = FamiliaClaude(modelo);
        if (familia == "antigo") return new JsonObject();
        if (!Esforcos.Contains(esforco)) esforco = "low";
        if (esforco == "none")
        {
            if (familia is "haiku5" or "desliga") return new JsonObject { ["thinking"] = new JsonObject { ["type"] = "disabled" } };
            if (familia == "sonnet55")
                return new JsonObject { ["thinking"] = new JsonObject { ["type"] = "between_tools" }, ["output_config"] = new JsonObject { ["effort"] = "low" } };
            return new JsonObject { ["output_config"] = new JsonObject { ["effort"] = "low" } };
        }
        if (familia == "adaptativo" && esforco == "xhigh") esforco = "high";
        return new JsonObject { ["thinking"] = new JsonObject { ["type"] = "adaptive" }, ["output_config"] = new JsonObject { ["effort"] = esforco } };
    }

    // ── pedido: formato da OpenAI → formato do Claude ────────────────────────

    private static readonly string[] TiposDeTexto = { "input_text", "output_text", "text" };
    private static readonly Regex DataUrl = new(@"^data:([^;,]+);base64,(.*)$", RegexOptions.Singleline);

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static string TextoDe(JsonNode? conteudo)
    {
        if (conteudo is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        var partes = new List<string>();
        if (conteudo is JsonArray arr)
            foreach (var p in arr)
                if (p is JsonObject o && TiposDeTexto.Contains(Str(o["type"])) && Str(o["text"]).Length > 0) partes.Add(Str(o["text"]));
        return string.Join("\n", partes);
    }

    private static JsonObject? BlocoDeImagem(JsonObject parte)
    {
        var url = parte["image_url"];
        string u = url is JsonObject uo ? Str(uo["url"]) : Str(url);
        if (u.Length == 0) return null;
        var m = DataUrl.Match(u);
        if (m.Success)
            return new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = m.Groups[1].Value, ["data"] = m.Groups[2].Value },
            };
        if (u.StartsWith("http://") || u.StartsWith("https://"))
            return new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "url", ["url"] = u } };
        return null;
    }

    private static List<JsonObject> BlocosDe(JsonNode? conteudo)
    {
        var blocos = new List<JsonObject>();
        if (conteudo is JsonValue v && v.TryGetValue<string>(out var s))
        {
            if (s.Trim().Length > 0) blocos.Add(new JsonObject { ["type"] = "text", ["text"] = s });
            return blocos;
        }
        if (conteudo is not JsonArray arr) return blocos;
        foreach (var p in arr)
        {
            if (p is not JsonObject o) continue;
            string tipo = Str(o["type"]);
            if (TiposDeTexto.Contains(tipo))
            {
                if (Str(o["text"]).Trim().Length > 0) blocos.Add(new JsonObject { ["type"] = "text", ["text"] = Str(o["text"]) });
            }
            else if (tipo is "input_image" or "image_url")
            {
                var b = BlocoDeImagem(o);
                if (b != null) blocos.Add(b);
            }
            else if (tipo == "image" && o["source"] is JsonObject) blocos.Add((JsonObject)o.DeepClone());
        }
        return blocos;
    }

    private sealed class Msg
    {
        public required string Papel;
        public List<JsonObject> Estavel = new();
        public List<JsonObject> Notas = new();
    }

    /// <summary>
    /// (system, messages) do Claude a partir de `instructions` + `input` da Responses: developer/system antes da
    /// primeira fala viram o system; developer depois de uma fala do usuário vai como mais um bloco na mesma
    /// mensagem; o Claude não aceita começar nem terminar pelo assistente; o ponto de cache fica no último bloco
    /// antes das notas e no fim do system. Não mexe na entrada.
    /// </summary>
    public static (JsonArray Sistema, JsonArray Mensagens) ConverterEntrada(string? instrucoes, JsonNode? entrada)
    {
        var sistema = new List<string>();
        if (!string.IsNullOrWhiteSpace(instrucoes)) sistema.Add(instrucoes.Trim());
        var itens = new List<JsonNode?>();
        if (entrada is JsonValue ev && ev.TryGetValue<string>(out var textoSolto))
            itens.Add(new JsonObject { ["role"] = "user", ["content"] = textoSolto });
        else if (entrada is JsonArray ea) itens.AddRange(ea);

        var mensagens = new List<Msg>();
        var pendentes = new List<JsonObject>();
        string? ultimoDoSistema = null;
        foreach (var no in itens)
        {
            if (no is not JsonObject item) continue;
            string papel = Str(item["role"]);
            if (papel is "developer" or "system")
            {
                string texto = TextoDe(item["content"]).Trim();
                if (texto.Length == 0) continue;
                var nota = new JsonObject { ["type"] = "text", ["text"] = texto };
                if (mensagens.Count == 0) { sistema.Add(texto); ultimoDoSistema = texto; }
                else if (mensagens[^1].Papel == "user") mensagens[^1].Notas.Add(nota);
                else pendentes.Add(nota);
                continue;
            }
            if (papel is not ("user" or "assistant")) continue;
            var blocos = BlocosDe(item["content"]);
            if (blocos.Count == 0) continue;
            if (papel == "user" && pendentes.Count > 0)
            {
                blocos = pendentes.Concat(blocos).ToList();
                pendentes = new List<JsonObject>();
            }
            if (mensagens.Count > 0 && mensagens[^1].Papel == papel)
            {
                var anterior = mensagens[^1];
                anterior.Estavel = anterior.Estavel.Concat(anterior.Notas).Concat(blocos).ToList();
                anterior.Notas = new List<JsonObject>();
            }
            else mensagens.Add(new Msg { Papel = papel, Estavel = blocos });
        }
        if (pendentes.Count > 0) mensagens.Add(new Msg { Papel = "user", Notas = pendentes });
        if (mensagens.Count == 0 && ultimoDoSistema != null && sistema.Count > 0 && sistema[^1] == ultimoDoSistema)
        {
            // conversa sem nenhuma fala: o último developer é a nota da vez e não pode entrar no cache do system
            sistema.RemoveAt(sistema.Count - 1);
            mensagens.Add(new Msg
            {
                Papel = "user",
                Estavel = new List<JsonObject> { new() { ["type"] = "text", ["text"] = Abertura } },
                Notas = new List<JsonObject> { new() { ["type"] = "text", ["text"] = ultimoDoSistema } },
            });
        }
        if (mensagens.Count == 0 || mensagens[0].Papel == "assistant")
            mensagens.Insert(0, new Msg { Papel = "user", Estavel = new List<JsonObject> { new() { ["type"] = "text", ["text"] = Abertura } } });
        if (mensagens[^1].Papel == "assistant")
            mensagens.Add(new Msg { Papel = "user", Estavel = new List<JsonObject> { new() { ["type"] = "text", ["text"] = Continue } } });

        // o ponto de cache no último bloco estável (antes das notas)
        int marcada = -1;
        for (int i = mensagens.Count - 1; i >= 0; i--) if (mensagens[i].Estavel.Count > 0) { marcada = i; break; }
        var saida = new JsonArray();
        for (int i = 0; i < mensagens.Count; i++)
        {
            var m = mensagens[i];
            var conteudo = new JsonArray();
            for (int k = 0; k < m.Estavel.Count; k++)
            {
                var b = (JsonObject)m.Estavel[k].DeepClone();
                if (i == marcada && k == m.Estavel.Count - 1) b["cache_control"] = Cache();
                conteudo.Add(b);
            }
            foreach (var n in m.Notas) conteudo.Add(n.DeepClone());
            saida.Add(new JsonObject { ["role"] = m.Papel, ["content"] = conteudo });
        }
        var blocosDoSistema = new JsonArray();
        if (sistema.Count > 0)
            blocosDoSistema.Add(new JsonObject { ["type"] = "text", ["text"] = string.Join("\n\n", sistema), ["cache_control"] = Cache() });
        return (blocosDoSistema, saida);
    }

    /// <summary>O corpo do POST /v1/messages (sem ferramentas: o chat e o tradutor não usam).</summary>
    public static JsonObject CorpoClaude(string modelo, JsonArray sistema, JsonArray mensagens, string esforco, int maxSaida, bool stream = false)
    {
        var corpo = new JsonObject { ["model"] = modelo, ["max_tokens"] = maxSaida, ["messages"] = mensagens.Parent == null ? mensagens : mensagens.DeepClone() };
        if (sistema.Count > 0) corpo["system"] = sistema.Parent == null ? sistema : sistema.DeepClone();
        var extra = ParametrosDeRaciocinio(modelo, esforco);
        if (extra["thinking"] is JsonNode t) corpo["thinking"] = t.DeepClone();
        if (extra["output_config"] is JsonObject oc && oc.Count > 0) corpo["output_config"] = oc.DeepClone();
        if (stream) corpo["stream"] = true;
        return corpo;
    }

    /// <summary>O corpo do POST /v1/responses da OpenAI: `store: false`, e `reasoning` só para modelo de raciocínio.</summary>
    public static JsonObject CorpoOpenAI(string modelo, string? instrucoes, JsonArray itens, string esforco, int maxSaida, bool stream = true)
    {
        var corpo = new JsonObject { ["model"] = modelo, ["input"] = itens.Parent == null ? itens : itens.DeepClone(), ["store"] = false, ["max_output_tokens"] = maxSaida };
        if (!string.IsNullOrWhiteSpace(instrucoes)) corpo["instructions"] = instrucoes;
        if (EhModeloDeRaciocinio(modelo)) corpo["reasoning"] = new JsonObject { ["effort"] = esforco };
        if (stream) corpo["stream"] = true;
        return corpo;
    }

    /// <summary>`usage` do Claude no formato da OpenAI (input_tokens com o cache dentro).</summary>
    public static JsonObject? UsoNormalizado(JsonObject? uso)
    {
        if (uso == null || uso.Count == 0) return null;
        long lido = Inteiro(uso["cache_read_input_tokens"]);
        long escrito = Inteiro(uso["cache_creation_input_tokens"]);
        long novo = Inteiro(uso["input_tokens"]);
        return new JsonObject
        {
            ["input_tokens"] = novo + lido + escrito,
            ["output_tokens"] = Inteiro(uso["output_tokens"]),
            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = lido, ["cache_write_tokens"] = escrito },
        };
    }

    public sealed record RespostaClaude(string Texto, string Parada, JsonObject? Uso, JsonObject? Detalhe);

    /// <summary>Lê os blocos pelo `type`, nunca pela posição: a resposta pode abrir com um bloco de pensamento.</summary>
    public static RespostaClaude LerRespostaClaude(JsonObject dados)
    {
        var blocos = (dados["content"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        string texto = string.Concat(blocos.Where(b => Str(b["type"]) == "text").Select(b => Str(b["text"]))).Trim();
        return new RespostaClaude(texto, Str(dados["stop_reason"]), UsoNormalizado(dados["usage"] as JsonObject), dados["stop_details"] as JsonObject);
    }
}

/// <summary>
/// Lembra que um lado ficou sem crédito, para não gastar uma viagem perdida a cada pedido. Passado o prazo, o
/// lado volta a ser tentado sozinho (o crédito pode ter voltado).
/// </summary>
internal sealed class Disjuntor
{
    private readonly double _segundos;
    private readonly Func<double> _relogio;
    private readonly Dictionary<string, double> _ate = new();
    private readonly object _trava = new();

    public Disjuntor(double segundos = Provedor.SegundosDoDisjuntor, Func<double>? relogio = null)
    {
        _segundos = segundos;
        _relogio = relogio ?? (() => Environment.TickCount64 / 1000.0);
    }

    public void Abrir(string lado) { lock (_trava) _ate[lado] = _relogio() + _segundos; }

    public void Fechar(string lado) { lock (_trava) _ate.Remove(lado); }

    public bool Aberto(string lado)
    {
        lock (_trava)
        {
            if (!_ate.TryGetValue(lado, out var ate)) return false;
            if (_relogio() >= ate) { _ate.Remove(lado); return false; }
            return true;
        }
    }

    public int MinutosRestantes(string lado)
    {
        lock (_trava)
        {
            if (!_ate.TryGetValue(lado, out var ate)) return 0;
            return Math.Max(0, (int)Math.Floor((ate - _relogio() + 59) / 60));
        }
    }
}
