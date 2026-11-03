using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ilha.Games;

/// <summary>
/// Where the games are and switching on their official data feeds: CS2's Game State Integration (a .cfg in its cfg
/// folder) and Rocket League's Stats API (PacketSendRate in its ini). Both take effect the next time the game opens.
/// Nothing else in the games is touched.
/// </summary>
internal static class GameSetup
{
    public const string Cs2File = "gamestate_integration_ilha.cfg";
    public const string Cs2Token = "ilha";

    private static readonly Regex Pair = new("\"((?:[^\"\\\\]|\\\\.)*)\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    private static IEnumerable<(string Key, string Value)> Pairs(string vdf) =>
        Pair.Matches(vdf).Select(m => (m.Groups[1].Value, m.Groups[2].Value));

    public static string? SteamFolder()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("SteamPath") is string s && Directory.Exists(s)) return Path.GetFullPath(s);
        }
        catch (Exception) { }
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    /// <summary>Every "path" in libraryfolders.vdf (the Steam folder itself first).</summary>
    public static List<string> SteamLibraries()
    {
        var r = new List<string>();
        if (SteamFolder() is not { } steam) return r;
        r.Add(steam);
        try
        {
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (var (k, v) in Pairs(File.ReadAllText(vdf)))
                {
                    if (k != "path") continue;
                    string p = v.Replace(@"\\", @"\");
                    if (Directory.Exists(p) && !r.Any(x => string.Equals(Path.GetFullPath(x), Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase))) r.Add(p);
                }
        }
        catch (Exception e) { Log.Escrever("jogos: libraryfolders.vdf: " + e.Message); }
        return r;
    }

    /// <summary>The install folder of a Steam app, from its appmanifest.</summary>
    public static string? SteamAppFolder(int appid)
    {
        foreach (var lib in SteamLibraries())
        {
            string acf = Path.Combine(lib, "steamapps", $"appmanifest_{appid}.acf");
            if (!File.Exists(acf)) continue;
            try
            {
                foreach (var (k, v) in Pairs(File.ReadAllText(acf)))
                {
                    if (k != "installdir") continue;
                    string p = Path.Combine(lib, "steamapps", "common", v);
                    if (Directory.Exists(p)) return p;
                }
            }
            catch (Exception) { }
        }
        return null;
    }

    /// <summary>The install folder of an Epic game, from the launcher's manifests (by the display name).</summary>
    public static string? EpicAppFolder(string name)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) return null;
        foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
        {
            try
            {
                using var d = JsonDocument.Parse(File.ReadAllText(f));
                var root = d.RootElement;
                if (root.TryGetProperty("DisplayName", out var n) && n.GetString() is { } dn && dn.Contains(name, StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("InstallLocation", out var l) && l.GetString() is { } loc && Directory.Exists(loc))
                    return loc;
            }
            catch (Exception) { }
        }
        return null;
    }

    /// <summary>The public Steam name of the account signed in last (loginusers.vdf, PersonaName only).</summary>
    public static string? SteamName()
    {
        if (SteamFolder() is not { } steam) return null;
        try
        {
            string vdf = Path.Combine(steam, "config", "loginusers.vdf");
            if (!File.Exists(vdf)) return null;
            string? recent = null, first = null;
            foreach (var block in File.ReadAllText(vdf).Split('}'))
            {
                string? persona = null;
                bool mostRecent = false;
                foreach (var (k, v) in Pairs(block))
                {
                    if (k == "PersonaName") persona = v;
                    if (k == "MostRecent" && v == "1") mostRecent = true;
                }
                if (persona == null) continue;
                first ??= persona;
                if (mostRecent) recent = persona;
            }
            return recent ?? first;
        }
        catch (Exception) { return null; }
    }

    public static string Cs2Cfg(int port) =>
        "\"Ilha\"\n{\n" +
        $" \"uri\"       \"http://127.0.0.1:{port}\"\n" +
        " \"timeout\"   \"5.0\"\n \"buffer\"    \"0.1\"\n \"throttle\"  \"0.25\"\n \"heartbeat\" \"10.0\"\n" +
        $" \"auth\"\n {{\n   \"token\" \"{Cs2Token}\"\n }}\n" +
        " \"data\"\n {\n" +
        "   \"provider\"            \"1\"\n   \"map\"                 \"1\"\n   \"round\"               \"1\"\n" +
        "   \"player_id\"           \"1\"\n   \"player_state\"        \"1\"\n   \"player_match_stats\"  \"1\"\n" +
        "   \"phase_countdowns\"    \"1\"\n }\n}\n";

    /// <summary>Writes the GSI file into CS2's cfg folder when it is missing or different. Returns what happened.</summary>
    public static string InstallCs2(int port)
    {
        if (SteamAppFolder(730) is not { } cs2) return "CS2 não encontrado";
        string cfg = Path.Combine(cs2, "game", "csgo", "cfg");
        if (!Directory.Exists(cfg)) return "pasta cfg do CS2 não encontrada";
        string file = Path.Combine(cfg, Cs2File), text = Cs2Cfg(port);
        try
        {
            if (File.Exists(file) && File.ReadAllText(file) == text) return "já instalado";
            File.WriteAllText(file, text, new UTF8Encoding(false));
            return "instalado (vale ao abrir o CS2 de novo)";
        }
        catch (Exception e) { return "não gravou: " + e.Message; }
    }

    public static string? RocketLeagueIni()
    {
        foreach (var dir in new[] { EpicAppFolder("Rocket League"), SteamAppFolder(252950) })
        {
            if (dir == null) continue;
            foreach (var name in new[] { "TAStatsAPI.ini", "DefaultStatsAPI.ini" })
            {
                string ini = Path.Combine(dir, "TAGame", "Config", name);
                if (File.Exists(ini)) return ini;
            }
        }
        return null;
    }

    /// <summary>
    /// The Stats API ships off (PacketSendRate=0): sets 10 packets a second, plenty for a score and light on the game. A
    /// rate already above 0 was set by him and stays.
    /// </summary>
    public static string EnableRocketLeague()
    {
        if (RocketLeagueIni() is not { } ini) return "Rocket League não encontrado";
        try
        {
            string text = File.ReadAllText(ini);
            if (WithRate(text) is not { } changed) return "já ligado";
            File.WriteAllText(ini, changed);
            return "ligado (vale ao abrir o Rocket League de novo)";
        }
        catch (Exception e) { return "não gravou: " + e.Message; }
    }

    /// <summary>The ini with PacketSendRate=10, or null when the rate is already above 0.</summary>
    public static string? WithRate(string text)
    {
        var m = Regex.Match(text, @"^\s*PacketSendRate\s*=\s*([0-9.]+)", RegexOptions.Multiline);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0) return null;
        text = m.Success
            ? text.Remove(m.Groups[1].Index, m.Groups[1].Length).Insert(m.Groups[1].Index, "10")
            : text.TrimEnd() + "\r\nPacketSendRate=10\r\n";
        if (!text.Contains("[TAGame.MatchStatsExporter_TA]")) text = "[TAGame.MatchStatsExporter_TA]\r\n" + text;
        return text;
    }
}
