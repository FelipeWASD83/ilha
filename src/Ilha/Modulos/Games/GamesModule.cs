using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Ilha.Games;

/// <summary>
/// The live score of competitive games ("placar" in `modoCompetitivo`, the default): CS2 through its Game State
/// Integration, Rocket League through its Stats API and Battlefield 6 by reading its HUD off the screen. The pill shows
/// the score while a match is on (over the game too: it is the one thing allowed there), with an animation for a kill,
/// a goal and the end; finished matches go to ~/.ilha/partidas.json for the card's last five.
/// </summary>
[Modulo("jogos", "Placar dos jogos", English = "Game score")]
internal sealed partial class GamesModule : Modulo
{
    private ConfigJogos _cfg = new();
    private ScoreActivity? _activity;
    private History? _history;
    private readonly List<GameSource> _sources = new();
    private readonly Dictionary<string, (Match M, DateTime At)> _matches = new();
    private readonly System.Windows.Forms.Timer _linger = new() { Interval = 9000 };
    // only while a match is on: is its game still running? (closed mid-match, the match goes at once)
    private readonly System.Windows.Forms.Timer _alive = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer _minute = new() { Interval = 60_000 };
    private TrophyStore _trophies = null!;
    private OwnClock _clock = null!;
    private TrophyRoom? _room;
    private int _minutes;
    private Dictionary<int, int> _steam = new();
    private DateTime _steamRead;
    // the Steam session going on now: hours at its start and the minutes counted since (see Playtime.WithLive)
    private readonly Dictionary<int, (int Baseline, int Extra)> _live = new();
    private int _running;

    /// <summary>Every game with hours: Steam's (read at most every 5 minutes) and the island's own clock.</summary>
    private List<GameHours> Hours()
    {
        if (DateTime.Now - _steamRead > TimeSpan.FromMinutes(5))
        {
            _steam = Playtime.Steam();
            _steamRead = DateTime.Now;
        }
        var known = new[] { "cs2", "bf6", "rl", "dota2", "deadlock", "tslgame", "r5apex", "rainbowsix", "marvelrivals-win64-shipping" }
            .Where(k => Playtime.SteamApp(k) != null).ToDictionary(k => Playtime.SteamApp(k)!.Value, k => k);
        var list = Playtime.WithLive(_steam, _live, _running).Select(kv =>
        {
            string key = known.TryGetValue(kv.Key, out var k) ? k : "steam:" + kv.Key;
            string name = SteamNames.Get(kv.Key) ?? (known.ContainsKey(kv.Key) ? Match.NameOf(key) : $"Steam {kv.Key}");
            return new GameHours(key, name, kv.Value, kv.Key);
        }).ToList();
        foreach (var (key, min) in _clock.Minutes)
        {
            var same = list.FindIndex(g => g.Key == key);
            if (same >= 0) list[same] = list[same] with { Minutes = list[same].Minutes + min };
            else list.Add(new GameHours(key, Match.NameOf(key), min, null));
        }
        return list;
    }

    /// <summary>New achievements become notices (three at most, then one that sums the rest).</summary>
    private void CheckTrophies(bool announce)
    {
        if (_stopped) return;
        var hours = Hours();
        // names of games with at least 10 hours that are not installed come from Steam's store, a few at a time
        SteamNames.Fetch(hours.Where(g => g.SteamApp != null && g.Minutes >= 600).Select(g => g.SteamApp!.Value), () => S.NaInterface(() => _room?.Changed()));
        var fresh = _trophies.Update(Trophies.Earned(hours));
        _room?.Changed();
        if (!announce || fresh.Count == 0) return;
        Log.Escrever($"jogos: {fresh.Count} conquista(s) nova(s): {string.Join(", ", fresh.Select(t => t.Id))}");
        foreach (var t in fresh.OrderByDescending(t => t.Hours).Take(3))
            S.Ilha.Alertar(new Alerta
            {
                Tipo = "conquista", Criar = p => Celebrations.Trophy(p, t), Segundos = 4f, Som = _cfg.Sons ? "conquista" : "",
                // unlocked while playing: it shows over the game too, like the score (3.5.0, his request)
                Importante = true, PermitidoNoCompetitivo = true, Dono = _room,
            });
    }
    private Alerta? _killAlert;
    private DateTime _lastKill;
    private int _streak;
    private string _shown = "";
    private bool _stopped;

