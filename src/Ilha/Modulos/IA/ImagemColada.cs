using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;

namespace Ilha.IA;

/// <summary>
/// Print ou imagem colada no chat: reduzida até o lado maior caber em 2576 px (o nível de alta resolução do Haiku
/// 5.5 e do Sonnet 5.5), em PNG (bom para texto de tela) ou JPEG quando o PNG passa de ~3,5 MB, e em base64
/// numa data URL (o provedor converte para o bloco de imagem do Claude). Guarda também uma miniatura em BGRA
/// pré-multiplicado para a cena desenhar.
/// </summary>
internal sealed class ImagemColada
{
    public required string DataUrl { get; init; }
    public required int Largura { get; init; }
    public required int Altura { get; init; }
    public required long Bytes { get; init; }
    public required byte[] Miniatura { get; init; }
    public required int MiniL { get; init; }
    public required int MiniA { get; init; }

    public const int LimiteBytes = 3_500_000;

    /// <summary>O tamanho final: o lado maior cabe em `ladoMax`, sem ampliar.</summary>
    public static (int L, int A) Ajustar(int l, int a, int ladoMax)
    {
        if (l <= 0 || a <= 0) return (Math.Max(1, l), Math.Max(1, a));
        int maior = Math.Max(l, a);
        if (maior <= ladoMax) return (l, a);
        double f = (double)ladoMax / maior;
        return (Math.Max(1, (int)Math.Round(l * f, MidpointRounding.AwayFromZero)), Math.Max(1, (int)Math.Round(a * f, MidpointRounding.AwayFromZero)));
    }

    private static Bitmap Reduzir(Image img, int l, int a, PixelFormat formato)
    {
        var b = new Bitmap(l, a, formato);
        using var g = Graphics.FromImage(b);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingMode = CompositingMode.SourceCopy;
        using var atr = new ImageAttributes();
        atr.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(img, new Rectangle(0, 0, l, a), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, atr);
        return b;
    }

    private static byte[] Jpeg(Bitmap b, long qualidade)
    {
        var cod = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, qualidade);
        using var ms = new MemoryStream();
        // JPEG não tem alfa: fundo branco por baixo
        using var opaca = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(opaca)) { g.Clear(Color.White); g.DrawImage(b, 0, 0, b.Width, b.Height); }
        opaca.Save(ms, cod, p);
        return ms.ToArray();
    }

    /// <summary>Prepara a imagem (pode rodar fora da thread da interface: a imagem é só deste chamador).</summary>
    public static ImagemColada Preparar(Image original, int ladoMax)
    {
        var (l, a) = Ajustar(original.Width, original.Height, ladoMax);
        using var reduzida = Reduzir(original, l, a, PixelFormat.Format32bppArgb);
        byte[] dados;
        string tipo;
        using (var ms = new MemoryStream())
        {
            reduzida.Save(ms, ImageFormat.Png);
            dados = ms.ToArray();
            tipo = "image/png";
        }
        if (dados.Length > LimiteBytes)
        {
            dados = Jpeg(reduzida, 90);
            tipo = "image/jpeg";
            // ainda grande (foto enorme e cheia de detalhe): menor e mais comprimida
            var lado = ladoMax;
            while (dados.Length > LimiteBytes && lado > 800)
            {
                lado = (int)(lado * 0.8);
                var (l2, a2) = Ajustar(original.Width, original.Height, lado);
                using var menor = Reduzir(original, l2, a2, PixelFormat.Format32bppArgb);
                dados = Jpeg(menor, 82);
                (l, a) = (l2, a2);
            }
        }
        var (ml, ma) = Ajustar(l, a, 128);
        byte[] mini;
        using (var m = Reduzir(reduzida, ml, ma, PixelFormat.Format32bppPArgb))
        {
            var bd = m.LockBits(new Rectangle(0, 0, ml, ma), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            mini = new byte[ml * ma * 4];
            for (int y = 0; y < ma; y++) System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, mini, y * ml * 4, ml * 4);
            m.UnlockBits(bd);
        }
        return new ImagemColada
        {
            DataUrl = $"data:{tipo};base64,{Convert.ToBase64String(dados)}",
            Largura = l, Altura = a, Bytes = dados.Length, Miniatura = mini, MiniL = ml, MiniA = ma,
        };
    }

    /// <summary>Desenha a miniatura dentro da caixa (DIP), mantendo a proporção.</summary>
    public unsafe void Desenhar(ID2D1DeviceContext dc, float x, float y, float caixaL, float caixaA)
    {
        float f = MathF.Min(caixaL / MiniL, caixaA / MiniA);
        float l = MiniL * f, a = MiniA * f;
        var props = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96f, 96f, BitmapOptions.None);
        fixed (byte* p = Miniatura)
        {
            using var bmp = dc.CreateBitmap(new Vortice.Mathematics.SizeI(MiniL, MiniA), (IntPtr)p, (uint)(MiniL * 4), props);
            dc.DrawBitmap(bmp, 1f, BitmapInterpolationMode.Linear, new Vortice.Mathematics.Rect(x + (caixaL - l) / 2, y + (caixaA - a) / 2, l, a));
        }
    }

    public Vector2 Tamanho => new(Largura, Altura);
}
