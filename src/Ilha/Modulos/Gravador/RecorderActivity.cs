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

    public override string Tipo => "gravador";
    public override string Nome => Lang.T("Gravador", "Recorder");
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;
    public override (float De, float Ate)? MeioLivreCompacta => (LABEL_END + 6, LC - TIME_SPACE);

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
        // a recording in progress is closed properly even when the module turns off
        if (_session != null && _phase == Phase.Recording) Task.Run(() => { try { _session.Stop(); } catch (Exception e) { Log.Escrever("gravador: " + e.Message); } });
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

    private RecordingOptions Options(RecordKind kind)
    {
        var c = _cfg();
        bool audio = kind == RecordKind.Audio;
        string folder = audio
            ? (string.IsNullOrWhiteSpace(c.PastaAudios) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Ilha") : c.PastaAudios)
            : (string.IsNullOrWhiteSpace(c.PastaVideos) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Ilha") : c.PastaVideos);
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
                c.Botoes.Add(new Botao { Area = new RectangleF(0, 0, c.Tamanho.X, c.Tamanho.Y), Acao = () => ShowInFolder(path) });
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

    private static void ShowInFolder(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception e) { Log.Escrever("gravador: explorer: " + e.Message); }
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

    public override void Dispose() => _second.Dispose();
}

/// <summary>The recorder's icons, made of shapes only (the menu tiles and the card's switch rows).</summary>
internal static class Icons
{
    /// <summary>A video camera: the body and the lens cone; a red dot on it while recording video.</summary>
    public static void Camera(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r, bool live)
    {
        Icone.Retangulo(dc, r.X + 1, r.Y + 6, 15, 12, 3, Cor.Branco);
        Icone.Triangulo(dc, f, new Vector2(r.X + 15, r.Y + 12), new Vector2(r.X + 23, r.Y + 7.5f), new Vector2(r.X + 23, r.Y + 16.5f), Cor.Branco);
        if (live) Icone.Circulo(dc, new Vector2(r.X + 8.5f, r.Y + 12), 3, Cor.Vermelho);
    }

    /// <summary>A microphone: the capsule, the cradle and the stand; red capsule while recording audio.</summary>
    public static void MicTile(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r, bool live) =>
        Mic(dc, f, new Vector2(r.X + 12, r.Y + 12), Cor.Branco, live ? Cor.Vermelho : Cor.Branco, 1.2f);

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
