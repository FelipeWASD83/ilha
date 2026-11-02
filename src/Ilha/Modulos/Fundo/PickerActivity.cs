using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Forms;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Windows.UI.Composition;
using RectangleF = System.Drawing.RectangleF;

namespace Ilha.LiveWallpaper;

/// <summary>
/// The wallpaper picker, like choosing a wallpaper on the iPhone or a face on the Apple Watch: a black card with the
/// chosen wallpapers in a row, the middle one big with its name under it and the neighbours smaller and dimmer at the
/// sides. The wheel, the arrows or a click on a neighbour slides the row (in the compositor); a click on the middle one,
/// or Enter, puts it on the selected monitor through Fundo Vivo. Only the wallpapers he picked show up: the "Escolher"
/// mode lists Fundo Vivo's whole library in a grid with a check on each one.
/// </summary>
internal sealed class PickerActivity : Atividade
{
    private const float L = 620, HEADER = 32, CARD_RADIUS = 12, SIDE_SCALE = 0.66f;
    private const int COLS = 4, VISIBLE_ROWS = 3;
    private const float TILE_GAP = 12, TITLE_H = 20, ROW_GAP = 10;
    private static readonly Color4 ButtonBg = new(1, 1, 1, 0.12f);

    private readonly Servicos S;
    private readonly Func<ConfigFundo> _cfg;
    private readonly Action<ConfigFundo> _save;
    private readonly ThumbCache _thumbs;
    private readonly Timer _noteTimer = new() { Interval = 3500 };

    private List<WallpaperItem> _library = new();
    private DateTime _scannedAt = DateTime.MinValue;
    private bool _scanning, _found = true;
    private List<MonitorEntry> _monitors = new();
    // -1 until the first open, which starts on the main monitor
    private int _monitor = -1;
    private Dictionary<string, string> _assigned = new(StringComparer.OrdinalIgnoreCase);
    private int _index, _scrollRow, _wheel;
    private bool _choosing, _applying, _keys;
    private string? _note;
    private bool _noteError;
    private string _layout = "";
    private float _scale = 1;
    // the cards of the carousel on screen; a scene being replaced still slides out with its own list
    private List<(WallpaperItem Item, SpriteVisual Visual)> _cards = new();
    private Compositor? _compositor;

    public override string Tipo => "fundo";
    public override string Nome => "Fundo Vivo";
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    public PickerActivity(Servicos s, Func<ConfigFundo> cfg, Action<ConfigFundo> save)
    {
        S = s;
        _cfg = cfg;
        _save = save;
        _thumbs = new ThumbCache(() => S.NaInterface(Redesenhar));
        _noteTimer.Tick += (_, _) => { _noteTimer.Stop(); _note = null; Redesenhar(); };
    }

    private bool Open => S.Ilha.Expandida == this;
    private MonitorEntry? Monitor => _monitors.Count == 0 ? null : _monitors[Math.Clamp(_monitor, 0, _monitors.Count - 1)];

    /// <summary>The chosen wallpapers that still exist in the library, in the order he picked them.</summary>
    private List<WallpaperItem> Favorites()
    {
        var byId = _library.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
        return _cfg().Favoritos.Select(id => byId.TryGetValue(id, out var i) ? i : null).OfType<WallpaperItem>().ToList();
    }

    // ------------------------------------------------------------------ opening

    public void OpenCard()
    {
        _choosing = false;
        _note = null;
        _monitors = FundoVivoBridge.Monitors();
        int primary = _monitors.FindIndex(m => m.Primary);
        if (_monitor >= _monitors.Count || _monitor < 0) _monitor = Math.Max(0, primary);
        ReadAssigned();
        Rescan();
        CenterOnCurrent();
        Relayout();
        S.Ilha.Expandir(this);
    }

    private void ReadAssigned()
    {
        var cfg = FundoVivoBridge.ReadConfig();
        _assigned = FundoVivoBridge.Assigned(cfg);
        _found = cfg != null || FundoVivoBridge.FindExe(_cfg().Exe) != null;
    }

