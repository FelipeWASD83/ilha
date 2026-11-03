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
        string layout = $"{m.Game}|{m.Kills != null}|{m.Extras.Count}|{m.Mode.Length > 0}";
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
        Glyphs.Draw(dc, p.G.Fabrica, m.Game, new Vector2(10, t.Y / 2), 8.5f, Cor.Branco);
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
        float mid = 96 - ScoreX;
        string us = m.Us?.ToString() ?? "–", them = m.Them?.ToString() ?? "–";
        var su = p.Texto.Medir(us, 16, FontWeight.Bold, tabular: true);
        float y = (t.Y - su.Y) / 2;
        p.Texto.Desenhar(dc, us, new Vector2(mid - 7 - su.X, y), 16, Cor.Branco, FontWeight.Bold, tabular: true);
        p.Texto.Desenhar(dc, them, new Vector2(mid + 7, y), 16, Cor.Branco, FontWeight.Bold, tabular: true);
        var sc = p.Texto.Medir(":", 15, FontWeight.Bold);
        p.Texto.Desenhar(dc, ":", new Vector2(mid - sc.X / 2, y - 1), 15, Cor.Secundario, FontWeight.Bold);
        var st = p.Texto.Medir(them, 16, FontWeight.Bold, tabular: true);
        Icone.Circulo(dc, new Vector2(mid - 7 - su.X - 8, t.Y / 2), 3.2f, m.UsColor);
        Icone.Circulo(dc, new Vector2(mid + 7 + st.X + 8, t.Y / 2), 3.2f, m.ThemColor);
    }

    private Cena Card(Palco p)
    {
        var m = _m;
        bool stats = m != null && (m.Kills != null || m.Extras.Count > 0);
        float h = 176 + (stats ? 34 : 0);
        var c = p.NovaCena(new Vector2(L, h));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var x = _m;
            if (x == null) return;
            var now = DateTime.Now;
            Glyphs.Draw(dc, p.G.Fabrica, x.Game, new Vector2(9, 10), 7.5f, Cor.Secundario);
            p.Texto.Desenhar(dc, x.GameName, new Vector2(24, 1), 13, Cor.Secundario, largura: 200);
            if (x.ClockAt(now) is double clock)
            {
                _drawnSecond = (long)clock;
                string tx = Match.Time(clock);
                var sz = p.Texto.Medir(tx, 13, FontWeight.SemiBold, tabular: true);
                p.Texto.Desenhar(dc, tx, new Vector2(L - sz.X, 1), 13, Cor.Branco, FontWeight.SemiBold, tabular: true);
            }
            string mode = string.Join(" · ", new[] { x.Mode, x.Map }.Where(s => s.Length > 0));
            p.Texto.Desenhar(dc, mode.Length > 0 ? mode : Lang.T("Partida", "Match"), new Vector2(0, 24), 16, Cor.Branco, largura: L - 90);
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
            Team(dc, p, x.UsLabel, x.UsColor, new Vector2(0, top + 14), left: true);
            Team(dc, p, x.ThemLabel, x.ThemColor, new Vector2(L, top + 14), left: false);

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

    private static void Team(ID2D1DeviceContext dc, Palco p, string label, Color4 color, Vector2 at, bool left)
    {
        if (label.Length == 0) return;
        var s = p.Texto.Medir(label, 13, FontWeight.SemiBold);
        float w = MathF.Min(s.X, 90);
        float x = left ? at.X + 12 : at.X - w;
        Icone.Circulo(dc, new Vector2(left ? at.X + 4 : at.X - w - 8, at.Y + s.Y / 2), 3.5f, color);
        p.Texto.Desenhar(dc, label, new Vector2(x, at.Y), 13, color, FontWeight.SemiBold, largura: 90);
    }
}

/// <summary>Each game's little mark, drawn from shapes (no logos: those are the games' brands).</summary>
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
