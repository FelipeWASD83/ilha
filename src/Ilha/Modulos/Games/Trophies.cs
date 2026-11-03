using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Vortice.Mathematics;

namespace Ilha.Games;

/// <summary>Hours of one game: from Steam's own count, or the island's own clock for games outside Steam.</summary>
internal sealed record GameHours(string Key, string Name, int Minutes, int? SteamApp);

/// <summary>An achievement: hours in one game, or hours in all of them.</summary>
internal sealed record Trophy(string Id, int Tier, int Hours, string GameKey, string GameName)
{
    public bool Total => GameKey.Length == 0;
}

/// <summary>
/// The trophies: per game at 10, 50, 100, 250, 500 and 1000 hours (bronze, silver, gold, platinum, diamond, legend),
/// and for all games together at 100, 500, 1000, 2500 and 5000 hours.
/// </summary>
internal static class Trophies
{
    public static readonly int[] GameTiers = { 10, 50, 100, 250, 500, 1000 };
    public static readonly int[] TotalTiers = { 100, 500, 1000, 2500, 5000 };

    public static string TierName(int tier) => tier switch
    {
        0 => Lang.T("Bronze", "Bronze"),
        1 => Lang.T("Prata", "Silver"),
        2 => Lang.T("Ouro", "Gold"),
        3 => Lang.T("Platina", "Platinum"),
        4 => Lang.T("Diamante", "Diamond"),
        _ => Lang.T("Lenda", "Legend"),
    };

    public static Color4 TierColor(int tier) => tier switch
    {
        0 => new Color4(0.8f, 0.5f, 0.28f, 1f),
        1 => new Color4(0.78f, 0.8f, 0.84f, 1f),
        2 => new Color4(1f, 0.8f, 0.22f, 1f),
        3 => new Color4(0.62f, 0.88f, 0.9f, 1f),
        4 => new Color4(0.55f, 0.75f, 1f, 1f),
        _ => new Color4(1f, 0.45f, 0.85f, 1f),
    };

    public static List<Trophy> Earned(IEnumerable<GameHours> games)
    {
        var list = games.ToList();
        var r = new List<Trophy>();
        foreach (var g in list)
            for (int t = 0; t < GameTiers.Length; t++)
                if (g.Minutes >= GameTiers[t] * 60) r.Add(new Trophy($"jogo:{g.Key}:{GameTiers[t]}", t, GameTiers[t], g.Key, g.Name));
        int total = list.Sum(g => g.Minutes);
        for (int t = 0; t < TotalTiers.Length; t++)
            if (total >= TotalTiers[t] * 60) r.Add(new Trophy($"total:{TotalTiers[t]}", t, TotalTiers[t], "", ""));
        return r;
    }

    /// <summary>The next goal of one game (hours) and how far it is (0 to 1), or null past the last tier.</summary>
    public static (int Hours, int Tier, float Progress)? Next(int minutes)
    {
        for (int t = 0; t < GameTiers.Length; t++)
            if (minutes < GameTiers[t] * 60)
            {
                int from = t == 0 ? 0 : GameTiers[t - 1] * 60;
                return (GameTiers[t], t, (minutes - from) / (float)(GameTiers[t] * 60 - from));
            }
        return null;
    }
}

/// <summary>
/// What was unlocked and when (~/.ilha/conquistas.json). The first time it only takes note of everything already earned
/// (no rain of notices for hundreds of hours played before the island), then reports what is new.
/// </summary>
internal sealed class TrophyStore
{
    private sealed class Data
    {
        [JsonPropertyName("seeded")] public bool Seeded { get; set; }
        [JsonPropertyName("unlocked")] public Dictionary<string, DateTime> Unlocked { get; set; } = new();
    }

    private readonly string _file;
    private Data _d = new();

    public TrophyStore(string file)
    {
        _file = file;
        try { if (File.Exists(file)) _d = JsonSerializer.Deserialize<Data>(File.ReadAllText(file)) ?? new(); }
        catch (Exception e) { Log.Escrever("jogos: conquistas ilegíveis, começa do zero: " + e.Message); }
    }

    public IReadOnlyDictionary<string, DateTime> Unlocked => _d.Unlocked;

    /// <summary>Keeps every earned trophy; returns only the ones that are news (none on the very first run).</summary>
    public List<Trophy> Update(List<Trophy> earned)
    {
        var fresh = earned.Where(t => !_d.Unlocked.ContainsKey(t.Id)).ToList();
        if (fresh.Count == 0 && _d.Seeded) return fresh;
        foreach (var t in fresh) _d.Unlocked[t.Id] = DateTime.Now;
        bool first = !_d.Seeded;
        _d.Seeded = true;
        Save();
        return first ? new List<Trophy>() : fresh;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_d, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _file, true);
        }
        catch (Exception e) { Log.Escrever("jogos: não gravou as conquistas: " + e.Message); }
    }
}

