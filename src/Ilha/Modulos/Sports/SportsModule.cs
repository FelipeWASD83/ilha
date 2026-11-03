using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Ilha.Games;

namespace Ilha.Sports;

/// <summary>
/// Live scores of real sports from ESPN's open scoreboards: the leagues, teams and drivers he picks in the settings.
/// A followed team's game (or a race with a followed driver, or any race when no driver is picked) goes to the pill
/// while it is live, with the goal animation; the "Sports" menu card lists what is live, today's and the coming games
/// and the latest results. Asks ESPN every 30 s while a followed game is live, every 5 min on a day with games left,
/// every 15 min otherwise.
/// </summary>
[Modulo("esportes", "Esportes", English = "Sports")]
internal sealed partial class SportsModule : Modulo
{
    private ConfigEsportes _cfg = new();
    private SportsActivity? _pill;
    private SportsCard? _card;
    private readonly Dictionary<string, List<SportEvent>> _today = new();
    private List<SportEvent> _past = new(), _next = new();
    private DateTime _extraAt;
    private readonly Dictionary<string, (int? Home, int? Away)> _scores = new();
    private CancellationTokenSource? _stop;
    private readonly SemaphoreSlim _wake = new(0);
    private bool _stopped;

    public override Type? TipoConfig => typeof(ConfigEsportes);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigEsportes>(cfg);
        _pill = new SportsActivity(S, () => _cfg.Pilotos);
        S.Ilha.Adicionar(_pill);
        _card = new SportsCard(S, Sections, () => { _wake.Release(); LoadExtra(force: false); });
        S.Ilha.Adicionar(_card);
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "esportes", Nome = Lang.T("Esportes", "Sports"), Ordem = 61,
            Abrir = _card.Open,
            Icone = (dc, p, r) => SportsCard.Icon(dc, p.G.Fabrica, r, IconSport()),
            Detalhe = () => _pill?.Current is { League.Racing: false } e ? $"{e.Home?.Score ?? 0} : {e.Away?.Score ?? 0}"
                : Leagues().Sum(l => _today.TryGetValue(l.Key, out var v) ? v.Count(x => x.State == EventState.Live) : 0) is int n and > 0
                    ? Lang.T($"{n} ao vivo", $"{n} live") : null,
        });
        _stop = new CancellationTokenSource();
        _ = Loop(_stop.Token);
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigEsportes>(cfg);
        _extraAt = default;
        _wake.Release();
    }

    private List<League> Leagues() => _cfg.Ligas.Select(League.Find).OfType<League>().ToList();

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshToday(ct);
            // read on this thread while the UI thread may be filling the lists: a clash only means the default wait
            TimeSpan wait;
            try { wait = Interval(); } catch (Exception) { wait = TimeSpan.FromMinutes(5); }
            try { await _wake.WaitAsync(wait, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private TimeSpan Interval()
    {
        var all = _today.Values.SelectMany(x => x).ToList();
        if (all.Any(e => e.State == EventState.Live && Followed(e))) return TimeSpan.FromSeconds(30);
        if (all.Any(e => e.State == EventState.Live) || all.Any(e => e.State == EventState.Pre && e.Start - DateTime.Now < TimeSpan.FromMinutes(20)))
            return TimeSpan.FromMinutes(1);
        if (all.Any(e => e.State == EventState.Pre)) return TimeSpan.FromMinutes(5);
        return TimeSpan.FromMinutes(15);
    }

    private async Task RefreshToday(CancellationToken ct)
    {
        var got = new Dictionary<string, List<SportEvent>>();
        foreach (var l in Leagues())
        {
            try { got[l.Key] = await Espn.Fetch(l, null, ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { Log.Escrever($"esportes: {l.Key} não respondeu: {e.Message}"); }
        }
        S.NaInterface(() =>
        {
            if (_stopped) return;
            foreach (var (k, v) in got) _today[k] = v;
            foreach (var k in _today.Keys.Except(Leagues().Select(l => l.Key)).ToList()) _today.Remove(k);
            Update();
            _card?.Changed();
        });
    }

    /// <summary>The days around today, only when the card opens (and at most every 30 minutes).</summary>
    private void LoadExtra(bool force)
    {
        if (!force && DateTime.Now - _extraAt < TimeSpan.FromMinutes(30)) return;
        _extraAt = DateTime.Now;
        var leagues = Leagues();
        var ct = _stop?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            var past = new List<SportEvent>();
            var next = new List<SportEvent>();
            foreach (var l in leagues)
                foreach (int d in new[] { -2, -1, 1, 2, 3, 4, 5 })
                {
                    try
                    {
                        var list = await Espn.Fetch(l, DateTime.Today.AddDays(d), ct);
                        (d < 0 ? past : next).AddRange(list);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception) { }
                }
            S.NaInterface(() =>
            {
                if (_stopped) return;
                _past = past;
                _next = next;
                _card?.Changed();
            });
        }, ct);
    }

    private bool FollowsTeams => _cfg.Times.Any(t => t.Trim().Length > 0);

    /// <summary>A game with a followed team, or a race (a followed driver, or any race when none is picked).</summary>
    private bool Followed(SportEvent e)
    {
        if (e.League.Racing) return _cfg.Pilotos.Count == 0 || e.Competitors.Any(c => Espn.Follows(c, _cfg.Pilotos));
        return e.Competitors.Any(c => Espn.Follows(c, _cfg.Times));
    }

    private void Update()
    {
        var live = _today.Values.SelectMany(x => x).Where(e => e.State == EventState.Live && Followed(e))
            // a race session only goes to the pill when it is the race, the sprint or qualifying
            .Where(e => !e.League.Racing || e.Session is "Race" or "Sprint" or "SR" or "Qual")
            .OrderBy(e => e.League.Racing).ToList();
        var show = live.FirstOrDefault();
        foreach (var e in live.Where(e => e.League.Sport == "soccer")) Goals(e);
        if (show?.Id != _pill?.Current?.Id)
            Log.Escrever(show == null ? "esportes: nenhum jogo seguido ao vivo" : $"esportes: na pílula {show.Name} ({show.League.Key}, {SportDraw.Status(show)})");
        _pill?.Show(show);
        S.Menu.Redesenhar();
    }

    /// <summary>A score that went up on a followed game: ours (the followed team scored) gets the full goal animation.</summary>
    private void Goals(SportEvent e)
    {
        var now = (e.Home?.Score, e.Away?.Score);
        if (_scores.TryGetValue(e.Id, out var before) && _cfg.Animacoes)
        {
            bool homeFollowed = e.Home != null && Espn.Follows(e.Home, _cfg.Times);
            if ((now.Item1 ?? 0) > (before.Home ?? 0)) Goal(homeFollowed);
            if ((now.Item2 ?? 0) > (before.Away ?? 0)) Goal(!homeFollowed);
        }
        _scores[e.Id] = now;
    }

    private void Goal(bool ours)
    {
        var color = ours ? TeamColors.Win : TeamColors.Orange;
        S.Ilha.Alertar(new Alerta
        {
            Tipo = "esporte-gol",
            Criar = p => Celebrations.Goal(p, ours, color),
            Segundos = ours ? 3.2f : 1.6f,
            Som = ours && _cfg.Sons ? "gol" : "",
            Importante = true,
            Dono = _pill,
        });
    }

    /// <summary>The sport of the menu icon: a live game (a followed one first), else the next one today.</summary>
    private string IconSport()
    {
        var today = _today.Values.SelectMany(x => x).ToList();
        var pick = today.Where(e => e.State == EventState.Live).OrderByDescending(Followed).FirstOrDefault()
            ?? today.Where(e => e.State == EventState.Pre && e.Start.Date == DateTime.Today && e.Start >= DateTime.Now.AddHours(-1))
                .OrderByDescending(Followed).ThenBy(e => e.Start).FirstOrDefault();
        return pick?.League.Sport ?? "soccer";
    }

    private List<(string, List<SportEvent>)> Sections()
    {
        var today = _today.Values.SelectMany(x => x).ToList();
        bool Mine(SportEvent e) => !FollowsTeams || e.League.Racing || Followed(e);
        var live = today.Where(e => e.State == EventState.Live).OrderByDescending(Followed).ToList();
        var later = today.Where(e => e.State == EventState.Pre).OrderBy(e => e.Start).ToList();
        var next = _next.Where(e => e.State == EventState.Pre && Mine(e)).OrderBy(e => e.Start).Take(6).ToList();
        var results = today.Where(e => e.State == EventState.Post).Concat(_past.Where(e => e.State == EventState.Post))
            .Where(Mine).OrderByDescending(Followed).ThenByDescending(e => e.Start).Take(6).ToList();
        return new List<(string, List<SportEvent>)>
        {
            (Lang.T("Ao vivo", "Live"), live),
            (Lang.T("Hoje", "Today"), later),
            (Lang.T("Próximos", "Coming up"), next),
            (Lang.T("Resultados", "Results"), results),
        };
    }

    public override void Parar()
    {
        _stopped = true;
        _stop?.Cancel();
        _pill?.Show(null);
    }
}

public sealed class ConfigEsportes
{
    [Label("Ligas", "Leagues")]
    [Help("Uma por linha: bra.1 (Série A), bra.2 (Série B), bra.copa_do_brazil, conmebol.libertadores, conmebol.sudamericana, " +
          "uefa.champions, eng.1 (Premier), esp.1 (La Liga), fifa.world (Copa do Mundo), f1, nba, nfl, atp e wta (tênis).",
        "One per line: bra.1 (Série A), bra.2 (Série B), bra.copa_do_brazil, conmebol.libertadores, conmebol.sudamericana, " +
        "uefa.champions, eng.1 (Premier), esp.1 (La Liga), fifa.world (World Cup), f1, nba, nfl, atp and wta (tennis).")]
    [JsonPropertyName("ligas")] public List<string> Ligas { get; set; } = new() { "bra.1", "bra.copa_do_brazil", "conmebol.libertadores", "f1" };

    [Label("Times que você segue", "Teams you follow")]
    [Help("Um por linha, como a ESPN escreve (Flamengo, Atlético-MG, Real Madrid; no tênis, o sobrenome do tenista). O jogo deles ao vivo vai para a pílula.",
        "One per line, as ESPN writes them (Flamengo, Atlético-MG, Real Madrid; for tennis, the player's surname). Their live game goes to the pill.")]
    [JsonPropertyName("times")] public List<string> Times { get; set; } = new();

    [Label("Pilotos que você segue", "Drivers you follow")]
    [Help("Sobrenome basta (Leclerc). Vazio = toda corrida ao vivo vai para a pílula com o líder.",
        "The surname is enough (Leclerc). Empty = every live race goes to the pill with the leader.")]
    [JsonPropertyName("pilotos")] public List<string> Pilotos { get; set; } = new();

    [Label("Animação de gol", "Goal animation")]
    [JsonPropertyName("animacoes")] public bool Animacoes { get; set; } = true;

    [Label("Som no gol do seu time", "Sound on your team's goal")]
    [JsonPropertyName("sons")] public bool Sons { get; set; } = true;
}
