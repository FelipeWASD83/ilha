using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Ilha.Calculadora;

internal sealed record Moeda(string Id, string Nome, string Simbolo, string[] Apelidos);

/// <summary>Quanto vale 1 unidade de cada moeda, em reais; quando é a cotação e de onde veio.</summary>
internal sealed class Cotacoes
{
    public Dictionary<string, double> Real { get; init; } = new();
    public DateTime Quando { get; init; }
    /// <summary>"AwesomeAPI" ou "BCE" (a Frankfurter, que publica a cotação do Banco Central Europeu uma vez por dia).</summary>
    public string Fonte { get; init; } = "";
    /// <summary>Quando a ilha buscou (é por ela que vale a validade de 10 minutos).</summary>
    public DateTime BuscadoEm { get; init; }
    /// <summary>A hora de cada cotação (na AwesomeAPI o bitcoin anda a cada minuto e o dólar para no fim de semana).</summary>
    public Dictionary<string, DateTime> Horas { get; init; } = new();

    public DateTime QuandoDe(string? id) => id != null && Horas.TryGetValue(id, out var h) ? h : Quando;
}

/// <summary>
/// Cotações e conversão entre moedas, porta do cambio.js da nova aba: a AwesomeAPI devolve tudo em reais, então o
/// real é o pivô (valor → reais → destino) e uma chamada traz todas as moedas. Reserva: Frankfurter (BCE, sem
/// chave, diária, sem peso argentino nem cripto). Sem chave nenhuma.
/// </summary>
internal static class Cambio
{
    public static readonly Moeda[] Moedas =
    {
        new("BRL", "Real", "R$", new[] { "brl", "real", "reais", "r$" }),
        new("USD", "Dólar americano", "US$", new[] { "usd", "dólar", "dólares", "dolares", "us$", "$", "dollar", "dollars", "dólar americano" }),
        new("EUR", "Euro", "€", new[] { "eur", "euro", "euros", "€" }),
        new("GBP", "Libra esterlina", "£", new[] { "gbp", "libra esterlina", "libras esterlinas", "£" }),
        new("JPY", "Iene", "¥", new[] { "jpy", "iene", "ienes", "yen", "¥" }),
        new("CAD", "Dólar canadense", "CA$", new[] { "cad", "dólar canadense", "dólares canadenses", "ca$" }),
        new("AUD", "Dólar australiano", "A$", new[] { "aud", "dólar australiano", "dólares australianos", "a$" }),
        new("CHF", "Franco suíço", "Fr", new[] { "chf", "franco", "francos", "franco suíço", "francos suíços" }),
        new("CNY", "Yuan", "CN¥", new[] { "cny", "yuan", "yuans", "renminbi", "cn¥" }),
        new("ARS", "Peso argentino", "AR$", new[] { "ars", "peso", "pesos", "peso argentino", "pesos argentinos", "ar$" }),
        new("BTC", "Bitcoin", "₿", new[] { "btc", "bitcoin", "bitcoins", "₿" }),
        new("ETH", "Ethereum", "Ξ", new[] { "eth", "ethereum", "ether" }),
    };

    public static Moeda? Achar(string id) => Moedas.FirstOrDefault(m => m.Id == id);

    public const string UrlAwesome = "https://economia.awesomeapi.com.br/json/last/";
    // api.frankfurter.app responde 301 para cá desde 2025 (conferido em 01/11/2026)
    public const string UrlFrankfurter = "https://api.frankfurter.dev/v1/latest?from=BRL&to=";
    // o que a Frankfurter tem da lista (sem ARS, BTC e ETH)
    private static readonly string[] NaFrankfurter = { "USD", "EUR", "GBP", "JPY", "CAD", "AUD", "CHF", "CNY" };
    public static readonly TimeSpan Validade = TimeSpan.FromMinutes(10);