    public override Type? TipoConfig => typeof(ConfigJogos);

    private bool ScoreMode => S.Config().ModoCompetitivo == ModosCompetitivos.Placar;

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigJogos>(cfg);
        _history = new History(Path.Combine(Config.Pasta, "partidas.json"));
        _activity = new ScoreActivity(S, g => _history.Last(g));
        S.Ilha.Adicionar(_activity);
        var overview = new GamesOverview(S, () => _activity.Ativa ? _activity.Current : null,
            () => { if (_activity.Ativa) S.Ilha.Expandir(_activity); }, g => _history.Last(g), () => _history.Games(), () => _history.Recent(5));
        S.Ilha.Adicionar(overview);
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "jogos", Nome = Lang.T("Jogos", "Games"), Ordem = 58,
            Abrir = overview.Open,
            Icone = (dc, p, r) => GamesOverview.Icon(dc, p.G.Fabrica, r, Cor.Branco),
            Detalhe = () => _activity.Ativa && _activity.Current is { Idle: false, HasScore: true } m ? $"{m.Us} : {m.Them}" : null,
        });
        // a finished match stays on the pill a few seconds with its result, then the pill goes back to normal
        _linger.Tick += (_, _) => { _linger.Stop(); Refresh(); };
        _alive.Tick += (_, _) => CheckAlive();

        // achievements: Steam's hours plus the island's own clock for games outside Steam
        _trophies = new TrophyStore(System.IO.Path.Combine(Config.Pasta, "conquistas.json"));
        _clock = new OwnClock(System.IO.Path.Combine(Config.Pasta, "tempo-de-jogo.json"));
        _room = new TrophyRoom(S, Hours, () => _trophies.Unlocked, () => CheckTrophies(announce: true));
        S.Ilha.Adicionar(_room);
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "conquistas", Nome = Lang.T("Conquistas", "Achievements"), Ordem = 62,
            Abrir = _room.Open,
            Icone = (dc, p, r) => TrophyRoom.MenuIcon(dc, p.G.Fabrica, r),
            Detalhe = () => _trophies.Unlocked.Count > 0 ? _trophies.Unlocked.Count.ToString() : null,
        });
        // once a minute: the island's clock counts the games outside Steam and the Steam game running now gets its
        // minute on top of the saved hours, so an achievement unlocks while playing; with a game running the trophies
        // are checked every minute, otherwise every 10 (Steam writes its hours when a game closes)
        _minute.Tick += (_, _) =>
        {
            bool playing = _clock.Tick();
            _running = Playtime.RunningSteamApp();
            if (_running > 0)
            {
                if (_live.TryGetValue(_running, out var s)) _live[_running] = (s.Baseline, s.Extra + 1);
                else _live[_running] = (_steam.TryGetValue(_running, out var saved) ? saved : 0, 1);
                playing = true;
            }
            if (playing || ++_minutes % 10 == 0) CheckTrophies(announce: true);
        };
        _minute.Start();
        CheckTrophies(announce: true);
        StartSources();
        // for photos: ILHA_FOTO_MVP=1 shows a won match's ending with the MVP crown 5 s after opening
        if (Environment.GetEnvironmentVariable("ILHA_FOTO_MVP") == "1")
        {
            var demo = new System.Windows.Forms.Timer { Interval = 5000 };
            demo.Tick += (_, _) => { demo.Stop(); demo.Dispose(); Show("jogo-fim", p => Celebrations.End(p, Outcome.Win, 3, 1, mvp: true), 5f, ""); };
            demo.Start();
        }
        // opened (or restarted) with a game already in front: the pill shows it right away
        Contexto(S.ContextoAtual());
        if (_cfg.InstalarIntegracoes) Task.Run(Install);
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        var old = _cfg;
        _cfg = Ler<ConfigJogos>(cfg);
        if (old.Cs2 != _cfg.Cs2 || old.RocketLeague != _cfg.RocketLeague || old.Bf6 != _cfg.Bf6 || old.PortaCs2 != _cfg.PortaCs2)
        {
            StopSources();
            StartSources();
        }
        Refresh();
    }

    private void Install()
    {
        if (_cfg.Cs2) Log.Escrever("jogos: integração do CS2: " + GameSetup.InstallCs2(_cfg.PortaCs2));
        if (_cfg.RocketLeague) Log.Escrever("jogos: Stats API do Rocket League: " + GameSetup.EnableRocketLeague());
    }

    private void StartSources()
    {
        Action<Action> ui = a => S.NaInterface(() => { if (!_stopped) a(); });
        if (_cfg.Cs2)
        {
            try { Add(new Cs2Source(_cfg.PortaCs2, ui)); }
            catch (Exception e) { Log.Escrever($"jogos: não abriu a porta {_cfg.PortaCs2} do CS2: {e.Message}"); }
        }
        if (_cfg.RocketLeague) Add(new RocketLeagueSource(_cfg.PortaRocketLeague, ui));
        if (_cfg.Bf6)
        {
            string? steam = null;
            Add(new Bf6Source(() => _cfg.NomeNoBf6.Length > 0 ? _cfg.NomeNoBf6 : steam ??= GameSetup.SteamName() ?? "",
                on => S.NaInterface(() => S.Ilha.ForaDasCapturas(on && !_stopped, "jogos")), ui));
        }
        var ctx = S.ContextoAtual();
        foreach (var s in _sources) s.Foreground(ctx.Processo);
    }

    private void Add(GameSource s)
    {
        s.Changed += m => OnChanged(s.Game, m);
        s.Happened += x => OnMoment(s.Game, x);
        _sources.Add(s);
    }

    private void StopSources()
    {
        foreach (var s in _sources) s.Dispose();
        _sources.Clear();
        _matches.Clear();
        S.Ilha.ForaDasCapturas(false, "jogos");
    }

    public override void Contexto(Contexto ctx)
    {
        foreach (var s in _sources) s.Foreground(ctx.Processo);
        // a competitive game in front: the pill stays as that game even with no match read (menus, queue, loading),
        // instead of the island vanishing; since when it counts from the first time it came to the front
        var now = DateTime.Now;
        if (_front.Length > 0 && _inFront.TryGetValue(_front, out var left)) _inFront[_front] = (left.Since, now);
        if (ctx.Jogo.Length > 0)
        {
            // away for more than 10 minutes counts as a new session (the game was most likely closed)
            if (!_inFront.TryGetValue(ctx.Jogo, out var seen) || now - seen.Last > TimeSpan.FromMinutes(10)) _inFront[ctx.Jogo] = (now, now);
            // the .exe of the window in front, for the icon of a game the island has no source for
            GameArt.Remember(GameOf(ctx.Jogo), ctx.Janela);
        }
        _front = ctx.Jogo;
        Refresh();
    }

    private string _front = "";
    private readonly Dictionary<string, (DateTime Since, DateTime Last)> _inFront = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The game's id for an .exe in the competitive list ("cs2", "rl", "bf6"; the name without .exe for others).</summary>
    internal static string GameOf(string exe) => exe.ToLowerInvariant() switch
    {
        "cs2.exe" => "cs2",
        "rocketleague.exe" => "rl",
        "bf6.exe" => "bf6",
        var x => x.EndsWith(".exe") ? x[..^4] : x,
    };

    /// <summary>The pill with the game in front and no match: its mark, its name and how long it has been open.</summary>
    private Match? Idle()
    {
        if (_front.Length == 0) return null;
        string game = GameOf(_front);
        var since = _inFront.TryGetValue(_front, out var t) ? t.Since : DateTime.Now;
        return new Match
        {
            Game = game,
            Idle = true,
            Exe = _front,
            Start = since,
            Clock = (DateTime.Now - since).TotalSeconds,
            ClockRead = DateTime.Now,
            ClockUp = true,
            Stage = Lang.T("Fora de partida", "Not in a match"),
        };
    }

    private void OnChanged(string game, Match? m)
    {
        if (m == null)
        {
            // gone: an ended match lingers with its result (see End), an abandoned one leaves now
            if (!(_matches.TryGetValue(game, out var last) && last.M.Outcome != null)) _matches.Remove(game);
        }
        else
        {
            _matches[game] = (m, DateTime.Now);
            _lastSeen[game] = m;
        }
        Refresh();
    }

    // the newest match of each game, kept after its source lets go of it (the end comes right after, for BF6)
    private readonly Dictionary<string, Match> _lastSeen = new();

    /// <summary>Shows the match that changed last (only one game runs at a time in practice).</summary>
    private void Refresh()
    {
        if (_activity == null) return;
        var now = DateTime.Now;
        foreach (var k in _matches.Where(x => x.Value.M.Outcome != null && now - x.Value.At > TimeSpan.FromSeconds(8)).Select(x => x.Key).ToList())
            _matches.Remove(k);
        var pick = _matches.OrderByDescending(x => x.Value.At).Select(x => x.Value.M).FirstOrDefault();
        _activity.Show(ScoreMode ? pick ?? Idle() : null);
        bool live = _matches.Values.Any(x => x.M.Outcome == null);
        if (live && !_alive.Enabled) _alive.Start();
        else if (!live) _alive.Stop();
        string shown = pick == null ? "" : $"{pick.Game}|{pick.Mode}|{pick.Map}";
        if (shown != _shown)
        {
            _shown = shown;
            // the raw mode too: what Premier sends was not seen yet
            Log.Escrever(pick == null ? "jogos: sem partida na pílula"
                : $"jogos: partida de {pick.Game} na pílula ({pick.Mode}, {pick.Map}{(pick.RawMode.Length > 0 ? ", modo bruto " + pick.RawMode : "")})");
        }
    }

    /// <summary>
    /// The game of a live match closed (by the process list, the same way the island names the window in front: no
    /// handle is opened on the game): the match goes now, with no result.
    /// </summary>
    private void CheckAlive()
    {
        if (_stopped) return;
        foreach (var s in _sources)
        {
            if (!_matches.TryGetValue(s.Game, out var v) || v.M.Outcome != null || Running(s.Process)) continue;
            s.Drop();
            _matches.Remove(s.Game);
            _lastSeen.Remove(s.Game);
            Log.Escrever($"jogos: {s.Game} fechou no meio da partida");
        }
        Refresh();
    }

    internal static bool Running(string process)
    {
        var list = System.Diagnostics.Process.GetProcessesByName(process);
        foreach (var p in list) p.Dispose();
        return list.Length > 0;
    }

    /// <summary>Every moment of every game, on the interface thread, whatever the score mode (the clips listen).</summary>
    internal static event Action<string, Moment>? MomentHappened;

    private void OnMoment(string game, Moment x)
    {
        if (_stopped) return;
        try { MomentHappened?.Invoke(game, x); }
        catch (Exception e) { Log.Escrever("jogos: quem ouve os lances: " + e.Message); }
        if (_activity == null || !ScoreMode) return;
        var m = _lastSeen.TryGetValue(game, out var seen) ? seen : null;
        switch (x.Kind)
        {
            case MomentKind.Kill when _cfg.Animacoes:
                var now = DateTime.Now;
                _streak = now - _lastKill < TimeSpan.FromSeconds(5) ? _streak + 1 : 1;
                _lastKill = now;
                // a new kill replaces the one on screen (a fast double kill becomes "x2", not a queue)
                if (_killAlert != null) S.Ilha.FecharAlerta(_killAlert);
                int streak = _streak;
                _killAlert = Show("jogo-abate", p => Celebrations.Kill(p, streak), 1.6f, _cfg.Sons ? "abate" : "");
                break;
            case MomentKind.Ace when _cfg.Animacoes:
                // the ace takes over the last kill's notice
                if (_killAlert != null) S.Ilha.FecharAlerta(_killAlert);
                _killAlert = null;
                Show("jogo-ace", p => Celebrations.Ace(p), 3.4f, _cfg.Sons ? "ace" : "");
                break;
            case MomentKind.Goal when _cfg.Animacoes:
                var color = x.Ours ? m?.UsColor ?? TeamColors.Blue : m?.ThemColor ?? TeamColors.Orange;
                bool ours = x.Ours;
                Show("jogo-gol", p => Celebrations.Goal(p, ours, color), ours ? 3.2f : 1.6f, ours && _cfg.Sons ? "gol" : "");
                break;
            case MomentKind.EpicSave when _cfg.Animacoes:
                var team = m?.UsColor ?? TeamColors.Blue;
                Show("jogo-defesa", p => Celebrations.Save(p, team), 2.8f, _cfg.Sons ? "gol" : "");
                break;
            case MomentKind.End:
                if (m != null && x.Outcome is Outcome o)
                {
                    m.Outcome = o;
                    m.Stage = Lang.T("Fim de jogo", "Game over");
                    _history?.Add(m);
                    // the final score stays on the pill a few seconds
                    _matches[game] = (m, DateTime.Now);
                    _linger.Stop();
                    _linger.Start();
                    Refresh();
                    Log.Escrever($"jogos: fim de partida de {m.Game}: {Record.Text(o)} {m.Us}:{m.Them}");
                    if (_cfg.Animacoes)
                    {
                        int? us = m.Us, them = m.Them;
                        bool mvp = m.Mvp == true;
                        string sound = !_cfg.Sons ? "" : o == Outcome.Win ? "vitoria" : o == Outcome.Loss ? "derrota" : "";
                        Show("jogo-fim", p => Celebrations.End(p, o, us, them, mvp), mvp ? 5f : 4f, sound);
                    }
                }
                break;
        }
    }

    private Alerta Show(string kind, Func<Palco, Cena> create, float seconds, string sound)
    {
        var a = new Alerta
        {
            Tipo = kind,
            Criar = create,
            Segundos = seconds,
            Som = sound,
            // over the game is the point of this mode, and Do Not Disturb reads "on" all the time on his PC
            PermitidoNoCompetitivo = true,
            Importante = true,
            Dono = _activity,
        };
        S.Ilha.Alertar(a);
        return a;
    }

    public override void Parar()
    {
        _stopped = true;
        _linger.Stop();
        _linger.Dispose();
        _alive.Stop();
        _alive.Dispose();
        _minute.Stop();
        _minute.Dispose();
        StopSources();
    }
}

