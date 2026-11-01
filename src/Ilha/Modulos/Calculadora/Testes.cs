using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Ilha.Calculadora;

/// <summary>
/// `Ilha.exe --testar calculadora`: o motor (a tabela da nova aba e mais), as unidades, o câmbio com cotação fixa,
/// o interpretador do campo e o histórico. Sem rede, sem janela e sem os Serviços.
/// </summary>
internal static class TestesCalculadora
{
    private sealed class Relatorio
    {
        private readonly StringBuilder _sb = new();
        public int Ok, Falhas;

        public void Anotar(string nome, bool ok, string? obs = null)
        {
            if (ok) Ok++; else Falhas++;
            _sb.Append(ok ? "ok      " : "FALHOU  ").Append(nome);
            if (!ok && obs != null) _sb.Append("  (").Append(obs).Append(')');
            _sb.Append('\n');
        }

        public override string ToString() => _sb.ToString().TrimEnd('\n');
    }

    private static bool Perto(double a, double b, double tol = 1e-9) => double.IsFinite(a) && Math.Abs(a - b) <= tol * Math.Max(1, Math.Abs(b));

    private static string? Falha(string t, bool graus = false)
    {
        try { Calculo.Avaliar(t, graus); return null; }
        catch (ErroDeCalculo e) { return e.Message; }
        catch (Exception e) { return "ERRO DE OUTRO TIPO: " + e.GetType().Name + ": " + e.Message; }
    }

    private static string Mostrar(double v) => double.IsFinite(v) ? v.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : v.ToString();

    public static string Rodar()
    {
        var r = new Relatorio();
        Motor(r);
        Erros(r);
        Numeros(r);
        TesteUnidades(r);
        TesteCambio(r, out var cot);
        Campo(r, cot);
        Historico(r);
        // só com ILHA_TESTE_REDE=1: as duas APIs de verdade (só leitura, sem chave); o autoteste normal não usa rede
        if (Environment.GetEnvironmentVariable("ILHA_TESTE_REDE") == "1") Rede(r);
        return r + $"\nok      {r.Ok} conferidos, {r.Falhas} falhas";
    }

    private static void Rede(Relatorio r)
    {
        using var http = Cambio.NovoHttp();
        foreach (var (nome, busca) in new (string, Func<Cotacoes>)[]
        {
            ("AwesomeAPI", () => Task.Run(() => Cambio.BuscarAwesome(http, default)).GetAwaiter().GetResult()),
            ("Frankfurter", () => Task.Run(() => Cambio.BuscarFrankfurter(http, default)).GetAwaiter().GetResult()),
        })
        {
            try
            {
                var c = busca();
                bool ok = c.Real.TryGetValue("USD", out var usd) && usd > 1 && usd < 50 && c.Real.TryGetValue("EUR", out var eur) && eur > 1 && eur < 50;
                r.Anotar($"rede: {nome} respondeu ({c.Real.Count - 1} moedas, 1 USD = R$ {Cambio.FormatarMoeda(usd, "BRL")}, de {c.Quando:dd/MM HH:mm})", ok);
            }
            catch (Exception e) { r.Anotar($"rede: {nome}", false, e.Message); }
        }
    }

    // ------------------------------------------------------------------ o motor

