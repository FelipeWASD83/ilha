using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ilha.Calculadora;

/// <summary>Uma unidade: fator até a base da categoria (valor_na_base = valor × fator), ou funções próprias (temperatura).</summary>
internal sealed class Unidade
{
    public required string Id { get; init; }
    public required string Nome { get; init; }
    public required string Sigla { get; init; }
    public double Fator { get; init; } = double.NaN;
    public Func<double, double>? ParaBase { get; init; }
    public Func<double, double>? DaBase { get; init; }
    /// <summary>Como a pessoa escreve ("km", "quilômetros"...). A sigla já conta.</summary>
    public string[] Apelidos { get; init; } = Array.Empty<string>();
    public Categoria Categoria { get; internal set; } = null!;
}

internal sealed class Categoria
{
    public required string Id { get; init; }
    public required string Nome { get; init; }
    public required Unidade[] Unidades { get; init; }
}

/// <summary>
/// Conversão de unidades, porta do unidades.js da nova aba. Dados têm o prefixo decimal (KB = 1000 B) e o binário
/// (KiB = 1024 B) lado a lado, porque o Windows mostra um e o fabricante do SSD vende o outro; "Rede" fica
/// separada de "Dados" para o Mb/s do teste de velocidade virar MB/s.
/// </summary>
internal static class Unidades
{
    private static Unidade U(string id, string nome, string sigla, double fator, params string[] apelidos) =>
        new() { Id = id, Nome = nome, Sigla = sigla, Fator = fator, Apelidos = apelidos };

    public static readonly Categoria[] Categorias = Montar(new[]
    {
        new Categoria
        {
            Id = "comprimento", Nome = "Comprimento", Unidades = new[]
            {
                U("mm", "milímetro", "mm", 0.001, "milímetro", "milímetros"),
                U("cm", "centímetro", "cm", 0.01, "centímetro", "centímetros"),
                U("m", "metro", "m", 1, "metro", "metros"),
                U("km", "quilômetro", "km", 1000, "quilômetro", "quilômetros", "kilometro", "kilometros"),
                U("pol", "polegada", "in", 0.0254, "pol", "polegada", "polegadas", "\"", "inch", "inches"),
                U("pe", "pé", "ft", 0.3048, "pé", "pés", "feet", "foot"),
                U("jarda", "jarda", "yd", 0.9144, "jarda", "jardas", "yard", "yards"),
                U("milha", "milha", "mi", 1609.344, "milha", "milhas", "mile", "miles"),
            },
        },
        new Categoria
        {
            Id = "massa", Nome = "Massa", Unidades = new[]
            {
                U("mg", "miligrama", "mg", 0.000001, "miligrama", "miligramas"),
                U("g", "grama", "g", 0.001, "grama", "gramas"),
                U("kg", "quilograma", "kg", 1, "quilo", "quilos", "quilograma", "quilogramas", "kilo", "kilos"),
                U("t", "tonelada", "t", 1000, "ton", "tonelada", "toneladas"),
                U("oz", "onça", "oz", 0.028349523125, "onça", "onças", "ounce", "ounces"),
                U("lb", "libra", "lb", 0.45359237, "lbs", "libra", "libras", "pound", "pounds"),
            },
        },
        new Categoria
        {
            Id = "temperatura", Nome = "Temperatura", Unidades = new[]
            {
                new Unidade { Id = "c", Nome = "Celsius", Sigla = "°C", ParaBase = v => v, DaBase = v => v, Apelidos = new[] { "c", "ºc", "celsius", "graus celsius" } },
                new Unidade { Id = "f", Nome = "Fahrenheit", Sigla = "°F", ParaBase = v => (v - 32) * 5 / 9, DaBase = c => c * 9 / 5 + 32, Apelidos = new[] { "f", "ºf", "fahrenheit" } },
                new Unidade { Id = "k", Nome = "Kelvin", Sigla = "K", ParaBase = v => v - 273.15, DaBase = c => c + 273.15, Apelidos = new[] { "k", "kelvin" } },
            },
        },
        new Categoria
        {
            Id = "volume", Nome = "Volume", Unidades = new[]
            {
                U("ml", "mililitro", "mL", 0.001, "ml", "mililitro", "mililitros"),
                U("l", "litro", "L", 1, "l", "litro", "litros"),
                U("m3", "metro cúbico", "m³", 1000, "m3", "metro cúbico", "metros cúbicos"),
                U("floz", "onça fluida (EUA)", "fl oz", 0.0295735295625, "floz", "onça fluida", "onças fluidas"),
                U("xicara", "xícara (EUA)", "cup", 0.2365882365, "xícara", "xícaras", "cups"),
                U("galao", "galão (EUA)", "gal", 3.785411784, "galão", "galões", "gallon", "gallons"),
            },
        },
        new Categoria
        {
            Id = "area", Nome = "Área", Unidades = new[]
            {
                U("cm2", "centímetro quadrado", "cm²", 0.0001, "cm2"),
                U("m2", "metro quadrado", "m²", 1, "m2", "metro quadrado", "metros quadrados"),
                U("ha", "hectare", "ha", 10000, "hectare", "hectares"),
                U("km2", "quilômetro quadrado", "km²", 1000000, "km2"),
                U("pe2", "pé quadrado", "ft²", 0.09290304, "ft2", "pe2", "sqft"),
                U("acre", "acre", "ac", 4046.8564224, "acre", "acres"),
            },
        },
        new Categoria
        {
            Id = "velocidade", Nome = "Velocidade", Unidades = new[]
            {
                U("ms", "metro por segundo", "m/s", 1, "mps"),
                U("kmh", "quilômetro por hora", "km/h", 1 / 3.6, "kmh", "kph"),
                U("mph", "milha por hora", "mph", 0.44704, "mi/h"),
                U("no", "nó", "kn", 1852.0 / 3600, "nó", "nós", "knot", "knots"),
            },
        },
        new Categoria
        {
            Id = "dados", Nome = "Dados", Unidades = new[]
            {
                U("bit", "bit", "bit", 1.0 / 8, "bits"),
                U("B", "byte", "B", 1, "byte", "bytes"),
                U("KB", "quilobyte (1000)", "KB", 1e3, "kilobyte", "quilobyte", "kilobytes", "quilobytes"),
                U("MB", "megabyte (1000²)", "MB", 1e6, "megabyte", "megabytes"),
                U("GB", "gigabyte (1000³)", "GB", 1e9, "gigabyte", "gigabytes", "giga", "gigas"),
                U("TB", "terabyte (1000⁴)", "TB", 1e12, "terabyte", "terabytes", "tera", "teras"),
                U("KiB", "kibibyte (1024)", "KiB", 1024),
                U("MiB", "mebibyte (1024²)", "MiB", 1024.0 * 1024),
                U("GiB", "gibibyte (1024³)", "GiB", 1024.0 * 1024 * 1024),
                U("TiB", "tebibyte (1024⁴)", "TiB", 1024.0 * 1024 * 1024 * 1024),
            },
        },
        new Categoria
        {
            // os de bit vêm antes: "mb/s" em minúsculas costuma ser a velocidade da internet, que é em bits
            Id = "rede", Nome = "Velocidade de rede", Unidades = new[]
            {
                U("kbps", "quilobit por segundo", "Kb/s", 1e3 / 8, "kbps", "kbit/s"),
                U("mbps", "megabit por segundo", "Mb/s", 1e6 / 8, "mbps", "mbit/s", "mega", "megas"),
                U("gbps", "gigabit por segundo", "Gb/s", 1e9 / 8, "gbps", "gbit/s"),
                U("KBs", "quilobyte por segundo", "KB/s", 1e3, "kB/s"),
                U("MBs", "megabyte por segundo", "MB/s", 1e6),
                U("GBs", "gigabyte por segundo", "GB/s", 1e9),
            },
        },
        new Categoria
        {
            Id = "tempo", Nome = "Tempo", Unidades = new[]
            {
                U("ms", "milissegundo", "ms", 0.001, "milissegundo", "milissegundos"),
                U("s", "segundo", "s", 1, "seg", "segundo", "segundos"),
                U("min", "minuto", "min", 60, "minuto", "minutos"),
                U("h", "hora", "h", 3600, "hr", "hora", "horas"),
                U("dia", "dia", "d", 86400, "dia", "dias"),
                U("sem", "semana", "sem", 604800, "semana", "semanas"),
            },
        },
    });

