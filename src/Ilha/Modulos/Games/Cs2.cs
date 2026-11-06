using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Ilha.Games;

/// <summary>
/// Reads CS2's Game State Integration payloads (what the game POSTs to us) into a match. Pure logic: the self test
/// feeds it recorded payloads. Our side comes from the payloads where the player is the local one (while dead the game
/// sends whoever is being spectated); scores follow the team, so after the half our side's score is still ours.
/// </summary>
internal sealed class Cs2State
{
    private Match? _match;
    private string _side = "";
    private int _kills = -1, _deaths = -1;
    private int _lastRoundWon = -1, _aceRound = -1;
    private bool _ended, _warmup;

    public Match? Current => _match;

    public static string ModeName(string mode) => mode switch
    {
        "competitive" => Lang.T("Competitivo", "Competitive"),
        "premier" => "Premier",
        "casual" => "Casual",
        "deathmatch" => Lang.T("Mata-mata", "Deathmatch"),
        "scrimcomp2v2" => "Wingman",
        "gungameprogressive" => Lang.T("Corrida armamentista", "Arms Race"),
        "gungametrbomb" => Lang.T("Demolição", "Demolition"),
        "training" => Lang.T("Treino", "Training"),
        "survival" => "Danger Zone",
        "" => "",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(mode.Replace('_', ' ')),
    };

    private static readonly Dictionary<string, string> Maps = new()
    {
        ["dust2"] = "Dust II", ["mirage"] = "Mirage", ["inferno"] = "Inferno", ["nuke"] = "Nuke", ["ancient"] = "Ancient",
        ["anubis"] = "Anubis", ["vertigo"] = "Vertigo", ["overpass"] = "Overpass", ["train"] = "Train", ["office"] = "Office",
        ["italy"] = "Italy", ["cache"] = "Cache", ["cobblestone"] = "Cobblestone",
    };

    public static string MapName(string map)
    {
        string m = map;
        int i = m.IndexOf('_');
        if (i > 0 && i <= 3) m = m[(i + 1)..];
        return Maps.TryGetValue(m, out var n) ? n : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(m.Replace('_', ' '));
    }

    private static string Label(string side) => side == "CT" ? "CT" : Lang.T("TR", "T");

