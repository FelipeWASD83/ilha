using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vortice.Mathematics;

namespace Ilha.Games;

internal enum Outcome { Win, Loss, Draw }

internal enum MomentKind { Kill, Death, Goal, Round, End, Ace, EpicSave }

/// <summary>Something that happened in the match: what the animations react to.</summary>
internal sealed record Moment(MomentKind Kind, bool Ours = true, string? Who = null, Outcome? Outcome = null);

/// <summary>The match as the island shows it. Each source fills what its game gives; the rest stays empty.</summary>
internal sealed class Match
{
    public required string Game { get; init; }
    public string Mode = "", Map = "";
    public DateTime Start = DateTime.Now;
    public int? Us, Them;
    public string UsLabel = "", ThemLabel = "";
    public Color4 UsColor = TeamColors.Blue, ThemColor = TeamColors.Red;
    /// <summary>Which team mark to draw ("cs2:ct", "cs2:t", "bf6:nato", "bf6:pax", "rl:blue", "rl:orange"; "" = a dot).</summary>
    public string UsArt = "", ThemArt = "";
    /// <summary>A mode badge drawn next to the mode ("premier"), "" for none.</summary>
    public string Badge = "";
    /// <summary>The mode exactly as the game sent it (logged once per match: Premier's value is not known yet).</summary>
    public string RawMode = "";
    public int? Kills, Deaths, Assists;
    /// <summary>The match's MVP (Rocket League: the best score of the winning team); null = the game does not say.</summary>
    public bool? Mvp;
    /// <summary>CS2's round MVP stars (CS2 has no match MVP the island can see: only our own numbers come).</summary>
    public int? MvpStars;
    /// <summary>Other numbers worth a line on the card ("Goals", "2").</summary>
    public List<(string Name, string Value)> Extras = new();
    /// <summary>The clock the game shows: seconds at <see cref="ClockRead"/>, running down (or up).</summary>
    public double? Clock;
    public DateTime ClockRead;
    public bool ClockUp;
    /// <summary>What the clock is ("Round 7", "Overtime", "Warmup").</summary>
    public string Stage = "";
    public Outcome? Outcome;
    /// <summary>No match: the game is just in front (menus, queue, loading). Shows its name and the time it is open.</summary>
    public bool Idle;
    /// <summary>The game's .exe (for the icon of a game the island has no source for).</summary>
    public string Exe = "";

    public string GameName => NameOf(Game);

    public static string NameOf(string game) => game switch
    {
        "cs2" => "Counter-Strike 2",
        "rl" => "Rocket League",
        "bf6" => "Battlefield 6",
        "valorant-win64-shipping" => "Valorant",
        "r5apex" or "r5apex_dx12" => "Apex Legends",
        "fortniteclient-win64-shipping" => "Fortnite",
        "overwatch" => "Overwatch 2",
        "league of legends" => "League of Legends",
        "rainbowsix" => "Rainbow Six Siege",
        "tslgame" => "PUBG",
        "cod" => "Call of Duty",
        "deadlock" => "Deadlock",
        "dota2" => "Dota 2",
        "marvelrivals-win64-shipping" => "Marvel Rivals",
        _ => game,
    };

    /// <summary>The clock now, counted from the last reading (null = the game gave none).</summary>
    public double? ClockAt(DateTime now)
    {
        if (Clock is not double c) return null;
        double passed = (now - ClockRead).TotalSeconds;
        return ClockUp ? c + passed : Math.Max(0, c - passed);
    }

