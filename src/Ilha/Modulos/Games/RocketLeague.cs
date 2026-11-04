using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Ilha.Games;

/// <summary>
/// Reads Rocket League's Stats API messages into a match. Pure logic: the self test feeds it recorded messages.
/// "Data" comes as a JSON string inside the envelope (seen on 03/11/2026). We are the player whose PrimaryId is this
/// PC's Steam account ("Steam|&lt;id64&gt;|0"); without it, the first player the camera follows (Target). Our team is read
/// from our Players entry on every update (4.0.3: it was taken once from Target, whose TeamNum is 0 = blue while the
/// match loads, so he was always blue). A goal is a team score going up in UpdateState (the GoalScored event repeats
/// with an empty scorer in some modes, the score does not).
/// </summary>
internal sealed class RocketLeagueState
{
    private Match? _match;
    private string _me = "";
    private int _myTeam = -1;
    private string _guid = "";
    private string? _myId;

    /// <summary>This PC's Steam id (64-bit), asked once per match; null = find us by the camera's target.</summary>
    public Func<string?>? LocalSteamId;
    private int _blue = -1, _orange = -1, _demos = -1;
    private bool _ended;

    public Match? Current => _match;

    // the API gives only the number; from the community lists, plus 63 seen on 03/11/2026 (Heatseeker, ranked). 3.9.0
    // (his request): the ranked ones by team size and the game's Brazilian names (Rixa, Basquete, Dia de Neve), casual
    // Chaos as 4v4. Any other id is an event or a limited mode: no name on the pill (the id goes to the log).
    private int _unnamed = -1;

    public static string PlaylistName(int id) => id switch
    {
        1 => "1v1",
        2 => "2v2",
        3 => "3v3",
        4 => Lang.T("4v4 casual", "Casual 4v4"),
        6 => Lang.T("Partida particular", "Private match"),
        8 => Lang.T("Exibição", "Exhibition"),
        10 => Lang.T("1v1 ranqueado", "Ranked 1v1"),
        11 => Lang.T("2v2 ranqueado", "Ranked 2v2"),
        13 => Lang.T("3v3 ranqueado", "Ranked 3v3"),
        15 => Lang.T("Dia de Neve", "Snow Day"),
        17 => Lang.T("Basquete", "Hoops"),
        18 => Lang.T("Rixa", "Rumble"),
        22 or 34 => Lang.T("Torneio", "Tournament"),
        23 => "Dropshot",
        27 => Lang.T("Basquete ranqueado", "Ranked Hoops"),
        28 => Lang.T("Rixa ranqueada", "Ranked Rumble"),
        29 => Lang.T("Dropshot ranqueado", "Ranked Dropshot"),
        30 => Lang.T("Dia de Neve ranqueado", "Ranked Snow Day"),
        38 => "Heatseeker",
        41 => "Gridiron",
        43 => "Knockout",
        63 => Lang.T("Heatseeker ranqueado", "Ranked Heatseeker"),
        _ => "",
    };

    // free play, training and the like: one player, nothing to score
    private static bool Practice(int playlist, int players) => players < 2 || playlist is 9 or 19 or 52;

