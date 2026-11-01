using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ilha.ContaGotas;

/// <summary>Uma cor de tela (8 bits por canal) e os formatos que o cartão mostra e copia.</summary>
internal readonly record struct CorDaTela(byte R, byte G, byte B)
{
    public static CorDaTela DeColorRef(uint c) => new((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));

    public string Hex(bool minusculas = false)
    {
        string s = $"#{R:X2}{G:X2}{B:X2}";
        return minusculas ? s.ToLowerInvariant() : s;
    }

    public string Rgb() => $"rgb({R}, {G}, {B})";

    public string Hsl()
    {
        var (h, s, l) = ParaHsl();
        return $"hsl({h}, {s}%, {l}%)";
    }

    /// <summary>Matiz em graus (0 a 359), saturação e luminosidade em % inteiros, como o CSS escreve.</summary>
    public (int H, int S, int L) ParaHsl()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        double h = 0, s = 0;
        if (d > 0)
        {
            s = d / (1 - Math.Abs(2 * l - 1));
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }
        int hi = (int)Math.Round(h, MidpointRounding.AwayFromZero) % 360;
        return (hi, (int)Math.Round(s * 100, MidpointRounding.AwayFromZero), (int)Math.Round(l * 100, MidpointRounding.AwayFromZero));
    }

    /// <summary>Luminância relativa (sRGB, 0 a 1): decide se a marca em cima da cor é branca ou preta.</summary>
    public double Luminancia()
    {
        static double Lin(byte c)
        {
            double v = c / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(R) + 0.7152 * Lin(G) + 0.0722 * Lin(B);
    }

    public bool Clara => Luminancia() > 0.18;

    /// <summary>"#RRGGBB", "RRGGBB", "#RGB" (maiúsculas ou minúsculas). Qualquer outra coisa: false.</summary>
    public static bool TentarLer(string? texto, out CorDaTela cor)
    {
        cor = default;
        if (texto == null) return false;
        var t = texto.Trim();
        if (t.StartsWith('#')) t = t[1..];
        if (t.Length == 3) t = new string(new[] { t[0], t[0], t[1], t[1], t[2], t[2] });
        if (t.Length != 6) return false;
        if (!uint.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v)) return false;
        cor = new CorDaTela((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public Vortice.Mathematics.Color4 Color4() => new(R / 255f, G / 255f, B / 255f, 1f);
}

/// <summary>As últimas cores pegas, a mais nova primeiro (vão na seção do módulo na config).</summary>
internal static class Historico
{
    public const int MAXIMO = 24;

    public static int Limite(int pedido) => Math.Clamp(pedido, 1, MAXIMO);

    /// <summary>A cor entra no topo; se já estava, sobe (sem repetir); passou do limite, a mais velha sai.</summary>
    public static void Lembrar(List<string> lista, CorDaTela cor, int limite)
    {
        string hex = cor.Hex();
        lista.RemoveAll(x => TentarNormalizar(x) == hex);
        lista.Insert(0, hex);
        limite = Limite(limite);
        if (lista.Count > limite) lista.RemoveRange(limite, lista.Count - limite);
    }

    /// <summary>A lista lida da config: só cores válidas, em "#RRGGBB" maiúsculo, sem repetir, até o limite.</summary>
    public static List<string> Limpar(IEnumerable<string>? lista, int limite)
    {
        var r = new List<string>();
        if (lista == null) return r;
        limite = Limite(limite);
        foreach (var x in lista)
        {
            var n = TentarNormalizar(x);
            if (n == null || r.Contains(n)) continue;
            r.Add(n);
            if (r.Count >= limite) break;
        }
        return r;
    }

    private static string? TentarNormalizar(string? x) => CorDaTela.TentarLer(x, out var c) ? c.Hex() : null;
}

/// <summary>Contas da lupa, sem janela: onde ela fica e o tamanho para o DPI do monitor.</summary>
internal static class Geometria
{
    /// <summary>Pixels de tela mostrados de cada lado da lupa (ímpar: o do meio é o do cursor).</summary>
    public const int AMOSTRA = 15;
    public const float CELULA_DIP = 9, BORDA_DIP = 2, VAO_DIP = 6, ETIQUETA_DIP = 26, DESLOCAMENTO_DIP = 22;

    public readonly record struct Medidas(int Celula, int Borda, int Vao, int Etiqueta, int Largura, int Altura, int Deslocamento);

    public static Medidas Medir(uint dpi)
    {
        float e = Math.Max(1, dpi) / 96f;
        int cel = Math.Max(4, (int)MathF.Round(CELULA_DIP * e));
        int borda = Math.Max(1, (int)MathF.Round(BORDA_DIP * e));
        int vao = (int)MathF.Round(VAO_DIP * e);
        int etq = (int)MathF.Round(ETIQUETA_DIP * e);
        int lado = AMOSTRA * cel + 2 * borda;
        return new Medidas(cel, borda, vao, etq, lado, lado + vao + etq, (int)MathF.Round(DESLOCAMENTO_DIP * e));
    }

    /// <summary>
    /// Canto de cima da lupa: embaixo e à direita do cursor; perto da borda direita (ou de baixo) do monitor, vira
    /// para o outro lado; e nunca sai do monitor. Tudo em pixel físico da tela virtual (pode ser negativo).
    /// </summary>
    public static (int X, int Y) Posicionar(int cx, int cy, int largura, int altura, int deslocamento,
        int esquerda, int topo, int direita, int baixo)
    {
        int x = cx + deslocamento, y = cy + deslocamento;
        if (x + largura > direita) x = cx - deslocamento - largura;
        if (y + altura > baixo) y = cy - deslocamento - altura;
        x = Math.Clamp(x, esquerda, Math.Max(esquerda, direita - largura));
        y = Math.Clamp(y, topo, Math.Max(topo, baixo - altura));
        return (x, y);
    }
}
