using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Windows.UI.Composition;

namespace Ilha.Games;

/// <summary>
/// The live score on the pill: the game's mark, our score and theirs in the team colors and the game clock. The card
/// has the mode and map, the big score, our numbers (K/D/A, goals...) and the last five results of that game. It goes
/// ahead of everything else while a match is on and is the one thing allowed over a competitive game.
/// </summary>
internal sealed class ScoreActivity : Atividade
{
    private const float LC = 214, HC = 24, L = 320;

    private readonly Servicos S;
    private readonly Func<string, List<Outcome>> _last;
    private readonly System.Windows.Forms.Timer _second = new() { Interval = 1000 };
    private Match? _m;
    private (Cena Scene, Superficie Surface)? _compact;
    private string _layout = "";
    private long _drawnSecond = -1;

    public override string Tipo => "jogo";
    public override string Nome => Lang.T("Placar", "Score");
    public override bool PermitidaNoCompetitivo => true;
    public override bool NaFrente => Ativa;
    public override bool AnunciaChegada => false;
    public override float? LarguraCompactaMinima => LC;

    public Match? Current => _m;

    public ScoreActivity(Servicos s, Func<string, List<Outcome>> last)
    {
        S = s;
        _last = last;
        // the clock moves on its own between readings: redraw when its second changes, only with a scene on screen
        _second.Tick += (_, _) =>
        {
            if (!TemCenaViva || _m?.Clock == null) { _second.Stop(); return; }
            long sec = (long)(_m.ClockAt(DateTime.Now) ?? 0);
            if (sec != _drawnSecond) Redesenhar();
        };
    }

    public void Show(Match? m)
    {
        var old = _m;
        _m = m;
        if (m == null) { Desligar(); return; }
        Ligar();
        string layout = $"{m.Game}|{m.Idle}|{m.Kills != null}|{m.Extras.Count}|{m.Mode.Length > 0}";
        if (layout != _layout) { _layout = layout; MudouTamanho(); }
        else if (old != null && old.Game == m.Game && (old.Us != m.Us || old.Them != m.Them) && _compact is { } c && TemCenaViva)
        {
            // the score turned: the old numbers leave upward and the new ones pop in
            var before = old;
            ContentSwap.Play(S.Palco, c.Scene, c.Surface, (dc, t) => DrawScore(dc, before, t), new Vector2(0, -1), 12, 300, pop: true);
            Redesenhar();
        }
        else Redesenhar();
        if (m.Clock != null && TemCenaViva && !_second.Enabled) _second.Start();
    }

    public override void ExpandidaMudou(bool aberta) { if (aberta) Redesenhar(); }

    public override void Dispose() => _second.Dispose();