/// <summary>
/// The island's own clock for games outside Steam (Steam counts its own): once a minute, every known game process that
/// is running gets a minute (by the process list, no handle opened). Kept in ~/.ilha/tempo-de-jogo.json.
/// </summary>
internal sealed class OwnClock
{
    private readonly string _file;
    private Dictionary<string, int> _minutes = new();

    // process name (no .exe) → game key, for games Steam does not count
    public static readonly Dictionary<string, string> Tracked = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RocketLeague"] = "rl",
        ["FortniteClient-Win64-Shipping"] = "fortniteclient-win64-shipping",
        ["VALORANT-Win64-Shipping"] = "valorant-win64-shipping",
        ["League of Legends"] = "league of legends",
        ["GenshinImpact"] = "genshinimpact",
        ["Overwatch"] = "overwatch",
        ["cod"] = "cod",
    };

    public OwnClock(string file)
    {
        _file = file;
        try { if (File.Exists(file)) _minutes = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(file)) ?? new(); }
        catch (Exception) { }
    }

    public IReadOnlyDictionary<string, int> Minutes => _minutes;

    /// <summary>One minute passed: the running tracked games get it. Returns true when something was counted.</summary>
    public bool Tick()
    {
        bool any = false;
        foreach (var (process, key) in Tracked)
        {
            if (!GamesModule.Running(process)) continue;
            _minutes[key] = (_minutes.TryGetValue(key, out var m) ? m : 0) + 1;
            any = true;
        }
        if (any)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file, JsonSerializer.Serialize(_minutes));
            }
            catch (Exception) { }
        }
        return any;
    }
}

/// <summary>
/// Names of Steam games: the installed ones from their appmanifest; the others from Steam's store (one small public
/// request per game, only for games with at least 10 hours, cached in ~/.ilha/jogos/steam-nomes.json).
/// </summary>
internal static class SteamNames
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static Dictionary<int, string>? _names;
    private static readonly object _lock = new();
    private static string File_ => Path.Combine(GameArt.Folder, "steam-nomes.json");

    public static string? Get(int app)
    {
        lock (_lock)
        {
            Load();
            return _names!.TryGetValue(app, out var n) ? n : null;
        }
    }

    private static void Load()
    {
        if (_names != null) return;
        _names = new();
        try { if (File.Exists(File_)) _names = JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(File_)) ?? new(); }
        catch (Exception) { }
        foreach (var lib in GameSetup.SteamLibraries())
        {
            try
            {
                foreach (var acf in Directory.EnumerateFiles(Path.Combine(lib, "steamapps"), "appmanifest_*.acf"))
                {
                    string t = File.ReadAllText(acf);
                    var id = System.Text.RegularExpressions.Regex.Match(t, "\"appid\"\\s+\"(\\d+)\"");
                    var nm = System.Text.RegularExpressions.Regex.Match(t, "\"name\"\\s+\"([^\"]+)\"");
                    if (id.Success && nm.Success) _names[int.Parse(id.Groups[1].Value)] = nm.Groups[1].Value;
                }
            }
            catch (Exception) { }
        }
    }

    private static bool _busy;
    // apps the store has no page for (removed games): not asked again until the island restarts
    private static readonly HashSet<int> _none = new();

    /// <summary>
    /// Asks the store for the missing names, one at a time 1.5 s apart (all of them since 3.8.0: the achievements list
    /// every game); `done` runs every ten names that arrived and at the end. One run at a time.
    /// </summary>
    public static void Fetch(IEnumerable<int> apps, Action done)
    {
        List<int> missing;
        lock (_lock)
        {
            Load();
            if (_busy) return;
            missing = apps.Where(a => !_names!.ContainsKey(a) && !_none.Contains(a)).Distinct().ToList();
            if (missing.Count == 0) return;
            _busy = true;
        }
        _ = Task.Run(async () =>
        {
            int got = 0;
            try
            {
            foreach (var app in missing)
            {
                try
                {
                    var json = await Http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={app}&filters=basic").ConfigureAwait(false);
                    using var d = JsonDocument.Parse(json);
                    if (d.RootElement.TryGetProperty(app.ToString(), out var node) && node.TryGetProperty("data", out var data)
                        && data.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                    {
                        lock (_lock) _names![app] = name;
                        if (++got % 10 == 0) { Save(); done(); }
                    }
                    else lock (_lock) _none.Add(app);
                }
                catch (Exception) { }
                await Task.Delay(1500).ConfigureAwait(false);
            }
            }
            finally
            {
                lock (_lock) _busy = false;
            }
            if (got == 0) return;
            Save();
            done();
        });
    }

    private static void Save()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(GameArt.Folder);
                File.WriteAllText(File_, JsonSerializer.Serialize(_names));
            }
            catch (Exception) { }
        }
    }
}