    private static readonly (string conta, bool graus, double ans, double esperado)[] Tabela =
    {
        // os mesmos casos do testar-ferramentas.js da nova aba
        ("2+3×4", false, 0, 14), ("(2+3)×4", false, 0, 20), ("1,5+1.5", false, 0, 3),
        ("6×7", false, 0, 42), ("6*7", false, 0, 42), ("8÷2", false, 0, 4), ("8/2", false, 0, 4), ("9−4", false, 0, 5),
        ("-2^2", false, 0, -4), ("2^-3", false, 0, 0.125), ("2^3^2", false, 0, 512), ("2**10", false, 0, 1024),
        ("200+10%", false, 0, 220), ("200-10%", false, 0, 180), ("200×10%", false, 0, 20), ("50%", false, 0, 0.5),
        ("2π", false, 0, 2 * Math.PI), ("3(4+1)", false, 0, 15), ("2sin(30)", true, 0, 1),
        ("sin(180)", true, 0, 0), ("cos(90)", true, 0, 0), ("cos(60)", true, 0, 0.5),
        ("sin(π/2)", false, 0, 1), ("acos(0.5)", true, 0, 60), ("atan(1)", true, 0, 45),
        ("log(1000)", false, 0, 3), ("log2(8)", false, 0, 3), ("ln(e)", false, 0, 1), ("√16", false, 0, 4), ("∛(27)", false, 0, 3), ("sqrt(81)", false, 0, 9),
        ("5!", false, 0, 120), ("0!", false, 0, 1), ("ans×2", false, 21, 42),
        // e mais
        ("sen(30)", true, 0, 0.5), ("sen 30", true, 0, 0.5), ("sin30", true, 0, 0.5), ("tg(45)", true, 0, 1), ("raiz(2)^2", false, 0, 2),
        ("arcsin(1)", true, 0, 90), ("asen(0.5)", true, 0, 30), ("arctan(1)", true, 0, 45),
        ("2 + 3 * 4 - 1", false, 0, 13), ("10/4", false, 0, 2.5), ("(1+2)(3+4)", false, 0, 21), ("(2)3", false, 0, 6),
        ("-(-3)", false, 0, 3), ("+5", false, 0, 5), ("3 × −2", false, 0, -6), ("1 − −1", false, 0, 2), ("7:2", false, 0, 3.5), ("3·4", false, 0, 12),
        ("abs(-7)", false, 0, 7), ("exp(0)", false, 0, 1), ("cbrt(-8)", false, 0, -2), ("sinh(0)", false, 0, 0), ("cosh(0)", false, 0, 1), ("tanh(0)", false, 0, 0),
        ("2e3", false, 0, 2000), ("1e-3", false, 0, 0.001), ("1E+2", false, 0, 100), (".5+,5", false, 0, 1), ("2e", false, 0, 2 * Math.E),
        ("pi", false, 0, Math.PI), ("PI", false, 0, Math.PI), ("e", false, 0, Math.E), ("e^2", false, 0, Math.E * Math.E),
        ("3!", false, 0, 6), ("(2+3)!", false, 0, 120), ("4!/2", false, 0, 12), ("3!!", false, 0, 720),
        ("2^0.5", false, 0, Math.Sqrt(2)), ("100 / (2 + 3)", false, 0, 20), ("10%", false, 0, 0.1), ("50 + 50%", false, 0, 75),
        ("√9+1", false, 0, 4), ("√(9+16)", false, 0, 5), ("log2 16", false, 0, 4), ("log 100", false, 0, 2), ("ln 1", false, 0, 0),
        ("log 25", false, 0, Math.Log10(25)), ("log 2", false, 0, Math.Log10(2)), ("log 20", false, 0, Math.Log10(20)), ("sqrt 25", false, 0, 5),
        ("2(3)", false, 0, 6), ("2sin(π/6)", false, 0, 1), ("cos(π)", false, 0, -1), ("tan(45)", true, 0, 1),
        ("ans", false, 7.5, 7.5), ("ans^2+1", false, 3, 10), ("ANS + 1", false, 1, 2),
        ("0,1+0,2", false, 0, 0.3), ("1/3*3", false, 0, 1), ("((((1))))", false, 0, 1), ("-0", false, 0, 0),
        // milhar à brasileira (o calculo.js dava 1,234 × 0,5 sem avisar)
        ("1.234,5 + 0,5", false, 0, 1235), ("1.234.567", false, 0, 1234567), ("1.5*2", false, 0, 3), ("12.345,67", false, 0, 12345.67),
    };

