using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ilha.Sports;

/// <summary>A league the island can follow: its key in the settings and its ESPN path.</summary>
internal sealed record League(string Key, string Path, string Pt, string En, bool Racing = false)
{
    public string Name => Lang.T(Pt, En);

    /// <summary>The first part of the ESPN path: soccer, racing, basketball, football or tennis.</summary>
    public string Sport => Path.Split('/')[0];

    public static readonly League[] All =
    {
        new("bra.1", "soccer/bra.1", "Brasileirão Série A", "Brasileirão Série A"),
        new("bra.2", "soccer/bra.2", "Brasileirão Série B", "Brasileirão Série B"),
        new("bra.copa_do_brazil", "soccer/bra.copa_do_brazil", "Copa do Brasil", "Copa do Brasil"),
        new("conmebol.libertadores", "soccer/conmebol.libertadores", "Libertadores", "Libertadores"),
        new("conmebol.sudamericana", "soccer/conmebol.sudamericana", "Sul-Americana", "Sudamericana"),
        new("uefa.champions", "soccer/uefa.champions", "Champions League", "Champions League"),
        new("eng.1", "soccer/eng.1", "Premier League", "Premier League"),
        new("esp.1", "soccer/esp.1", "La Liga", "La Liga"),
        new("fifa.world", "soccer/fifa.world", "Copa do Mundo", "World Cup"),
        new("f1", "racing/f1", "Fórmula 1", "Formula 1", Racing: true),
        new("nba", "basketball/nba", "NBA", "NBA"),
        new("nfl", "football/nfl", "NFL", "NFL"),
        new("atp", "tennis/atp", "Tênis ATP", "ATP tennis"),
        new("wta", "tennis/wta", "Tênis WTA", "WTA tennis"),
    };

    public static League? Find(string key) => All.FirstOrDefault(l => string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase));
}

internal enum EventState { Pre, Live, Post }

/// <summary>One side of a game (a team) or one driver of a race.</summary>
internal sealed record Competitor(string Id, string Name, string Short, string Abbrev, int? Score, string Logo, bool Home, int Order);

/// <summary>A game or a race session, as the island shows it.</summary>
internal sealed record SportEvent(string Id, League League, string Name, DateTime Start, EventState State, string Detail, string Clock,
    List<Competitor> Competitors, string Session)
{
    public Competitor? Home => Competitors.FirstOrDefault(c => c.Home) ?? Competitors.FirstOrDefault();
    public Competitor? Away => Competitors.FirstOrDefault(c => !c.Home && c != Home) ?? Competitors.Skip(1).FirstOrDefault();
}

