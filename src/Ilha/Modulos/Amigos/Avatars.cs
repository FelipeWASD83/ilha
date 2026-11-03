using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Ilha.Friends;

/// <summary>
/// Friends' avatars from Steam's image server, downloaded once into ~/.ilha/amigos/avatares/ (by the URL, which changes
/// when the picture does) and kept as round 64 px premultiplied pixels (the circle is cut into the alpha).
/// </summary>
internal static class Avatars
{
    private const int Side = 64;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Dictionary<string, (byte[] Px, int Side)?> _loaded = new();
    private static readonly HashSet<string> _fetching = new();
    private static readonly object _lock = new();

    public static string Folder => Path.Combine(Config.Pasta, "amigos", "avatares");

    private static string FileOf(string url) =>
        Path.Combine(Folder, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant() + ".jpg");

    public static (byte[] Px, int Side)? Get(string url, Action arrived)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
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
                Log.Escrever("amigos: avatar não baixou: " + e.Message);
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
            using var bmp = new System.Drawing.Bitmap(Side, Side, PixelFormat.Format32bppPArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, Side, Side);
            }
            var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, Side, Side), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            var o = new byte[Side * Side * 4];
            try { for (int y = 0; y < Side; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, o, y * Side * 4, Side * 4); }
            finally { bmp.UnlockBits(bd); }
            Round(o, Side);
            return (o, Side);
        }
        catch (Exception e)
        {
            Log.Escrever("amigos: avatar ilegível: " + e.Message);
            return null;
        }
    }

    /// <summary>Cuts a circle into premultiplied BGRA pixels, with one pixel of soft edge.</summary>
    internal static void Round(byte[] px, int side)
    {
        float c = side / 2f, r = side / 2f;
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                float dx = x + 0.5f - c, dy = y + 0.5f - c;
                float k = Math.Clamp(r - MathF.Sqrt(dx * dx + dy * dy), 0, 1);
                if (k >= 1) continue;
                int i = (y * side + x) * 4;
                for (int j = 0; j < 4; j++) px[i + j] = (byte)(px[i + j] * k);
            }
    }
}