    private static void Motor(Relatorio r)
    {
        int certos = 0;
        var errados = new List<string>();
        foreach (var (conta, graus, ans, esperado) in Tabela)
        {
            double v;
            try { v = Calculo.Avaliar(conta, graus, ans); }
            catch (Exception e) { errados.Add($"{conta} deu erro: {e.Message}"); continue; }
            if (Perto(v, esperado, 1e-12)) certos++;
            else errados.Add($"{conta} = {Mostrar(v)}, esperava {Mostrar(esperado)}");
        }
        r.Anotar($"motor: tabela de {Tabela.Length} contas", errados.Count == 0, string.Join("; ", errados));
        r.Anotar("motor: sin 180° e cos 90° saem 0 exato (sem resíduo)", Calculo.Avaliar("sin(180)", true) == 0 && Calculo.Avaliar("cos(90)", true) == 0);
        r.Anotar("motor: log(1000) é 3 exato e log2(8) é 3 exato", Calculo.Avaliar("log(1000)") == 3 && Calculo.Avaliar("log2(8)") == 3);
        r.Anotar("motor: 170! passa de 1e300", Calculo.Avaliar("170!") > 1e300);
        r.Anotar("motor: radianos por padrão (sin(30) não é 0,5)", !Perto(Calculo.Avaliar("sin(30)"), 0.5, 1e-6));
        r.Anotar("motor: -0 vira 0", BitConverter.DoubleToInt64Bits(Calculo.Avaliar("-0")) == 0);
    }

    private static readonly (string conta, bool graus, string trecho)[] TabelaDeErros =
    {
        ("2,5!", false, "Fatorial"), ("(-3)!", false, "Fatorial"), ("171!", false, "grande demais"),
        ("tan(90)", true, "tangente"), ("tan(270)", true, "tangente"), ("1/0", false, "zero"), ("5/(2-2)", false, "zero"),
        ("(1+2", false, "parêntese"), ("1+2)", false, "parêntese"), ("sin(1", false, "parêntese"),
        ("√(-4)", false, "negativo"), ("ln(0)", false, "logaritmo"), ("log(-1)", false, "logaritmo"), ("log2(0)", false, "logaritmo"),
        ("foo(2)", false, "Não conheço"), ("  ", false, "Escreva"), ("", false, "Escreva"), ("1+", false, "parou no meio"),
        ("2$", false, "não entendi"), ("10^400", false, "Grande demais"), ("1e308*10", false, "Grande demais"),
        ("asin(2)", false, "Arco-seno"), ("acos(-2)", false, "Arco-cosseno"), ("(-8)^0.5", false, "Potência sem resultado"),
        ("sin", false, "precisa de um valor"), ("2*/3", false, "Não esperava"), ("()", false, "Não esperava"), ("2+3=", false, "não entendi"),
    };

    private static void Erros(Relatorio r)
    {
        var errados = new List<string>();
        foreach (var (conta, graus, trecho) in TabelaDeErros)
        {
            var f = Falha(conta, graus);
            if (f == null || !f.Contains(trecho, StringComparison.OrdinalIgnoreCase)) errados.Add($"\"{conta}\" → {f ?? "sem erro"}");
        }
        r.Anotar($"motor: {TabelaDeErros.Length} erros em português (divisão por zero, parêntese, raiz negativa...)", errados.Count == 0, string.Join("; ", errados));
        var ataques = new[] { "alert(1)", "process.exit()", "constructor", "`x`", "1;2", "a=1", "System.IO.File", "__proto__", "{}", "[1]" };
        var passaram = ataques.Where(t => Falha(t) == null || Falha(t)!.StartsWith("ERRO DE OUTRO TIPO")).ToList();
        r.Anotar("motor: nada que não seja conta vira código (texto de ataque só dá erro de conta)", passaram.Count == 0, string.Join(", ", passaram));
    }

    // ------------------------------------------------------------------ números na tela