    /// <summary>The library again when it is older than 30 s (a few hundred small project.json files, off the UI thread).</summary>
    private void Rescan()
    {
        if (_scanning || DateTime.UtcNow - _scannedAt < TimeSpan.FromSeconds(30)) return;
        _scanning = true;
        Task.Run(() => FundoVivoBridge.Scan(FundoVivoBridge.ReadConfig())).ContinueWith(t => S.NaInterface(() =>
        {
            _scanning = false;
            if (t.IsFaulted) { Log.Escrever("fundo: biblioteca: " + t.Exception?.GetBaseException().Message); return; }
            _library = t.Result;
            _scannedAt = DateTime.UtcNow;
            Log.Escrever($"fundo: biblioteca com {_library.Count} wallpapers, {Favorites().Count} escolhidos");
            CenterOnCurrent();
            Relayout();
        }));
    }

    private void CenterOnCurrent()
    {
        var fav = Favorites();
        if (Monitor is { } m && _assigned.TryGetValue(m.Id, out var cur))
        {
            int i = fav.FindIndex(f => string.Equals(f.Id, cur, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) { _index = i; return; }
        }
        _index = Math.Clamp(_index, 0, Math.Max(0, fav.Count - 1));
    }

    public override void ExpandidaMudou(bool aberta)
    {
        if (aberta && !_keys) { S.Entrada.Navegar += OnKey; _keys = true; }
        else if (!aberta && _keys) { S.Entrada.Navegar -= OnKey; _keys = false; }
    }

    private void OnKey(Keys k)
    {
        if (!Open || _choosing) return;
        switch (k)
        {
            case Keys.Left: Move(-1); break;
            case Keys.Right: Move(+1); break;
            case Keys.Enter: Apply(); break;
            case Keys.Up: SetMonitor(_monitor - 1); break;
            case Keys.Down: SetMonitor(_monitor + 1); break;
        }
    }

    public override bool Rolar(int delta)
    {
        if (!Open) return false;
        // one step per notch; a smooth wheel sends small deltas, added up until a notch's worth
        _wheel += delta;
        int steps = _wheel / 120;
        if (steps == 0) return true;
        _wheel -= steps * 120;
        if (_choosing)
        {
            int rows = (int)Math.Ceiling(_library.Count / (double)COLS);
            int next = Math.Clamp(_scrollRow - steps, 0, Math.Max(0, rows - VISIBLE_ROWS));
            if (next != _scrollRow) { _scrollRow = next; Relayout(); }
        }
        else Move(-steps);
        return true;
    }

    // ------------------------------------------------------------------ actions

    private void Move(int by)
    {
        int count = Favorites().Count;
        if (count == 0) return;
        int next = Math.Clamp(_index + by, 0, count - 1);
        if (next == _index) return;
        _index = next;
        Animate(animate: true);
        Redesenhar();
    }

    private void SetMonitor(int i)
    {
        if (_monitors.Count < 2) return;
        i = Math.Clamp(i, 0, _monitors.Count - 1);
        if (i == _monitor) return;
        _monitor = i;
        CenterOnCurrent();
        Relayout();
    }

    private void Apply()
    {
        var fav = Favorites();
        if (_applying || fav.Count == 0 || Monitor is not { } mon) return;
        var item = fav[Math.Clamp(_index, 0, fav.Count - 1)];
        _applying = true;
        string? exeCfg = _cfg().Exe;
        Task.Run(() =>
        {
            FundoVivoBridge.Assign(mon, item);
            if (FundoVivoBridge.Reload()) return "reload";
            var exe = FundoVivoBridge.FindExe(exeCfg) ?? throw new FileNotFoundException(Lang.T("Fundo Vivo não encontrado", "Fundo Vivo not found"));
            return FundoVivoBridge.StartEngine(exe) ? "abriu" : throw new InvalidOperationException(Lang.T("O Fundo Vivo não abriu", "Fundo Vivo didn't open"));
        }).ContinueWith(t => S.NaInterface(() =>
        {
            _applying = false;
            if (t.IsFaulted)
            {
                var e = t.Exception!.GetBaseException();
                Log.Escrever("fundo: não aplicou: " + e.Message);
                Note(e.Message, error: true);
                return;
            }
            _assigned[mon.Id] = item.Id;
            Log.Escrever($"fundo: {item.Title} em {mon.Label} ({t.Result})");
            Note(Lang.T("Aplicado", "Applied"), error: false);
        }));
    }

    private void Note(string text, bool error)
    {
        _note = text;
        _noteError = error;
        _noteTimer.Stop();
        _noteTimer.Start();
        Redesenhar();
    }

    private void Toggle(WallpaperItem item)
    {
        var c = _cfg();
        if (c.Favoritos.RemoveAll(id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase)) == 0) c.Favoritos.Add(item.Id);
        _save(c);
        Redesenhar();
    }

