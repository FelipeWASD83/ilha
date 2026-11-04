using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ilha.Games;

/// <summary>
/// Hours played, from Steam's own file on this PC (userdata\&lt;account&gt;\config\localconfig.vdf, the "Playtime" of each
/// app, in minutes): no login, no web. Steam writes it when a game closes, so a running session shows up after.
/// </summary>
internal static class Playtime
{
    private static readonly Regex Token = new("\"((?:[^\"\\\\]|\\\\.)*)\"|([{}])", RegexOptions.Compiled);

    /// <summary>Minutes per Steam app id, summed over the accounts on this PC (empty when Steam is not found).</summary>
    public static Dictionary<int, int> Steam()
    {
        var r = new Dictionary<int, int>();
        if (GameSetup.SteamFolder() is not { } steam) return r;
        string users = Path.Combine(steam, "userdata");
        if (!Directory.Exists(users)) return r;
        foreach (var dir in Directory.EnumerateDirectories(users))
        {
            string cfg = Path.Combine(dir, "config", "localconfig.vdf");
            if (!File.Exists(cfg)) continue;
            try
            {
                foreach (var (app, min) in Read(File.ReadAllText(cfg)))
                    r[app] = (r.TryGetValue(app, out var v) ? v : 0) + min;
            }
            catch (Exception e) { Log.Escrever("jogos: localconfig.vdf: " + e.Message); }
        }
        return r;
    }

    /// <summary>The Steam game running now (HKCU\Software\Valve\Steam RunningAppID), 0 when none.</summary>
    public static int RunningSteamApp()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return k?.GetValue("RunningAppID") is int id && id > 0 ? id : 0;
        }
        catch (Exception) { return 0; }
    }

    /// <summary>
    /// Steam's hours plus the session going on now (3.5.0, for achievements unlocking while playing): Steam writes a
    /// game's playtime only when it closes, so each running minute is added on top of the hours saved when the session
    /// began, until Steam writes a bigger number of its own. <paramref name="live"/> is (hours at the start, minutes
    /// counted since) per app and is kept up to date here.
    /// </summary>
    internal static Dictionary<int, int> WithLive(Dictionary<int, int> steam, Dictionary<int, (int Baseline, int Extra)> live, int running)
    {
        var r = new Dictionary<int, int>(steam);
        foreach (var (app, (b, e)) in live.ToList())
        {
            int saved = steam.TryGetValue(app, out var s) ? s : 0;
            if (saved > b)
            {
                // Steam wrote the session: its number counts from here
                if (app == running) live[app] = (saved, 0); else live.Remove(app);
                continue;
            }
            if (app != running && e == 0) { live.Remove(app); continue; }
            r[app] = Math.Max(saved, b + e);
        }
        return r;
    }

    /// <summary>The (app id, minutes) pairs under Software\Valve\Steam\apps of one localconfig.vdf text.</summary>
    internal static List<(int App, int Minutes)> Read(string vdf)
    {
        var r = new List<(int, int)>();
        var path = new List<string>();
        string? key = null;
        foreach (System.Text.RegularExpressions.Match m in Token.Matches(vdf))
        {
            if (m.Groups[2].Success)
            {
                if (m.Groups[2].Value == "{") { path.Add(key ?? ""); key = null; }
                else if (path.Count > 0) path.RemoveAt(path.Count - 1);
                continue;
            }
            string s = m.Groups[1].Value;
            if (key == null) { key = s; continue; }
            // a value: Playtime of an app, four levels under ...\Steam\apps\<id>
            if (key == "Playtime" && path.Count >= 2 && string.Equals(path[^2], "apps", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(path[^1], out int app) && int.TryParse(s, out int min) && min > 0)
                r.Add((app, min));
            key = null;
        }
        return r;
    }

    /// <summary>The Steam app id of the games the island knows.</summary>
    public static int? SteamApp(string game) => game switch
    {
        "cs2" => 730,
        "bf6" => 2807960,
        "rl" => 252950,
        "dota2" => 570,
        "deadlock" => 1422450,
        "tslgame" => 578080,
        "r5apex" or "r5apex_dx12" => 1172470,
        "rainbowsix" => 359550,
        "marvelrivals-win64-shipping" => 2767030,
        "bodycam-win64-shipping" => 2406770,
        _ => null,
    };
}
