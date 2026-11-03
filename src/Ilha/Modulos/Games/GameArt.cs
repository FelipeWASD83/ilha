using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Ilha.Games;

/// <summary>
/// The games' own art, read from the games installed on this PC (they are brands: nothing of it ships with the island
/// or goes into the repository). The game's icon comes from its .exe; CS2's side shields and the Premier badge are SVGs
/// inside its pak01 VPK; BF6's faction emblems are cut from its loading screen the first time each side shows up and
/// kept in ~/.ilha/jogos/. Anything missing falls back to the drawn marks.
/// </summary>
internal static class GameArt
{
    public const string CtLogo = "panorama/images/icons/ct_logo.vsvg_c";
    public const string TLogo = "panorama/images/icons/t_logo.vsvg_c";
    public const string Premier = "panorama/images/icons/ui/premier.vsvg_c";

    private static readonly Dictionary<string, (byte[] Px, int Side)?> _icons = new();
    private static readonly Dictionary<string, string?> _svgs = new();
    private static readonly object _lock = new();

    public static string Folder => Path.Combine(Config.Pasta, "jogos");

    // ------------------------------------------------------------------ game icons

    public static string? ExeOf(string game) => game switch
    {
        "cs2" => GameSetup.SteamAppFolder(730) is { } d ? Path.Combine(d, "game", "bin", "win64", "cs2.exe") : null,
        "bf6" => GameSetup.SteamAppFolder(2807960) is { } d ? Path.Combine(d, "bf6.exe") : null,
        "rl" => (GameSetup.EpicAppFolder("Rocket League") ?? GameSetup.SteamAppFolder(252950)) is { } d
            ? Path.Combine(d, "Binaries", "Win64", "RocketLeague.exe") : null,
        _ => null,
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    /// <summary>The game's icon at 128 px (premultiplied BGRA), or null.</summary>
    public static (byte[] Px, int Side)? GameIcon(string game)
    {
        lock (_lock)
        {
            if (_icons.TryGetValue(game, out var cached)) return cached;
            (byte[], int)? r = null;
            try
            {
                if (ExeOf(game) is { } exe && File.Exists(exe))
                {
                    var h = new IntPtr[1];
                    var ids = new uint[1];
                    if (PrivateExtractIconsW(exe, 0, 128, 128, h, ids, 1, 0) == 1 && h[0] != IntPtr.Zero)
                    {
                        try
                        {
                            using var ico = System.Drawing.Icon.FromHandle(h[0]);
                            using var bmp = ico.ToBitmap();
                            r = (Pixels(bmp, 128, 128), 128);
                        }
                        finally { DestroyIcon(h[0]); }
                    }
                }
            }
            catch (Exception e) { Log.Escrever($"jogos: ícone de {game}: {e.Message}"); }
            _icons[game] = r;
            return r;
        }
    }

    // ------------------------------------------------------------------ CS2's SVGs

    /// <summary>An SVG from CS2's pak01 VPK (the .vsvg_c resource keeps the SVG text as it is), or null.</summary>
    public static string? Cs2Svg(string entry)
    {
        lock (_lock)
        {
            if (_svgs.TryGetValue(entry, out var cached)) return cached;
            string? svg = null;
            try
            {
                if (GameSetup.SteamAppFolder(730) is { } cs2 && Vpk.Read(Path.Combine(cs2, "game", "csgo"), "pak01", entry) is { } data)
                {
                    string t = Encoding.UTF8.GetString(data);
                    int i = t.IndexOf("<svg", StringComparison.Ordinal), j = t.LastIndexOf("</svg>", StringComparison.Ordinal);
                    if (i >= 0 && j > i) svg = Fit(t[i..(j + 6)]);
                }
            }
            catch (Exception e) { Log.Escrever($"jogos: {entry} do CS2: {e.Message}"); }
            _svgs[entry] = svg;
            return svg;
        }
    }

    /// <summary>
    /// The root's fixed width and height become 100%: Direct2D sizes the drawing by them, not by the viewport, so a
    /// 256px icon would always come out at 256 DIP.
    /// </summary>
    public static string Fit(string svg)
    {
        int end = svg.IndexOf('>');
        if (end < 0) return svg;
        string head = svg[..end];
        head = System.Text.RegularExpressions.Regex.Replace(head, "\\s(width|height)=\"[^\"]*\"", " $1=\"100%\"");
        return head + svg[end..];
    }

    /// <summary>Width over height from the root's viewBox (1 when it has none).</summary>
    public static float Aspect(string svg)
    {
        var m = System.Text.RegularExpressions.Regex.Match(svg, "viewBox=\"\\s*[-0-9.]+\\s+[-0-9.]+\\s+([0-9.]+)\\s+([0-9.]+)");
        return m.Success && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)
               && float.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h) && h > 0
            ? w / h : 1f;
    }

    // ------------------------------------------------------------------ BF6 faction emblems

    public static string EmblemFile(string faction) => Path.Combine(Folder, $"bf6-{faction.ToLowerInvariant()}.png");

    public static bool HasEmblem(string faction) => File.Exists(EmblemFile(faction));

    /// <summary>Keeps an emblem cut from the screen (BGRA, top-down) as a PNG.</summary>
    public static void SaveEmblem(string faction, byte[] bgra, int w, int h)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            using var bmp = new System.Drawing.Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
            bmp.UnlockBits(bd);
            string tmp = EmblemFile(faction) + ".tmp";
            bmp.Save(tmp, ImageFormat.Png);
            File.Move(tmp, EmblemFile(faction), true);
            lock (_lock) _icons.Remove("bf6:" + faction);
            Log.Escrever($"jogos: emblema de {faction} do BF6 guardado");
        }
        catch (Exception e) { Log.Escrever($"jogos: emblema de {faction}: {e.Message}"); }
    }

    public static (byte[] Px, int Side)? Emblem(string faction)
    {
        lock (_lock)
        {
            string key = "bf6:" + faction;
            if (_icons.TryGetValue(key, out var cached)) return cached;
            (byte[], int)? r = null;
            try
            {
                if (HasEmblem(faction))
                {
                    using var src = new System.Drawing.Bitmap(EmblemFile(faction));
                    r = (Pixels(src, 96, 96), 96);
                }
            }
            catch (Exception e) { Log.Escrever($"jogos: emblema de {faction}: {e.Message}"); }
            _icons[key] = r;
            return r;
        }
    }

    // ------------------------------------------------------------------ drawing

    private static byte[] Pixels(System.Drawing.Image src, int w, int h)
    {
        using var bmp = new System.Drawing.Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.Clear(System.Drawing.Color.Transparent);
            g.DrawImage(src, 0, 0, w, h);
        }
        var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var o = new byte[w * h * 4];
        try { for (int y = 0; y < h; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, o, y * w * 4, w * 4); }
        finally { bmp.UnlockBits(bd); }
        return o;
    }

    /// <summary>Square premultiplied pixels drawn into a square of `side` DIP with its corner at (x, y).</summary>
    public static unsafe void Draw(ID2D1DeviceContext dc, (byte[] Px, int Side) img, float x, float y, float side)
    {
        var props = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));
        fixed (byte* d = img.Px)
        {
            using var bmp = dc.CreateBitmap(new SizeI(img.Side, img.Side), (IntPtr)d, (uint)(img.Side * 4), props);
            Bitmaps.Draw(dc, bmp, x, y, side, side, BitmapInterpolationMode.Linear);
        }
    }

    /// <summary>An SVG drawn to fit a box (DIP), keeping its proportions; false when Direct2D could not read it.</summary>
    public static bool DrawSvg(ID2D1DeviceContext dc, string svg, float x, float y, float w, float h)
    {
        try
        {
            using var dc5 = dc.QueryInterface<ID2D1DeviceContext5>();
            using var stream = new SharpGen.Runtime.Win32.ComStreamProxy(new MemoryStream(Encoding.UTF8.GetBytes(svg)));
            using var doc = dc5.CreateSvgDocument(stream, new Size(w, h));
            var old = dc.Transform;
            dc.Transform = Matrix3x2.CreateTranslation(x, y) * old;
            dc5.DrawSvgDocument(doc);
            dc.Transform = old;
            return true;
        }
        catch (Exception e)
        {
            Log.Escrever("jogos: svg: " + e.Message);
            return false;
        }
    }
}

