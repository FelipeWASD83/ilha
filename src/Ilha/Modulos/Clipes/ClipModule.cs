using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Ilha.Games;
using Ilha.Recorder;

namespace Ilha.Clips;

/// <summary>
/// Clips (3.7.0, his request): while a competitive game is in front the ring keeps the last seconds; a moment worth it
/// (a double kill or more, an ace, a win, our goal, an epic save) cuts the last `segundos` about 3 s after it (moments
/// close together become one clip), into a pending folder. When the match ends, the island asks: save or discard
/// (no answer keeps them). Ctrl+Alt+C cuts one right away in any competitive game, saved at once. The saved ones show
/// in the Games card.
/// </summary>
[Modulo("clipes", "Clipes", English = "Clips")]
internal sealed class ClipModule : Modulo
{
    private ConfigClipes _cfg = new();
    private ClipRing? _ring;
    private ClipActivity? _activity;
    private string? _ffmpeg;
    private string _game = "";
    private bool _stopped, _cutting, _endPending;
    private int _restarts;
    private readonly List<string> _reasons = new();
    private readonly Dictionary<string, DateTime> _lastKill = new();
    private readonly List<ClipFile> _pending = new();
    private readonly System.Windows.Forms.Timer _cutTimer = new() { Interval = 3000 };
    private readonly System.Windows.Forms.Timer _stopLater = new() { Interval = 60_000 };
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer _expire = new() { Interval = 180_000 };

    public override Type? TipoConfig => typeof(ConfigClipes);

