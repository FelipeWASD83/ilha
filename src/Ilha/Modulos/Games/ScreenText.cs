using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Ilha.Games;

/// <summary>A word the OCR read, with its box in the image it read.</summary>
internal sealed record Word(string Text, float X, float Y, float W, float H);

/// <summary>A line the OCR read: its words left to right.</summary>
internal sealed record Line(List<Word> Words)
{
    public string Text => string.Join(" ", Words.Select(w => w.Text));
}

/// <summary>
/// Reading text off the screen: a GDI copy of a small rectangle (like OBS's display capture: nothing touches the game)
/// and Windows' own OCR (Windows.Media.Ocr, the languages installed with Windows). Images are 32-bit BGRA, top-down.
/// </summary>
internal sealed class ScreenText
{
    private readonly OcrEngine _engine;

    public ScreenText()
    {
        _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                  ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
                  ?? throw new InvalidOperationException("nenhum idioma de OCR instalado no Windows");
    }

    public string Language => _engine.RecognizerLanguage.LanguageTag;

    // ------------------------------------------------------------------ capture

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    /// <summary>A rectangle of the screen (physical pixels, virtual-screen coordinates) as BGRA bytes.</summary>
    public static byte[]? Grab(Rectangle r)
    {
        if (r.Width <= 0 || r.Height <= 0) return null;
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr mem = CreateCompatibleDC(screen);
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = r.Width, biHeight = -r.Height, biPlanes = 1, biBitCount = 32 };
        IntPtr dib = CreateDIBSection(mem, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero) return null;
            IntPtr old = SelectObject(mem, dib);
            bool ok = BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, 0x00CC0020);
            SelectObject(mem, old);
            if (!ok) return null;
            var data = new byte[r.Width * r.Height * 4];
            Marshal.Copy(bits, data, 0, data.Length);
            // GDI leaves alpha at 0, which the scaler and the OCR read as transparent
            for (int i = 3; i < data.Length; i += 4) data[i] = 255;
            return data;
        }
        finally
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // ------------------------------------------------------------------ images

    /// <summary>Loads a file as BGRA (the self test reads the recorded photos).</summary>
    public static (byte[] Data, int W, int H) Load(string file)
    {
        using var src = new Bitmap(file);
        using var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, src.Width, src.Height);
        return (ToBytes(bmp), bmp.Width, bmp.Height);
    }

    public static byte[] Crop(byte[] img, int w, Rectangle r)
    {
        var o = new byte[r.Width * r.Height * 4];
        for (int y = 0; y < r.Height; y++) System.Buffer.BlockCopy(img, ((r.Y + y) * w + r.X) * 4, o, y * r.Width * 4, r.Width * 4);
        return o;
    }

    private static byte[] ToBytes(Bitmap bmp)
    {
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var o = new byte[bmp.Width * bmp.Height * 4];
            for (int y = 0; y < bmp.Height; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, o, y * bmp.Width * 4, bmp.Width * 4);
            return o;
        }
        finally { bmp.UnlockBits(bd); }
    }

    private static Bitmap FromBytes(byte[] img, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(img, y * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
        bmp.UnlockBits(bd);
        return bmp;
    }

    /// <summary>Bigger (OCR reads small HUD digits much better at 3 to 4 times their size).</summary>
    public static byte[] Scale(byte[] img, int w, int h, int k, bool smooth = true)
    {
        using var src = FromBytes(img, w, h);
        using var dst = new Bitmap(w * k, h * k, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = smooth ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, w * k, h * k));
        }
        return ToBytes(dst);
    }

    /// <summary>Black text on white: the pixels `keep` accepts (hue in degrees, saturation and value 0 to 1) become ink.</summary>
    public static byte[] Mask(byte[] img, Func<float, float, float, bool> keep)
    {
        var o = new byte[img.Length];
        for (int i = 0; i < img.Length; i += 4)
        {
            float b = img[i] / 255f, g = img[i + 1] / 255f, r = img[i + 2] / 255f;
            float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
            float s = max <= 0 ? 0 : (max - min) / max, hue = 0;
            if (max > min)
            {
                float d = max - min;
                hue = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
                if (hue < 0) hue += 360;
            }
            byte v = keep(hue, s, max) ? (byte)0 : (byte)255;
            o[i] = o[i + 1] = o[i + 2] = v;
            o[i + 3] = 255;
        }
        return o;
    }

    /// <summary>White border around the image (OCR misses text touching the edge).</summary>
    public static (byte[] Data, int W, int H) Pad(byte[] img, int w, int h, int p)
    {
        int nw = w + 2 * p, nh = h + 2 * p;
        var o = new byte[nw * nh * 4];
        Array.Fill(o, (byte)255);
        for (int y = 0; y < h; y++) System.Buffer.BlockCopy(img, y * w * 4, o, ((y + p) * nw + p) * 4, w * 4);
        return (o, nw, nh);
    }

    // ------------------------------------------------------------------ OCR

    public List<Line> Read(byte[] img, int w, int h)
    {
        var dw = new DataWriter();
        dw.WriteBytes(img);
        using var sb = SoftwareBitmap.CreateCopyFromBuffer(dw.DetachBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
        var res = _engine.RecognizeAsync(sb).AsTask().GetAwaiter().GetResult();
        return res.Lines.Select(l => new Line(l.Words.Select(x => new Word(x.Text, (float)x.BoundingRect.X, (float)x.BoundingRect.Y,
            (float)x.BoundingRect.Width, (float)x.BoundingRect.Height)).ToList())).ToList();
    }

    /// <summary>The first number of 1 to 4 digits in what was read (OCR's O and o count as 0).</summary>
    public static int? Number(IEnumerable<Line> lines)
    {
        foreach (var l in lines)
            foreach (var w in l.Words)
            {
                string t = new(w.Text.Select(c => c is 'O' or 'o' ? '0' : c).Where(char.IsDigit).ToArray());
                if (t.Length is >= 1 and <= 4 && w.Text.Count(char.IsLetter) <= 1) return int.Parse(t);
            }
        return null;
    }
}