    protected override Cena Montar(Palco p, Forma f)
    {
        if (_m?.Clock != null && !_second.Enabled) _second.Start();
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(24, 24));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    if (_m == null) return;
                    string s = _m.HasScore ? $"{_m.Us}:{_m.Them}" : "–";
                    var sz = p.Texto.Medir(s, 10, FontWeight.Bold, tabular: true);
                    p.Texto.Desenhar(dc, s, new Vector2((t.X - sz.X) / 2, (t.Y - sz.Y) / 2), 10, Cor.Branco, FontWeight.Bold, tabular: true);
                });
                return c;
            }
            case Forma.Compacta:
            {
                var c = p.NovaCena(new Vector2(LC, HC));
                // the mark and the clock on one layer, the score on its own: only the score moves when it turns
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) => { if (_m != null) DrawFrame(dc, _m, t); });
                var s = p.Desenho(c, new Vector2(ScoreX, 0), new Vector2(ScoreW, HC), (dc, t) => { if (_m != null) DrawScore(dc, _m, t); });
                _compact = (c, s);
                c.AoSoltar += () => { if (_compact?.Scene == c) _compact = null; };
                return c;
            }
            default:
                return Card(p);
        }
    }

    private const float ScoreX = 24, ScoreW = 144;

    private void DrawFrame(ID2D1DeviceContext dc, Match m, Vector2 t)
    {
        var p = S.Palco;
        Marks.Game(dc, p.G.Fabrica, m.Game, new Vector2(10, t.Y / 2), 20, Cor.Branco);
        if (m.ClockAt(DateTime.Now) is double clock)
        {
            _drawnSecond = (long)clock;
            string tx = Match.Time(clock);
            var sz = p.Texto.Medir(tx, 13, FontWeight.SemiBold, tabular: true);
            p.Texto.Desenhar(dc, tx, new Vector2(t.X - sz.X, (t.Y - sz.Y) / 2), 13, Cor.Secundario, FontWeight.SemiBold, tabular: true);
        }
    }

    /// <summary>Our score left of the colon, theirs right, each with its team's dot (inside the score layer).</summary>
    private void DrawScore(ID2D1DeviceContext dc, Match m, Vector2 t)
    {
        var p = S.Palco;
        if (m.Idle)
        {
            // no match: the game's name where the score goes
            var sn = p.Texto.Medir(m.GameName, 13, FontWeight.SemiBold);
            float w = MathF.Min(sn.X, t.X);
            p.Texto.Desenhar(dc, m.GameName, new Vector2((t.X - w) / 2, (t.Y - sn.Y) / 2), 13, Cor.Branco, FontWeight.SemiBold, largura: t.X);
            return;
        }
        float mid = 96 - ScoreX;
        string us = m.Us?.ToString() ?? "–", them = m.Them?.ToString() ?? "–";
        var su = p.Texto.Medir(us, 16, FontWeight.Bold, tabular: true);
        float y = (t.Y - su.Y) / 2;
        p.Texto.Desenhar(dc, us, new Vector2(mid - 7 - su.X, y), 16, Cor.Branco, FontWeight.Bold, tabular: true);
        p.Texto.Desenhar(dc, them, new Vector2(mid + 7, y), 16, Cor.Branco, FontWeight.Bold, tabular: true);
        var sc = p.Texto.Medir(":", 15, FontWeight.Bold);
        p.Texto.Desenhar(dc, ":", new Vector2(mid - sc.X / 2, y - 1), 15, Cor.Secundario, FontWeight.Bold);
        var st = p.Texto.Medir(them, 16, FontWeight.Bold, tabular: true);
        Marks.Team(dc, p.G.Fabrica, m.UsArt, m.UsColor, new Vector2(mid - 7 - su.X - 13, t.Y / 2), 17);
        Marks.Team(dc, p.G.Fabrica, m.ThemArt, m.ThemColor, new Vector2(mid + 7 + st.X + 13, t.Y / 2), 17);
    }

    private Cena Card(Palco p)
    {
        var m = _m;
        if (m is { Idle: true }) return IdleCard(p);
        bool stats = m != null && (m.Kills != null || m.Extras.Count > 0);
        float h = 176 + (stats ? 34 : 0);
        var c = p.NovaCena(new Vector2(L, h));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var x = _m;
            if (x == null) return;
            var now = DateTime.Now;
            Marks.Game(dc, p.G.Fabrica, x.Game, new Vector2(9, 10), 18, Cor.Secundario);
            p.Texto.Desenhar(dc, x.GameName, new Vector2(24, 1), 13, Cor.Secundario, largura: 200);
            if (x.ClockAt(now) is double clock)
            {
                _drawnSecond = (long)clock;
                string tx = Match.Time(clock);
                var sz = p.Texto.Medir(tx, 13, FontWeight.SemiBold, tabular: true);
                p.Texto.Desenhar(dc, tx, new Vector2(L - sz.X, 1), 13, Cor.Branco, FontWeight.SemiBold, tabular: true);
            }
            // Premier's badge is its name; Rush's is a symbol, with the name after it
            string mode = string.Join(" · ", new[] { x.Badge == "premier" ? "" : x.Mode, x.Map }.Where(s => s.Length > 0));
            float mx = 0;
            if (x.Badge.Length > 0 && Marks.Badge(dc, x.Badge, 0, x.Badge == "rush" ? 24 : 27, x.Badge == "rush" ? 22 : 17) is float bw and > 0) mx = bw + 8;
            p.Texto.Desenhar(dc, mode.Length > 0 ? mode : Lang.T("Partida", "Match"), new Vector2(mx, 24), 16, Cor.Branco, largura: L - 90 - mx);
            if (x.Stage.Length > 0)
            {
                var ss = p.Texto.Medir(x.Stage, 12, FontWeight.Normal);
                p.Texto.Desenhar(dc, x.Stage, new Vector2(L - MathF.Min(ss.X, 120), 28), 12, Cor.Secundario, FontWeight.Normal, largura: 120);
            }

            // the big score
            float mid = L / 2, top = 56;
            string us = x.Us?.ToString() ?? "–", them = x.Them?.ToString() ?? "–";
            var bu = p.Texto.Medir(us, 40, FontWeight.Bold, tabular: true);
            p.Texto.Desenhar(dc, us, new Vector2(mid - 16 - bu.X, top), 40, Cor.Branco, FontWeight.Bold, tabular: true);
            p.Texto.Desenhar(dc, them, new Vector2(mid + 16, top), 40, Cor.Branco, FontWeight.Bold, tabular: true);
            var colon = p.Texto.Medir(":", 34, FontWeight.Bold);
            p.Texto.Desenhar(dc, ":", new Vector2(mid - colon.X / 2, top + 1), 34, Cor.Secundario, FontWeight.Bold);
            Team(dc, p, x.UsLabel, x.UsArt, x.UsColor, new Vector2(0, top + 4), left: true);
            Team(dc, p, x.ThemLabel, x.ThemArt, x.ThemColor, new Vector2(L, top + 4), left: false);

            float y = top + 66;
            if (stats)
            {
                var items = new List<(string, string)>();
                if (x.Kills != null) items.Add(("K / D / A", $"{x.Kills} / {x.Deaths?.ToString() ?? "–"} / {x.Assists?.ToString() ?? "–"}"));
                items.AddRange(x.Extras);
                float cx = 0;
                foreach (var (name, value) in items)
                {
                    var vn = p.Texto.Medir(value, 15, FontWeight.Bold, tabular: true);
                    var nn = p.Texto.Medir(name, 11, FontWeight.Normal);
                    float w = MathF.Max(vn.X, nn.X) + 14;
                    if (cx + w > L) break;
                    p.Texto.Desenhar(dc, value, new Vector2(cx, y), 15, Cor.Branco, FontWeight.Bold, tabular: true);
                    p.Texto.Desenhar(dc, name, new Vector2(cx, y + 19), 11, Cor.Secundario, FontWeight.Normal);
                    cx += w;
                }
                y += 40;
            }

            // the last five of this game, oldest first, the next one an empty ring
            p.Texto.Desenhar(dc, Lang.T("Últimas partidas", "Last matches"), new Vector2(0, y + 4), 12, Cor.Secundario, FontWeight.Normal);
            var last = _last(x.Game);
            float bx = L - 5 * 26 + 4;
            for (int i = 0; i < 5; i++)
            {
                var center = new Vector2(bx + i * 26 + 9, y + 12);
                if (i < last.Count)
                {
                    var o = last[i];
                    Icone.Circulo(dc, center, 9.5f, o == Outcome.Win ? TeamColors.Win : o == Outcome.Loss ? TeamColors.Loss : Cor.Fraco);
                    string letter = o == Outcome.Win ? Lang.T("V", "W") : o == Outcome.Loss ? Lang.T("D", "L") : Lang.T("E", "D");
                    var ls = p.Texto.Medir(letter, 11, FontWeight.Bold);
                    p.Texto.Desenhar(dc, letter, center - ls / 2 + new Vector2(0, -0.5f), 11, new Color4(0, 0, 0, 0.82f), FontWeight.Bold);
                }
                else
                {
                    using var b = dc.CreateSolidColorBrush(Cor.Fraco);
                    dc.DrawEllipse(new Ellipse(center, 8.5f, 8.5f), b, 1.6f);
                }
            }
        });
        return c;
    }

    /// <summary>The card with the game open and no match: its icon, name, the time it is open and the last results.</summary>
    private Cena IdleCard(Palco p)
    {
        var c = p.NovaCena(new Vector2(L, 104));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var x = _m;
            if (x == null) return;
            Marks.Game(dc, p.G.Fabrica, x.Game, new Vector2(20, 22), 40, Cor.Branco);
            p.Texto.Desenhar(dc, x.GameName, new Vector2(52, 2), 18, Cor.Branco, largura: L - 120);
            p.Texto.Desenhar(dc, x.Stage, new Vector2(52, 26), 13, Cor.Secundario, FontWeight.Normal, largura: L - 120);
            if (x.ClockAt(DateTime.Now) is double open)
            {
                _drawnSecond = (long)open;
                string tx = Match.Time(open);
                var sz = p.Texto.Medir(tx, 15, FontWeight.SemiBold, tabular: true);
                p.Texto.Desenhar(dc, tx, new Vector2(L - sz.X, 4), 15, Cor.Branco, FontWeight.SemiBold, tabular: true);
                var sl = p.Texto.Medir(Lang.T("aberto há", "open for"), 11, FontWeight.Normal);
                p.Texto.Desenhar(dc, Lang.T("aberto há", "open for"), new Vector2(L - sl.X, 26), 11, Cor.Secundario, FontWeight.Normal);
            }
            LastFive(dc, p, x.Game, 62);
        });
        return c;
    }

    private void LastFive(ID2D1DeviceContext dc, Palco p, string game, float y)
    {
        p.Texto.Desenhar(dc, Lang.T("Últimas partidas", "Last matches"), new Vector2(0, y + 4), 12, Cor.Secundario, FontWeight.Normal);
        var last = _last(game);
        float bx = L - 5 * 26 + 4;
        for (int i = 0; i < 5; i++)
        {
            var center = new Vector2(bx + i * 26 + 9, y + 12);
            if (i < last.Count)
            {
                var o = last[i];
                Icone.Circulo(dc, center, 9.5f, o == Outcome.Win ? TeamColors.Win : o == Outcome.Loss ? TeamColors.Loss : Cor.Fraco);
                string letter = o == Outcome.Win ? Lang.T("V", "W") : o == Outcome.Loss ? Lang.T("D", "L") : Lang.T("E", "D");
                var ls = p.Texto.Medir(letter, 11, FontWeight.Bold);
                p.Texto.Desenhar(dc, letter, center - ls / 2 + new Vector2(0, -0.5f), 11, new Color4(0, 0, 0, 0.82f), FontWeight.Bold);
            }
            else
            {
                using var b = dc.CreateSolidColorBrush(Cor.Fraco);
                dc.DrawEllipse(new Ellipse(center, 8.5f, 8.5f), b, 1.6f);
            }
        }
    }

    /// <summary>A team on the card: its mark big at the outer edge, the name under it in the team color.</summary>
    private static void Team(ID2D1DeviceContext dc, Palco p, string label, string art, Color4 color, Vector2 at, bool left)
    {
        const float side = 34;
        float cx = left ? at.X + side / 2 : at.X - side / 2;
        Marks.Team(dc, p.G.Fabrica, art, color, new Vector2(cx, at.Y + side / 2), side);
        if (label.Length == 0) return;
        var s = p.Texto.Medir(label, 12, FontWeight.SemiBold);
        float w = MathF.Min(s.X, 90);
        p.Texto.Desenhar(dc, label, new Vector2(left ? MathF.Max(0, cx - w / 2) : MathF.Min(at.X - w, cx - w / 2), at.Y + side + 3), 12, color, FontWeight.SemiBold, largura: 90);
    }
}