    public static string Time(double seconds)
    {
        long s = (long)Math.Ceiling(Math.Max(0, seconds) - 0.0001);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    public bool HasScore => Us != null && Them != null;

    /// <summary>A copy for the UI thread: the source keeps changing its own on its threads.</summary>
    public Match Copy()
    {
        var c = (Match)MemberwiseClone();
        c.Extras = new List<(string, string)>(Extras);
        return c;
    }
}

internal static class TeamColors
{
    public static readonly Color4 Blue = new(0.27f, 0.56f, 1f, 1f);
    public static readonly Color4 Orange = new(1f, 0.56f, 0.16f, 1f);
    public static readonly Color4 Red = new(1f, 0.33f, 0.29f, 1f);
    public static readonly Color4 Ct = new(0.42f, 0.6f, 0.92f, 1f);
    public static readonly Color4 T = new(0.93f, 0.68f, 0.27f, 1f);
    public static readonly Color4 Win = new(0.2f, 0.84f, 0.42f, 1f);
    public static readonly Color4 Loss = new(1f, 0.33f, 0.29f, 1f);
}

/// <summary>A finished match, kept for the "last 5" on the card.</summary>
internal sealed class Record
{
    [JsonPropertyName("game")] public string Game { get; set; } = "";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("map")] public string Map { get; set; } = "";
    [JsonPropertyName("outcome")] public string Outcome { get; set; } = "";
    [JsonPropertyName("us")] public int? Us { get; set; }
    [JsonPropertyName("them")] public int? Them { get; set; }
    [JsonPropertyName("end")] public DateTime End { get; set; }
    // 3.8.0: older records have none of these
    [JsonPropertyName("kills")] public int? Kills { get; set; }
    [JsonPropertyName("deaths")] public int? Deaths { get; set; }
    [JsonPropertyName("mvp")] public bool? Mvp { get; set; }
    [JsonPropertyName("stars")] public int? Stars { get; set; }

    [JsonIgnore]
    public Outcome? Read => Outcome switch
    {
        "win" => Games.Outcome.Win,
        "loss" => Games.Outcome.Loss,
        "draw" => Games.Outcome.Draw,
        _ => null,
    };

    public static string Text(Outcome o) => o switch
    {
        Games.Outcome.Win => "win",
        Games.Outcome.Loss => "loss",
        _ => "draw",
    };
}

/// <summary>The finished matches in ~/.ilha/partidas.json (only on this PC; the newest 300).</summary>
internal sealed class History
{
    private readonly string _file;
    private List<Record> _list = new();

    public History(string file)
    {
        _file = file;
        try
        {
            if (File.Exists(file)) _list = JsonSerializer.Deserialize<List<Record>>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception e) { Log.Escrever("jogos: histórico ilegível, começa vazio: " + e.Message); }
    }

    public void Add(Match m)
    {
        if (m.Outcome is not Outcome o) return;
        _list.Add(new Record
        {
            Game = m.Game, Mode = m.Mode, Map = m.Map, Outcome = Record.Text(o), Us = m.Us, Them = m.Them, End = DateTime.Now,
            Kills = m.Kills, Deaths = m.Deaths, Mvp = m.Mvp, Stars = m.MvpStars,
        });
        if (_list.Count > 300) _list.RemoveRange(0, _list.Count - 300);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_list, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _file, true);
        }
        catch (Exception e) { Log.Escrever("jogos: não gravou o histórico: " + e.Message); }
    }

    /// <summary>The newest `n` finished matches of every game, newest first (the Games card).</summary>
    public List<Record> Recent(int n = 5) => _list.Where(x => x.Read != null).TakeLast(n).Reverse().ToList();

    /// <summary>Every game with a finished match kept.</summary>
    public IEnumerable<string> Games() => _list.Select(x => x.Game).Distinct().ToList();

    /// <summary>The newest `n` results of one game, oldest first.</summary>
    public List<Outcome> Last(string game, int n = 5) =>
        _list.Where(x => x.Game == game && x.Read != null).TakeLast(n).Select(x => x.Read!.Value).ToList();
}

/// <summary>
/// One game's data source. It runs on its own threads and hands everything to `dispatch` (the UI thread on the island,
/// the caller itself in the self test): `Changed` with the match (null = no match going on) and `Happened` with the
/// moments.
/// </summary>
internal abstract class GameSource : IDisposable
{
    private readonly Action<Action> _dispatch;
    public event Action<Match?>? Changed;
    public event Action<Moment>? Happened;

    protected GameSource(Action<Action> dispatch) => _dispatch = dispatch;

    public abstract string Game { get; }

    protected void Publish(Match? m, IEnumerable<Moment> moments)
    {
        var list = moments.ToList();
        m = m?.Copy();
        _dispatch(() =>
        {
            Changed?.Invoke(m);
            foreach (var x in list) Happened?.Invoke(x);
        });
    }

    /// <summary>The foreground changed (only the BF6 reader cares: it reads the screen, so the game must be in front).</summary>
    public virtual void Foreground(string process) { }

    /// <summary>The game's process name (no .exe): the module drops the match as soon as it is gone.</summary>
    public abstract string Process { get; }

    /// <summary>The game closed: forget the match now.</summary>
    public abstract void Drop();

    public abstract void Dispose();
}
