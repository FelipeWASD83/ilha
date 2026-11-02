using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Windows.UI.Composition;
using RectangleF = System.Drawing.RectangleF;

namespace Ilha.Recorder;

/// <summary>
/// The recorder on the island. Two menu tiles open its card: video (screen, camera, or the screen with the camera in a
/// rounded corner) and audio. While it records, the pill shows the red dot and the time, and the island keeps itself
/// out of the capture. It never announces itself with a sound: the sound would go into the recording.
/// </summary>
internal sealed class RecorderActivity : Atividade
{
    public enum Panel { Video, Audio }
    private enum Phase { Idle, Starting, Recording, Finishing }

    private const float L = 340, LC = 196, LABEL_END = 60, TIME_SPACE = 66;
    private static readonly Color4 ButtonBg = new(1f, 1f, 1f, 0.12f);

    private readonly Servicos S;
    private readonly Func<ConfigGravador> _cfg;
    private readonly Action<ConfigGravador> _save;
    private readonly System.Windows.Forms.Timer _second = new() { Interval = 1000 };
    private Panel _panel = Panel.Video;
    private Phase _phase = Phase.Idle;
    private RecordingSession? _session;
    private RecordKind _kind;
    private DateTime _startedUtc;
    private string? _error;
    private long _drawn = -1;
    private string _layout = "";
    private bool _stopped;
    private readonly List<(RectangleF Area, string Mode)> _modePills = new();

    // the live waveform on the pill while recording audio (like the iPhone's Voice Memos): the newest bar at the right
    private const int WAVE_BARS = 22;
    private const float WAVE_FLOOR = 0.12f;
    private readonly float[] _history = new float[WAVE_BARS];
    private readonly List<SpriteVisual[]> _waves = new();
    private readonly System.Windows.Forms.Timer _waveTick = new() { Interval = 33 };
    private ImplicitAnimationCollection? _implicit;

    public override string Tipo => "gravador";
    public override string Nome => Lang.T("Gravador", "Recorder");
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;
    private bool RecordingAudio => _phase == Phase.Recording && _kind == RecordKind.Audio;
    // an audio recording takes the pill: the waveform is the point (his request on 02/11/2026)
    public override bool NaFrente => RecordingAudio;
    public override (float De, float Ate)? MeioLivreCompacta => RecordingAudio ? null : (LABEL_END + 6, LC - TIME_SPACE);

    /// <summary>The menu redraws its two tiles (the dot on the icon, the detail line).</summary>
    public event Action? MenuChanged;

    /// <summary>The hotkey as it ended up registered ("Ctrl + Alt + G"), shown on the video card; null when taken.</summary>
    public string? HotkeyLabel { get; set; }

    public RecorderActivity(Servicos s, Func<ConfigGravador> cfg, Action<ConfigGravador> save)
    {
        S = s;
        _cfg = cfg;
        _save = save;
        _second.Tick += (_, _) =>
        {
            if (!TemCenaViva || _phase != Phase.Recording) { _second.Stop(); return; }
            if (Seconds() != _drawn) Redesenhar();
            MenuChanged?.Invoke();
        };
        _waveTick.Tick += (_, _) => Wave();
    }

    // ------------------------------------------------------------------ waveform

    private void EvaluateWave()
    {
        bool run = RecordingAudio && _waves.Count > 0;
        if (run && !_waveTick.Enabled) _waveTick.Start();
        else if (!run && _waveTick.Enabled) _waveTick.Stop();
        if (!RecordingAudio) Array.Clear(_history);
    }

    /// <summary>One step: the newest level enters at the right and every bar moves one to the left.</summary>
    private void Wave()
    {
        if (!RecordingAudio || _session == null) { EvaluateWave(); return; }
        // the square root lifts quiet speech, which peaks well under full scale
        float level = MathF.Min(1, MathF.Sqrt(_session.TakePeak()) * 1.15f);
        Array.Copy(_history, 1, _history, 0, WAVE_BARS - 1);
        _history[^1] = level;
        foreach (var bars in _waves)
            for (int i = 0; i < bars.Length; i++)
                bars[i].Scale = new Vector3(1, WAVE_FLOOR + (1 - WAVE_FLOOR) * _history[i], 1);
    }

    private ImplicitAnimationCollection Implicit(Compositor c)
    {
        if (_implicit != null) return _implicit;
        // each new Scale (30 a second) is eased in the compositor, so the bars move smoothly at the monitor's rate
        var a = c.CreateVector3KeyFrameAnimation();
        a.Target = "Scale";
        a.InsertExpressionKeyFrame(1f, "this.FinalValue");
        a.Duration = TimeSpan.FromMilliseconds(60);
        var col = c.CreateImplicitAnimationCollection();
        col["Scale"] = a;
        return _implicit = col;
    }