    private static void Numeros(Relatorio r)
    {
        var casos = new (double n, string esperado)[]
        {
            (1234567.891, "1.234.567,891"), (1e21, "1e21"), (1e-12, "1e-12"), (0, "0"), (-2.5, "-2,5"), (0.1 + 0.2, "0,3"),
            (1.5e-12, "1,5e-12"), (1234.5, "1.234,5"), (1e15, "1e15"), (999999999999999, "1e15"), (2.0 / 3, "0,666666666667"),
            (100, "100"), (1000, "1.000"), (-1234567, "-1.234.567"), (123456789012345, "123.456.789.012.000"), (1e-9, "0,000000001"),
            (-3.25e20, "-3,25e20"),
        };
        var errados = casos.Select(c => (c, s: Calculo.Formatar(c.n))).Where(x => x.s != x.c.esperado).Select(x => $"{Mostrar(x.c.n)} → {x.s}").ToList();
        r.Anotar("tela: vírgula decimal, milhar com ponto, 12 algarismos e notação científica nos extremos", errados.Count == 0, string.Join("; ", errados));

        var dig = new (double n, string esperado)[]
        {
            (1234.5, "1234,5"), (1e21, "1e+21"), (-0.5, "-0,5"), (1e-8, "1e-8"), (2.0 / 3, "0,666666666667"), (1234567, "1234567"), (0, "0"), (0.1 + 0.2, "0,3"),
        };
        var errDig = dig.Select(c => (c, s: Calculo.ParaDigitar(c.n))).Where(x => x.s != x.c.esperado).Select(x => $"{Mostrar(x.c.n)} → {x.s}").ToList();
        r.Anotar("tela: o que o Enter copia não tem milhar (\"1234,5\")", errDig.Count == 0, string.Join("; ", errDig));
        r.Anotar("tela: o copiado volta a ser a mesma conta", Calculo.Avaliar(Calculo.ParaDigitar(1234.5)) == 1234.5 && Calculo.Avaliar(Calculo.ParaDigitar(-0.5)) == -0.5
            && Calculo.Avaliar(Calculo.ParaDigitar(1e21)) == 1e21);
    }

    // ------------------------------------------------------------------ unidades

    private static void TesteUnidades(Relatorio r)
    {
        double C(double v, string cat, string de, string para) => Unidades.Converter(v, cat, de, para);
        r.Anotar("unidades: 1 km = 1000 m", C(1, "comprimento", "km", "m") == 1000);
        r.Anotar("unidades: 1 polegada = 2,54 cm", Perto(C(1, "comprimento", "pol", "cm"), 2.54));
        r.Anotar("unidades: 100 °C = 212 °F e 0 °C = 273,15 K", Perto(C(100, "temperatura", "c", "f"), 212) && Perto(C(0, "temperatura", "c", "k"), 273.15));
        r.Anotar("unidades: −40 °F = −40 °C (a temperatura não é proporcional)", Perto(C(-40, "temperatura", "f", "c"), -40));
        r.Anotar("unidades: 1 GiB = 1,073741824 GB", Perto(C(1, "dados", "GiB", "GB"), 1.073741824));
        r.Anotar("unidades: 1000 GB = 0,909... TiB (o SSD de 1 TB do fabricante)", Perto(C(1000, "dados", "GB", "TiB"), 1e12 / Math.Pow(1024, 4)));
        r.Anotar("unidades: 800 Mb/s = 100 MB/s (internet)", Perto(C(800, "rede", "mbps", "MBs"), 100));
        r.Anotar("unidades: 36 km/h = 10 m/s", Perto(C(36, "velocidade", "kmh", "ms"), 10));
        r.Anotar("unidades: 1 mi = 1,609344 km", Perto(C(1, "comprimento", "milha", "km"), 1.609344));
        r.Anotar("unidades: 1 lb = 453,59237 g e 1 galão = 3,785411784 L", Perto(C(1, "massa", "lb", "g"), 453.59237) && Perto(C(1, "volume", "galao", "l"), 3.785411784));
        r.Anotar("unidades: 1 semana = 10080 min e 1 acre = 4046,8564224 m²", Perto(C(1, "tempo", "sem", "min"), 10080) && Perto(C(1, "area", "acre", "m2"), 4046.8564224));
        bool idaEVolta = Unidades.Categorias.All(c => c.Unidades.All(a => c.Unidades.All(b => Perto(C(C(7.5, c.Id, a.Id, b.Id), c.Id, b.Id, a.Id), 7.5, 1e-9))));
        r.Anotar("unidades: ida e volta devolve o mesmo valor (todas as categorias)", idaEVolta);
        bool recusou1 = false, recusou2 = false;
        try { C(1, "x", "a", "b"); } catch (ArgumentException) { recusou1 = true; }
        try { C(1, "massa", "kg", "zz"); } catch (ArgumentException) { recusou2 = true; }
        r.Anotar("unidades: categoria ou unidade inventada dá erro, não NaN calado", recusou1 && recusou2);
        r.Anotar("unidades: LerNumero aceita \"1.234,5\", \"3,14\", \"1.234.567\" e recusa lixo",
            Unidades.LerNumero("1.234,5") == 1234.5 && Unidades.LerNumero("3,14") == 3.14 && Unidades.LerNumero("1.234.567") == 1234567
            && double.IsNaN(Unidades.LerNumero("abc")) && double.IsNaN(Unidades.LerNumero("")));
        var fmt = new (double n, string esperado)[] { (2.5, "2,5"), (1000000, "1.000.000"), (0, "0"), (1234.5678, "1234,57"), (12345.678, "12.345,7"), (6.2137119223733, "6,21371"), (1e21, "1,0000e+21"), (-273.15, "-273,15") };
        var errFmt = fmt.Select(c => (c, s: Unidades.Formatar(c.n))).Where(x => x.s != x.c.esperado).Select(x => $"{Mostrar(x.c.n)} → {x.s}").ToList();
        r.Anotar("unidades: formatar usa vírgula, 6 algarismos e milhar só a partir de 10 mil", errFmt.Count == 0, string.Join("; ", errFmt));
    }