/// <summary>
/// The marks on the score: the game's own icon and team art when the game is installed (<see cref="GameArt"/>), the
/// drawn shapes otherwise.
/// </summary>
internal static class Marks
{
    public static void Game(ID2D1DeviceContext dc, ID2D1Factory1 f, string game, Vector2 c, float side, Color4 fallback)
    {
        if (GameArt.IconFor(game, Playtime.SteamApp(game) ?? (game.StartsWith("steam:") && int.TryParse(game[6..], out var app) ? app : null)) is { } icon)
            Rounded(dc, f, c.X - side / 2, c.Y - side / 2, side, () => GameArt.Draw(dc, icon, c.X - side / 2, c.Y - side / 2, side));
        else Glyphs.Draw(dc, f, game, c, side * 0.42f, fallback);
    }

    /// <summary>Draws inside a square with rounded corners (square logos get the corners every icon on the island has).</summary>
    private static void Rounded(ID2D1DeviceContext dc, ID2D1Factory1 f, float x, float y, float side, Action draw)
    {
        var rr = new RoundedRectangle(new System.Drawing.RectangleF(x, y, side, side), side * 0.22f, side * 0.22f);
        using var geo = f.CreateRoundedRectangleGeometry(rr);
        dc.PushLayer(new LayerParameters1 { ContentBounds = new Vortice.RawRectF(x, y, x + side, y + side), GeometricMask = geo, Opacity = 1, MaskTransform = System.Numerics.Matrix3x2.Identity }, null!);
        draw();
        dc.PopLayer();
    }