    public static HttpClient NovoHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Ilha-calculadora/" + Atualizacao.Versao);
        return h;
    }

    public static string Pares => string.Join(",", Moedas.Where(m => m.Id != "BRL").Select(m => m.Id + "-BRL"));

    private static double Numero(JsonNode? n)
    {
        if (n is not JsonValue v) return double.NaN;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return double.NaN;
    }

    /// <summary>Resposta da AwesomeAPI em cotações. Par que falta ou vem quebrado é pulado, não derruba o resto.</summary>
    public static Cotacoes LerAwesome(JsonNode? json, DateTime agora)
    {
        var real = new Dictionary<string, double> { ["BRL"] = 1 };
        var horas = new Dictionary<string, DateTime>();
        long quando = 0;
        foreach (var m in Moedas)
        {
            if (m.Id == "BRL") continue;
            var item = json?[m.Id + "BRL"];
            double r = Numero(item?["bid"]);
            if (!double.IsFinite(r) || r <= 0) continue;
            real[m.Id] = r;
            double t = Numero(item?["timestamp"]);
            if (!double.IsFinite(t) || t <= 0) continue;
            horas[m.Id] = DateTimeOffset.FromUnixTimeSeconds((long)t).LocalDateTime;
            if (t > quando) quando = (long)t;
        }
        return new Cotacoes
        {
            Real = real, Horas = horas, Fonte = "AwesomeAPI", BuscadoEm = agora,
            Quando = quando > 0 ? DateTimeOffset.FromUnixTimeSeconds(quando).LocalDateTime : agora,
        };
    }

    /// <summary>Resposta da Frankfurter ({"base":"BRL","date":...,"rates":{"USD":0.18}}): 1 USD = 1 / 0,18 reais.</summary>
    public static Cotacoes LerFrankfurter(JsonNode? json, DateTime agora)
    {
        var real = new Dictionary<string, double> { ["BRL"] = 1 };
        if (json?["rates"] is JsonObject taxas && (json["base"]?.GetValue<string>() ?? "BRL") == "BRL")
            foreach (var (id, n) in taxas)
            {
                double t = Numero(n);
                if (Achar(id) != null && double.IsFinite(t) && t > 0) real[id] = 1 / t;
            }
        DateTime quando = DateTime.TryParseExact(json?["date"]?.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : agora;
        return new Cotacoes { Real = real, Fonte = "BCE", Quando = quando, BuscadoEm = agora };
    }

    /// <summary>Quanto vale `valor` em `de` na moeda `para` (NaN se faltar cotação).</summary>
    public static double Converter(double valor, string de, string para, Cotacoes c)
    {
        if (!c.Real.TryGetValue(de, out var a) || !c.Real.TryGetValue(para, out var b) || !double.IsFinite(valor)) return double.NaN;
        return valor * a / b;
    }

    private static int Casas(double valor, string id)
    {
        double abs = Math.Abs(valor);
        return id is "BTC" or "ETH" ? 6 : abs != 0 && abs < 1 ? 4 : 2;
    }

    /// <summary>Casas que fazem sentido: cripto e valor abaixo de 1 pedem mais, real e dólar, duas ("1.234,50").</summary>
    public static string FormatarMoeda(double valor, string id, bool milhar = true)
    {
        if (!double.IsFinite(valor)) return "";
        string fmt = (milhar ? "#,##0.00" : "0.00") + new string('#', Casas(valor, id) - 2);
        return valor.ToString(fmt, Calculo.Br);
    }

    /// <summary>Busca as cotações: AwesomeAPI e, se ela falhar, a Frankfurter. Lança com mensagem em português.</summary>
    public static async Task<Cotacoes> Buscar(HttpClient http, CancellationToken ct)
    {
        string? erro;
        try
        {
            var c = await BuscarAwesome(http, ct).ConfigureAwait(false);
            if (c.Real.Count >= 2) return c;
            erro = "A AwesomeAPI não devolveu cotação nenhuma.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { erro = e is HttpRequestException h && h.StatusCode != null ? $"A AwesomeAPI respondeu {(int)h.StatusCode.Value}." : "Não consegui buscar as cotações."; }
        Log.Escrever("calculadora: " + erro + " Tentando a Frankfurter.");
        try
        {
            var c = await BuscarFrankfurter(http, ct).ConfigureAwait(false);
            if (c.Real.Count >= 2) return c;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { Log.Escrever("calculadora: Frankfurter falhou: " + e.Message); }
        throw new Exception(erro + " Confira a internet.");
    }

    public static async Task<Cotacoes> BuscarAwesome(HttpClient http, CancellationToken ct) =>
        LerAwesome(await Json(http, UrlAwesome + Pares, ct).ConfigureAwait(false), DateTime.Now);

    public static async Task<Cotacoes> BuscarFrankfurter(HttpClient http, CancellationToken ct) =>
        LerFrankfurter(await Json(http, UrlFrankfurter + string.Join(",", NaFrankfurter), ct).ConfigureAwait(false), DateTime.Now);

    private static async Task<JsonNode?> Json(HttpClient http, string url, CancellationToken ct)
    {
        using var r = await http.GetAsync(url, ct).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        return JsonNode.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }
}
