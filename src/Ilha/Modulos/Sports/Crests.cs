using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Ilha.Sports;

/// <summary>
/// Team crests from ESPN's image server, downloaded once into ~/.ilha/esportes/escudos/ (they are the clubs' brands:
/// never in the repository) and kept as 64 px premultiplied pixels. A crest not there yet asks for a redraw when it
/// arrives.
/// </summary>
internal static class Crests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Dictionary<string, (byte[] Px, int Side)?> _loaded = new();
    private static readonly HashSet<string> _fetching = new();
    private static readonly object _lock = new();

    public static string Folder => Path.Combine(Config.Pasta, "esportes", "escudos");

    private static string FileOf(string url)
    {
        var h = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant();
        return Path.Combine(Folder, h + ".png");
    }

    /// <summary>The crest if it is here; otherwise null now, and `arrived` runs (on any thread) once it is downloaded.</summary>
    public static (byte[] Px, int Side)? Get(string url, Action arrived)
    {
        if (url.Length == 0 || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        lock (_lock)
        {
            if (_loaded.TryGetValue(url, out var px)) return px;
            string file = FileOf(url);
            if (File.Exists(file))
            {
                var r = Decode(file);
                _loaded[url] = r;
                return r;
            }
            if (!_fetching.Add(url)) return null;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                Directory.CreateDirectory(Folder);
                string file = FileOf(url), tmp = file + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
                File.Move(tmp, file, true);
                lock (_lock) { _loaded.Remove(url); _fetching.Remove(url); }
                arrived();
            }
            catch (Exception e)
            {
                Log.Escrever("esportes: escudo não baixou: " + e.Message);
                // no retry storm: a failed crest stays empty until the island opens again
                lock (_lock) _loaded[url] = null;
            }
        });
        return null;
    }

    private static (byte[], int)? Decode(string file)
    {
        try
        {
            using var src = new System.Drawing.Bitmap(file);
            const int side = 64;
            using var bmp = new System.Drawing.Bitmap(side, side, PixelFormat.Format32bppPArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.Clear(System.Drawing.Color.Transparent);
                // keep the proportions inside the square
                float k = Math.Min(side / (float)src.Width, side / (float)src.Height);
                float w = src.Width * k, h = src.Height * k;
                g.DrawImage(src, (side - w) / 2, (side - h) / 2, w, h);
            }
            var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, side, side), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            var o = new byte[side * side * 4];
            try { for (int y = 0; y < side; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, o, y * side * 4, side * 4); }
            finally { bmp.UnlockBits(bd); }
            return (o, side);
        }
        catch (Exception e)
        {
            Log.Escrever("esportes: escudo ilegível: " + e.Message);
            return null;
        }
    }
}