    public static void Team(ID2D1DeviceContext dc, ID2D1Factory1 f, string art, Color4 color, Vector2 c, float side)
    {
        float x = c.X - side / 2, y = c.Y - side / 2;
        switch (art)
        {
            case "cs2:ct" or "cs2:t":
                if (GameArt.Cs2Svg(art == "cs2:ct" ? GameArt.CtLogo : GameArt.TLogo) is { } svg && GameArt.DrawSvg(dc, svg, x, y, side, side)) return;
                break;
            case "bf6:nato" or "bf6:pax":
                string faction = art[4..];
                if (GameArt.Emblem(faction) is { } em)
                {
                    // the emblem as the loading screen shows it, in a rounded tile
                    Rounded(dc, f, x, y, side, () => GameArt.Draw(dc, em, x, y, side));
                    return;
                }
                Faction(dc, f, faction, c, side, color);
                return;
            case "rl:blue" or "rl:orange":
                Icone.Circulo(dc, c, side * 0.42f, new Color4(color.R, color.G, color.B, 0.28f));
                Icone.Circulo(dc, c, side * 0.27f, color);
                return;
        }
        Icone.Circulo(dc, c, side * 0.2f, color);
    }

    /// <summary>A faction mark until its emblem was seen: a compass star for NATO, a shield for PAX.</summary>
    private static void Faction(ID2D1DeviceContext dc, ID2D1Factory1 f, string faction, Vector2 c, float side, Color4 color)
    {
        float r = side / 2;
        Icone.Retangulo(dc, c.X - r, c.Y - r, side, side, side * 0.18f, new Color4(0.24f, 0.26f, 0.28f, 1));
        using var g = f.CreatePathGeometry();
        using (var s = g.Open())
        {
            if (faction == "nato")
            {
                for (int i = 0; i < 8; i++)
                {
                    float a = -MathF.PI / 2 + i * MathF.PI / 4, rr = i % 2 == 0 ? r * 0.72f : r * 0.16f;
                    var pt = c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * rr;
                    if (i == 0) s.BeginFigure(pt, FigureBegin.Filled); else s.AddLine(pt);
                }
            }
            else
            {
                s.BeginFigure(c + new Vector2(-r * 0.55f, -r * 0.6f), FigureBegin.Filled);
                s.AddLine(c + new Vector2(r * 0.55f, -r * 0.6f));
                s.AddLine(c + new Vector2(r * 0.55f, r * 0.05f));
                s.AddLine(c + new Vector2(0, r * 0.68f));
                s.AddLine(c + new Vector2(-r * 0.55f, r * 0.05f));
            }
            s.EndFigure(FigureEnd.Closed);
            s.Close();
        }
        using var b = dc.CreateSolidColorBrush(Cor.Branco);
        dc.FillGeometry(g, b);
    }