/// <summary>Reads one file out of a Source 2 VPK (version 2 directory + numbered archives). Read only.</summary>
internal static class Vpk
{
    public static byte[]? Read(string folder, string pak, string entry)
    {
        string dir = Path.Combine(folder, pak + "_dir.vpk");
        if (!File.Exists(dir)) return null;
        int dot = entry.LastIndexOf('.'), slash = entry.LastIndexOf('/');
        string ext = entry[(dot + 1)..], path = slash > 0 ? entry[..slash] : " ", name = entry[(slash + 1)..dot];
        using var f = new BinaryReader(File.OpenRead(dir));
        uint sig = f.ReadUInt32(), ver = f.ReadUInt32(), tree = f.ReadUInt32();
        if (sig != 0x55AA1234) return null;
        int header = ver == 2 ? 28 : 12;
        if (ver == 2) f.ReadBytes(16);
        string Str()
        {
            var b = new List<byte>();
            byte c;
            while ((c = f.ReadByte()) != 0) b.Add(c);
            return Encoding.UTF8.GetString(b.ToArray());
        }
        for (string e = Str(); e.Length > 0; e = Str())
            for (string p = Str(); p.Length > 0; p = Str())
                for (string n = Str(); n.Length > 0; n = Str())
                {
                    f.ReadUInt32();
                    ushort pre = f.ReadUInt16(), arc = f.ReadUInt16();
                    uint off = f.ReadUInt32(), len = f.ReadUInt32();
                    f.ReadUInt16();
                    byte[] preload = f.ReadBytes(pre);
                    if (e != ext || p != path || n != name) continue;
                    if (arc == 0x7FFF)
                    {
                        f.BaseStream.Seek(header + tree + off, SeekOrigin.Begin);
                        return Concat(preload, f.ReadBytes((int)len));
                    }
                    using var a = File.OpenRead(Path.Combine(folder, $"{pak}_{arc:000}.vpk"));
                    a.Seek(off, SeekOrigin.Begin);
                    var data = new byte[len];
                    a.ReadExactly(data);
                    return Concat(preload, data);
                }
        return null;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        System.Buffer.BlockCopy(a, 0, r, 0, a.Length);
        System.Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }
}