    private static readonly Dictionary<string, string> Arenas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Stadium"] = "DFH Stadium", ["cs"] = "Champions Field", ["EuroStadium"] = "Mannfield", ["Park"] = "Beckwith Park",
        ["TrainStation"] = "Urban Central", ["Utopia"] = "Utopia Coliseum", ["Wasteland"] = "Wasteland",
        ["NeoTokyo"] = "Neo Tokyo", ["UF"] = "Forbidden Temple", ["Underwater"] = "AquaDome", ["Farm"] = "Farmstead",
        ["beach"] = "Salty Shores", ["arc"] = "Starbase ARC", ["music"] = "Neon Fields", ["ChinaTown"] = "Forbidden Temple",
        ["street"] = "Sovereign Heights", ["outlaw"] = "Deadeye Canyon", ["woods"] = "Estadio Vida",
    };

    public static string ArenaName(string arena)
    {
        string a = arena;
        foreach (var suffix in new[] { "_P", "_Day_P", "_Night_P", "_Dusk_P" })
            if (a.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { a = a[..^suffix.Length]; break; }
        int i = a.IndexOf('_');
        string head = i > 0 ? a[..i] : a;
        return Arenas.TryGetValue(a, out var n) || Arenas.TryGetValue(head, out n) ? n : a.Replace('_', ' ');
    }

    public List<Moment> Apply(string ev, JsonElement data, DateTime now)
    {
        var moments = new List<Moment>();
        switch (ev)
        {
            case "UpdateState":
                Update(data, now, moments);
                break;
            case "MatchEnded":
                if (_match != null && !_ended && data.TryGetProperty("WinnerTeamNum", out var w) && w.TryGetInt32(out int winner))
                {
                    _ended = true;
                    _match.Outcome = winner == _myTeam ? Outcome.Win : Outcome.Loss;
                    _match.Mvp = _me.Length > 0 && Mvp(_scores, winner) is { } best ? best == _me : null;
                    _match.Stage = Lang.T("Fim de jogo", "Game over");
                    moments.Add(new Moment(MomentKind.End, Outcome: _match.Outcome));
                }
                break;
            case "StatfeedEvent":
                // an epic save of ours: the feed names who made it (MainTarget)
                if (_match != null && !_ended && Str(data, "EventName") == "EpicSave" && _me.Length > 0 && Str(data, "MainTarget", "Name") == _me)
                {
                    _epicSaves++;
                    SetEpicSaves(_match);
                    moments.Add(new Moment(MomentKind.EpicSave));
                }
                break;
            case "MatchDestroyed":
                // left the match (the result is already in, or it was abandoned)
                _match = null;
                _ended = false;
                break;
        }
        return moments;
    }

    private int _epicSaves;
    // everyone's name, team and score in the last update: the MVP is picked from them when the match ends
    private List<(string Name, int Team, int Score)> _scores = new();

    /// <summary>Rocket League's MVP: the best score of the winning team (null when nobody of that team is known).</summary>
    internal static string? Mvp(IEnumerable<(string Name, int Team, int Score)> scores, int winner) =>
        scores.Where(s => s.Team == winner).OrderByDescending(s => s.Score).Select(s => s.Name).FirstOrDefault();

    private void SetEpicSaves(Match m)
    {
        int i = m.Extras.FindIndex(e => e.Item1 == Lang.T("Def. épicas", "Epic saves"));
        if (i >= 0) m.Extras[i] = (m.Extras[i].Item1, _epicSaves.ToString());
    }

    private void Update(JsonElement d, DateTime now, List<Moment> moments)
    {
        if (!d.TryGetProperty("Game", out var g)) return;
        int playlist = g.TryGetProperty("PlaylistId", out var pid) && pid.TryGetInt32(out int pl) ? pl : 0;
        int players = d.TryGetProperty("Players", out var ps) && ps.ValueKind == JsonValueKind.Array ? ps.GetArrayLength() : 0;
        if (Practice(playlist, players)) { _match = null; return; }

        // a new MatchGuid is a new match even if MatchDestroyed never came (online and LAN matches only have one)
        string guid = Str(d, "MatchGuid");
        if (_match != null && guid.Length > 0 && _guid.Length > 0 && guid != _guid) _match = null;
        if (guid.Length > 0) _guid = guid;

        if (_match == null)
        {
            _match = new Match { Game = "rl", Start = now };
            _blue = _orange = _demos = -1;
            _epicSaves = 0;
            _scores = new();
            _me = "";
            _myTeam = -1;
            _myId = LocalSteamId?.Invoke();
            _ended = false;
        }
        var m = _match;
        if (ps.ValueKind == JsonValueKind.Array)
        {
            // us by the Steam account; else the camera's target, kept once known (it follows a teammate after a demo)
            if (_myId != null && ps.EnumerateArray().FirstOrDefault(p => Str(p, "PrimaryId").StartsWith($"Steam|{_myId}|", StringComparison.Ordinal)) is { ValueKind: JsonValueKind.Object } mine
                && Str(mine, "Name") is { Length: > 0 } myName)
                _me = myName;
            else if (_me.Length == 0 && Bool(g, "bHasTarget") && Str(g, "Target", "Name") is { Length: > 0 } target)
                _me = target;
            foreach (var p in ps.EnumerateArray())
                if (_me.Length > 0 && Str(p, "Name") == _me && Int(p, "TeamNum") is 0 or 1) { _myTeam = Int(p, "TeamNum")!.Value; break; }
        }
        int us = _myTeam == 1 ? 1 : 0;
        m.Mode = PlaylistName(playlist);
        if (m.Mode.Length == 0 && _unnamed != playlist) { _unnamed = playlist; Log.Escrever($"jogos: rocket league na playlist {playlist} (sem nome)"); }
        m.Map = ArenaName(Str(g, "Arena"));

        int blue = 0, orange = 0;
        string blueName = Lang.T("Azul", "Blue"), orangeName = Lang.T("Laranja", "Orange");
        if (g.TryGetProperty("Teams", out var teams) && teams.ValueKind == JsonValueKind.Array)
            foreach (var t in teams.EnumerateArray())
            {
                int num = Int(t, "TeamNum") ?? -1, score = Int(t, "Score") ?? 0;
                string name = Str(t, "Name");
                if (num == 0) { blue = score; if (name.Length > 0) blueName = name; }
                if (num == 1) { orange = score; if (name.Length > 0) orangeName = name; }
            }
        if (_blue >= 0 && blue > _blue) moments.Add(new Moment(MomentKind.Goal, Ours: us == 0));
        if (_orange >= 0 && orange > _orange) moments.Add(new Moment(MomentKind.Goal, Ours: us == 1));
        _blue = blue;
        _orange = orange;
        m.Us = us == 0 ? blue : orange;
        m.Them = us == 0 ? orange : blue;
        m.UsLabel = us == 0 ? blueName : orangeName;
        m.ThemLabel = us == 0 ? orangeName : blueName;
        m.UsColor = us == 0 ? TeamColors.Blue : TeamColors.Orange;
        m.ThemColor = us == 0 ? TeamColors.Orange : TeamColors.Blue;
        m.UsArt = us == 0 ? "rl:blue" : "rl:orange";
        m.ThemArt = us == 0 ? "rl:orange" : "rl:blue";

        if (ps.ValueKind == JsonValueKind.Array)
            _scores = ps.EnumerateArray().Select(p => (Str(p, "Name"), Int(p, "TeamNum") ?? -1, Int(p, "Score") ?? 0)).ToList();
        if (ps.ValueKind == JsonValueKind.Array)
            foreach (var p in ps.EnumerateArray())
            {
                if (Str(p, "Name") != _me || _me.Length == 0) continue;
                int demos = Int(p, "Demos") ?? 0;
                if (_demos >= 0 && demos > _demos && demos - _demos <= 3) for (int i = _demos; i < demos; i++) moments.Add(new Moment(MomentKind.Kill));
                _demos = demos;
                m.Kills = null;
                m.Extras.Clear();
                m.Extras.Add((Lang.T("Gols", "Goals"), (Int(p, "Goals") ?? 0).ToString()));
                m.Extras.Add((Lang.T("Assist.", "Assists"), (Int(p, "Assists") ?? 0).ToString()));
                m.Extras.Add((Lang.T("Defesas", "Saves"), (Int(p, "Saves") ?? 0).ToString()));
                m.Extras.Add((Lang.T("Def. épicas", "Epic saves"), _epicSaves.ToString()));
                m.Extras.Add(("Demos", demos.ToString()));
                m.Extras.Add((Lang.T("Pontos", "Score"), (Int(p, "Score") ?? 0).ToString()));
            }

        int secs = Int(g, "TimeSeconds") ?? 0;
        bool ot = Bool(g, "bOvertime");
        m.Clock = secs;
        m.ClockRead = now;
        m.ClockUp = ot;
        // the clock only runs while the ball is in play; a reading every packet keeps it honest
        m.Stage = _ended ? Lang.T("Fim de jogo", "Game over") : ot ? Lang.T("Prorrogação", "Overtime") : Bool(g, "bReplay") ? Lang.T("Replay do gol", "Goal replay") : "";
    }

    private static string Str(JsonElement e, params string[] path)
    {
        foreach (var k in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(k, out e)) return "";
        return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
    }

    private static int? Int(JsonElement e, params string[] path)
    {
        foreach (var k in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(k, out e)) return null;
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : null;
    }

    private static bool Bool(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Splits a stream of concatenated JSON objects; returns the complete ones and keeps the rest in `buffer`.</summary>
    public static List<string> Split(StringBuilder buffer)
    {
        var r = new List<string>();
        int depth = 0, start = -1;
        bool inString = false, escape = false;
        for (int i = 0; i < buffer.Length; i++)
        {
            char c = buffer[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') { if (depth++ == 0) start = i; }
            else if (c == '}' && depth > 0 && --depth == 0 && start >= 0)
            {
                r.Add(buffer.ToString(start, i - start + 1));
                buffer.Remove(0, i + 1);
                i = -1;
                start = -1;
            }
        }
        return r;
    }

    /// <summary>One message: the envelope with "Event" and "Data" (an object or a JSON string holding one).</summary>
    public List<Moment> ApplyMessage(string json, DateTime now)
    {
        using var env = JsonDocument.Parse(json);
        string ev = Str(env.RootElement, "Event");
        if (!env.RootElement.TryGetProperty("Data", out var data)) return new();
        if (data.ValueKind == JsonValueKind.String)
        {
            using var inner = JsonDocument.Parse(data.GetString() ?? "{}");
            return Apply(ev, inner.RootElement.Clone(), now);
        }
        return Apply(ev, data, now);
    }
}

/// <summary>Connects to the Stats API socket (127.0.0.1:49123) while the game runs; tries again every few seconds.</summary>
internal sealed class RocketLeagueSource : GameSource
{
    private readonly RocketLeagueState _state = new() { LocalSteamId = GameSetup.SteamId64 };
    private readonly Thread _thread;
    private volatile bool _stop;
    private TcpClient? _client;
    private readonly int _port;

    public override string Game => "rl";
    public override string Process => "RocketLeague";

    public override void Drop()
    {
        lock (_state) _state.Apply("MatchDestroyed", default, DateTime.Now);
    }

    public RocketLeagueSource(int port, Action<Action> dispatch) : base(dispatch)
    {
        _port = port;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ilha-rocket-league" };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stop)
        {
            try
            {
                using var c = new TcpClient();
                _client = c;
                c.Connect("127.0.0.1", _port);
                Log.Escrever("jogos: rocket league conectado na Stats API");
                var s = c.GetStream();
                var buf = new byte[65536];
                var text = new StringBuilder();
                var decoder = Encoding.UTF8.GetDecoder();
                var chars = new char[65536];
                int n;
                while (!_stop && (n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    int k = decoder.GetChars(buf, 0, n, chars, 0);
                    text.Append(chars, 0, k);
                    if (text.Length > 4_000_000) text.Clear();
                    foreach (var msg in RocketLeagueState.Split(text))
                    {
                        List<Moment> moments;
                        Match? m;
                        lock (_state)
                        {
                            try { moments = _state.ApplyMessage(msg, DateTime.Now); }
                            catch (JsonException) { continue; }
                            m = _state.Current;
                        }
                        Publish(m, moments);
                    }
                }
            }
            catch (Exception) { }
            finally { _client = null; }
            // the game closed or is not running: the match is gone
            bool had;
            lock (_state) { had = _state.Current != null; if (had) _state.Apply("MatchDestroyed", default, DateTime.Now); }
            if (had) Publish(null, Array.Empty<Moment>());
            for (int i = 0; i < 50 && !_stop; i++) Thread.Sleep(100);
        }
    }

    public override void Dispose()
    {
        _stop = true;
        try { _client?.Close(); } catch (Exception) { }
    }
}
