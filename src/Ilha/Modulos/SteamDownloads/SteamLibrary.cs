using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ilha.Games;

namespace Ilha.SteamDownloads;

/// <summary>One thing Steam is downloading: a game (install or update) or Workshop items of a game.</summary>
internal sealed record Download(string Key, int App, string Name, long Done, long Total, bool Paused, bool Workshop)
{
    public double? Fraction => Total > 0 ? Math.Clamp(Done / (double)Total, 0, 1) : null;
}

/// <summary>
/// Reads what Steam is downloading from its own files (3.9.0, his request), no Steam API: each library's
/// appmanifest_&lt;app&gt;.acf (StateFlags, BytesToDownload, BytesDownloaded) and the folders Steam fills while it
/// downloads (steamapps\downloading\&lt;app&gt;, steamapps\workshop\downloads\&lt;app&gt;). Steam does not always rewrite the
/// manifest during a download, so the progress is the larger of the manifest's count and the size of the folder.
/// </summary>
internal static class SteamLibrary
{
    // EAppState bits: UpdateRunning, UpdateStarted, Preallocating, Downloading, Staging, Committing
    private const int Active = 256 | 1024 | 524288 | 1048576 | 2097152 | 4194304;
    private const int PausedBit = 512, Installed = 4;

    public sealed record Manifest(int App, string Name, int Flags, long ToDownload, long Downloaded);

    public static Manifest? ReadManifest(string text)
    {
        int app = 0, flags = 0;
        string name = "";
        long to = 0, done = 0;
        foreach (var (k, v) in GameSetup.Pairs(text))
        {
            switch (k)
            {
                case "appid": int.TryParse(v, out app); break;
                case "name": if (name.Length == 0) name = v; break;
                case "StateFlags": int.TryParse(v, out flags); break;
                case "BytesToDownload": long.TryParse(v, out to); break;
                case "BytesDownloaded": long.TryParse(v, out done); break;
            }
        }
        return app == 0 ? null : new Manifest(app, name, flags, to, done);
    }

    /// <summary>Downloading (or paused mid-download) by the state bits.</summary>
    public static bool IsDownloading(int flags) => (flags & Active) != 0 || (flags & PausedBit) != 0;

    public static bool IsPaused(int flags) => (flags & PausedBit) != 0 && (flags & (1048576 | 524288)) == 0;

    public static bool IsInstalled(int flags) => (flags & Installed) != 0 && !IsDownloading(flags);

    public static long FolderSize(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            long s = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { s += new FileInfo(f).Length; } catch (Exception) { }
            return s;
        }
        catch (Exception) { return 0; }
    }

    /// <summary>Every download now, plus the manifests read (to tell a finished one from a cancelled one).</summary>
    public static (List<Download> Now, Dictionary<int, Manifest> Manifests) Scan(IEnumerable<string> libraries)
    {
        var now = new List<Download>();
        var manifests = new Dictionary<int, Manifest>();
        foreach (var lib in libraries)
        {
            string apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            try
            {
                foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    Manifest? m;
                    try
                    {
                        using var fs = new FileStream(acf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var rd = new StreamReader(fs);
                        m = ReadManifest(rd.ReadToEnd());
                    }
                    catch (Exception) { continue; }
                    if (m == null) continue;
                    manifests[m.App] = m;
                    if (!IsDownloading(m.Flags)) continue;
                    long folder = FolderSize(Path.Combine(apps, "downloading", m.App.ToString()));
                    long done = Math.Max(m.Downloaded, Math.Min(folder, m.ToDownload > 0 ? m.ToDownload : folder));
                    now.Add(new Download("app:" + m.App, m.App, m.Name, done, m.ToDownload, IsPaused(m.Flags), false));
                }
                string ws = Path.Combine(apps, "workshop", "downloads");
                if (Directory.Exists(ws))
                    foreach (var dir in Directory.EnumerateDirectories(ws))
                    {
                        if (!int.TryParse(Path.GetFileName(dir), out int app)) continue;
                        if (!Directory.EnumerateFileSystemEntries(dir).Any()) continue;
                        long size = FolderSize(dir);
                        if (size == 0) continue;
                        now.Add(new Download("ws:" + app, app, "", size, 0, false, true));
                    }
            }
            catch (Exception e) { Log.Escrever("downloads da steam: " + e.Message); }
        }
        // the Workshop folders name only the game: its name from its own manifest, else the store's (cached)
        for (int i = 0; i < now.Count; i++)
            if (now[i].Workshop)
            {
                string game = manifests.TryGetValue(now[i].App, out var m) && m.Name.Length > 0 ? m.Name : SteamNames.Get(now[i].App) ?? "Steam";
                now[i] = now[i] with { Name = game };
            }
        return (now, manifests);
    }

    public static string Bytes(double b) =>
        b >= 1e9 ? (b / 1e9).ToString("0.0", Lang.Culture) + " GB" : b >= 1e6 ? (b / 1e6).ToString("0", Lang.Culture) + " MB" : (b / 1e3).ToString("0", Lang.Culture) + " KB";

    public static string Eta(double seconds) =>
        seconds < 60 ? Lang.T("menos de 1 min", "under 1 min")
        : seconds < 3600 ? $"{Math.Ceiling(seconds / 60):0} min"
        : $"{Math.Floor(seconds / 3600):0} h {Math.Round(seconds % 3600 / 60):0} min";
}