    // ------------------------------------------------------------------ câmbio

    private static void TesteCambio(Relatorio r, out Cotacoes cot)
    {
        var agora = new DateTime(2026, 11, 1, 15, 0, 0);
        // a mesma resposta falsa do testar-ferramentas.js
        var resposta = JsonNode.Parse("""
            {
              "USDBRL": { "bid": "5.20", "pctChange": "-0.2", "timestamp": "1790000000" },
              "EURBRL": { "bid": "5.80", "pctChange": "0.1", "timestamp": "1790000100" },
              "BTCBRL": { "bid": "440000", "pctChange": "-0.05", "timestamp": "1790000200" },
              "JPYBRL": { "bid": "lixo" }
            }
            """);
        cot = Cambio.LerAwesome(resposta, agora);
        var c = cot;
        r.Anotar("câmbio: lê as moedas boas e pula a quebrada", c.Real.ContainsKey("USD") && c.Real.ContainsKey("EUR") && c.Real.ContainsKey("BTC") && !c.Real.ContainsKey("JPY") && c.Real["BRL"] == 1,
            string.Join(",", c.Real.Keys));
        r.Anotar("câmbio: o horário é o da cotação mais recente", c.Quando == DateTimeOffset.FromUnixTimeSeconds(1790000200).LocalDateTime && c.BuscadoEm == agora && c.Fonte == "AwesomeAPI");
        r.Anotar("câmbio: cada moeda guarda a hora da cotação dela", c.QuandoDe("USD") == DateTimeOffset.FromUnixTimeSeconds(1790000000).LocalDateTime && c.QuandoDe("BTC") == c.Quando && c.QuandoDe(null) == c.Quando);
        r.Anotar("câmbio: 100 USD → BRL pelo pivô", Perto(Cambio.Converter(100, "USD", "BRL", c), 520));
        r.Anotar("câmbio: 100 USD → EUR passa pelo real", Perto(Cambio.Converter(100, "USD", "EUR", c), 520 / 5.8));
        r.Anotar("câmbio: moeda sem cotação dá NaN", double.IsNaN(Cambio.Converter(1, "USD", "JPY", c)));
        r.Anotar("câmbio: resposta vazia não traz cotação nenhuma (a busca recusa)", Cambio.LerAwesome(JsonNode.Parse("{}"), agora).Real.Count == 1 && Cambio.LerAwesome(null, agora).Real.Count == 1);
        r.Anotar("câmbio: a busca pede os 11 pares de uma vez", Cambio.Pares.Split(',').Length == 11 && Cambio.Pares.StartsWith("USD-BRL,EUR-BRL"));
        r.Anotar("câmbio: formatarMoeda usa 2 casas no dólar, 4 abaixo de 1 e 6 em cripto",
            Cambio.FormatarMoeda(1234.5, "USD") == "1.234,50" && Cambio.FormatarMoeda(0.0342, "BRL") == "0,0342" && Cambio.FormatarMoeda(0.00227272727, "BTC") == "0,002273"
            && Cambio.FormatarMoeda(1234.5, "USD", milhar: false) == "1234,50",
            $"{Cambio.FormatarMoeda(1234.5, "USD")} {Cambio.FormatarMoeda(0.0342, "BRL")} {Cambio.FormatarMoeda(0.00227272727, "BTC")}");
        var fr = Cambio.LerFrankfurter(JsonNode.Parse("""{"amount":1.0,"base":"BRL","date":"2026-10-30","rates":{"USD":0.2,"EUR":0.16,"XYZ":5,"GBP":"lixo"}}"""), agora);
        r.Anotar("câmbio: a Frankfurter (base BRL) vira reais por moeda e pula o que não conhece",
            Perto(fr.Real["USD"], 5) && Perto(fr.Real["EUR"], 6.25) && !fr.Real.ContainsKey("XYZ") && !fr.Real.ContainsKey("GBP") && fr.Fonte == "BCE" && fr.Quando == new DateTime(2026, 10, 30));
        var outraBase = Cambio.LerFrankfurter(JsonNode.Parse("""{"base":"EUR","date":"2026-10-30","rates":{"USD":1.1}}"""), agora);
        r.Anotar("câmbio: Frankfurter com outra base não é lida como se fosse real", outraBase.Real.Count == 1);
        r.Anotar("câmbio: a hora da cotação (hoje só a hora, outro dia com a data, BCE só o dia)",
            AtividadeCalculadora.Hora(new DateTime(2026, 11, 1, 14, 5, 0), "AwesomeAPI", agora) == "14:05"
            && AtividadeCalculadora.Hora(new DateTime(2026, 10, 31, 19, 1, 0), "AwesomeAPI", agora) == "31/10 19:01"
            && AtividadeCalculadora.Hora(new DateTime(2026, 10, 30), "BCE", agora) == "30/10");
    }

