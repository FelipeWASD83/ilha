using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Ilha.LiveWallpaper;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI.Composition;
using RectangleF = System.Drawing.RectangleF;

namespace Ilha.Recorder;

/// <summary>A recording on disk: a video (MP4) from the videos folder or an audio (WAV) from the audio folder.</summary>
internal sealed record RecordingFile(string Path, bool Video, DateTime When)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>
/// The island's own player for the latest recordings: a black window with rounded corners, the list on the left (newest
/// first, with a thumbnail and the length) and the player on the right. Video plays through Windows' MediaPlayer drawn
/// in the compositor (the same one as the island); audio shows its waveform, lit up to where it is. Space plays and
/// pauses, the arrows seek 5 s and move through the list, Esc closes. One window at a time.
/// </summary>
internal sealed class RecordingsWindow : Form
{
    private const float W = 1000, H = 620, HEADER = 60, LIST_X = 16, LIST_W = 330, ROW = 68, PAD = 24;
    private const float PLAYER_X = LIST_X + LIST_W + 16, CONTROLS_H = 76;
    private const int MAX_ITEMS = 40;
    private static RecordingsWindow? _open;

    private readonly Servicos S;
    private readonly Func<(string Videos, string Audios)> _folders;
    private readonly Palco _p;
    private readonly float _s;
    private Windows.UI.Composition.Desktop.DesktopWindowTarget? _target;
    private ContainerVisual? _root;
    private Superficie? _static, _dynamic;
    private SpriteVisual? _videoVisual;
    private CompositionSurfaceBrush? _videoBrush;
    private MediaPlayer? _player;
    private MediaSource? _source;
    private MediaPlayerSurface? _surface;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 100 };
    private readonly ThumbCache _thumbs;
    private readonly Dictionary<string, TimeSpan> _lengths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float[]?> _waves = new(StringComparer.OrdinalIgnoreCase);
    private List<RecordingFile> _items = new();
    private int _selected = -1, _hover = -1, _scroll;
    private bool _seeking, _closeHover;
    private uint _videoW, _videoH;

    private RectangleF PlayerArea => new(PLAYER_X, HEADER + 4, W - PLAYER_X - PAD, H - HEADER - 4 - CONTROLS_H - 16);
    private RectangleF ControlsArea => new(PLAYER_X, H - CONTROLS_H - 12, W - PLAYER_X - PAD, CONTROLS_H);
    private RectangleF PlayButton => new(ControlsArea.X, ControlsArea.Y + 14, 48, 48);
    private RectangleF FolderButton => new(ControlsArea.Right - 150, ControlsArea.Y + 25, 150, 28);
    private RectangleF SeekBar => new(PlayButton.Right + 18, ControlsArea.Y + 36, FolderButton.X - 18 - (PlayButton.Right + 18) - 96, 6);
    private RectangleF CloseButton => new(W - PAD - 30, 16, 30, 30);
    private int VisibleRows => (int)((H - HEADER - 16) / ROW);

    /// <summary>Opens the window (or brings the open one forward), with `select` chosen when it is in the list.</summary>
    public static void Open(Servicos s, Func<(string, string)> folders, string? select = null)
    {
        if (_open is { IsDisposed: false } w)
        {
            w.Reload(select);
            if (w.WindowState == FormWindowState.Minimized) w.WindowState = FormWindowState.Normal;
            w.Activate();
            return;
        }
        _open = new RecordingsWindow(s, folders);
        // shown first: the composition tree (and the video's visual) is built when the window gets its handle
        _open.Show();
        _open.Reload(select);
        _open.Activate();
    }

    public static void CloseIfOpen()
    {
        if (_open is { IsDisposed: false } w) w.Close();
    }

    private RecordingsWindow(Servicos s, Func<(string, string)> folders)
    {
        S = s;
        _folders = folders;
        _p = s.Palco;
        _thumbs = new ThumbCache(() => S.NaInterface(() => { if (!IsDisposed) _static?.Redesenhar(); }));
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = System.Drawing.Color.Black;
        KeyPreview = true;
        ShowInTaskbar = true;
        Text = Lang.T("Gravações · Ilha", "Recordings · Ilha");
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        var screen = Screen.PrimaryScreen!.WorkingArea;
        _s = DeviceDpi / 96f;
        Size = new System.Drawing.Size((int)(W * _s), (int)(H * _s));
        Location = new System.Drawing.Point(screen.X + (screen.Width - Width) / 2, screen.Y + (screen.Height - Height) / 2);
        _tick.Tick += (_, _) => { if (_player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing || _seeking) _dynamic?.Redesenhar(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr h, int msg, IntPtr w, IntPtr l);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2, dark = 1, border = 0x002A2A2A;
        DwmSetWindowAttribute(Handle, 33, ref round, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE: round
        DwmSetWindowAttribute(Handle, 20, ref dark, 4);    // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(Handle, 34, ref border, 4);  // DWMWA_BORDER_COLOR: a dark gray rim
        Build();
    }

    // ------------------------------------------------------------------ the composition tree

    private Vector2 Px(Vector2 dip) => new(MathF.Round(dip.X * _s), MathF.Round(dip.Y * _s));

    private void Build()
    {
        var c = _p.C;
        _target = _p.G.Alvo(Handle);
        _root = c.CreateContainerVisual();
        _root.Size = Px(new Vector2(W, H));
        _target.Root = _root;

        _static = _p.G.NovaSuperficie(Px(new Vector2(W, H)), (dc, t) => Scaled(dc, DrawStatic), emPixels: true);
        _root.Children.InsertAtTop(Sprite(_static, Vector2.Zero));

        var pa = PlayerArea;
        _videoVisual = c.CreateSpriteVisual();
        _videoVisual.Offset = new Vector3(Px(new Vector2(pa.X, pa.Y)), 0);
        _videoVisual.Size = Px(new Vector2(pa.Width, pa.Height));
        var geo = c.CreateRoundedRectangleGeometry();
        geo.Size = _videoVisual.Size;
        geo.CornerRadius = new Vector2(14 * _s);
        _videoVisual.Clip = c.CreateGeometricClip(geo);
        _root.Children.InsertAtTop(_videoVisual);

        // the waveform and the controls change while playing: their own surface, redrawn alone
        var dynArea = new RectangleF(PLAYER_X, HEADER + 4, W - PLAYER_X - PAD, H - HEADER - 16);
        _dynamic = _p.G.NovaSuperficie(Px(new Vector2(dynArea.Width, dynArea.Height)), (dc, t) => Scaled(dc, d => DrawDynamic(d, dynArea)), emPixels: true);
        _root.Children.InsertAtTop(Sprite(_dynamic, new Vector2(dynArea.X, dynArea.Y)));
    }

    private SpriteVisual Sprite(Superficie s, Vector2 dip)
    {
        var v = _p.C.CreateSpriteVisual();
        v.Size = s.TamanhoPx;
        v.Offset = new Vector3(Px(dip), 0);
        v.Brush = s.Pincel;
        return v;
    }

    private void Scaled(ID2D1DeviceContext dc, Action<ID2D1DeviceContext> draw)
    {
        var before = dc.Transform;
        dc.Transform = Matrix3x2.CreateScale(_s) * before;
        try { draw(dc); }
        catch (Exception e) { Log.Escrever("gravações: desenho: " + e.Message); }
        finally { dc.Transform = before; }
    }

    // ------------------------------------------------------------------ items

    private void Reload(string? select)
    {
        var (videos, audios) = _folders();
        var list = new List<RecordingFile>();
        void Add(string dir, string pattern, bool video)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, pattern))
                {
                    // the pieces of a recording in progress (or one that failed to join) are not recordings yet
                    string n = Path.GetFileName(f);
                    if (n.Contains(".parcial.", StringComparison.OrdinalIgnoreCase) || n.Contains(".som-do-pc.", StringComparison.OrdinalIgnoreCase)
                        || n.Contains(".microfone.", StringComparison.OrdinalIgnoreCase) || n.Contains(".pc-sound.", StringComparison.OrdinalIgnoreCase)
                        || n.Contains(".microphone.", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new RecordingFile(f, video, File.GetLastWriteTime(f)));
                }
            }
            catch (Exception e) { Log.Escrever("gravações: lendo " + dir + ": " + e.Message); }
        }
        Add(videos, "*.mp4", true);
        Add(audios, "*.wav", false);
        _items = list.OrderByDescending(i => i.When).Take(MAX_ITEMS).ToList();
        foreach (var it in _items) LoadDetails(it);
        int want = select != null ? _items.FindIndex(i => string.Equals(i.Path, select, StringComparison.OrdinalIgnoreCase)) : -1;
        int keep = _selected >= 0 && _selected < _items.Count ? _selected : 0;
        Select(want >= 0 ? want : _items.Count > 0 ? keep : -1, play: want >= 0);
    }

    /// <summary>Length (and, for audio, the waveform) off the interface thread.</summary>
    private void LoadDetails(RecordingFile it)
    {
        if (_lengths.ContainsKey(it.Path)) return;
        _lengths[it.Path] = TimeSpan.Zero;
        Task.Run(async () =>
        {
            TimeSpan len = TimeSpan.Zero;
            float[]? wave = null;
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(it.Path);
                len = it.Video ? (await file.Properties.GetVideoPropertiesAsync()).Duration : (await file.Properties.GetMusicPropertiesAsync()).Duration;
            }
            catch { }
            if (!it.Video)
            {
                try { wave = WavePeaks(it.Path, 240, out var wavLen); if (len == TimeSpan.Zero) len = wavLen; }
                catch (Exception e) { Log.Escrever("gravações: forma de onda: " + e.Message); }
            }
            S.NaInterface(() =>
            {
                if (IsDisposed) return;
                _lengths[it.Path] = len;
                _waves[it.Path] = wave;
                _static?.Redesenhar();
                _dynamic?.Redesenhar();
            });
        });
    }

    /// <summary>`count` peaks (0 to 1) of a PCM or float WAV, and its length. Reads the file once, in blocks.</summary>
    internal static float[] WavePeaks(string path, int count, out TimeSpan length)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var r = new BinaryReader(f);
        if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("não é RIFF");
        r.ReadUInt32();
        if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("não é WAVE");
        int channels = 2, rate = 48000, bits = 16, tag = 1;
        long dataStart = -1, dataLen = 0;
        while (f.Position + 8 <= f.Length)
        {
            string id = new string(r.ReadChars(4));
            long size = r.ReadUInt32();
            long next = f.Position + size + (size & 1);
            if (id == "fmt ")
            {
                tag = r.ReadUInt16(); channels = r.ReadUInt16(); rate = (int)r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt16(); bits = r.ReadUInt16();
                if (tag == 0xFFFE && size >= 40) { r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt32(); tag = r.ReadUInt16(); }
            }
            else if (id == "data") { dataStart = f.Position; dataLen = Math.Min(size, f.Length - f.Position); break; }
            f.Position = next;
        }
        if (dataStart < 0) throw new InvalidDataException("sem dados");
        int bytes = bits / 8, block = Math.Max(1, bytes * channels);
        long frames = dataLen / block;
        length = TimeSpan.FromSeconds(frames / (double)Math.Max(1, rate));
        var peaks = new float[count];
        if (frames == 0) return peaks;
        var buf = new byte[block * 4096];
        long frame = 0;
        f.Position = dataStart;
        while (frame < frames)
        {
            int want = (int)Math.Min(4096, frames - frame);
            int got = f.Read(buf, 0, want * block) / block;
            if (got <= 0) break;
            for (int i = 0; i < got; i++)
            {
                int o = i * block;
                float v = tag == 3 && bits == 32 ? MathF.Abs(BitConverter.ToSingle(buf, o))
                    : bits == 16 ? MathF.Abs(BitConverter.ToInt16(buf, o) / 32768f)
                    : bits == 24 ? MathF.Abs(((buf[o] | (buf[o + 1] << 8) | (buf[o + 2] << 16)) << 8 >> 8) / 8388608f)
                    : MathF.Abs(BitConverter.ToInt32(buf, o) / 2147483648f);
                int k = (int)((frame + i) * count / frames);
                if (v > peaks[k]) peaks[k] = v;
            }
            frame += got;
        }
        float max = peaks.Max();
        if (max > 0.001f) for (int i = 0; i < count; i++) peaks[i] = MathF.Sqrt(peaks[i] / max);
        return peaks;
    }

    // ------------------------------------------------------------------ playing

    private void Select(int index, bool play)
    {
        StopPlayer();
        _selected = index;
        _static?.Redesenhar();
        _dynamic?.Redesenhar();
        if (index < 0 || index >= _items.Count) return;
        int visible = VisibleRows;
        if (index < _scroll) _scroll = index;
        else if (index >= _scroll + visible) _scroll = index - visible + 1;
        var it = _items[index];
        try
        {
            _player = new MediaPlayer { AutoPlay = false, IsLoopingEnabled = false };
            // keeps the island's own player out of the Windows media flyout and the media keys
            _player.CommandManager.IsEnabled = false;
            _player.PlaybackSession.PlaybackStateChanged += (_, _) => S.NaInterface(() => { if (!IsDisposed) { _dynamic?.Redesenhar(); UpdateTick(); } });
            _player.MediaEnded += (_, _) => S.NaInterface(() => { if (!IsDisposed && _player != null) { _player.PlaybackSession.Position = TimeSpan.Zero; _dynamic?.Redesenhar(); } });
            _player.MediaFailed += (_, e) => Log.Escrever($"gravações: não tocou ({e.Error}): {e.ErrorMessage}");
            _player.PlaybackSession.NaturalVideoSizeChanged += (s, _) =>
            {
                uint w = s.NaturalVideoWidth, h = s.NaturalVideoHeight;
                S.NaInterface(() => { if (!IsDisposed) { _videoW = w; _videoH = h; SizeVideo(); } });
            };
            _source = MediaSource.CreateFromUri(new Uri(it.Path));
            _player.Source = _source;
            if (it.Video && _videoVisual != null)
            {
                _surface = _player.GetSurface(_p.C);
                _videoBrush = _p.C.CreateSurfaceBrush(_surface.CompositionSurface);
                _videoBrush.Stretch = CompositionStretch.Uniform;
                _videoBrush.HorizontalAlignmentRatio = 0.5f;
                _videoBrush.VerticalAlignmentRatio = 0.5f;
                _videoVisual.Brush = _videoBrush;
                SizeVideo();
            }
            if (play) _player.Play();
        }
        catch (Exception e) { Log.Escrever("gravações: abrir " + it.Path + ": " + e.Message); }
    }

    /// <summary>The player letterboxes inside its surface, so the surface takes the video's shape at the size it shows.</summary>
    private void SizeVideo()
    {
        if (_player == null || _videoVisual == null) return;
        var sz = _videoVisual.Size;
        if (_videoW == 0 || _videoH == 0) { _player.SetSurfaceSize(new Windows.Foundation.Size(sz.X, sz.Y)); return; }
        double k = Math.Min(1.0, Math.Min(sz.X / _videoW, sz.Y / _videoH));
        _player.SetSurfaceSize(new Windows.Foundation.Size(Math.Max(1, Math.Round(_videoW * k)), Math.Max(1, Math.Round(_videoH * k))));
    }

    private void StopPlayer()
    {
        _tick.Stop();
        if (_videoVisual != null) _videoVisual.Brush = null;
        _videoBrush?.Dispose();
        _videoBrush = null;
        _surface?.Dispose();
        _surface = null;
        if (_player != null)
        {
            try { _player.Pause(); _player.Source = null; } catch { }
            _player.Dispose();
            _player = null;
        }
        // the source holds the decoder: closed now, not when the GC gets to it
        _source?.Dispose();
        _source = null;
        _videoW = _videoH = 0;
    }

    private void UpdateTick()
    {
        bool playing = _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        if (playing && !_tick.Enabled) _tick.Start();
        else if (!playing && !_seeking && _tick.Enabled) _tick.Stop();
    }

    private void TogglePlay()
    {
        if (_player == null) { if (_selected >= 0) Select(_selected, play: true); return; }
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause();
        else _player.Play();
    }

    private TimeSpan Length => _player?.PlaybackSession.NaturalDuration is { } d && d > TimeSpan.Zero ? d
        : _selected >= 0 && _selected < _items.Count && _lengths.TryGetValue(_items[_selected].Path, out var l) ? l : TimeSpan.Zero;

    private void SeekTo(float x)
    {
        var bar = SeekBar;
        if (_player == null || Length <= TimeSpan.Zero) return;
        float f = Math.Clamp((x - bar.X) / bar.Width, 0, 1);
        _player.PlaybackSession.Position = TimeSpan.FromTicks((long)(Length.Ticks * f));
        _dynamic?.Redesenhar();
    }

    private void SeekBy(double seconds)
    {
        if (_player == null) return;
        var to = _player.PlaybackSession.Position + TimeSpan.FromSeconds(seconds);
        _player.PlaybackSession.Position = to < TimeSpan.Zero ? TimeSpan.Zero : to > Length ? Length : to;
        _dynamic?.Redesenhar();
    }

    // ------------------------------------------------------------------ drawing

    private static string Clock(TimeSpan t) => RecorderActivity.Clock(t);

    private void DrawStatic(ID2D1DeviceContext dc)
    {
        _p.Texto.Desenhar(dc, Lang.T("Gravações", "Recordings"), new Vector2(PAD, 16), 22, Cor.Branco, FontWeight.SemiBold);
        string sub = _items.Count switch
        {
            0 => Lang.T("nenhuma ainda", "none yet"),
            1 => Lang.T("1 gravação", "1 recording"),
            _ => Lang.T($"as {_items.Count} mais recentes", $"the latest {_items.Count}"),
        };
        var tm = _p.Texto.Medir(Lang.T("Gravações", "Recordings"), 22, FontWeight.SemiBold);
        _p.Texto.Desenhar(dc, sub, new Vector2(PAD + tm.X + 12, 24), 13, Cor.Secundario, FontWeight.Normal);
        // close
        var cb = CloseButton;
        var cc = new Vector2(cb.X + cb.Width / 2, cb.Y + cb.Height / 2);
        Icone.Circulo(dc, cc, 15, new Color4(1, 1, 1, _closeHover ? 0.18f : 0.08f));
        Icone.Linha(dc, cc + new Vector2(-5, -5), cc + new Vector2(5, 5), 1.8f, Cor.Branco, _p.G.Fabrica);
        Icone.Linha(dc, cc + new Vector2(5, -5), cc + new Vector2(-5, 5), 1.8f, Cor.Branco, _p.G.Fabrica);
        // the player's box
        var pa = PlayerArea;
        Icone.Retangulo(dc, pa.X, pa.Y, pa.Width, pa.Height, 14, new Color4(1, 1, 1, 0.045f));
        if (_items.Count == 0)
        {
            string e = Lang.T("Grave pelo menu da ilha: Gravar vídeo ou Gravar áudio.", "Record from the island's menu: Record video or Record audio.");
            var em = _p.Texto.Medir(e, 14, FontWeight.Normal);
            _p.Texto.Desenhar(dc, e, new Vector2(pa.X + (pa.Width - em.X) / 2, pa.Y + pa.Height / 2 - 10), 14, Cor.Secundario, FontWeight.Normal);
        }
        // the list
        dc.PushAxisAlignedClip(new RectangleF(LIST_X, HEADER, LIST_W, H - HEADER - 12), AntialiasMode.Aliased);
        for (int row = 0; row <= VisibleRows; row++)
        {
            int i = _scroll + row;
            if (i >= _items.Count) break;
            var it = _items[i];
            float y = HEADER + 4 + row * ROW;
            if (i == _selected) Icone.Retangulo(dc, LIST_X, y, LIST_W, ROW - 6, 12, new Color4(1, 1, 1, 0.12f));
            else if (i == _hover) Icone.Retangulo(dc, LIST_X, y, LIST_W, ROW - 6, 12, new Color4(1, 1, 1, 0.05f));
            var thumb = new RectangleF(LIST_X + 8, y + 7, 84, ROW - 20);
            if (it.Video)
            {
                if (_thumbs.Get(new WallpaperItem(it.Path, it.Name, "video", it.Path, null)) is { } t) t.Draw(dc, thumb.X, thumb.Y, thumb.Width, thumb.Height, 7);
                else Icone.Retangulo(dc, thumb.X, thumb.Y, thumb.Width, thumb.Height, 7, new Color4(1, 1, 1, 0.08f));
            }
            else MiniWave(dc, thumb, _waves.TryGetValue(it.Path, out var w) ? w : null);
            float tx = thumb.Right + 12;
            _p.Texto.Desenhar(dc, Title(it), new Vector2(tx, y + 11), 14, Cor.Branco, FontWeight.SemiBold, largura: LIST_X + LIST_W - tx - 8);
            string kind = it.Video ? Lang.T("Vídeo", "Video") : Lang.T("Áudio", "Audio");
            string len = _lengths.TryGetValue(it.Path, out var l) && l > TimeSpan.Zero ? " · " + Clock(l) : "";
            _p.Texto.Desenhar(dc, kind + len, new Vector2(tx, y + 33), 12, Cor.Secundario, FontWeight.Normal, largura: LIST_X + LIST_W - tx - 8);
        }
        dc.PopAxisAlignedClip();
        int rows = _items.Count;
        if (rows > VisibleRows)
        {
            float trackH = H - HEADER - 20, barH = MathF.Max(30, trackH * VisibleRows / rows);
            float barY = HEADER + 4 + (trackH - barH) * _scroll / Math.Max(1, rows - VisibleRows);
            Icone.Retangulo(dc, LIST_X + LIST_W + 4, barY, 3, barH, 1.5f, new Color4(1, 1, 1, 0.25f));
        }
    }

    /// <summary>"02/11 · 17:38" from the file's time (the names are all "Gravação" plus the date).</summary>
    private static string Title(RecordingFile it) =>
        it.When.ToString(Lang.T("dd/MM · HH:mm", "MMM d · h:mm tt"), Lang.Culture);

    private static void MiniWave(ID2D1DeviceContext dc, RectangleF r, float[]? peaks)
    {
        Icone.Retangulo(dc, r.X, r.Y, r.Width, r.Height, 7, new Color4(1, 0.23f, 0.19f, 0.16f));
        const int N = 16;
        float step = (r.Width - 12) / N;
        for (int i = 0; i < N; i++)
        {
            float v = peaks == null || peaks.Length == 0 ? 0.25f + 0.2f * MathF.Abs(MathF.Sin(i * 1.7f)) : peaks[i * peaks.Length / N];
            float h = MathF.Max(2, (r.Height - 14) * v);
            Icone.Retangulo(dc, r.X + 6 + i * step + step * 0.2f, r.Y + (r.Height - h) / 2, step * 0.6f, h, step * 0.3f, new Color4(1, 0.36f, 0.32f, 0.95f));
        }
    }

    private void DrawDynamic(ID2D1DeviceContext dc, RectangleF area)
    {
        dc.Transform = Matrix3x2.CreateTranslation(-area.X, -area.Y) * dc.Transform;
        if (_selected < 0 || _selected >= _items.Count) return;
        var it = _items[_selected];
        var pos = _player?.PlaybackSession.Position ?? TimeSpan.Zero;
        var len = Length;
        float progress = len > TimeSpan.Zero ? (float)Math.Clamp(pos.TotalSeconds / len.TotalSeconds, 0, 1) : 0;
        var pa = PlayerArea;
        if (!it.Video)
        {
            var peaks = _waves.TryGetValue(it.Path, out var w) ? w : null;
            float x0 = pa.X + 40, x1 = pa.Right - 40, mid = pa.Y + pa.Height / 2, half = pa.Height * 0.32f;
            if (peaks == null) _p.Texto.Desenhar(dc, Lang.T("Lendo o áudio…", "Reading the audio…"), new Vector2(x0, mid - 8), 13, Cor.Secundario, FontWeight.Normal);
            else
            {
                float step = (x1 - x0) / peaks.Length;
                for (int i = 0; i < peaks.Length; i++)
                {
                    float h = MathF.Max(2, half * 2 * peaks[i]);
                    bool played = (i + 0.5f) / peaks.Length <= progress;
                    Icone.Retangulo(dc, x0 + i * step + step * 0.18f, mid - h / 2, MathF.Max(1.5f, step * 0.64f), h, step * 0.32f,
                        played ? new Color4(1, 0.36f, 0.32f, 1) : new Color4(1, 1, 1, 0.22f));
                }
            }
        }
        // controls
        var pb = PlayButton;
        var pc = new Vector2(pb.X + pb.Width / 2, pb.Y + pb.Height / 2);
        Icone.Circulo(dc, pc, pb.Width / 2, Cor.Branco);
        var dark = new Color4(0, 0, 0, 1);
        if (_player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            Icone.Retangulo(dc, pc.X - 7, pc.Y - 8, 5, 16, 1.5f, dark);
            Icone.Retangulo(dc, pc.X + 2, pc.Y - 8, 5, 16, 1.5f, dark);
        }
        else Icone.Triangulo(dc, _p.G.Fabrica, pc + new Vector2(-5, -9), pc + new Vector2(10, 0), pc + new Vector2(-5, 9), dark);
        var sb = SeekBar;
        Icone.Retangulo(dc, sb.X, sb.Y, sb.Width, sb.Height, sb.Height / 2, new Color4(1, 1, 1, 0.18f));
        Icone.Retangulo(dc, sb.X, sb.Y, MathF.Max(sb.Height, sb.Width * progress), sb.Height, sb.Height / 2, Cor.Branco);
        Icone.Circulo(dc, new Vector2(sb.X + sb.Width * progress, sb.Y + sb.Height / 2), 7, Cor.Branco);
        string time = Clock(pos) + " / " + Clock(len);
        _p.Texto.Desenhar(dc, time, new Vector2(sb.Right + 14, sb.Y - 7), 13, Cor.Secundario, FontWeight.Medium, tabular: true);
        _p.Texto.Desenhar(dc, it.Name, new Vector2(sb.X, sb.Y - 30), 13, Cor.Branco, FontWeight.Medium, largura: sb.Width);
        var fb = FolderButton;
        Icone.Retangulo(dc, fb.X, fb.Y, fb.Width, fb.Height, fb.Height / 2, new Color4(1, 1, 1, 0.12f));
        string label = Lang.T("Mostrar na pasta", "Show in folder");
        var lm = _p.Texto.Medir(label, 12.5f, FontWeight.Medium);
        _p.Texto.Desenhar(dc, label, new Vector2(fb.X + (fb.Width - lm.X) / 2, fb.Y + (fb.Height - lm.Y) / 2), 12.5f, Cor.Branco, FontWeight.Medium);
    }

    // ------------------------------------------------------------------ input

    private Vector2 Dip(System.Drawing.Point p) => new(p.X / _s, p.Y / _s);

    private int RowAt(Vector2 d)
    {
        if (d.X < LIST_X || d.X > LIST_X + LIST_W || d.Y < HEADER + 4) return -1;
        int i = _scroll + (int)((d.Y - HEADER - 4) / ROW);
        return i < _items.Count ? i : -1;
    }

    private static bool In(RectangleF r, Vector2 d, float pad = 0) =>
        d.X >= r.X - pad && d.X <= r.Right + pad && d.Y >= r.Y - pad && d.Y <= r.Bottom + pad;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var d = Dip(e.Location);
        if (In(CloseButton, d, 4)) { Close(); return; }
        if (In(PlayButton, d, 2)) { TogglePlay(); return; }
        if (In(FolderButton, d, 2))
        {
            if (_selected >= 0 && _selected < _items.Count)
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_items[_selected].Path}\""); } catch { }
            return;
        }
        if (In(SeekBar, d, 10)) { _seeking = true; Capture = true; SeekTo(d.X); _tick.Start(); return; }
        int row = RowAt(d);
        if (row >= 0) { Select(row, play: true); return; }
        if (In(PlayerArea, d)) { TogglePlay(); return; }
        if (d.Y < HEADER)
        {
            // the header drags the window, like a title bar
            ReleaseCapture();
            SendMessageW(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var d = Dip(e.Location);
        if (_seeking) { SeekTo(d.X); return; }
        int hover = RowAt(d);
        bool close = In(CloseButton, d, 4);
        if (hover != _hover || close != _closeHover)
        {
            _hover = hover;
            _closeHover = close;
            _static?.Redesenhar();
        }
        Cursor = hover >= 0 || close || In(PlayButton, d) || In(FolderButton, d) || In(SeekBar, d, 10) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_seeking) return;
        _seeking = false;
        Capture = false;
        UpdateTick();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover == -1 && !_closeHover) return;
        _hover = -1;
        _closeHover = false;
        _static?.Redesenhar();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int max = Math.Max(0, _items.Count - VisibleRows);
        int next = Math.Clamp(_scroll - Math.Sign(e.Delta), 0, max);
        if (next == _scroll) return;
        _scroll = next;
        _static?.Redesenhar();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: Close(); return true;
            case Keys.Space: TogglePlay(); return true;
            case Keys.Left: SeekBy(-5); return true;
            case Keys.Right: SeekBy(5); return true;
            case Keys.Up: if (_selected > 0) Select(_selected - 1, play: false); return true;
            case Keys.Down: if (_selected < _items.Count - 1) Select(_selected + 1, play: false); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(System.Drawing.Color.Black);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopPlayer();
        _tick.Dispose();
        _thumbs.Dispose();
        _static?.Dispose();
        _dynamic?.Dispose();
        _videoVisual?.Dispose();
        _root?.Dispose();
        _target?.Dispose();
        if (_open == this) _open = null;
        base.OnFormClosed(e);
    }
}
