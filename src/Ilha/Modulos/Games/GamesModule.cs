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
        // a finished match stays on the pill a few seconds with its result, then the pill goes back to normal
        _linger.Tick += (_, _) => { _linger.Stop(); Refresh(); };
        StartSources();
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
        Refresh();
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
        _activity.Show(ScoreMode ? pick : null);
        string shown = pick == null ? "" : $"{pick.Game}|{pick.Mode}|{pick.Map}";
        if (shown != _shown)
        {
            _shown = shown;
            Log.Escrever(pick == null ? "jogos: sem partida na pílula" : $"jogos: partida de {pick.Game} na pílula ({pick.Mode}, {pick.Map})");
        }
    }

    private void OnMoment(string game, Moment x)
    {
        if (_stopped || _activity == null || !ScoreMode) return;
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
                _killAlert = Show("jogo-abate", p => Celebrations.Kill(p, streak), 1.4f, "");
                break;
            case MomentKind.Goal when _cfg.Animacoes:
                var color = x.Ours ? m?.UsColor ?? TeamColors.Blue : m?.ThemColor ?? TeamColors.Orange;
                bool ours = x.Ours;
                Show("jogo-gol", p => Celebrations.Goal(p, ours, color), ours ? 2.4f : 1.6f, ours && _cfg.Sons ? "gol" : "");
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
                        Show("jogo-fim", p => Celebrations.End(p, o, us, them), 4f, o == Outcome.Win && _cfg.Sons ? "vitoria" : "");
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

    [Label("Som no gol e na vitória", "Sound on goals and wins")]
    [JsonPropertyName("sons")] public bool Sons { get; set; } = true;

    [Label("Porta do CS2", "CS2 port")]
    [JsonPropertyName("portaCs2")] public int PortaCs2 { get; set; } = 47681;

    [Label("Porta da Stats API do Rocket League", "Rocket League Stats API port")]
    [JsonPropertyName("portaRocketLeague")] public int PortaRocketLeague { get; set; } = 49123;
}