/// <summary>
/// ESPN's open scoreboard JSON (site.api.espn.com, no key; not documented, so every field is optional here and a missing
/// one only leaves that bit out). See docs/pesquisa-placar-de-esportes.md.
/// </summary>
internal static class Espn
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    static Espn() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Ilha/3 (+https://github.com/FelipeWASD83/ilha)");

    public static string Url(League l, DateTime? day = null) =>
        $"https://site.api.espn.com/apis/site/v2/sports/{l.Path}/scoreboard" + (day is DateTime d ? "?dates=" + d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "");

    public static async Task<List<SportEvent>> Fetch(League l, DateTime? day, CancellationToken ct)
    {
        var text = await Http.GetStringAsync(Url(l, day), ct).ConfigureAwait(false);
        return Parse(l, text, day);
    }

    public static List<SportEvent> Parse(League l, string json, DateTime? day = null)
    {
        var r = new List<SportEvent>();
        using var d = JsonDocument.Parse(json);
        if (!d.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) return r;
        foreach (var e in events.EnumerateArray())
        {
            try
            {
                if (l.Sport == "tennis") r.AddRange(Matches(l, e, (day ?? DateTime.Today).Date));
                else if (Event(l, e) is { } ev) r.Add(ev);
            }
            catch (Exception) { }
        }
        return r;
    }

    /// <summary>
    /// A tennis event is a whole tournament with every match inside its groupings: the singles matches of that day (and
    /// any live one) become games of their own, scored in sets won.
    /// </summary>
    private static IEnumerable<SportEvent> Matches(League l, JsonElement e, DateTime day)
    {
        if (!e.TryGetProperty("groupings", out var groups) || groups.ValueKind != JsonValueKind.Array) yield break;
        foreach (var g in groups.EnumerateArray())
        {
            if (!Str(g, "grouping", "slug").Contains("singles") || !g.TryGetProperty("competitions", out var comps) || comps.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var comp in comps.EnumerateArray())
            {
                var state = State(comp);
                DateTime.TryParse(Str(comp, "date"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start);
                start = start.ToLocalTime();
                if (state != EventState.Live && start.Date != day) continue;
                var players = new List<Competitor>();
                if (comp.TryGetProperty("competitors", out var cps) && cps.ValueKind == JsonValueKind.Array)
                    foreach (var c in cps.EnumerateArray())
                    {
                        if (!c.TryGetProperty("athlete", out var a) || a.ValueKind != JsonValueKind.Object) continue;
                        int sets = c.TryGetProperty("linescores", out var ls) && ls.ValueKind == JsonValueKind.Array
                            ? ls.EnumerateArray().Count(s => s.TryGetProperty("winner", out var w) && w.ValueKind == JsonValueKind.True) : 0;
                        string name = Str(a, "displayName");
                        string shortName = Str(a, "shortName") is { Length: > 0 } sn ? sn : name;
                        players.Add(new Competitor(Str(c, "id"), name, shortName, shortName, state == EventState.Pre ? null : sets,
                            Str(a, "flag", "href"), Str(c, "homeAway") == "home", int.TryParse(Str(c, "order"), out var o) ? o : 99));
                    }
                if (players.Count < 2 || players.Any(p => p.Name == "TBD")) continue;
                var status = comp.TryGetProperty("status", out var cs) ? cs : default;
                yield return new SportEvent(Str(comp, "id"), l, $"{players[0].Short} x {players[1].Short}", start, state,
                    Str(status, "type", "shortDetail"), "", players, Str(comp, "round", "displayName"));
            }
        }
    }

    private static SportEvent? Event(League l, JsonElement e)
    {
        if (!e.TryGetProperty("competitions", out var comps) || comps.ValueKind != JsonValueKind.Array || comps.GetArrayLength() == 0) return null;
        // a race weekend has one competition per session: the live one, else the race, else the last one
        var list = comps.EnumerateArray().ToList();
        JsonElement comp = list[0];
        string session = "";
        if (l.Racing)
        {
            comp = list.FirstOrDefault(c => State(c) == EventState.Live) is { ValueKind: JsonValueKind.Object } live ? live
                : list.FirstOrDefault(c => Str(c, "type", "abbreviation") == "Race") is { ValueKind: JsonValueKind.Object } race ? race
                : list[^1];
            session = Str(comp, "type", "abbreviation");
        }
        var status = comp.TryGetProperty("status", out var cs) ? cs : e.TryGetProperty("status", out var es) ? es : default;
        var state = State(comp);
        var competitors = new List<Competitor>();
        if (comp.TryGetProperty("competitors", out var cps) && cps.ValueKind == JsonValueKind.Array)
            foreach (var c in cps.EnumerateArray())
            {
                var who = c.TryGetProperty("team", out var t) ? t : c.TryGetProperty("athlete", out var a) ? a : default;
                if (who.ValueKind != JsonValueKind.Object) continue;
                int? score = int.TryParse(Str(c, "score"), out var sc) ? sc : null;
                competitors.Add(new Competitor(Str(who, "id"), Str(who, "displayName"), Str(who, "shortDisplayName"), Str(who, "abbreviation"),
                    score, Str(who, "logo"), Str(c, "homeAway") == "home", int.TryParse(Str(c, "order"), out var o) ? o : 99));
            }
        if (l.Racing) competitors = competitors.OrderBy(c => c.Order).ToList();
        DateTime.TryParse(Str(comp, "date") is { Length: > 0 } cd ? cd : Str(e, "date"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start);
        return new SportEvent(Str(e, "id"), l, Str(e, "name"), start.ToLocalTime(), state,
            Str(status, "type", "shortDetail"), Str(status, "displayClock"), competitors, session);
    }

    private static EventState State(JsonElement comp) =>
        (comp.TryGetProperty("status", out var s) ? Str(s, "type", "state") : "") switch
        {
            "in" => EventState.Live,
            "post" => EventState.Post,
            _ => EventState.Pre,
        };

    private static string Str(JsonElement e, params string[] path)
    {
        foreach (var k in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(k, out e)) return "";
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? "",
            JsonValueKind.Number => e.GetRawText(),
            _ => "",
        };
    }

    /// <summary>Upper case, no accents, letters and digits only (to match a team the user typed).</summary>
    public static string Fold(string s)
    {
        var b = new StringBuilder();
        foreach (char c in s.ToUpperInvariant().Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(c)) b.Append(c);
        return b.ToString();
    }

    /// <summary>Is this competitor one of the followed names (team or driver)? Whole name, short name or abbreviation.</summary>
    public static bool Follows(Competitor c, IEnumerable<string> names)
    {
        foreach (var n in names)
        {
            string f = Fold(n);
            if (f.Length == 0) continue;
            if (Fold(c.Name) == f || Fold(c.Short) == f || Fold(c.Abbrev) == f) return true;
            // a surname alone finds a driver ("Leclerc")
            if (f.Length >= 4 && Fold(c.Name).EndsWith(f)) return true;
        }
        return false;
    }
}
