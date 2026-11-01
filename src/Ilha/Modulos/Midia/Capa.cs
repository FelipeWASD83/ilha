using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

// não é Ilha.Modulos.Midia: o núcleo tem a classe estática Ilha.Modulos e o namespace colidiria com ela
namespace Ilha.Midia;

/// <summary>
/// A capa decodificada (quadrada, BGRA pré-multiplicado). Cada tamanho em pixels que a ilha desenha é reduzido uma
/// vez pelo GDI+ (bicúbica) e guardado, depois desenhado 1:1 com os cantos arredondados.
/// </summary>
internal sealed class Capa
{
    public byte[] Pixels { get; }
    public int Lado { get; }
    private readonly Dictionary<int, byte[]> _tamanhos = new();

    public Capa(byte[] pixels, int lado)
    {
        if (pixels.Length < lado * lado * 4) throw new ArgumentException("capa menor que o lado");
        Pixels = pixels;
        Lado = lado;
    }

    /// <summary>Lê a Thumbnail do SMTC: corta o centro quadrado e reduz para no máximo `lado` px.</summary>
    public static async Task<Capa?> Decodificar(IRandomAccessStreamReference referencia, int lado = 256)
    {
        using var fluxo = await referencia.OpenReadAsync();
        var dec = await BitmapDecoder.CreateAsync(fluxo);
        uint w = dec.PixelWidth, h = dec.PixelHeight;
        if (w == 0 || h == 0) return null;
        uint menor = Math.Min(w, h);
        uint q = (uint)Math.Min(lado, (int)menor);
        double esc = q / (double)menor;
        uint sw = Math.Max(q, (uint)Math.Round(w * esc)), sh = Math.Max(q, (uint)Math.Round(h * esc));
        var tr = new BitmapTransform
        {
            ScaledWidth = sw,
            ScaledHeight = sh,
            InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds { X = (sw - q) / 2, Y = (sh - q) / 2, Width = q, Height = q },
        };
        var dados = await dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, tr,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var px = dados.DetachPixelData();
        return px.Length >= q * q * 4 ? new Capa(px, (int)q) : null;
    }

    public byte[] NoTamanho(int lado)
    {
        if (lado == Lado) return Pixels;
        if (_tamanhos.TryGetValue(lado, out var p)) return p;
        var alca = GCHandle.Alloc(Pixels, GCHandleType.Pinned);
        try
        {
            using var original = new System.Drawing.Bitmap(Lado, Lado, Lado * 4, System.Drawing.Imaging.PixelFormat.Format32bppPArgb, alca.AddrOfPinnedObject());
            using var reduzida = new System.Drawing.Bitmap(lado, lado, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = System.Drawing.Graphics.FromImage(reduzida))
            using (var atrib = new System.Drawing.Imaging.ImageAttributes())
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                // sem isto a bicúbica puxa transparente de fora e escurece a borda
                atrib.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                g.DrawImage(original, new System.Drawing.Rectangle(0, 0, lado, lado), 0, 0, Lado, Lado, System.Drawing.GraphicsUnit.Pixel, atrib);
            }
            var bd = reduzida.LockBits(new System.Drawing.Rectangle(0, 0, lado, lado), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            p = new byte[lado * lado * 4];
            for (int y = 0; y < lado; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, p, y * lado * 4, lado * 4);
            reduzida.UnlockBits(bd);
        }
        finally { alca.Free(); }
        _tamanhos[lado] = p;
        return p;
    }

    /// <summary>Desenha a capa (ou o quadrado com a nota, sem capa) com lado e raio em DIP.</summary>
    public static unsafe void Desenhar(ID2D1DeviceContext dc, Palco p, Capa? capa, float x, float y, float lado, float raio)
    {
        if (capa == null)
        {
            Icone.Retangulo(dc, x, y, lado, lado, raio, new Color4(1f, 1f, 1f, 0.14f));
            // nota musical
            var tinta = new Color4(1f, 1f, 1f, 0.6f);
            float u = lado / 24f;
            Icone.Circulo(dc, new Vector2(x + 9.5f * u, y + 16.5f * u), 2.6f * u, tinta);
            Icone.Retangulo(dc, x + 11.2f * u, y + 6.5f * u, 1.5f * u, 10.5f * u, 0.5f * u, tinta);
            Icone.Retangulo(dc, x + 11.2f * u, y + 6.5f * u, 5.5f * u, 1.8f * u, 0.6f * u, tinta);
            return;
        }
        float e = p.Escala;
        int px = Math.Max(1, (int)MathF.Round(lado * e));
        var dados = capa.NoTamanho(px);
        // posição em pixel inteiro e lado exato: a cópia sai 1:1, sem borrar
        float x0 = MathF.Round(x * e) / e, y0 = MathF.Round(y * e) / e, l = px / e;
        var props = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96f * e, 96f * e, BitmapOptions.None);
        fixed (byte* d = dados)
        {
            using var bmp = dc.CreateBitmap(new SizeI(px, px), (IntPtr)d, (uint)(px * 4), props);
            using var pincel = dc.CreateBitmapBrush(bmp, new BitmapBrushProperties1(ExtendMode.Clamp, ExtendMode.Clamp, InterpolationMode.NearestNeighbor), null);
            pincel.Transform = Matrix3x2.CreateTranslation(x0, y0);
            var rr = new RoundedRectangle(new System.Drawing.RectangleF(x0, y0, l, l), raio, raio);
            dc.FillRoundedRectangle(ref rr, pincel);
        }
    }
}

