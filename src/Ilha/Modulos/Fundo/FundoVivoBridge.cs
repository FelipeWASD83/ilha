using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace Ilha.LiveWallpaper;

/// <summary>A wallpaper in Fundo Vivo's library, as its own Library.Scan sees it (video and web from Wallpaper Engine, plus the user's files).</summary>
internal sealed record WallpaperItem(string Id, string Title, string Type, string File, string? Preview);

/// <summary>A monitor with the id Fundo Vivo keys its config by (the device path from QueryDisplayConfig).</summary>
internal sealed record MonitorEntry(string Id, string Label, int Width, int Height, bool Primary, int Left);

/// <summary>
/// The island's side of Fundo Vivo (a separate public app, github.com/FelipeWASD83/fundo-vivo). Nothing of it is linked:
/// the island reads its library the way it does, writes the chosen wallpaper into its config.json the way its settings
/// window does (read again right before writing, change one monitor, save through a temporary file), and sends the
/// running engine the same "reload" command over WM_COPYDATA. FUNDOVIVO_HOME moves the data folders, like in the app.
/// </summary>
internal static class FundoVivoBridge
{
    public const string EngineWindowClass = "FundoVivoEngine";

    private static string? TestHome => Environment.GetEnvironmentVariable("FUNDOVIVO_HOME") is { Length: > 0 } h ? h : null;

