using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Ilha.Recorder;

/// <summary>
/// The two still images ffmpeg uses for the camera inside the screen recording: the rounded mask (white = camera shows)
/// and the soft shadow under it (black with a blurred alpha, bigger than the camera by the blur's reach).
/// </summary>
internal static class PipFrame
{
    public static void Write(PipLayout l, string maskPath, string shadowPath)
    {
        using (var mask = new Bitmap(l.W, l.H, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(mask))
            {
                g.Clear(Color.Black);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using var path = Rounded(new RectangleF(0, 0, l.W, l.H), l.Radius);
                g.FillPath(Brushes.White, path);
            }
            mask.Save(maskPath, ImageFormat.Png);
        }

        int sw = l.W + 2 * l.Margin, sh = l.H + 2 * l.Margin;
        var alpha = new float[sw * sh];
        using (var shape = new Bitmap(sw, sh, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(shape))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Rounded(new RectangleF(l.Margin, l.Margin, l.W, l.H), l.Radius);
                g.FillPath(Brushes.White, path);
            }
            var px = Pixels(shape);
            for (int i = 0; i < alpha.Length; i++) alpha[i] = (px[i] >>> 24) / 255f;
        }
        // three box blurs of radius ~sigma make a gaussian close enough
        int r = Math.Max(1, l.Blur);
        for (int k = 0; k < 3; k++) { BoxH(alpha, sw, sh, r); BoxV(alpha, sw, sh, r); }
        const float STRENGTH = 0.62f;
        var outPx = new int[sw * sh];
        for (int i = 0; i < outPx.Length; i++)
            outPx[i] = (int)Math.Clamp(MathF.Round(alpha[i] * STRENGTH * 255), 0, 255) << 24;
        using var shadow = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
        var data = shadow.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < sh; y++) Marshal.Copy(outPx, y * sw, data.Scan0 + y * data.Stride, sw);
        }
        finally { shadow.UnlockBits(data); }
        shadow.Save(shadowPath, ImageFormat.Png);
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static int[] Pixels(Bitmap b)
    {
        var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new int[b.Width * b.Height];
            for (int y = 0; y < b.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * b.Width, b.Width);
            return px;
        }
        finally { b.UnlockBits(data); }
    }

    private static void BoxH(float[] a, int w, int h, int r)
    {
        var row = new float[w];
        for (int y = 0; y < h; y++)
        {
            int o = y * w;
            float sum = 0;
            for (int x = -r; x <= r; x++) sum += a[o + Math.Clamp(x, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                row[x] = sum / (2 * r + 1);
                sum += a[o + Math.Min(x + r + 1, w - 1)] - a[o + Math.Max(x - r, 0)];
            }
            Array.Copy(row, 0, a, o, w);
        }
    }

    private static void BoxV(float[] a, int w, int h, int r)
    {
        var col = new float[h];
        for (int x = 0; x < w; x++)
        {
            float sum = 0;
            for (int y = -r; y <= r; y++) sum += a[Math.Clamp(y, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                col[y] = sum / (2 * r + 1);
                sum += a[Math.Min(y + r + 1, h - 1) * w + x] - a[Math.Max(y - r, 0) * w + x];
            }
            for (int y = 0; y < h; y++) a[y * w + x] = col[y];
        }
    }
}