    internal string SavedFolder => ClipStore.Folder(_cfg.Pasta);
    internal IReadOnlyList<ClipFile> PendingClips => _pending;
    internal static ClipModule? Current { get; private set; }

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigClipes>(cfg);
        Current = this;
        _ffmpeg = FfmpegTools.Find(null);
        _ring = new ClipRing(Path.Combine(Path.GetTempPath(), "ilha-clipes-anel"));
        _activity = new ClipActivity(S, this);
        S.Ilha.Adicionar(_activity);
        S.Atalhos.Registrar("clipe", new Atalho { Mods = Nativo.MOD_CONTROL | Nativo.MOD_ALT, Tecla = 0x43 }, Manual);
        GamesModule.MomentHappened += OnMoment;
        _cutTimer.Tick += (_, _) => { _cutTimer.Stop(); CutPending(); };
        _stopLater.Tick += (_, _) => { _stopLater.Stop(); StopRing("o jogo saiu da frente"); };
        _watch.Tick += (_, _) => Watch();
        _expire.Tick += (_, _) => { _expire.Stop(); Decide(keep: true, why: "sem resposta"); };
        if (_ffmpeg == null) Log.Escrever("clipes: sem ffmpeg (winget install Gyan.FFmpeg), nada é gravado");
        KeepLeftovers();
        Contexto(S.ContextoAtual());
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        bool before = _cfg.Ligado;
        _cfg = Ler<ConfigClipes>(cfg);
        if (before && !_cfg.Ligado) StopRing("desligado nas configurações");
        else Contexto(S.ContextoAtual());
    }

    /// <summary>A competitive game in front (any mode): the ring runs; it stops a minute after the game leaves the front.</summary>
    public override void Contexto(Contexto ctx)
    {
        if (_stopped) return;
        bool game = ctx.Jogo.Length > 0;
        if (game && _cfg.Ligado && _ffmpeg != null)
        {
            _stopLater.Stop();
            _game = FocusName(ctx.Jogo);
            if (_ring?.Running != true) { _restarts = 0; StartRing(); }
        }
        else if (_ring?.Running == true && !_stopLater.Enabled) _stopLater.Start();
    }

    private static string FocusName(string exe)
    {
        string key = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
        return Match.NameOf(key) is { Length: > 0 } n && n != key ? n : Path.GetFileNameWithoutExtension(exe);
    }

    private void StartRing()
    {
        if (_ring == null || _ffmpeg == null) return;
        int fps = Math.Clamp(_cfg.Fps, 24, 120), cq = Math.Clamp(_cfg.Qualidade, 15, 35);
        if (!_ring.Start(_ffmpeg, fps, cq, out string err)) { Log.Escrever("clipes: o anel não ligou: " + err); return; }
        _watch.Start();
    }

    private void StopRing(string why)
    {
        _watch.Stop();
        _stopLater.Stop();
        if (_ring?.Running != true) return;
        // a cut waiting for its last seconds goes first
        if (_cutTimer.Enabled) { _cutTimer.Stop(); CutPending(then: () => { _ring?.Stop(); Log.Escrever("clipes: anel desligado (" + why + ")"); }); return; }
        _ring.Stop();
        Log.Escrever("clipes: anel desligado (" + why + ")");
    }

    /// <summary>ffmpeg ended by itself (the screen capture lost, out of disk): logged and tried again, three times.</summary>
    private void Watch()
    {
        if (_ring == null || _ring.Running || _stopLater.Enabled) return;
        Log.Escrever("clipes: o ffmpeg do anel parou sozinho: " + _ring.Errors());
        if (++_restarts > 3) { _watch.Stop(); return; }
        StartRing();
    }

    // ------------------------------------------------------------------ moments

    private void OnMoment(string game, Moment x)
    {
        if (_stopped || !_cfg.Lances) return;
        switch (x.Kind)
        {
            case MomentKind.Kill:
                var now = DateTime.Now;
                bool streak = _lastKill.TryGetValue(game, out var last) && now - last < TimeSpan.FromSeconds(6);
                _lastKill[game] = now;
                if (streak) Trigger(Lang.T("multi kill", "multi kill"));
                break;
            case MomentKind.Ace: Trigger("ace"); break;
            case MomentKind.EpicSave: Trigger(Lang.T("defesa épica", "epic save")); break;
            case MomentKind.Goal when x.Ours: Trigger(Lang.T("gol", "goal")); break;
            case MomentKind.End:
                if (x.Outcome == Outcome.Win) Trigger(Lang.T("vitória", "win"));
                _endPending = true;
                if (!_cutTimer.Enabled && !_cutting) AskAtEnd();
                break;
        }
    }

    /// <summary>A moment: the cut waits 3 s for what comes right after; another moment in that time joins the same clip.</summary>
    private void Trigger(string reason)
    {
        if (_ring?.Running != true) return;
        if (!_reasons.Contains(reason)) _reasons.Add(reason);
        _cutTimer.Stop();
        _cutTimer.Start();
    }

    private void CutPending(Action? then = null)
    {
        if (_ring == null || _ffmpeg == null || _reasons.Count == 0) { then?.Invoke(); return; }
        string reason = string.Join(", ", _reasons);
        _reasons.Clear();
        string folder = ClipStore.Pending(SavedFolder);
        Cut(folder, reason, done: clip =>
        {
            if (clip != null) _pending.Add(clip);
            then?.Invoke();
            if (_endPending) AskAtEnd();
        });
    }

    private void Cut(string folder, string reason, Action<ClipFile?> done)
    {
        var ring = _ring!;
        string ffmpeg = _ffmpeg!;
        int seconds = Math.Clamp(_cfg.Segundos, 10, 45);
        var when = DateTime.Now;
        string name = ClipStore.Name(_game, when, reason);
        string video = Path.Combine(folder, name + ".mp4"), thumb = Path.Combine(ClipStore.Thumbs(folder), name + ".jpg");
        _cutting = true;
        Task.Run(() =>
        {
            Directory.CreateDirectory(ClipStore.Thumbs(folder));
            string? err = ring.Cut(ffmpeg, seconds, video);
            if (err == null && ClipRing.Thumbnail(ffmpeg, video, thumb) is { } terr) Log.Escrever("clipes: miniatura: " + terr);
            return err;
        }).ContinueWith(t => S.NaInterface(() =>
        {
            _cutting = false;
            string? err = t.IsFaulted ? t.Exception?.GetBaseException().Message : t.Result;
            if (err != null) { Log.Escrever($"clipes: o clipe ({reason}) não saiu: {err}"); done(null); return; }
            Log.Escrever($"clipes: clipe {name}");
            done(new ClipFile(video, thumb, when, reason));
        }));
    }

    /// <summary>Ctrl+Alt+C: the last seconds right now, saved at once.</summary>
    private void Manual()
    {
        if (_ring?.Running != true)
        {
            Notice(Lang.T("Clipe só com um jogo competitivo na frente", "Clips only with a competitive game in front"), null);
            return;
        }
        Cut(SavedFolder, Lang.T("atalho", "hotkey"), clip => { if (clip != null) Notice(Lang.T("Clipe salvo", "Clip saved"), clip); });
    }

    private void Notice(string text, ClipFile? clip) => S.Ilha.Alertar(new Alerta
    {
        Tipo = "clipe", Som = "", Importante = true, PermitidoNoCompetitivo = true, Segundos = 2.2f,
        Criar = p => ClipActivity.NoticeCard(p, text, clip),
    });

    /// <summary>Clips still pending from a session that ended without an answer (the island closed): kept, like no answer.</summary>
    private void KeepLeftovers()
    {
        try
        {
            string pending = ClipStore.Pending(SavedFolder);
            if (!Directory.Exists(pending)) return;
            int n = 0;
            foreach (var v in Directory.GetFiles(pending, "*.mp4"))
            {
                string name = Path.GetFileNameWithoutExtension(v);
                ClipStore.Keep(new ClipFile(v, Path.Combine(ClipStore.Thumbs(pending), name + ".jpg"), File.GetCreationTime(v), ""), SavedFolder);
                n++;
            }
            if (n > 0) Log.Escrever($"clipes: {n} clipe(s) que ficaram esperando foram guardados");
        }
        catch (Exception e) { Log.Escrever("clipes: pendentes antigos: " + e.Message); }
    }

    // ------------------------------------------------------------------ end of the match

    private void AskAtEnd()
    {
        _endPending = false;
        if (_pending.Count == 0) return;
        _activity?.Ask();
        _expire.Stop();
        _expire.Start();
        Log.Escrever($"clipes: fim de partida, {_pending.Count} clipe(s) esperando");
    }

    /// <summary>The prompt's answer: keep moves the pending clips to the clips folder, discard deletes them.</summary>
    internal void Decide(bool keep, string why = "")
    {
        _expire.Stop();
        if (_pending.Count == 0) { _activity?.Done(); return; }
        int n = _pending.Count;
        foreach (var c in _pending.ToList())
        {
            try { if (keep) ClipStore.Keep(c, SavedFolder); else ClipStore.Discard(c); }
            catch (Exception e) { Log.Escrever("clipes: " + e.Message); }
        }
        _pending.Clear();
        Log.Escrever($"clipes: {n} clipe(s) {(keep ? "salvos" : "descartados")}{(why.Length > 0 ? " (" + why + ")" : "")}");
        _activity?.Done();
    }

    public override void Parar()
    {
        _stopped = true;
        GamesModule.MomentHappened -= OnMoment;
        // leaving with clips waiting: they are kept, like a prompt nobody answered
        if (_pending.Count > 0) Decide(keep: true, why: "ilha fechando");
        foreach (var t in new[] { _cutTimer, _stopLater, _watch, _expire }) { t.Stop(); t.Dispose(); }
        _ring?.Dispose();
        if (Current == this) Current = null;
    }

    public override string Autoteste() => ClipTests.Run();
}