    // ------------------------------------------------------------------ o campo inteiro

    private static void Campo(Relatorio r, Cotacoes cot)
    {
        var casos = new (string texto, double ans, string principal)[]
        {
            ("10 km em mi", 0, "= 6,21371 mi"), ("32 f em c", 0, "= 0 °C"), ("100 c para f", 0, "= 212 °F"), ("0 k em c", 0, "= -273,15 °C"),
            ("1 GiB em GB", 0, "= 1,07374 GB"), ("800 mbps em MB/s", 0, "= 100 MB/s"), ("800 Mb/s em MB/s", 0, "= 100 MB/s"),
            ("2×5 kg em lb", 0, "= 22,0462 lb"), ("km em m", 0, "= 1000 m"), ("10km->mi", 0, "= 6,21371 mi"), ("10 km → mi", 0, "= 6,21371 mi"),
            ("10 in in cm", 0, "= 25,4 cm"), ("1 h em min", 0, "= 60 min"), ("500 ms em s", 0, "= 0,5 s"), ("36 km/h em m/s", 0, "= 10 m/s"),
            ("1 milha em km", 0, "= 1,60934 km"), ("5 pés em metros", 0, "= 1,524 m"), ("1 xícara em ml", 0, "= 236,588 mL"), ("1 xicara pra ml", 0, "= 236,588 mL"),
            ("10 l em galões", 0, "= 2,64172 gal"), ("1 TB em GiB", 0, "= 931,323 GiB"), ("1 ha em m²", 0, "= 10.000 m²"), ("1 ha em m2", 0, "= 10.000 m²"),
            ("1 dia em h", 0, "= 24 h"), ("2 toneladas em kg", 0, "= 2000 kg"), ("1 GB em MB", 0, "= 1000 MB"), ("1 gb em mb", 0, "= 1000 MB"), ("1 kn em km/h", 0, "= 1,852 km/h"),
            ("100 usd em brl", 0, "= R$ 520,00"), ("100 USD em EUR", 0, "= € 89,66"), ("100 dólares em reais", 0, "= R$ 520,00"), ("100 dolares para real", 0, "= R$ 520,00"),
            ("R$ 52 em usd", 0, "= US$ 10,00"), ("US$ 10 em reais", 0, "= R$ 52,00"), ("100 usd", 0, "= R$ 520,00"), ("100 euros", 0, "= R$ 580,00"),
            ("1 btc em usd", 0, "= US$ 84.615,38"), ("1000 brl em btc", 0, "= ₿ 0,002273"), ("2*50 usd em brl", 0, "= R$ 520,00"),
            ("2+2=", 0, "= 4"), ("200 + 10%", 0, "= 220"), ("1234,5 * 1", 0, "= 1.234,5"), ("ans + 1", 41, "= 42"), ("0,1 + 0,2", 0, "= 0,3"),
            ("sin(30)", 0, "= 0,5"), ("2^10", 0, "= 1.024"),
        };
        var errados = new List<string>();
        foreach (var (texto, ans, principal) in casos)
        {
            var res = Interpretador.Interpretar(texto, true, ans, cot);
            if (!res.Ok || res.Principal != principal) errados.Add($"\"{texto}\" → {(res.Ok ? res.Principal : "erro: " + res.Detalhe)}");
        }
        r.Anotar($"campo: {casos.Length} contas e conversões (unidade, moeda com cotação fixa, conta pura)", errados.Count == 0, string.Join("; ", errados));

        var c1 = Interpretador.Interpretar("100 usd em brl", true, 0, cot);
        r.Anotar("campo: o Enter copia o número sem símbolo nem milhar", c1.Copia == "520,00" && Interpretador.Interpretar("1 btc em brl", true, 0, cot).Copia == "440000,00"
            && Interpretador.Interpretar("1 ha em m2", true, 0, cot).Copia == "10000" && Interpretador.Interpretar("1234,5*1", true, 0, cot).Copia == "1234,5");
        r.Anotar("campo: a conversão de moeda diz a cotação usada", c1.Detalhe == "1 USD = R$ 5,20" && c1.MoedaDe == "USD" && c1.MoedaRef == "USD"
            && Interpretador.Interpretar("R$ 52 em eur", true, 0, cot).MoedaRef == "EUR" && Interpretador.Interpretar("10 reais", true, 0, cot).MoedaRef == null, c1.Detalhe);

        var semCot = Interpretador.Interpretar("100 usd em brl", true, 0, null);
        r.Anotar("campo: sem cotação nenhuma, pede para buscar", semCot.PrecisaCotacao && !semCot.Ok);
        r.Anotar("campo: conta pura não pede cotação", !Interpretador.Interpretar("2+2", true, 0, null).PrecisaCotacao && Interpretador.Interpretar("2+2", true, 0, null).MoedaDe == null);

        var erros = new (string texto, string trecho)[]
        {
            ("10 km em kg", "Não dá para converter"), ("10 km", "destino"), ("100 usd em jpy", "iene"), ("100 em usd", "Faltou"),
            ("2+", "parou no meio"), ("10 usd em km", "Não dá para converter"), ("abc", "Não conheço"),
        };
        var errErros = new List<string>();
        foreach (var (texto, trecho) in erros)
        {
            var res = Interpretador.Interpretar(texto, true, 0, cot);
            if (res.Ok || !res.Detalhe.Contains(trecho, StringComparison.OrdinalIgnoreCase)) errErros.Add($"\"{texto}\" → {(res.Ok ? res.Principal : res.Detalhe)}");
        }
        r.Anotar($"campo: {erros.Length} erros que explicam o que faltou", errErros.Count == 0, string.Join("; ", errErros));
        r.Anotar("campo: vazio não é erro", Interpretador.Interpretar("   ", true, 0, cot).Tipo == TipoResultado.Vazio);
        r.Anotar("campo: graus ou radianos vêm da config", Interpretador.Interpretar("sin(30)", true, 0, null).Principal == "= 0,5" && Interpretador.Interpretar("sin(30)", false, 0, null).Principal != "= 0,5");
        r.Anotar("campo: MB e Mb são coisas diferentes quando escritos certo", Interpretador.AcharAlvo("MB")!.Unidade!.Id == "MB" && Interpretador.AcharAlvo("Mb/s")!.Unidade!.Id == "mbps" && Interpretador.AcharAlvo("MB/s")!.Unidade!.Id == "MBs");
        r.Anotar("campo: \"ms\" é milissegundo e \"m/s\" é velocidade", Interpretador.AcharAlvo("ms")!.Unidade!.Categoria.Id == "tempo" && Interpretador.AcharAlvo("m/s")!.Unidade!.Categoria.Id == "velocidade");
        r.Anotar("campo: libra sozinha é massa; libra esterlina é moeda", Interpretador.AcharAlvo("libras")!.Unidade?.Id == "lb" && Interpretador.AcharAlvo("libra esterlina")!.Moeda?.Id == "GBP");
    }