    public List<Moment> Apply(JsonElement p, DateTime now)
    {
        var moments = new List<Moment>();
        string me = Str(p, "provider", "steamid");
        bool hasMap = p.TryGetProperty("map", out var map) && map.ValueKind == JsonValueKind.Object;
        if (!hasMap)
        {
            // back in the menu: the match is over (with its result already recorded, or abandoned)
            if (_match != null && Str(p, "player", "activity") == "menu") Reset();
            return moments;
        }

        if (_match == null)
        {
            _match = new Match { Game = "cs2", Start = now };
            _kills = _deaths = -1;
            _lastRoundWon = _aceRound = -1;
            _ended = false;
            _side = "";
        }
        var m = _match;
        m.RawMode = Str(map, "mode");
        m.Mode = ModeName(m.RawMode);
        // Premier's value in the integration was not seen yet: anything that names it gets the badge
        m.Badge = m.RawMode.Contains("premier", StringComparison.OrdinalIgnoreCase) ? "premier"
            : string.Equals(m.RawMode, "rush", StringComparison.OrdinalIgnoreCase) ? "rush" : "";
        m.Map = MapName(Str(map, "name"));

        bool local = me.Length > 0 && Str(p, "player", "steamid") == me;
        if (local)
        {
            string team = Str(p, "player", "team");
            // 4.2.0: he still saw the wrong side on 04/11/2026; the log says what was read, the samples keep the payloads
            if (team is "CT" or "T" && team != _side) Log.Escrever($"jogos: cs2 lado {team} (do jogador local, antes {(_side.Length > 0 ? _side : "nenhum")})");
            if (team is "CT" or "T") _side = team;
            if (p.TryGetProperty("player", out var pl) && pl.TryGetProperty("match_stats", out var st))
            {
                int k = Int(st, "kills") ?? 0, d = Int(st, "deaths") ?? 0;
                // stats reset when warmup ends: that is not a kill or a death; a jump of many at once is a catch-up
                // (payloads that did not arrive), not kills happening now
                if (_kills >= 0 && k > _kills && k - _kills <= 3) for (int i = _kills; i < k; i++) moments.Add(new Moment(MomentKind.Kill));
                if (_deaths >= 0 && d > _deaths) moments.Add(new Moment(MomentKind.Death));
                _kills = k;
                _deaths = d;
                m.Kills = k;
                m.Deaths = d;
                m.Assists = Int(st, "assists");
                m.Extras.Clear();
                if (Int(st, "mvps") is int mvps) { m.Extras.Add(("MVPs", mvps.ToString())); m.MvpStars = mvps; }
                if (Int(st, "score") is int score) m.Extras.Add((Lang.T("Pontos", "Score"), score.ToString()));
            }
            // an ace: the whole enemy team in one round (5 in competitive and Premier, 2 in Wingman), once per round
            int needed = Str(map, "mode") switch { "competitive" or "premier" => 5, "scrimcomp2v2" => 2, _ => 0 };
            int roundNow = Int(map, "round") ?? 0;
            if (needed > 0 && (Int(p, "player", "state", "round_kills") ?? 0) >= needed && roundNow != _aceRound && Str(map, "phase") != "warmup")
            {
                _aceRound = roundNow;
                moments.Add(new Moment(MomentKind.Ace));
            }
        }

        int ct = Int(map, "team_ct", "score") ?? 0, t = Int(map, "team_t", "score") ?? 0;
        string side = _side.Length > 0 ? _side : "CT";
        string other = side == "CT" ? "T" : "CT";
        m.Us = side == "CT" ? ct : t;
        m.Them = side == "CT" ? t : ct;
        m.UsLabel = Label(side);
        m.ThemLabel = Label(other);
        m.UsColor = side == "CT" ? TeamColors.Ct : TeamColors.T;
        m.ThemColor = side == "CT" ? TeamColors.T : TeamColors.Ct;
        m.UsArt = side == "CT" ? "cs2:ct" : "cs2:t";
        m.ThemArt = side == "CT" ? "cs2:t" : "cs2:ct";

        string phase = Str(map, "phase");
        int round = Int(map, "round") ?? 0;
        m.Stage = phase switch
        {
            "warmup" => Lang.T("Aquecimento", "Warmup"),
            "intermission" => Lang.T("Intervalo", "Halftime"),
            "gameover" => Lang.T("Fim de jogo", "Game over"),
            _ => Lang.T($"Rodada {round + 1}", $"Round {round + 1}"),
        };
        if (Str(p, "round", "phase") == "over" && Str(p, "round", "win_team") is { Length: > 0 } win && round != _lastRoundWon && phase != "warmup")
        {
            _lastRoundWon = round;
            moments.Add(new Moment(MomentKind.Round, Ours: win == side));
        }

        // the match time, counted by the island: the real game did not send its round countdown (phase_countdowns) on
        // 03/11/2026, and a round timer jumping between phases is not the match's time anyway. It restarts when
        // warmup ends.
        if (m.Clock == null || (_warmup && phase != "warmup"))
        {
            m.Clock = 0;
            m.ClockRead = now;
            m.ClockUp = true;
        }
        _warmup = phase == "warmup";
        if (p.TryGetProperty("phase_countdowns", out var pc) && Str(pc, "phase") == "bomb") m.Stage = Lang.T("Bomba plantada", "Bomb planted");

        if (phase == "gameover" && !_ended)
        {
            _ended = true;
            m.Outcome = m.Us > m.Them ? Outcome.Win : m.Us < m.Them ? Outcome.Loss : Outcome.Draw;
            // deathmatch and arms race have no team score worth a result
            if (Str(map, "mode") is "deathmatch" or "gungameprogressive" or "training") m.Outcome = null;
            moments.Add(new Moment(MomentKind.End, Outcome: m.Outcome));
        }
        return moments;
    }

    public void Reset()
    {
        _match = null;
        _side = "";
    }

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

    private static int? Int(JsonElement e, params string[] path)
    {
        foreach (var k in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(k, out e)) return null;
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : null;
    }
}

/// <summary>
/// Samples of what CS2 really sends (4.2.0): he saw the wrong side and the code reads right on recorded payloads, so the
/// next matches leave evidence in ~/.ilha/jogos/cs2-amostras.jsonl: the first payload of each match, every payload where
/// the player block changes who or which team it is (local or spectated, CT or T), and the last 20 when the match goes.
/// One JSON per line ({"quando", "tipo", "payload"}); over 200 KB the older half goes. It stays on this PC (it has his
/// Steam id and name).
/// </summary>
internal sealed class Cs2Samples
{
    private const long Max = 200_000;
    private readonly string _file;
    private readonly Queue<string> _recent = new();
    private string _lastKey = "";
    private bool _inMatch;

    public Cs2Samples(string file) => _file = file;