public sealed class ConfigJogos
{
    [Label("Counter-Strike 2", "Counter-Strike 2")]
    [Help("Pela integração oficial do jogo (Game State Integration).", "Through the game's official Game State Integration.")]
    [JsonPropertyName("cs2")] public bool Cs2 { get; set; } = true;

    [Label("Rocket League", "Rocket League")]
    [Help("Pela Stats API oficial do jogo.", "Through the game's official Stats API.")]
    [JsonPropertyName("rocketLeague")] public bool RocketLeague { get; set; } = true;

    [Label("Battlefield 6", "Battlefield 6")]
    [Help("Lendo o placar na tela (o jogo não tem integração). Só a Conquista está calibrada.",
        "Reading the score off the screen (the game has no integration). Only Conquest is calibrated.")]
    [JsonPropertyName("bf6")] public bool Bf6 { get; set; } = true;

    [Label("Ligar as integrações nos jogos", "Switch the integrations on in the games")]
    [Help("Põe o arquivo da integração na pasta do CS2 e liga a Stats API no Rocket League. Vale ao abrir o jogo de novo.",
        "Puts the integration file in CS2's folder and turns the Stats API on in Rocket League. Takes effect when the game opens again.")]
    [JsonPropertyName("instalarIntegracoes")] public bool InstalarIntegracoes { get; set; } = true;

    [Label("Seu nome no BF6", "Your name in BF6")]
    [Help("Para achar seus abates no feed. Vazio = o seu nome na Steam.", "To find your kills in the feed. Empty = your Steam name.")]
    [JsonPropertyName("nomeNoBf6")] public string NomeNoBf6 { get; set; } = "";

    [Label("Animações", "Animations")]
    [Help("Caveira no abate, GOOOOL no gol, GG ou EZ no fim.", "Skull on a kill, GOOOOL on a goal, GG or EZ at the end.")]
    [JsonPropertyName("animacoes")] public bool Animacoes { get; set; } = true;

    [Label("Sons de abate, gol e fim de partida", "Sounds for kills, goals and the match end")]
    [JsonPropertyName("sons")] public bool Sons { get; set; } = true;

    [Label("Porta do CS2", "CS2 port")]
    [JsonPropertyName("portaCs2")] public int PortaCs2 { get; set; } = 47681;

    [Label("Porta da Stats API do Rocket League", "Rocket League Stats API port")]
    [JsonPropertyName("portaRocketLeague")] public int PortaRocketLeague { get; set; } = 49123;
}