    private void WaveBars(Palco p, Cena c, float x0, float x1, float height)
    {
        float step = (x1 - x0) / WAVE_BARS, w = MathF.Max(2, step * 0.55f);
        var bars = new SpriteVisual[WAVE_BARS];
        for (int i = 0; i < WAVE_BARS; i++)
        {
            var v = p.Bloco(c, new Vector2(x0 + i * step, (24 - height) / 2), new Vector2(w, height), new Color4(1, 1, 1, 0.92f));
            v.CenterPoint = new Vector3(v.Size.X / 2, v.Size.Y / 2, 0);
            v.Scale = new Vector3(1, WAVE_FLOOR + (1 - WAVE_FLOOR) * _history[i], 1);
            v.ImplicitAnimations = Implicit(p.C);
            bars[i] = v;
        }
        _waves.Add(bars);
        c.AoSoltar += () => { _waves.Remove(bars); EvaluateWave(); };
        EvaluateWave();
    }

    private bool Open => S.Ilha.Expandida == this;
    public bool Recording(Panel p) => (_phase is Phase.Recording or Phase.Starting) && (p == Panel.Audio) == (_kind == RecordKind.Audio);
    public bool Busy => _phase != Phase.Idle;

    // ------------------------------------------------------------------ actions

    public void OpenPanel(Panel p)
    {
        if (_stopped) return;
        if (!Busy) _panel = p;
        _error = null;
        Refresh();
        S.Ilha.Expandir(this);
    }

    /// <summary>The hotkey: starts the video recording in the chosen mode, or stops whatever is recording.</summary>
    public void Toggle()
    {
        if (_stopped) return;
        if (_phase == Phase.Recording) StopRecording();
        else if (_phase == Phase.Idle) StartRecording(VideoKind(_cfg().Modo));
    }

    public void Stop()
    {
        _stopped = true;
        _second.Stop();
        if (Open) S.Ilha.Recolher();
        // a recording in progress is finished here, waiting for it: the module also stops when the island quits, and a
        // stop left to a background task died with the process (02/11/2026: an update closed the island mid-recording
        // and left only the tracks, without the final file)
        if (_session != null && _phase is Phase.Recording or Phase.Finishing)
        {
            Log.Escrever("gravador: a ilha está parando no meio de uma gravação; terminando antes");
            try { if (_phase == Phase.Recording) _session.Stop(); } catch (Exception e) { Log.Escrever("gravador: " + e.Message); }
        }
        S.Ilha.ForaDasCapturas(false);
        Desligar();
    }

    internal static RecordKind VideoKind(string mode) => mode switch
    {
        "camera" => RecordKind.Camera,
        "telaCamera" => RecordKind.ScreenCamera,
        _ => RecordKind.Screen,
    };

    private static string ModeId(RecordKind k) => k switch { RecordKind.Camera => "camera", RecordKind.ScreenCamera => "telaCamera", _ => "tela" };

    private static string KindName(RecordKind k) => k switch
    {
        RecordKind.Screen => Lang.T("Tela", "Screen"),
        RecordKind.Camera => Lang.T("Câmera", "Camera"),
        RecordKind.ScreenCamera => Lang.T("Tela + câmera", "Screen + camera"),
        _ => Lang.T("Áudio", "Audio"),
    };