    private void Choose(bool on)
    {
        _choosing = on;
        _scrollRow = 0;
        if (!on) CenterOnCurrent();
        Relayout();
    }

    private void Relayout()
    {
        string k = $"{_choosing}|{_monitor}|{_monitors.Count}|{Favorites().Count}|{_library.Count}|{_found}|{_scrollRow}";
        if (k != _layout) { _layout = k; MudouTamanho(); }
        else Redesenhar();
    }

    /// <summary>A monitor's name on the island: main, portrait, or its number.</summary>
    private string MonitorName(int i)
    {
        var m = _monitors[i];
        if (m.Primary) return Lang.T("Principal", "Main");
        if (m.Height > m.Width) return Lang.T("Em pé", "Portrait");
        return Lang.T("Monitor ", "Monitor ") + (i + 1);
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>The middle card's size: the selected monitor's shape, at most 340 wide and 230 tall.</summary>
    private Vector2 CardSize()
    {
        float aspect = Monitor is { } m && m.Height > 0 ? m.Width / (float)m.Height : 16f / 9f;
        float w = 340, h = w / aspect;
        if (h > 230) { h = 230; w = h * aspect; }
        return new Vector2(MathF.Round(w), MathF.Round(h));
    }

    private float Spacing(Vector2 card) => card.X / 2 + card.X * SIDE_SCALE / 2 + 20;

    private float CarouselTop => HEADER + 10;

    private (Vector3 Offset, float Scale, float Opacity) Place(int d, Vector2 card)
    {
        int ad = Math.Abs(d);
        float scale = ad == 0 ? 1 : ad == 1 ? SIDE_SCALE : 0.56f;
        float x = L / 2 + Math.Sign(d) * (ad == 0 ? 0 : Spacing(card) + (ad - 1) * (card.X * 0.6f + 14));
        float opacity = ad == 0 ? 1 : ad == 1 ? 0.5f : ad == 2 ? 0.2f : 0;
        var center = new Vector2(x, CarouselTop + card.Y / 2);
        return (new Vector3((center.X - card.X / 2) * _scale, (center.Y - card.Y / 2) * _scale, 0), scale, opacity);
    }

    private void Animate(bool animate)
    {
        if (_compositor == null) return;
        var card = CardSize();
        var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f));
        for (int i = 0; i < _cards.Count; i++)
        {
            var (_, v) = _cards[i];
            var (offset, scale, opacity) = Place(i - _index, card);
            if (!animate)
            {
                v.Offset = offset;
                v.Scale = new Vector3(scale, scale, 1);
                v.Opacity = opacity;
                continue;
            }
            var o = _compositor.CreateVector3KeyFrameAnimation();
            o.InsertKeyFrame(1f, offset, ease);
            o.Duration = TimeSpan.FromMilliseconds(340);
            v.StartAnimation("Offset", o);
            var s = _compositor.CreateVector3KeyFrameAnimation();
            s.InsertKeyFrame(1f, new Vector3(scale, scale, 1), ease);
            s.Duration = TimeSpan.FromMilliseconds(340);
            v.StartAnimation("Scale", s);
            var a = _compositor.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(1f, opacity, ease);
            a.Duration = TimeSpan.FromMilliseconds(340);
            v.StartAnimation("Opacity", a);
        }
    }

    // ------------------------------------------------------------------ scenes

    protected override Cena Montar(Palco p, Forma f)
    {
        _scale = p.Escala;
        _compositor = p.C;
        if (f != Forma.Expandida) return p.NovaCena(new Vector2(24, 24));
        if (!_found) return Message(p, Lang.T("Fundo Vivo não encontrado", "Fundo Vivo not found"),
            Lang.T("O app fica em github.com/FelipeWASD83/fundo-vivo", "The app is at github.com/FelipeWASD83/fundo-vivo"), false);
        if (_choosing) return ChooseScene(p);
        if (Favorites().Count == 0)
            return Message(p, _scanning && _library.Count == 0 ? Lang.T("Lendo a biblioteca…", "Reading the library…") : Lang.T("Nenhum wallpaper escolhido", "No wallpaper chosen"),
                Lang.T("Escolha os que aparecem aqui, da biblioteca do Fundo Vivo.", "Pick the ones that show here, from Fundo Vivo's library."), true);
        return CarouselScene(p);
    }

    private void Header(ID2D1DeviceContext dc, Palco p, string title, string button, List<(RectangleF Area, int Monitor)> pills)
    {
        p.Texto.Desenhar(dc, title, new Vector2(0, 6), 13, Cor.Secundario, largura: 200);
        foreach (var (r, i) in pills)
        {
            bool on = i == _monitor;
            Icone.Retangulo(dc, r.X, r.Y, r.Width, r.Height, r.Height / 2, on ? Cor.Branco : ButtonBg);
            string label = MonitorName(i);
            var m = p.Texto.Medir(label, 12.5f, FontWeight.Medium);
            p.Texto.Desenhar(dc, label, new Vector2(r.X + (r.Width - m.X) / 2, r.Y + (r.Height - m.Y) / 2), 12.5f,
                on ? new Color4(0, 0, 0, 1) : Cor.Branco, FontWeight.Medium);
        }
        var b = ButtonArea(p, button);
        Icone.Retangulo(dc, b.X, b.Y, b.Width, b.Height, b.Height / 2, ButtonBg);
        var bm = p.Texto.Medir(button, 12.5f, FontWeight.Medium);
        p.Texto.Desenhar(dc, button, new Vector2(b.X + (b.Width - bm.X) / 2, b.Y + (b.Height - bm.Y) / 2), 12.5f, Cor.Branco, FontWeight.Medium);
    }

    private static RectangleF ButtonArea(Palco p, string label)
    {
        float w = p.Texto.Medir(label, 12.5f, FontWeight.Medium).X + 26;
        return new RectangleF(L - w, 2, w, 28);
    }

    private List<(RectangleF Area, int Monitor)> Pills(Palco p)
    {
        var list = new List<(RectangleF, int)>();
        if (_monitors.Count < 2) return list;
        float total = 0;
        var widths = new List<float>();
        for (int i = 0; i < _monitors.Count; i++)
        {
            float w = p.Texto.Medir(MonitorName(i), 12.5f, FontWeight.Medium).X + 24;
            widths.Add(w);
            total += w + 6;
        }
        float x = (L - total + 6) / 2;
        for (int i = 0; i < _monitors.Count; i++)
        {
            list.Add((new RectangleF(x, 2, widths[i], 28), i));
            x += widths[i] + 6;
        }
        return list;
    }

    private Cena CarouselScene(Palco p)
    {
        var fav = Favorites();
        _index = Math.Clamp(_index, 0, fav.Count - 1);
        var card = CardSize();
        float yText = CarouselTop + card.Y + 14;
        var c = p.NovaCena(new Vector2(L, yText + 64));
        var pills = Pills(p);
        string choose = Lang.T("Escolher", "Choose");
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var favNow = Favorites();
            Header(dc, p, "Fundo Vivo", choose, pills);
            if (favNow.Count == 0) return;
            var item = favNow[Math.Clamp(_index, 0, favNow.Count - 1)];
            var tm = p.Texto.Medir(item.Title, 15, FontWeight.SemiBold);
            float tw = MathF.Min(tm.X, L - 40);
            p.Texto.Desenhar(dc, item.Title, new Vector2((L - tw) / 2, yText), 15, Cor.Branco, FontWeight.SemiBold, largura: L - 40);
            bool current = Monitor is { } m && _assigned.TryGetValue(m.Id, out var cur) && string.Equals(cur, item.Id, StringComparison.OrdinalIgnoreCase);
            string line = _note ?? (_applying ? Lang.T("Aplicando…", "Applying…") : current ? Lang.T("No monitor agora", "On the monitor now")
                : Lang.T("Clique ou Enter para usar", "Click or Enter to use"));
            var color = _note != null ? (_noteError ? Cor.Vermelho : Cor.Verde) : current ? Cor.Verde : Cor.Secundario;
            var lm = p.Texto.Medir(line, 12, FontWeight.Normal);
            p.Texto.Desenhar(dc, line, new Vector2((L - lm.X) / 2, yText + 23), 12, color, FontWeight.Normal);
            Dots(dc, p, yText + 50, favNow.Count);
        });
        var cards = new List<(WallpaperItem Item, SpriteVisual Visual)>();
        _cards = cards;
        foreach (var item in fav)
        {
            var it = item;
            var s = p.G.NovaSuperficie(card, (dc, sz) => DrawCard(dc, p, it, sz));
            c.Superficies.Add(s);
            var v = p.C.CreateSpriteVisual();
            v.Size = s.TamanhoPx;
            v.Brush = s.Pincel;
            v.CenterPoint = new Vector3(s.TamanhoPx.X / 2, s.TamanhoPx.Y / 2, 0);
            c.Raiz.Children.InsertAtTop(v);
            cards.Add((it, v));
        }
        Animate(animate: false);
        foreach (var (r, i) in pills) c.Botoes.Add(new Botao { Area = r, Acao = () => SetMonitor(i) });
        c.Botoes.Add(new Botao { Area = ButtonArea(p, choose), Acao = () => Choose(true) });
        // three fixed zones over the row: the left one goes back, the middle one applies, the right one goes forward
        float left = L / 2 - card.X / 2, right = L / 2 + card.X / 2;
        c.Botoes.Add(new Botao { Area = new RectangleF(0, CarouselTop, left, card.Y), Acao = () => Move(-1) });
        c.Botoes.Add(new Botao { Area = new RectangleF(left, CarouselTop, card.X, card.Y), Acao = Apply });
        c.Botoes.Add(new Botao { Area = new RectangleF(right, CarouselTop, L - right, card.Y), Acao = () => Move(+1) });
        c.AoSoltar += () => { if (ReferenceEquals(_cards, cards)) _cards = new(); };
        return c;
    }

    private void DrawCard(ID2D1DeviceContext dc, Palco p, WallpaperItem item, Vector2 size)
    {
        if (_thumbs.Get(item) is { } t) t.Draw(dc, 0, 0, size.X, size.Y, CARD_RADIUS);
        else Placeholder(dc, p, item, 0, 0, size.X, size.Y, CARD_RADIUS);
        // a thin light rim, so a dark wallpaper still has an edge on the black card
        using var b = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 0.14f));
        var rr = new RoundedRectangle(new RectangleF(0.5f, 0.5f, size.X - 1, size.Y - 1), CARD_RADIUS, CARD_RADIUS);
        dc.DrawRoundedRectangle(rr, b, 1f);
    }

    /// <summary>While the thumbnail loads (or when there is none): a soft gradient with the kind of wallpaper.</summary>
    private static void Placeholder(ID2D1DeviceContext dc, Palco p, WallpaperItem item, float x, float y, float w, float h, float radius)
    {
        using (var stops = dc.CreateGradientStopCollection(new[]
               {
                   new GradientStop { Position = 0, Color = new Color4(0.22f, 0.22f, 0.27f, 1) },
                   new GradientStop { Position = 1, Color = new Color4(0.10f, 0.10f, 0.13f, 1) },
               }))
        using (var g = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = new Vector2(x, y), EndPoint = new Vector2(x + w, y + h) }, stops))
        {
            var rr = new RoundedRectangle(new RectangleF(x, y, w, h), radius, radius);
            dc.FillRoundedRectangle(ref rr, g);
        }
        var c = new Vector2(x + w / 2, y + h / 2);
        var tint = new Color4(1, 1, 1, 0.5f);
        float u = MathF.Min(w, h) / 120f;
        if (item.Type == "video")
            Icone.Triangulo(dc, p.G.Fabrica, c + new Vector2(-9, -12) * u, c + new Vector2(13, 0) * u, c + new Vector2(-9, 12) * u, tint);
        else
        {
            using var b = dc.CreateSolidColorBrush(tint);
            dc.DrawEllipse(new Ellipse(c, 13 * u, 13 * u), b, 2 * u);
            dc.DrawEllipse(new Ellipse(c, 6 * u, 13 * u), b, 1.6f * u);
            Icone.Linha(dc, c + new Vector2(-13, 0) * u, c + new Vector2(13, 0) * u, 1.6f * u, tint, p.G.Fabrica);
        }
    }

    /// <summary>The page dots under the title ("3 de 20" instead when there are too many).</summary>
    private void Dots(ID2D1DeviceContext dc, Palco p, float y, int count)
    {
        if (count <= 1) return;
        if (count > 15)
        {
            string s = Lang.T($"{_index + 1} de {count}", $"{_index + 1} of {count}");
            var m = p.Texto.Medir(s, 11.5f, FontWeight.Medium);
            p.Texto.Desenhar(dc, s, new Vector2((L - m.X) / 2, y - 3), 11.5f, Cor.Secundario, FontWeight.Medium);
            return;
        }
        const float D = 6, GAP = 7;
        float total = count * D + (count - 1) * GAP;
        float x = (L - total) / 2;
        for (int i = 0; i < count; i++)
            Icone.Circulo(dc, new Vector2(x + i * (D + GAP) + D / 2, y + D / 2), D / 2, i == _index ? Cor.Branco : new Color4(1, 1, 1, 0.28f));
    }

    private Cena ChooseScene(Palco p)
    {
        float tileW = (L - 12 - (COLS - 1) * TILE_GAP) / COLS, thumbH = MathF.Round(tileW * 9 / 16);
        float rowH = thumbH + TITLE_H + ROW_GAP, gridTop = HEADER + 10;
        int rows = (int)Math.Ceiling(_library.Count / (double)COLS);
        int shown = Math.Max(1, Math.Min(VISIBLE_ROWS, rows));
        var c = p.NovaCena(new Vector2(L, gridTop + shown * rowH - ROW_GAP + 6));
        string done = Lang.T("Pronto", "Done");
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var fav = new HashSet<string>(_cfg().Favoritos, StringComparer.OrdinalIgnoreCase);
            string title = _library.Count == 0
                ? (_scanning ? Lang.T("Lendo a biblioteca…", "Reading the library…") : Lang.T("A biblioteca do Fundo Vivo está vazia", "Fundo Vivo's library is empty"))
                : Lang.T($"Os que aparecem aqui: {fav.Count(id => _library.Any(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase)))} de {_library.Count}",
                    $"Showing here: {fav.Count(id => _library.Any(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase)))} of {_library.Count}");
            Header(dc, p, title, done, new());
            for (int r = 0; r < shown; r++)
                for (int col = 0; col < COLS; col++)
                {
                    int i = (_scrollRow + r) * COLS + col;
                    if (i >= _library.Count) break;
                    var item = _library[i];
                    float x = col * (tileW + TILE_GAP), y = gridTop + r * rowH;
                    if (_thumbs.Get(item) is { } th) th.Draw(dc, x, y, tileW, thumbH, 8, fav.Contains(item.Id) ? 1 : 0.55f);
                    else Placeholder(dc, p, item, x, y, tileW, thumbH, 8);
                    bool on = fav.Contains(item.Id);
                    var badge = new Vector2(x + tileW - 13, y + 13);
                    Icone.Circulo(dc, badge, 9, on ? Cor.Verde : new Color4(0, 0, 0, 0.45f));
                    using (var ring = dc.CreateSolidColorBrush(new Color4(1, 1, 1, on ? 1 : 0.75f)))
                        if (!on) dc.DrawEllipse(new Ellipse(badge, 8.2f, 8.2f), ring, 1.4f);
                    if (on)
                    {
                        Icone.Linha(dc, badge + new Vector2(-4, 0.3f), badge + new Vector2(-1.2f, 3.2f), 1.9f, Cor.Branco, p.G.Fabrica);
                        Icone.Linha(dc, badge + new Vector2(-1.2f, 3.2f), badge + new Vector2(4.2f, -3), 1.9f, Cor.Branco, p.G.Fabrica);
                    }
                    p.Texto.Desenhar(dc, item.Title, new Vector2(x + 2, y + thumbH + 3), 11.5f, on ? Cor.Branco : Cor.Secundario,
                        FontWeight.Normal, largura: tileW - 4);
                }
            if (rows > VISIBLE_ROWS)
            {
                float trackH = shown * rowH - ROW_GAP, barH = MathF.Max(24, trackH * VISIBLE_ROWS / rows);
                float barY = gridTop + (trackH - barH) * _scrollRow / Math.Max(1, rows - VISIBLE_ROWS);
                Icone.Retangulo(dc, L - 4, barY, 3, barH, 1.5f, new Color4(1, 1, 1, 0.3f));
            }
        });
        c.Botoes.Add(new Botao { Area = ButtonArea(p, done), Acao = () => Choose(false) });
        for (int r = 0; r < shown; r++)
            for (int col = 0; col < COLS; col++)
            {
                int row = r, column = col;
                c.Botoes.Add(new Botao
                {
                    Area = new RectangleF(column * (tileW + TILE_GAP), gridTop + row * rowH, tileW, thumbH + TITLE_H),
                    Acao = () =>
                    {
                        int i = (_scrollRow + row) * COLS + column;
                        if (i < _library.Count) Toggle(_library[i]);
                    },
                });
            }
        return c;
    }

    private Cena Message(Palco p, string title, string text, bool chooseButton)
    {
        var c = p.NovaCena(new Vector2(L, chooseButton ? 122 : 74));
        var b = new RectangleF((L - 160) / 2, 80, 160, 34);
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            p.Texto.Desenhar(dc, "Fundo Vivo", new Vector2(0, 0), 13, Cor.Secundario, largura: 200);
            var tm = p.Texto.Medir(title, 16, FontWeight.SemiBold);
            p.Texto.Desenhar(dc, title, new Vector2((L - tm.X) / 2, 24), 16, Cor.Branco, FontWeight.SemiBold);
            var xm = p.Texto.Medir(text, 12, FontWeight.Normal);
            p.Texto.Desenhar(dc, text, new Vector2((L - xm.X) / 2, 50), 12, Cor.Secundario, FontWeight.Normal);
            if (!chooseButton) return;
            Icone.Retangulo(dc, b.X, b.Y, b.Width, b.Height, b.Height / 2, Cor.Branco);
            string label = Lang.T("Escolher wallpapers", "Choose wallpapers");
            var lm = p.Texto.Medir(label, 13, FontWeight.SemiBold);
            p.Texto.Desenhar(dc, label, new Vector2(b.X + (b.Width - lm.X) / 2, b.Y + (b.Height - lm.Y) / 2), 13, new Color4(0, 0, 0, 1), FontWeight.SemiBold);
        });
        if (chooseButton) c.Botoes.Add(new Botao { Area = b, Acao = () => Choose(true) });
        return c;
    }

    /// <summary>The menu's detail line: what is on the main monitor now.</summary>
    public string? MenuDetail()
    {
        try
        {
            var stamp = File.Exists(FundoVivoBridge.ConfigFile) ? File.GetLastWriteTimeUtc(FundoVivoBridge.ConfigFile) : DateTime.MinValue;
            if (stamp != _detailStamp)
            {
                _detailStamp = stamp;
                var cfg = FundoVivoBridge.ReadConfig();
                var main = FundoVivoBridge.Monitors().FirstOrDefault(m => m.Primary);
                _detail = main != null && cfg?["monitors"]?[main.Id]?["title"]?.GetValue<string>() is { } t ? t : null;
            }
            return _detail;
        }
        catch { return null; }
    }

    private DateTime _detailStamp = DateTime.MaxValue;
    private string? _detail;

    public override void Dispose()
    {
        _noteTimer.Dispose();
        _thumbs.Dispose();
    }
}