    private static Categoria[] Montar(Categoria[] cats)
    {
        foreach (var c in cats)
            foreach (var u in c.Unidades) u.Categoria = c;
        return cats;
    }

    public static Categoria? Categoria(string id) => Categorias.FirstOrDefault(c => c.Id == id);

    private static Unidade Achar(Categoria cat, string id) =>
        cat.Unidades.FirstOrDefault(u => u.Id == id) ?? throw new ArgumentException("Unidade desconhecida: " + id);

    public static double Converter(double valor, string categoriaId, string deId, string paraId)
    {
        var cat = Categoria(categoriaId) ?? throw new ArgumentException("Categoria desconhecida: " + categoriaId);
        return Converter(valor, Achar(cat, deId), Achar(cat, paraId));
    }

    public static double Converter(double valor, Unidade de, Unidade para)
    {
        if (de.Categoria != para.Categoria) throw new ArgumentException($"{de.Sigla} e {para.Sigla} não são da mesma grandeza");
        if (!double.IsFinite(valor)) return double.NaN;
        if (!double.IsNaN(de.Fator) && !double.IsNaN(para.Fator)) return valor * de.Fator / para.Fator;
        return para.DaBase!(de.ParaBase!(valor));
    }

    /// <summary>Número para a tela: 6 algarismos, sem zero de sobra, milhar só a partir de 10 mil.</summary>
    public static string Formatar(double n, int casas = 6)
    {
        if (!double.IsFinite(n)) return "";
        if (n == 0) return "0";
        double abs = Math.Abs(n);
        if (abs >= 1e15 || abs < 1e-9)
        {
            // toExponential(4) do JS: quatro casas fixas e o sinal do expoente ("1,2346e+21")
            string s = n.ToString("E4", CultureInfo.InvariantCulture);
            int iE = s.IndexOf('E');
            int exp = int.Parse(s[(iE + 1)..], CultureInfo.InvariantCulture);
            return s[..iE].Replace('.', ',') + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString(CultureInfo.InvariantCulture);
        }
        double arredondado = double.Parse(n.ToString("G" + casas, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
        return arredondado.ToString(abs >= 10000 ? "#,##0.############" : "0.############", Calculo.Br);
    }

    /// <summary>Lê o que a pessoa digitou: vírgula decimal e ponto de milhar ("1.234,5"). NaN se não for número.</summary>
    public static double LerNumero(string? texto)
    {
        string t = new string((texto ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (t.Length == 0) return double.NaN;
        if (t.Contains(',')) t = t.Replace(".", "").Replace(',', '.');
        else if (t.Count(c => c == '.') > 1) t = t.Replace(".", "");
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }
}