    /// <summary>Where videos and audios go: the configured folders, else Videos\Ilha and Music\Ilha.</summary>
    internal (string Videos, string Audios) Folders()
    {
        var c = _cfg();
        return (string.IsNullOrWhiteSpace(c.PastaVideos) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Ilha") : c.PastaVideos,
            string.IsNullOrWhiteSpace(c.PastaAudios) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Ilha") : c.PastaAudios);
    }

    /// <summary>The island's own window with the latest recordings (the one just saved selected and playing).</summary>
    public void OpenRecordings(string? select = null)
    {
        if (Open) S.Ilha.Recolher();
        RecordingsWindow.Open(S, Folders, select);
    }

    /// <summary>At start: recordings left in pieces by a quit or a crash are joined in the background, and announced.</summary>
    public void RecoverLeftovers()
    {
        var (videos, audios) = Folders();
        string? ffmpeg = FfmpegTools.Find(_cfg().Ffmpeg);
        Task.Run(() => RecordingSession.RecoverOrphans(videos, audios, ffmpeg)).ContinueWith(t => S.NaInterface(() =>
        {
            if (_stopped || t.IsFaulted || t.Result.Count == 0) return;
            AlertSaved(t.Result[^1], Lang.T("Recuperada: a ilha tinha fechado no meio da gravação", "Recovered: the island had closed mid-recording"));
        }));
    }

    private RecordingOptions Options(RecordKind kind)
    {
        var c = _cfg();
        bool audio = kind == RecordKind.Audio;
        string folder = audio ? Folders().Audios : Folders().Videos;
        return new RecordingOptions
        {
            Kind = kind,
            PcSound = audio ? c.AudioSomDoPc : c.SomDoPc,
            Mic = audio ? c.AudioMicrofone : c.Microfone,
            Ffmpeg = FfmpegTools.Find(c.Ffmpeg),
            Camera = c.Camera,
            Folder = folder,
            Fps = Math.Clamp(c.Fps, 10, 120),
            Cq = Math.Clamp(c.Qualidade, 10, 40),
        };
    }

    private void StartRecording(RecordKind kind)
    {
        if (_phase != Phase.Idle) return;
        var opt = Options(kind);
        _kind = kind;
        _error = null;
        _phase = Phase.Starting;
        _startedUtc = DateTime.UtcNow;
        // out of the capture before the first frame, so not even the closing card gets into the video
        bool screen = kind is RecordKind.Screen or RecordKind.ScreenCamera;
        if (screen) S.Ilha.ForaDasCapturas(true);
        if (Open) S.Ilha.Recolher();
        Ligar();
        Refresh();
        Task.Run(() => RecordingSession.Start(opt)).ContinueWith(t => S.NaInterface(() =>
        {
            if (t.IsFaulted || _stopped)
            {
                if (!t.IsFaulted) { Task.Run(() => t.Result.Stop()); return; }
                var e = t.Exception!.GetBaseException();
                Log.Escrever("gravador: não começou: " + e.Message);
                _phase = Phase.Idle;
                if (screen) S.Ilha.ForaDasCapturas(false);
                Desligar();
                _error = e is RecorderException ? e.Message : Lang.T("Não deu para gravar: ", "Couldn't record: ") + e.Message;
                AlertError(_error);
                Refresh();
                return;
            }
            var s = t.Result;
            _session = s;
            _startedUtc = s.StartedUtc;
            _phase = Phase.Recording;
            s.Died += why => S.NaInterface(() =>
            {
                if (_session != s || _phase != Phase.Recording) return;
                Log.Escrever("gravador: o ffmpeg parou sozinho: " + why);
                _error = why;
                StopRecording();
            });
            if (TemCenaViva) _second.Start();
            Refresh();
        }));
    }

    private void StopRecording()
    {
        var s = _session;
        if (_phase != Phase.Recording || s == null) return;
        _phase = Phase.Finishing;
        _second.Stop();
        Refresh();
        Task.Run(s.Stop).ContinueWith(t => S.NaInterface(() =>
        {
            _session = null;
            _phase = Phase.Idle;
            S.Ilha.ForaDasCapturas(false);
            Desligar();
            if (t.IsFaulted)
            {
                var e = t.Exception!.GetBaseException();
                Log.Escrever("gravador: ao parar: " + e);
                AlertError(Lang.T("Não deu para salvar: ", "Couldn't save: ") + e.Message);
            }
            else if (t.Result.FinalPath is { } path)
            {
                Log.Escrever("gravador: salvo em " + path);
                AlertSaved(path, _error);
            }
            else AlertError(t.Result.Error ?? Lang.T("A gravação falhou", "The recording failed"), t.Result.KeptFolder);
            _error = null;
            Refresh();
        }));
    }

    private void Refresh()
    {
        MenuChanged?.Invoke();
        EvaluateWave();
        string k = $"{_phase}|{_panel}|{_kind}|{_error != null}";
        if (k != _layout) { _layout = k; MudouTamanho(); }
        else Redesenhar();
    }

    private void SetMode(string mode)
    {
        var c = _cfg();
        if (c.Modo == mode) return;
        c.Modo = mode;
        _save(c);
        Redesenhar();
    }

    private void Flip(Func<ConfigGravador, bool> get, Action<ConfigGravador, bool> set)
    {
        var c = _cfg();
        set(c, !get(c));
        _save(c);
        Redesenhar();
    }

    // ------------------------------------------------------------------ alerts

    private void AlertSaved(string path, string? note)
    {
        string name = Path.GetFileName(path);
        Alertar(new Alerta
        {
            Tipo = "gravador",
            Som = "",
            Importante = true,
            Segundos = 6,
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(300, note != null ? 58 : 40));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    Icone.Circulo(dc, new Vector2(20, 20), 16, Cor.Verde);
                    Icone.Linha(dc, new Vector2(12.5f, 20.5f), new Vector2(17.5f, 25.5f), 2.4f, Cor.Branco, p.G.Fabrica);
                    Icone.Linha(dc, new Vector2(17.5f, 25.5f), new Vector2(27.5f, 14.5f), 2.4f, Cor.Branco, p.G.Fabrica);
                    p.Texto.Desenhar(dc, Lang.T("Gravação salva", "Recording saved"), new Vector2(46, 2), 14, Cor.Branco, FontWeight.SemiBold, largura: t.X - 46);
                    p.Texto.Desenhar(dc, name, new Vector2(46, 21), 12, Cor.Secundario, FontWeight.Normal, largura: t.X - 46);
                    if (note != null) p.Texto.Desenhar(dc, note, new Vector2(46, 39), 11.5f, Cor.Laranja, FontWeight.Normal, largura: t.X - 46);
                });
                c.Botoes.Add(new Botao { Area = new RectangleF(0, 0, c.Tamanho.X, c.Tamanho.Y), Acao = () => OpenRecordings(path) });
                return c;
            },
        });
    }

    private void AlertError(string text, string? folder = null)
    {
        Alertar(new Alerta
        {
            Tipo = "gravador",
            Som = "",
            Importante = true,
            Segundos = 6,
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(300, 40));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    Icone.Circulo(dc, new Vector2(20, 20), 16, Cor.Vermelho);
                    Icone.Retangulo(dc, 18.6f, 10.5f, 2.8f, 12, 1.4f, Cor.Branco);
                    Icone.Circulo(dc, new Vector2(20, 27.5f), 1.7f, Cor.Branco);
                    p.Texto.Desenhar(dc, Lang.T("Gravador", "Recorder"), new Vector2(46, 2), 14, Cor.Branco, FontWeight.SemiBold, largura: t.X - 46);
                    p.Texto.Desenhar(dc, text, new Vector2(46, 21), 12, Cor.Secundario, FontWeight.Normal, largura: t.X - 46);
                });
                if (folder != null) c.Botoes.Add(new Botao { Area = new RectangleF(0, 0, c.Tamanho.X, c.Tamanho.Y), Acao = () => OpenFolder(folder) });
                return c;
            },
        });
    }

    private static readonly RectangleF RecordingsPillArea = new(L - 108, 0, 108, 22);

    /// <summary>The "Recordings" pill at the top right of both cards: opens the island's own player window.</summary>
    private static void RecordingsPill(ID2D1DeviceContext dc, Palco p)
    {
        var r = RecordingsPillArea;
        Icone.Retangulo(dc, r.X, r.Y, r.Width, r.Height, r.Height / 2, ButtonBg);
        // a small play triangle before the word
        Icone.Triangulo(dc, p.G.Fabrica, new Vector2(r.X + 12, r.Y + 6.5f), new Vector2(r.X + 19.5f, r.Y + 11), new Vector2(r.X + 12, r.Y + 15.5f), Cor.Branco);
        string label = Lang.T("Gravações", "Recordings");
        var m = p.Texto.Medir(label, 12, FontWeight.Medium);
        p.Texto.Desenhar(dc, label, new Vector2(r.X + 26, r.Y + (r.Height - m.Y) / 2), 12, Cor.Branco, FontWeight.Medium);
    }

    private static void OpenFolder(string folder)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\""); }
        catch (Exception e) { Log.Escrever("gravador: explorer: " + e.Message); }
    }

    // ------------------------------------------------------------------ menu

    public string? MenuDetail(Panel p)
    {
        if (!Recording(p)) return null;
        if (_phase == Phase.Starting) return Lang.T("começando", "starting");
        return ElapsedText();
    }

    private string ElapsedText() => Clock(DateTime.UtcNow - _startedUtc);

    // ------------------------------------------------------------------ text

    internal static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        long s = (long)t.TotalSeconds;
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    private long Seconds() => (long)(DateTime.UtcNow - _startedUtc).TotalSeconds;

    private string Time()
    {
        _drawn = Seconds();
        return _phase == Phase.Recording ? ElapsedText() : "0:00";
    }

    private string Sources()
    {
        var s = _session;
        var parts = new List<string>();
        if (s?.PcSound == true) parts.Add(Lang.T("som do PC", "PC sound"));
        if (s?.Mic == true) parts.Add(Lang.T("microfone", "microphone"));
        return parts.Count == 0 ? Lang.T("sem som", "no sound") : string.Join(" · ", parts);
    }

    private string StateLabel() => _phase switch
    {
        Phase.Starting => Lang.T("Começando…", "Starting…"),
        Phase.Finishing => Lang.T("Salvando…", "Saving…"),
        _ => "REC",
    };

    // ------------------------------------------------------------------ scenes

    /// <summary>The red dot in its own visual: it pulses by opacity in the compositor, no redraw from the app.</summary>
    private static void Dot(Palco p, Cena c, Vector2 center, float radius, Color4 color, bool pulse)
    {
        float side = radius * 2 + 2;
        var s = p.G.NovaSuperficie(new Vector2(side, side), (dc, t) => Icone.Circulo(dc, t / 2, radius, color));
        var v = p.C.CreateSpriteVisual();
        v.Size = s.TamanhoPx;
        v.Offset = new Vector3(p.Px(center.X - side / 2), p.Px(center.Y - side / 2), 0);
        v.Brush = s.Pincel;
        c.Raiz.Children.InsertAtTop(v);
        c.Superficies.Add(s);
        if (!pulse) return;
        var ease = p.C.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.55f, 1f));
        var a = p.C.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0f, 1f);
        a.InsertKeyFrame(0.5f, 0.28f, ease);
        a.InsertKeyFrame(1f, 1f, ease);
        a.Duration = TimeSpan.FromSeconds(1.5);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        v.StartAnimation("Opacity", a);
    }

    private Color4 DotColor => _phase == Phase.Recording ? Cor.Vermelho : Cor.Secundario;

    protected override Cena Montar(Palco p, Forma f)
    {
        if (_phase == Phase.Recording) _second.Start();
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(24, 24));
                Dot(p, c, new Vector2(12, 12), 5, DotColor, _phase == Phase.Recording);
                return c;
            }
            case Forma.Compacta:
            {
                var c = p.NovaCena(new Vector2(LC, 24));
                Dot(p, c, new Vector2(6, 12), 5, DotColor, _phase == Phase.Recording);
                if (RecordingAudio)
                {
                    // the waveform where the "REC" label and the clock would be, the time at the right
                    WaveBars(p, c, 18, LC - TIME_SPACE + 2, 20);
                    p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                    {
                        string time = Time();
                        var tm = p.Texto.Medir(time, 15, FontWeight.SemiBold, tabular: true);
                        p.Texto.Desenhar(dc, time, new Vector2(t.X - tm.X, (t.Y - tm.Y) / 2), 15, Cor.Branco, FontWeight.SemiBold, tabular: true);
                    });
                    return c;
                }
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    string label = StateLabel();
                    bool rec = _phase == Phase.Recording;
                    var m = p.Texto.Medir(label, 12, FontWeight.Bold);
                    p.Texto.Desenhar(dc, label, new Vector2(17, (t.Y - m.Y) / 2), 12, rec ? Cor.Vermelho : Cor.Secundario, FontWeight.Bold,
                        largura: rec ? LABEL_END - 17 : t.X - 17);
                    if (!rec) return;
                    string time = Time();
                    var tm = p.Texto.Medir(time, 15, FontWeight.SemiBold, tabular: true);
                    p.Texto.Desenhar(dc, time, new Vector2(t.X - tm.X, (t.Y - tm.Y) / 2), 15, Cor.Branco, FontWeight.SemiBold, tabular: true);
                });
                return c;
            }
            default:
                return Busy ? RecordingScene(p) : _panel == Panel.Video ? VideoScene(p) : AudioScene(p);
        }
    }

    private Cena RecordingScene(Palco p)
    {
        var c = p.NovaCena(new Vector2(L, 94));
        Dot(p, c, new Vector2(8, 47), 6.5f, DotColor, _phase == Phase.Recording);
        var stop = new RectangleF(L - 44, 24, 40, 40);
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            string title = _phase switch
            {
                Phase.Starting => Lang.T("Começando · ", "Starting · ") + KindName(_kind),
                Phase.Finishing => Lang.T("Salvando · ", "Saving · ") + KindName(_kind),
                _ => Lang.T("Gravando · ", "Recording · ") + KindName(_kind),
            };
            p.Texto.Desenhar(dc, title, new Vector2(0, 0), 13, Cor.Secundario, largura: t.X);
            p.Texto.Desenhar(dc, Time(), new Vector2(20, 20), 40, _phase == Phase.Recording ? Cor.Branco : Cor.Secundario, FontWeight.Medium,
                tabular: true, largura: stop.X - 26);
            if (_phase == Phase.Recording)
            {
                var cs = new Vector2(stop.X + 20, stop.Y + 20);
                var v = Cor.Vermelho;
                Icone.Circulo(dc, cs, 20, new Color4(v.R, v.G, v.B, 0.22f));
                Icone.Retangulo(dc, cs.X - 6.5f, cs.Y - 6.5f, 13, 13, 2.5f, v);
            }
            string line = _phase == Phase.Finishing ? Lang.T("Juntando o vídeo e o som", "Putting video and sound together") : Sources();
            p.Texto.Desenhar(dc, line, new Vector2(0, 76), 12, Cor.Secundario, FontWeight.Normal, largura: t.X);
        });
        if (_phase == Phase.Recording) c.Botoes.Add(new Botao { Area = new RectangleF(stop.X - 3, stop.Y - 3, 46, 46), Acao = StopRecording });
        return c;
    }

    private const float ROW = 36, SWITCH_W = 44, SWITCH_H = 26, BUTTON_H = 42;

    private Cena VideoScene(Palco p)
    {
        const float yModes = 24, yRows = 70;
        float yButton = yRows + 2 * ROW + 10;
        float h = yButton + BUTTON_H + 30 + (_error != null ? 18 : 0);
        var c = p.NovaCena(new Vector2(L, h));
        // the mode pills, measured once per layout (their width follows the text)
        _modePills.Clear();
        float x = 0;
        foreach (var mode in new[] { "tela", "camera", "telaCamera" })
        {
            string label = KindName(VideoKind(mode));
            float w = p.Texto.Medir(label, 13, FontWeight.Medium).X + 28;
            _modePills.Add((new RectangleF(x, yModes, w, 32), mode));
            x += w + 6;
        }
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var cfg = _cfg();
            p.Texto.Desenhar(dc, Lang.T("Gravar vídeo", "Record video"), new Vector2(0, 0), 13, Cor.Secundario, largura: t.X);
            RecordingsPill(dc, p);
            foreach (var (r, mode) in _modePills)
            {
                bool on = cfg.Modo == mode || (mode == "tela" && cfg.Modo is not ("camera" or "telaCamera"));
                Icone.Retangulo(dc, r.X, r.Y, r.Width, r.Height, r.Height / 2, on ? Cor.Branco : ButtonBg);
                string label = KindName(VideoKind(mode));
                var m = p.Texto.Medir(label, 13, FontWeight.Medium);
                p.Texto.Desenhar(dc, label, new Vector2(r.X + (r.Width - m.X) / 2, r.Y + (r.Height - m.Y) / 2), 13,
                    on ? new Color4(0, 0, 0, 1) : Cor.Branco, FontWeight.Medium);
            }
            SwitchRow(dc, p, yRows, Lang.T("Som do PC", "PC sound"), cfg.SomDoPc, Icons.Speaker);
            SwitchRow(dc, p, yRows + ROW, Lang.T("Microfone", "Microphone"), cfg.Microfone, Icons.Mic);
            RecordButton(dc, p, yButton, Lang.T("Gravar", "Record"));
            string where = string.IsNullOrWhiteSpace(cfg.PastaVideos) ? Lang.T("Vídeos\\Ilha", "Videos\\Ilha") : cfg.PastaVideos;
            string keys = HotkeyLabel is { } k ? k + Lang.T(" começa e para · ", " starts and stops · ") : "";
            p.Texto.Desenhar(dc, keys + Lang.T("salva em ", "saves to ") + where,
                new Vector2(0, yButton + BUTTON_H + 10), 11.5f, Cor.Secundario, FontWeight.Normal, largura: t.X);
            if (_error != null) p.Texto.Desenhar(dc, _error, new Vector2(0, yButton + BUTTON_H + 28), 12, Cor.Vermelho, FontWeight.Normal, largura: t.X);
        });
        foreach (var (r, mode) in _modePills) c.Botoes.Add(new Botao { Area = r, Acao = () => SetMode(mode) });
        c.Botoes.Add(new Botao { Area = new RectangleF(0, yRows, L, ROW), Acao = () => Flip(x => x.SomDoPc, (x, v) => x.SomDoPc = v) });
        c.Botoes.Add(new Botao { Area = new RectangleF(0, yRows + ROW, L, ROW), Acao = () => Flip(x => x.Microfone, (x, v) => x.Microfone = v) });
        c.Botoes.Add(new Botao { Area = new RectangleF(0, yButton, L, BUTTON_H), Acao = () => StartRecording(VideoKind(_cfg().Modo)) });
        c.Botoes.Add(new Botao { Area = RecordingsPillArea, Acao = () => OpenRecordings() });
        return c;
    }

    private Cena AudioScene(Palco p)
    {
        const float yRows = 26;
        float yButton = yRows + 2 * ROW + 10;
        float h = yButton + BUTTON_H + 30 + (_error != null ? 18 : 0);
        var c = p.NovaCena(new Vector2(L, h));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var cfg = _cfg();
            p.Texto.Desenhar(dc, Lang.T("Gravar áudio", "Record audio"), new Vector2(0, 0), 13, Cor.Secundario, largura: t.X);
            RecordingsPill(dc, p);
            SwitchRow(dc, p, yRows, Lang.T("Microfone", "Microphone"), cfg.AudioMicrofone, Icons.Mic);
            SwitchRow(dc, p, yRows + ROW, Lang.T("Som do PC", "PC sound"), cfg.AudioSomDoPc, Icons.Speaker);
            RecordButton(dc, p, yButton, Lang.T("Gravar", "Record"));
            string where = string.IsNullOrWhiteSpace(cfg.PastaAudios) ? Lang.T("Músicas\\Ilha", "Music\\Ilha") : cfg.PastaAudios;
            p.Texto.Desenhar(dc, Lang.T("Salva em WAV em ", "Saves a WAV to ") + where, new Vector2(0, yButton + BUTTON_H + 10), 11.5f,
                Cor.Secundario, FontWeight.Normal, largura: t.X);
            if (_error != null) p.Texto.Desenhar(dc, _error, new Vector2(0, yButton + BUTTON_H + 28), 12, Cor.Vermelho, FontWeight.Normal, largura: t.X);
        });
        c.Botoes.Add(new Botao { Area = new RectangleF(0, yRows, L, ROW), Acao = () => Flip(x => x.AudioMicrofone, (x, v) => x.AudioMicrofone = v) });
        c.Botoes.Add(new Botao { Area = new RectangleF(0, yRows + ROW, L, ROW), Acao = () => Flip(x => x.AudioSomDoPc, (x, v) => x.AudioSomDoPc = v) });
        c.Botoes.Add(new Botao { Area = new RectangleF(0, yButton, L, BUTTON_H), Acao = () => StartRecording(RecordKind.Audio) });
        c.Botoes.Add(new Botao { Area = RecordingsPillArea, Acao = () => OpenRecordings() });
        return c;
    }

    private static void SwitchRow(ID2D1DeviceContext dc, Palco p, float y, string label, bool on,
        Action<ID2D1DeviceContext, ID2D1Factory1, Vector2, Color4> icon)
    {
        float cy = y + ROW / 2;
        icon(dc, p.G.Fabrica, new Vector2(10, cy), new Color4(1, 1, 1, 0.85f));
        p.Texto.Desenhar(dc, label, new Vector2(28, cy - 9), 13, Cor.Branco, largura: 220);
        float x = L - SWITCH_W, sy = cy - SWITCH_H / 2;
        Icone.Retangulo(dc, x, sy, SWITCH_W, SWITCH_H, SWITCH_H / 2, on ? Cor.Verde : Cor.Fraco);
        float knob = SWITCH_H - 6;
        Icone.Circulo(dc, new Vector2(on ? x + SWITCH_W - 3 - knob / 2 : x + 3 + knob / 2, cy), knob / 2, Cor.Branco);
    }

    private static void RecordButton(ID2D1DeviceContext dc, Palco p, float y, string label)
    {
        Icone.Retangulo(dc, 0, y, L, BUTTON_H, BUTTON_H / 2, Cor.Vermelho);
        var m = p.Texto.Medir(label, 15, FontWeight.SemiBold);
        float total = 14 + 8 + m.X;
        float x = (L - total) / 2;
        Icone.Circulo(dc, new Vector2(x + 7, y + BUTTON_H / 2), 7, Cor.Branco);
        p.Texto.Desenhar(dc, label, new Vector2(x + 22, y + (BUTTON_H - m.Y) / 2), 15, Cor.Branco, FontWeight.SemiBold);
    }

    public override void Dispose()
    {
        _second.Dispose();
        _waveTick.Dispose();
    }
}