public sealed class ConfigClipes
{
    [Label("Gravar os últimos segundos dos jogos competitivos", "Keep the last seconds of competitive games")]
    [Help("Enquanto um jogo competitivo está na frente, o ffmpeg guarda os últimos segundos da tela e do som (na placa de vídeo, como o gravador).",
        "While a competitive game is in front, ffmpeg keeps the last seconds of the screen and the sound (on the graphics card, like the recorder).")]
    [JsonPropertyName("ligado")] public bool Ligado { get; set; } = true;

    [Label("Clipe sozinho nos lances", "Clip the big moments by itself")]
    [Help("Double kill ou mais, ace, vitória, gol do seu time e defesa épica. No fim da partida, a ilha pergunta se salva.",
        "A double kill or more, an ace, a win, your team's goal and an epic save. At the end of the match, the island asks whether to keep them.")]
    [JsonPropertyName("lances")] public bool Lances { get; set; } = true;

    [Label("Duração do clipe (segundos)", "Clip length (seconds)")]
    [Help("15, 30 ou até 45.", "15, 30 or up to 45.")]
    [JsonPropertyName("segundos")] public int Segundos { get; set; } = 30;

    [Label("Quadros por segundo", "Frames per second")]
    [JsonPropertyName("fps")] public int Fps { get; set; } = 60;

    [Label("Qualidade (menor = melhor e maior)", "Quality (lower = better and bigger)")]
    [JsonPropertyName("qualidade")] public int Qualidade { get; set; } = 23;

    [Label("Pasta dos clipes", "Clips folder")]
    [Help("Vazio = Vídeos\\Ilha\\Clipes", "Empty = Videos\\Ilha\\Clips")]
    [JsonPropertyName("pasta")] public string Pasta { get; set; } = "";
}