    public static string ConfigFile => Path.Combine(TestHome is { } h ? Path.Combine(h, "roaming")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FundoVivo"), "config.json");

    // ------------------------------------------------------------------ config

    /// <summary>The config as JSON, or null when Fundo Vivo never saved one. Retries a file held for a moment.</summary>
    public static JsonObject? ReadConfig()
    {
        if (!File.Exists(ConfigFile)) return null;
        for (int attempt = 0; ; attempt++)
        {
            try { return JsonNode.Parse(File.ReadAllText(ConfigFile))?.AsObject(); }
            catch (IOException) when (attempt < 20) { Thread.Sleep(50); }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>The item id on each monitor (by monitor id).</summary>
    public static Dictionary<string, string> Assigned(JsonObject? cfg)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (cfg?["monitors"] is JsonObject mons)
            foreach (var (id, node) in mons)
                if (node?["itemId"]?.GetValue<string>() is { } item) map[id] = item;
        return map;
    }

    /// <summary>
    /// Puts `item` on `monitor`: the config is read again right before writing (the app and its engine save the same
    /// file), only that monitor's entry changes, and the file is replaced through a uniquely named temporary file.
    /// </summary>
    public static void Assign(MonitorEntry monitor, WallpaperItem item)
    {
        var cfg = ReadConfig();
        if (cfg == null)
        {
            if (File.Exists(ConfigFile)) throw new IOException("config.json do Fundo Vivo ilegível");
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
            cfg = new JsonObject();
        }
        Apply(cfg, monitor, item);
        string tmp = $"{ConfigFile}.ilha.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(tmp, ConfigFile, true); return; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(50); }
            catch { try { File.Delete(tmp); } catch { } throw; }
        }
    }

    /// <summary>The change itself, on the JSON (every other field is kept as it was).</summary>
    internal static void Apply(JsonObject cfg, MonitorEntry monitor, WallpaperItem item)
    {
        if (cfg["monitors"] is not JsonObject mons) cfg["monitors"] = mons = new JsonObject();
        mons[monitor.Id] = new JsonObject
        {
            ["itemId"] = item.Id,
            ["type"] = item.Type,
            ["file"] = item.File,
            ["title"] = item.Title,
            ["monitorName"] = monitor.Label,
        };
    }

    // ------------------------------------------------------------------ engine

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? cls, string? name);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w, ref COPYDATASTRUCT l, uint flags, uint ms, out IntPtr result);

    public static bool EngineRunning => FindWindowW(EngineWindowClass, null) != IntPtr.Zero;

    /// <summary>Tells the running engine to read the config again. False when it is not running.</summary>
    public static unsafe bool Reload()
    {
        IntPtr target = FindWindowW(EngineWindowClass, null);
        if (target == IntPtr.Zero) return false;
        byte[] bytes = Encoding.UTF8.GetBytes("{\"cmd\":\"reload\"}");
        fixed (byte* p = bytes)
        {
            var cds = new COPYDATASTRUCT { cbData = bytes.Length, lpData = (IntPtr)p };
            // SMTO_ABORTIFHUNG: a stuck engine never freezes the island
            SendMessageTimeoutW(target, 0x4A, IntPtr.Zero, ref cds, 0x2, 3000, out _);
        }
        return true;
    }

    /// <summary>
    /// FundoVivo.exe: the configured path, else the running app's, else the Run entry, else the Start menu shortcut.
    /// </summary>
    public static string? FindExe(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        foreach (var p in Process.GetProcessesByName("FundoVivo"))
        {
            using (p)
            {
                try { if (p.MainModule?.FileName is { } f && File.Exists(f)) return f; } catch { }
            }
        }
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            foreach (var name in run?.GetValueNames() ?? Array.Empty<string>())
                if (run!.GetValue(name) is string v && Regex.Match(v, "\"?([^\"]*FundoVivo\\.exe)\"?", RegexOptions.IgnoreCase) is { Success: true } m && File.Exists(m.Groups[1].Value))
                    return m.Groups[1].Value;
        }
        catch { }
        var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Fundo Vivo.lnk");
        if (File.Exists(lnk) && ShortcutTarget(lnk) is { } t && File.Exists(t)) return t;
        return null;
    }

    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            try { return (string)shell.CreateShortcut(lnk).TargetPath; }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch { return null; }
    }

    /// <summary>Starts the engine (it reads the config on its own when it opens).</summary>
    public static bool StartEngine(string exe)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, "--engine") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
            return true;
        }
        catch (Exception e) { Log.Escrever("fundo: não abriu o motor: " + e.Message); return false; }
    }

    // ------------------------------------------------------------------ library

    /// <summary>What the app's library shows that can go on a monitor: Wallpaper Engine video and web, and the user's files.</summary>
    public static List<WallpaperItem> Scan(JsonObject? cfg)
    {
        var list = new List<WallpaperItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool we = cfg?["scanWallpaperEngine"] is not JsonValue v || v.GetValueKind() != JsonValueKind.False;
        if (we)
            foreach (var root in WallpaperEngineFolders())
                foreach (var dir in SafeDirs(root))
                    if (Project(dir) is { } it && seen.Add(it.Id)) list.Add(it);
        if (cfg?["userItems"] is JsonArray users)
            foreach (var u in users.OfType<JsonObject>())
            {
                string? file = u["file"]?.GetValue<string>(), id = u["id"]?.GetValue<string>();
                if (file == null || id == null || !File.Exists(file) || !seen.Add(id)) continue;
                string type = u["type"]?.GetValue<string>() ?? "video";
                list.Add(new WallpaperItem(id, u["title"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(file), type, file,
                    type == "image" ? file : null));
            }
        return list;
    }

    /// <summary>The library's id of a file: its full path in lower case (Fundo Vivo's Library.IdFor).</summary>
    public static string IdFor(string file) => Path.GetFullPath(file).ToLowerInvariant();

    internal static WallpaperItem? Project(string dir)
    {
        string path = Path.Combine(dir, "project.json");
        if (!File.Exists(path)) return null;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip,
            })?.AsObject();
            if (root == null) return null;
            string? type = root["type"]?.ToString()?.Trim().ToLowerInvariant();
            if (type is not ("video" or "web")) return null;
            string file = Path.Combine(dir, root["file"]?.ToString() ?? "");
            if (!File.Exists(file)) return null;
            string? preview = root["preview"]?.ToString() is { Length: > 0 } pv ? Path.Combine(dir, pv) : null;
            string title = root["title"]?.ToString() is { } t && !string.IsNullOrWhiteSpace(t) ? t.Trim() : Path.GetFileName(dir);
            return new WallpaperItem(IdFor(file), title, type, file, preview != null && File.Exists(preview) ? preview : null);
        }
        catch { return null; }
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.GetDirectories(root); } catch { return Array.Empty<string>(); }
    }

    /// <summary>Workshop downloads and the user's own Wallpaper Engine projects, across every Steam library.</summary>
    public static IEnumerable<string> WallpaperEngineFolders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string lib in SteamLibraries())
            foreach (string sub in new[] { @"steamapps\workshop\content\431960", @"steamapps\common\wallpaper_engine\projects\myprojects" })
            {
                string dir = Path.Combine(lib, sub);
                if (Directory.Exists(dir) && seen.Add(Path.GetFullPath(dir))) yield return dir;
            }
    }

    private static IEnumerable<string> SteamLibraries()
    {
        string? steam = null;
        try { steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; } catch { }
        steam ??= @"C:\Program Files (x86)\Steam";
        steam = steam.Replace('/', '\\');
        var libs = new List<string> { steam };
        try
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(steam, @"steamapps\libraryfolders.vdf")), "\"path\"\\s+\"(.+?)\""))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        catch { }
        return libs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ monitors

    private delegate bool MonitorEnumProc(IntPtr h, IntPtr hdc, ref Nativo.RECT r, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize; public Nativo.RECT rcMonitor, rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfoEx(IntPtr m, ref MONITORINFOEX mi);

    /// <summary>The monitors from left to right, each with Fundo Vivo's id for it.</summary>
    public static List<MonitorEntry> Monitors()
    {
        var names = DisplayNames();
        var list = new List<MonitorEntry>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref Nativo.RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfoEx(h, ref mi)) return true;
            names.TryGetValue(mi.szDevice, out var dn);
            string label = string.IsNullOrWhiteSpace(dn.Friendly) ? mi.szDevice.Replace(@"\\.\", "") : dn.Friendly;
            list.Add(new MonitorEntry(dn.Path ?? mi.szDevice, label, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                (mi.dwFlags & 1) != 0, mi.rcMonitor.Left));
            return true;
        }, IntPtr.Zero);
        return list.OrderBy(m => m.Left).ToList();
    }

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct PathSourceInfo { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public int outputTechnology; public int rotation;
        public int scaling; public uint refreshNum, refreshDen; public int scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PathInfo { public PathSourceInfo source; public PathTargetInfo target; public uint flags; }
    [StructLayout(LayoutKind.Sequential, Size = 64)] private struct ModeInfo { public int infoType; public uint id; public LUID adapterId; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfoHeader { public int type; public int size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName { public DeviceInfoHeader header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader header; public uint flags; public int outputTechnology; public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName info);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName info);

    /// <summary>GDI name (\\.\DISPLAY1) to the monitor's device path and friendly name: the same lookup Fundo Vivo does.</summary>
    private static Dictionary<string, (string? Path, string? Friendly)> DisplayNames()
    {
        var map = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const uint QDC_ONLY_ACTIVE_PATHS = 2;
            PathInfo[]? paths = null;
            uint np = 0, nm = 0;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out np, out nm) != 0) return map;
                paths = new PathInfo[np];
                var modes = new ModeInfo[nm];
                int r = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
                if (r == 0) break;
                if (r != 122 || attempt == 4) return map;
                Thread.Sleep(100);
            }
            for (int i = 0; i < np; i++)
            {
                var p = paths![i];
                var src = new SourceDeviceName { header = { type = 1, size = Marshal.SizeOf<SourceDeviceName>(), adapterId = p.source.adapterId, id = p.source.id } };
                if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;
                var tgt = new TargetDeviceName { header = { type = 2, size = Marshal.SizeOf<TargetDeviceName>(), adapterId = p.target.adapterId, id = p.target.id } };
                if (DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;
                map[src.viewGdiDeviceName] = (string.IsNullOrEmpty(tgt.monitorDevicePath) ? null : tgt.monitorDevicePath, tgt.monitorFriendlyDeviceName);
            }
        }
        catch (Exception e) { Log.Escrever("fundo: QueryDisplayConfig falhou: " + e.Message); }
        return map;
    }
}