/// <summary>The recorder's icons, made of shapes only (the menu tiles and the card's switch rows).</summary>
internal static class Icons
{
    /// <summary>A video camera: the body and the lens cone; a red dot on it while recording video.</summary>
    /// <summary>
    /// The menu's camera, in the spirit of the iOS 18 camera icon: a silver body with a raised top, a deep blue glass
    /// lens in a dark ring with a glint, and the small window at the top right, which turns red while recording video.
    /// </summary>
    public static void Camera(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r, bool live)
    {
        float x = r.X, y = r.Y;
        var top = new Color4(0.94f, 0.94f, 0.96f, 1);
        var bottom = new Color4(0.64f, 0.64f, 0.68f, 1);
        using (var silver = Linear(dc, new Vector2(x, y + 4), new Vector2(x, y + 20.5f), top, bottom))
        {
            Rounded(dc, x + 7, y + 3.6f, 7, 4.4f, 1.6f, silver);
            Rounded(dc, x + 1.2f, y + 6.4f, 21.6f, 14, 3.6f, silver);
        }
        // the seam under the raised top and the shadow line at the bottom of the body
        Icone.Retangulo(dc, x + 1.2f, y + 9.6f, 21.6f, 0.6f, 0, new Color4(0, 0, 0, 0.16f));
        var lens = new Vector2(x + 12, y + 13.6f);
        Icone.Circulo(dc, lens, 5.9f, new Color4(0.10f, 0.10f, 0.11f, 1));
        Icone.Circulo(dc, lens, 4.9f, new Color4(0.36f, 0.36f, 0.39f, 1));
        using (var glass = Radial(dc, lens + new Vector2(-1.1f, -1.2f), 4.6f,
                   new Color4(0.30f, 0.46f, 0.86f, 1), new Color4(0.03f, 0.05f, 0.14f, 1)))
            dc.FillEllipse(new Ellipse(lens, 4.3f, 4.3f), glass);
        Icone.Circulo(dc, lens, 1.6f, new Color4(0.02f, 0.02f, 0.05f, 0.9f));
        Icone.Circulo(dc, lens + new Vector2(-1.7f, -1.8f), 1.05f, new Color4(1, 1, 1, 0.85f));
        Icone.Circulo(dc, lens + new Vector2(1.9f, 1.8f), 0.5f, new Color4(1, 1, 1, 0.35f));
        Icone.Circulo(dc, new Vector2(x + 19, y + 9.4f), 1.25f, live ? Cor.Vermelho : new Color4(0.27f, 0.27f, 0.30f, 1));
    }

