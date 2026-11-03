using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;

namespace Ilha.Clips;

/// <summary>A clip on disk: the video and its small frame.</summary>
internal sealed record ClipFile(string Video, string Thumb, DateTime When, string Reason)
{
    public string Title => Path.GetFileNameWithoutExtension(Video);
}

/// <summary>
/// Where the clips live: Videos\Ilha\Clipes (or the configured folder), the ones waiting for the end of the match in
/// ".pendentes" inside it, and the small frames in ".miniaturas". Opening and showing in the folder go through the shell.
/// </summary>
internal static class ClipStore
{
    public static string Folder(string? configured) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Ilha", Lang.T("Clipes", "Clips"))
            : configured.Trim();

    public static string Pending(string folder) => Path.Combine(folder, ".pendentes");
    public static string Thumbs(string folder) => Path.Combine(folder, ".miniaturas");

    /// <summary>"CS2 2026-11-03 16-21-05 ace" (no characters Windows refuses in a file name).</summary>
    public static string Name(string game, DateTime when, string reason)
    {
        string n = $"{game} {when:yyyy-MM-dd HH-mm-ss} {reason}".Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '-');
        return n;
    }

    /// <summary>The saved clips, newest first.</summary>
    public static List<ClipFile> Recent(string folder, int count)
    {
        try
        {
            if (!Directory.Exists(folder)) return new();
            return new DirectoryInfo(folder).GetFiles("*.mp4").OrderByDescending(f => f.CreationTimeUtc).Take(count)
                .Select(f => new ClipFile(f.FullName, Path.Combine(Thumbs(folder), Path.GetFileNameWithoutExtension(f.Name) + ".jpg"), f.CreationTime, ""))
                .ToList();
        }
        catch (Exception) { return new(); }
    }

    /// <summary>A pending clip goes to the clips folder (its frame too). Returns where it ended up.</summary>
    public static ClipFile Keep(ClipFile c, string folder)
    {
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Thumbs(folder));
        string video = Path.Combine(folder, Path.GetFileName(c.Video)), thumb = Path.Combine(Thumbs(folder), Path.GetFileName(c.Thumb));
        if (File.Exists(c.Video)) File.Move(c.Video, video, true);
        if (File.Exists(c.Thumb)) File.Move(c.Thumb, thumb, true);
        return c with { Video = video, Thumb = thumb };
    }

    public static void Discard(ClipFile c)
    {
        try { File.Delete(c.Video); } catch (Exception) { }
        try { File.Delete(c.Thumb); } catch (Exception) { }
    }

    public static void Open(string video)
    {
        try { Process.Start(new ProcessStartInfo(video) { UseShellExecute = true }); }
        catch (Exception e) { Log.Escrever("clipes: não abriu " + video + ": " + e.Message); }
    }

    public static void ShowInFolder(string video)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + video }, UseShellExecute = false }); }
        catch (Exception e) { Log.Escrever("clipes: pasta: " + e.Message); }
    }

    // ------------------------------------------------------------------ the small frames on the cards

    private static readonly Dictionary<string, (byte[] Px, int W, int H)?> _thumbs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A JPEG frame as opaque BGRA pixels (cached; null when the file is not there or does not read).</summary>
    public static (byte[] Px, int W, int H)? Load(string jpg)
    {
        lock (_thumbs)
        {
            if (_thumbs.TryGetValue(jpg, out var cached) && cached != null) return cached;
            (byte[], int, int)? r = null;
            try
            {
                if (File.Exists(jpg))
                {
                    using var bmp = new System.Drawing.Bitmap(jpg);
                    var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        var px = new byte[bmp.Width * bmp.Height * 4];
                        for (int y = 0; y < bmp.Height; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, px, y * bmp.Width * 4, bmp.Width * 4);
                        r = (px, bmp.Width, bmp.Height);
                    }
                    finally { bmp.UnlockBits(bd); }
                }
            }
            catch (Exception) { r = null; }
            _thumbs[jpg] = r;
            return r;
        }
    }

    /// <summary>A frame drawn into (x, y, w, h), cropped to fill it, with rounded corners.</summary>
    public static unsafe void Draw(ID2D1DeviceContext dc, ID2D1Factory1 f, string jpg, float x, float y, float w, float h, float radius)
    {
        if (Load(jpg) is not { } img)
        {
            Icone.Retangulo(dc, x, y, w, h, radius, new Vortice.Mathematics.Color4(1, 1, 1, 0.08f));
            return;
        }
        var props = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore));
        fixed (byte* d = img.Px)
        {
            using var bmp = dc.CreateBitmap(new Vortice.Mathematics.SizeI(img.W, img.H), (IntPtr)d, (uint)(img.W * 4), props);
            float scale = MathF.Max(w / img.W, h / img.H);
            float dw = img.W * scale, dh = img.H * scale;
            using var clip = f.CreateRoundedRectangleGeometry(new RoundedRectangle(new Vortice.RawRectF(x, y, x + w, y + h), radius, radius));
            dc.PushLayer(new LayerParameters1 { ContentBounds = new Vortice.RawRectF(x, y, x + w, y + h), GeometricMask = clip, Opacity = 1, MaskTransform = System.Numerics.Matrix3x2.Identity }, null!);
            Bitmaps.Draw(dc, bmp, x + (w - dw) / 2, y + (h - dh) / 2, dw, dh, BitmapInterpolationMode.Linear);
            dc.PopLayer();
        }
    }
}