/// <summary>A cor do acento puxada da capa: a cor saturada de maior peso, clareada para ler bem no preto.</summary>
internal static class CorDaCapa
{
    private const int GRADE = 32;
    private const int FAIXAS = 24;

    /// <summary>De BGRA pré-multiplicado. null = capa sem cor (cinza, preto e branco): fica a cor padrão.</summary>
    public static Color4? Escolher(ReadOnlySpan<byte> bgra, int w, int h)
    {
        if (w <= 0 || h <= 0 || bgra.Length < w * h * 4) return null;
        int gw = Math.Min(w, GRADE), gh = Math.Min(h, GRADE);
        Span<double> peso = stackalloc double[FAIXAS], sr = stackalloc double[FAIXAS], sg = stackalloc double[FAIXAS], sb = stackalloc double[FAIXAS];
        Span<int> contagem = stackalloc int[FAIXAS];
        int celulas = 0;
        for (int cy = 0; cy < gh; cy++)
        for (int cx = 0; cx < gw; cx++)
        {
            // média do bloco (reduzir para ~32x32)
            int x0 = cx * w / gw, x1 = Math.Max(x0 + 1, (cx + 1) * w / gw);
            int y0 = cy * h / gh, y1 = Math.Max(y0 + 1, (cy + 1) * h / gh);
            double r = 0, g = 0, b = 0, a = 0;
            int n = 0;
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * 4;
                b += bgra[i];
                g += bgra[i + 1];
                r += bgra[i + 2];
                a += bgra[i + 3];
                n++;
            }
            celulas++;
            if (a / n < 128) continue;
            // tira o pré-multiplicado
            r /= a; g /= a; b /= a;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double croma = max - min, luz = (max + min) / 2;
            // quase preto, quase branco ou cinza não contam
            if (luz < 0.1 || luz > 0.93 || croma < 0.16) continue;
            int faixa = (int)(Matiz(r, g, b, max, croma) / (360.0 / FAIXAS)) % FAIXAS;
            peso[faixa] += croma;
            sr[faixa] += r * croma;
            sg[faixa] += g * croma;
            sb[faixa] += b * croma;
            contagem[faixa]++;
        }
        // agrupa cada faixa de matiz com as vizinhas e fica com o grupo de maior peso
        int melhor = -1;
        double melhorPeso = 0;
        for (int f = 0; f < FAIXAS; f++)
        {
            double pf = peso[(f + FAIXAS - 1) % FAIXAS] + peso[f] + peso[(f + 1) % FAIXAS];
            if (pf > melhorPeso) { melhorPeso = pf; melhor = f; }
        }
        if (melhor < 0) return null;
        int cont = 0;
        double pr = 0, pg = 0, pb = 0;
        foreach (int f in new[] { (melhor + FAIXAS - 1) % FAIXAS, melhor, (melhor + 1) % FAIXAS })
        {
            cont += contagem[f];
            pr += sr[f];
            pg += sg[f];
            pb += sb[f];
        }
        // menos de 3% da capa com cor é ruído (um logo pequeno numa capa cinza): fica a padrão
        if (cont < Math.Max(1, celulas * 0.03)) return null;
        return Ajustar(pr / melhorPeso, pg / melhorPeso, pb / melhorPeso);
    }

    private static double Matiz(double r, double g, double b, double max, double croma)
    {
        double hh;
        if (max == r) hh = (g - b) / croma % 6;
        else if (max == g) hh = (b - r) / croma + 2;
        else hh = (r - g) / croma + 4;
        hh *= 60;
        return hh < 0 ? hh + 360 : hh;
    }

    /// <summary>Saturação mínima e luz suficiente para contrastar com o preto da pílula (luminância relativa ≥ 0,2, contraste ≥ 5:1).</summary>
    public static Color4 Ajustar(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        double s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        double h = d == 0 ? 0 : Matiz(r, g, b, max, d);
        s = Math.Clamp(Math.Max(s, 0.5), 0, 1);
        l = Math.Clamp(l, 0.5, 0.78);
        var (rr, gg, bb) = DeHsl(h, s, l);
        while (Luminancia(rr, gg, bb) < 0.2 && l < 0.86)
        {
            l += 0.02;
            (rr, gg, bb) = DeHsl(h, s, l);
        }
        return new Color4((float)rr, (float)gg, (float)bb, 1f);
    }

    public static (double, double, double) DeHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double hp = h / 60 % 6;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        var (r, g, b) = hp switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        double m = l - c / 2;
        return (r + m, g + m, b + m);
    }

    /// <summary>Luminância relativa (WCAG) de uma cor sRGB de 0 a 1.</summary>
    public static double Luminancia(double r, double g, double b)
    {
        static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
    }
}