    /// <summary>A mode badge at (x, y), `h` DIP tall; returns its width.</summary>
    public static float Badge(ID2D1DeviceContext dc, string badge, float x, float y, float h)
    {
        string? entry = badge switch { "premier" => GameArt.Premier, "rush" => GameArt.Rush, _ => null };
        if (entry != null && GameArt.Cs2Svg(entry) is { } svg)
        {
            float w = h * GameArt.Aspect(svg);
            if (GameArt.DrawSvg(dc, svg, x, y, w, h)) return w;
        }
        return 0;
    }
}

/// <summary>Each game's little mark, drawn from shapes: the fallback when the game's own icon is not found.</summary>
internal static class Glyphs
{
    public static void Draw(ID2D1DeviceContext dc, ID2D1Factory1 f, string game, Vector2 c, float r, Color4 color)
    {
        using var b = dc.CreateSolidColorBrush(color);
        switch (game)
        {
            case "cs2":
                // a crosshair
                dc.DrawEllipse(new Ellipse(c, r * 0.62f, r * 0.62f), b, r * 0.2f);
                foreach (var d in new[] { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1) })
                    Icone.Linha(dc, c + d * r * 0.35f, c + d * r * 1.05f, r * 0.2f, color, f);
                break;
            case "rl":
                // a ball with its seams
                dc.DrawEllipse(new Ellipse(c, r * 0.92f, r * 0.92f), b, r * 0.18f);
                Icone.Circulo(dc, c, r * 0.32f, color);
                for (int i = 0; i < 5; i++)
                {
                    float a = -MathF.PI / 2 + i * MathF.Tau / 5;
                    var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
                    Icone.Linha(dc, c + dir * r * 0.32f, c + dir * r * 0.9f, r * 0.14f, color, f);
                }
                break;
            default:
                // a five-pointed star
                using (var g = f.CreatePathGeometry())
                {
                    using (var s = g.Open())
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            float a = -MathF.PI / 2 + i * MathF.PI / 5, rr = i % 2 == 0 ? r : r * 0.42f;
                            var pt = c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * rr;
                            if (i == 0) s.BeginFigure(pt, FigureBegin.Filled); else s.AddLine(pt);
                        }
                        s.EndFigure(FigureEnd.Closed);
                        s.Close();
                    }
                    dc.FillGeometry(g, b);
                }
                break;
        }
    }
}