    // ------------------------------------------------------------------ histórico e estado

    private static void Historico(Relatorio r)
    {
        var h = new List<ItemHistorico>();
        for (int i = 1; i <= 7; i++) EstadoCalculadora.Lembrar(h, $"{i}+{i}", (2 * i).ToString());
        r.Anotar("histórico: guarda só as 5 últimas, a mais nova em cima", h.Count == 5 && h[0].Conta == "7+7" && h[4].Conta == "3+3");
        EstadoCalculadora.Lembrar(h, "5+5", "10");
        r.Anotar("histórico: repetir uma conta sobe ela sem duplicar", h.Count == 5 && h[0].Conta == "5+5" && h.Count(x => x.Conta == "5+5") == 1);

        var e = new EstadoCalculadora { Ans = 42.5, Historico = h, Cotacoes = new Cotacoes { Real = new() { ["BRL"] = 1, ["USD"] = 5.2 }, Fonte = "AwesomeAPI", Quando = new DateTime(2026, 11, 1, 14, 0, 0), BuscadoEm = new DateTime(2026, 11, 1, 14, 1, 0), Horas = new() { ["USD"] = new DateTime(2026, 10, 30, 19, 0, 0) } } };
        var volta = EstadoCalculadora.Ler(EstadoCalculadora.Serializar(e));
        r.Anotar("estado: histórico, ans e a última cotação voltam do disco iguais",
            volta.Ans == 42.5 && volta.Historico.Count == 5 && volta.Historico[0].Conta == "5+5" && volta.Cotacoes != null && volta.Cotacoes.Real["USD"] == 5.2
            && volta.Cotacoes.Quando == e.Cotacoes.Quando && volta.Cotacoes.Fonte == "AwesomeAPI" && volta.Cotacoes.QuandoDe("USD") == new DateTime(2026, 10, 30, 19, 0, 0));
        var ruim = EstadoCalculadora.Ler("""{"historico":[{"conta":"","resultado":"x"},{"conta":"1+1","resultado":"2"}],"ans":0,"cotacoes":{"Real":{"BRL":1,"USD":-3}}}""");
        r.Anotar("estado: cotação estragada e conta vazia no arquivo são descartadas", ruim.Cotacoes == null && ruim.Historico.Count == 1);
    }
}
