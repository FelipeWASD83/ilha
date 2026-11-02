using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Ilha.LiveWallpaper;

/// <summary>A decoded thumbnail: BGRA premultiplied pixels, at most 640 px wide.</summary>
internal sealed class Thumb
{
    public required byte[] Pixels;
    public required int Width, Height;

    /// <summary>
    /// Draws the thumbnail covering the rectangle (cropped to its shape, like Fundo Vivo's Fill) with rounded corners.
    /// A new D2D bitmap per draw, the way the media cover does it: the cards redraw rarely.
    /// </summary>
    public unsafe void Draw(ID2D1DeviceContext dc, float x, float y, float w, float h, float radius, float opacity = 1)
    {
        var props = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.None);
        fixed (byte* d = Pixels)
        {
            using var bmp = dc.CreateBitmap(new SizeI(Width, Height), (IntPtr)d, (uint)(Width * 4), props);
            using var brush = dc.CreateBitmapBrush(bmp, new BitmapBrushProperties1(ExtendMode.Clamp, ExtendMode.Clamp, Vortice.Direct2D1.InterpolationMode.HighQualityCubic), null);
            float s = MathF.Max(w / Width, h / Height);
            brush.Transform = Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(x + (w - Width * s) / 2, y + (h - Height * s) / 2);
            brush.Opacity = opacity;
            var rr = new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), radius, radius);
            dc.FillRoundedRectangle(ref rr, brush);
        }
    }
}

/// <summary>
/// Loads wallpaper thumbnails on one background thread: the Wallpaper Engine preview (first frame of a GIF), the image
/// itself, or the Windows thumbnail of a video (what Fundo Vivo's library shows). Each is decoded once and kept.
/// </summary>
internal sealed class ThumbCache : IDisposable
{
    private const int MAX_WIDTH = 640;
    private static readonly string[] Images = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };

    private readonly ConcurrentDictionary<string, Thumb?> _done = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _asked = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<WallpaperItem> _queue = new();
    private readonly Action _loaded;
    private Thread? _worker;

    /// <summary>`loaded` runs on a worker thread whenever a thumbnail is ready (the caller goes back to the UI thread).</summary>
    public ThumbCache(Action loaded) => _loaded = loaded;

    private static string Key(WallpaperItem i) => i.Preview ?? i.File;

    /// <summary>The thumbnail if it is ready; otherwise it is asked for (once) and null comes back.</summary>
    public Thumb? Get(WallpaperItem item)
    {
        string key = Key(item);
        if (_done.TryGetValue(key, out var t)) return t;
        lock (_asked)
        {
            if (!_asked.Add(key)) return null;
        }
        if (_worker == null)
        {
            _worker = new Thread(Work) { IsBackground = true, Name = "ilha-fundo-miniaturas", Priority = ThreadPriority.BelowNormal };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }
        _queue.Add(item);
        return null;
    }

    private void Work()
    {
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                Thumb? t = null;
                try { t = Load(item); }
                catch (Exception e) { Log.Escrever($"fundo: miniatura de {item.Title}: {e.Message}"); }
                _done[Key(item)] = t;
                _loaded();
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    internal static Thumb? Load(WallpaperItem item)
    {
        string path = item.Preview ?? item.File;
        if (Images.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            try
            {
                using var ms = new MemoryStream(File.ReadAllBytes(path));
                using var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
                return FromImage(img);
            }
            catch when (item.Preview != null) { path = item.File; }
        }
        using var shell = ShellThumbnail(path);
        return shell == null ? null : FromImage(shell);
    }

    private static Thumb FromImage(Image img)
    {
        int w = Math.Min(MAX_WIDTH, img.Width);
        int h = Math.Max(1, (int)Math.Round(img.Height * (w / (double)img.Width)));
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        using (var attr = new ImageAttributes())
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;
            attr.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(img, new Rectangle(0, 0, w, h), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attr);
        }
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * w * 4, w * 4);
            return new Thumb { Pixels = px, Width = w, Height = h };
        }
        finally { bmp.UnlockBits(data); }
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap); }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bind, [MarshalAs(UnmanagedType.LPStruct)] Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    /// <summary>The Windows thumbnail (videos: a frame), or null when Windows has none.</summary>
    private static Bitmap? ShellThumbnail(string path)
    {
        if (!File.Exists(path)) return null;
        SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
        try
        {
            const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_THUMBNAILONLY = 0x8;
            if (factory.GetImage(new SIZE { cx = MAX_WIDTH, cy = MAX_WIDTH * 9 / 16 }, SIIGBF_BIGGERSIZEOK | SIIGBF_THUMBNAILONLY, out IntPtr hbmp) != 0) return null;
            try { return Image.FromHbitmap(hbmp); }
            finally { DeleteObject(hbmp); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    public void Dispose() => _queue.CompleteAdding();
}