    public void Feed(JsonElement root, byte[] body)
    {
        string json = Encoding.UTF8.GetString(body);
        bool hasMap = root.TryGetProperty("map", out var map) && map.ValueKind == JsonValueKind.Object;
        string me = Get(root, "provider", "steamid"), who = Get(root, "player", "steamid");
        string key = $"{(who.Length > 0 && who == me ? "local" : "outro")}|{Get(root, "player", "team")}|{Get(root, "player", "activity")}";
        if (hasMap && !_inMatch) { _inMatch = true; _recent.Clear(); Write("inicio", json); }
        else if (hasMap && key != _lastKey) Write("jogador " + key, json);
        _lastKey = key;
        _recent.Enqueue(json);
        while (_recent.Count > 20) _recent.Dequeue();
        if (!hasMap && _inMatch)
        {
            _inMatch = false;
            foreach (var r in _recent) Write("fim", r);
            _recent.Clear();
        }
    }

    private static string Get(JsonElement e, params string[] path)
    {
        foreach (var k in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(k, out e)) return "";
        return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
    }

    private void Write(string kind, string payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        string line = "{\"quando\":" + JsonSerializer.Serialize(DateTime.Now.ToString("O")) + ",\"tipo\":" + JsonSerializer.Serialize(kind)
            + ",\"payload\":" + (payload.TrimStart().StartsWith('{') ? payload.Replace("\r", "").Replace("\n", "") : JsonSerializer.Serialize(payload)) + "}\n";
        File.AppendAllText(_file, line, new UTF8Encoding(false));
        var fi = new FileInfo(_file);
        if (fi.Length > Max)
        {
            var lines = File.ReadAllLines(_file);
            File.WriteAllLines(_file, lines.Skip(lines.Length / 2), new UTF8Encoding(false));
        }
    }
}

/// <summary>
/// The local endpoint CS2 posts to (127.0.0.1 only). A plain socket and a minimal HTTP reader: http.sys
/// (HttpListener) would need a URL reservation.
/// </summary>
internal sealed class Cs2Source : GameSource
{
    private readonly Cs2State _state = new();
    private readonly TcpListener _listener;
    private readonly Thread _thread;
    private volatile bool _stop;
    private DateTime _lastPost;
    private readonly Timer _idle;

    public override string Game => "cs2";
    public override string Process => "cs2";

    public override void Drop()
    {
        lock (_state) _state.Reset();
    }

    public Cs2Source(int port, Action<Action> dispatch) : base(dispatch)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _thread = new Thread(Loop) { IsBackground = true, Name = "ilha-cs2" };
        _thread.Start();
        // the game closed without going back to the menu: no post for a while ends the match
        _idle = new Timer(_ => CheckIdle(), null, 15_000, 15_000);
    }

    private void Loop()
    {
        while (!_stop)
        {
            TcpClient c;
            try { c = _listener.AcceptTcpClient(); }
            catch (Exception) { if (_stop) return; continue; }
            try { using (c) Serve(c); }
            catch (Exception e) { Log.Escrever("jogos: cs2: " + e.Message); }
        }
    }

    private void Serve(TcpClient c)
    {
        c.ReceiveTimeout = 3000;
        var s = c.GetStream();
        var head = new StringBuilder();
        int b, length = 0;
        // headers up to the blank line
        while ((b = s.ReadByte()) >= 0)
        {
            head.Append((char)b);
            if (head.Length >= 4 && head[^1] == '\n' && head.ToString().EndsWith("\r\n\r\n")) break;
            if (head.Length > 16_384) return;
        }
        foreach (var line in head.ToString().Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[15..].Trim(), out length);
        if (length <= 0 || length > 4_000_000) return;
        var body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = s.Read(body, read, length - read);
            if (n <= 0) break;
            read += n;
        }
        var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        s.Write(ok, 0, ok.Length);
        if (read < length) return;
        Receive(body);
    }

    private void Receive(byte[] body)
    {
        using var d = JsonDocument.Parse(body);
        var root = d.RootElement;
        if (root.TryGetProperty("auth", out var a) && a.TryGetProperty("token", out var t) && t.GetString() != GameSetup.Cs2Token) return;
        _lastPost = DateTime.UtcNow;
        try { _samples.Feed(root, body); }
        catch (Exception e) { Log.Escrever("jogos: cs2 amostras: " + e.Message); }
        List<Moment> moments;
        Match? m;
        lock (_state)
        {
            moments = _state.Apply(root, DateTime.Now);
            m = _state.Current;
        }
        Publish(m, moments);
    }

    private readonly Cs2Samples _samples = new(Path.Combine(Config.Pasta, "jogos", "cs2-amostras.jsonl"));

    private void CheckIdle()
    {
        lock (_state)
        {
            if (_state.Current == null || DateTime.UtcNow - _lastPost < TimeSpan.FromSeconds(40)) return;
            _state.Reset();
        }
        Publish(null, Array.Empty<Moment>());
    }

    public override void Dispose()
    {
        _stop = true;
        _idle.Dispose();
        try { _listener.Stop(); } catch (Exception) { }
    }
}