    /// <summary>
    /// The menu's recorder: a studio microphone with a silver capsule, its grille lines and middle band, the cradle and a
    /// stand with a base. While recording audio a red dot shows at its top right.
    /// </summary>
    public static void MicTile(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r, bool live)
    {
        float x = r.X, y = r.Y;
        var light = new Color4(0.95f, 0.95f, 0.97f, 1);
        var dark = new Color4(0.60f, 0.60f, 0.64f, 1);
        using (var silver = Linear(dc, new Vector2(x + 8, y), new Vector2(x + 16, y), light, dark))
            Rounded(dc, x + 8, y + 1.2f, 8, 13.6f, 4, silver);
        var grille = new Color4(0.25f, 0.25f, 0.28f, 0.75f);
        foreach (float gy in new[] { 4.4f, 6.2f, 8.0f })
            Icone.Linha(dc, new Vector2(x + 9.3f, y + gy), new Vector2(x + 14.7f, y + gy), 0.7f, grille, f);
        Icone.Retangulo(dc, x + 8, y + 9.6f, 8, 1.4f, 0, new Color4(0.38f, 0.38f, 0.42f, 1));
        var metal = new Color4(0.82f, 0.82f, 0.86f, 1);
        using (var g = f.CreatePathGeometry())
        {
            using (var sink = g.Open())
            {
                sink.BeginFigure(new Vector2(x + 5.4f, y + 7.6f), FigureBegin.Hollow);
                sink.AddLine(new Vector2(x + 5.4f, y + 10.4f));
                sink.AddArc(new ArcSegment
                {
                    Point = new Vector2(x + 18.6f, y + 10.4f), Size = new Vortice.Mathematics.Size(6.6f, 6.6f),
                    SweepDirection = SweepDirection.CounterClockwise, ArcSize = ArcSize.Small,
                });
                sink.AddLine(new Vector2(x + 18.6f, y + 7.6f));
                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }
            using var b = dc.CreateSolidColorBrush(metal);
            using var round = f.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round });
            dc.DrawGeometry(g, b, 1.7f, round);
        }
        Icone.Linha(dc, new Vector2(x + 12, y + 17), new Vector2(x + 12, y + 20.4f), 1.9f, metal, f);
        using (var foot = Linear(dc, new Vector2(x, y + 20), new Vector2(x, y + 22.6f), light, dark))
            Rounded(dc, x + 7, y + 20.2f, 10, 2.4f, 1.2f, foot);
        if (live) Icone.Circulo(dc, new Vector2(x + 20.2f, y + 3.4f), 2.2f, Cor.Vermelho);
    }

    private static ID2D1LinearGradientBrush Linear(ID2D1DeviceContext dc, Vector2 a, Vector2 b, Color4 from, Color4 to)
    {
        using var stops = dc.CreateGradientStopCollection(new[] { new GradientStop { Position = 0, Color = from }, new GradientStop { Position = 1, Color = to } });
        return dc.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = a, EndPoint = b }, stops);
    }

    private static ID2D1RadialGradientBrush Radial(ID2D1DeviceContext dc, Vector2 center, float radius, Color4 inner, Color4 outer)
    {
        using var stops = dc.CreateGradientStopCollection(new[] { new GradientStop { Position = 0, Color = inner }, new GradientStop { Position = 1, Color = outer } });
        return dc.CreateRadialGradientBrush(new RadialGradientBrushProperties { Center = center, RadiusX = radius, RadiusY = radius }, stops);
    }

    private static void Rounded(ID2D1DeviceContext dc, float x, float y, float w, float h, float radius, ID2D1Brush brush)
    {
        var rr = new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), radius, radius);
        dc.FillRoundedRectangle(ref rr, brush);
    }

    public static void Mic(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, Color4 color) => Mic(dc, f, c, color, color, 0.75f);

    private static void Mic(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, Color4 color, Color4 capsule, float s)
    {
        Icone.Retangulo(dc, c.X - 3.5f * s, c.Y - 9 * s, 7 * s, 12 * s, 3.5f * s, capsule);
        using (var g = f.CreatePathGeometry())
        {
            using (var sink = g.Open())
            {
                sink.BeginFigure(new Vector2(c.X - 6.5f * s, c.Y - 2 * s), FigureBegin.Hollow);
                sink.AddArc(new ArcSegment
                {
                    Point = new Vector2(c.X + 6.5f * s, c.Y - 2 * s), Size = new Vortice.Mathematics.Size(6.5f * s, 6.5f * s),
                    SweepDirection = SweepDirection.CounterClockwise, ArcSize = ArcSize.Small,
                });
                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }
            using var b = dc.CreateSolidColorBrush(color);
            dc.DrawGeometry(g, b, 1.8f * s);
        }
        Icone.Linha(dc, new Vector2(c.X, c.Y + 4.5f * s), new Vector2(c.X, c.Y + 8 * s), 1.8f * s, color, f);
        Icone.Linha(dc, new Vector2(c.X - 4 * s, c.Y + 8.5f * s), new Vector2(c.X + 4 * s, c.Y + 8.5f * s), 1.8f * s, color, f);
    }

    /// <summary>A speaker with two sound waves.</summary>
    public static void Speaker(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, Color4 color)
    {
        Icone.Retangulo(dc, c.X - 8, c.Y - 3, 4, 6, 1, color);
        Icone.Triangulo(dc, f, new Vector2(c.X - 5, c.Y - 3), new Vector2(c.X + 1, c.Y - 7.5f), new Vector2(c.X + 1, c.Y + 7.5f), color);
        Icone.Triangulo(dc, f, new Vector2(c.X - 5, c.Y - 3), new Vector2(c.X + 1, c.Y + 7.5f), new Vector2(c.X - 5, c.Y + 3), color);
        foreach (float r in new[] { 4f, 7.5f })
        {
            using var g = f.CreatePathGeometry();
            using (var sink = g.Open())
            {
                float a = r * 0.7f;
                sink.BeginFigure(new Vector2(c.X + 1 + a, c.Y - a), FigureBegin.Hollow);
                sink.AddArc(new ArcSegment
                {
                    Point = new Vector2(c.X + 1 + a, c.Y + a), Size = new Vortice.Mathematics.Size(r, r),
                    SweepDirection = SweepDirection.Clockwise, ArcSize = ArcSize.Small,
                });
                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }
            using var b = dc.CreateSolidColorBrush(color);
            dc.DrawGeometry(g, b, 1.6f);
        }
    }
}
