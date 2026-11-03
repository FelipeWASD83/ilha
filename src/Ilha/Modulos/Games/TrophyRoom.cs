using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Games;

/// <summary>
/// The "Achievements" card from the menu: how many are unlocked and the hours of all games, the five medals of the
/// total, then the most played games, each with its six medals and a bar to the next one.
/// </summary>
internal sealed class TrophyRoom : Atividade
{
    private const float L = 380, ROW = 44;
    private const int GAMES = 8;

    private readonly Servicos S;
    private readonly Func<List<GameHours>> _hours;
    private readonly Func<IReadOnlyDictionary<string, DateTime>> _unlocked;
    private readonly Action _refresh;

    public override string Tipo => "conquistas";
    public override string Nome => Lang.T("Conquistas", "Achievements");
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    public TrophyRoom(Servicos s, Func<List<GameHours>> hours, Func<IReadOnlyDictionary<string, DateTime>> unlocked, Action refresh)
    {
        S = s;
        _hours = hours;
        _unlocked = unlocked;
        _refresh = refresh;
    }

    public void Open()
    {
        _refresh();
        MudouTamanho();
        S.Ilha.Expandir(this);
    }

    public void Changed()
    {
        if (TemCenaViva) MudouTamanho();
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        var games = _hours().Where(g => g.Minutes > 0).OrderByDescending(g => g.Minutes).Take(GAMES).ToList();
        var unlocked = _unlocked();
        int totalMin = _hours().Sum(g => g.Minutes);
        var c = p.NovaCena(new Vector2(L, 92 + games.Count * ROW));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var fab = p.G.Fabrica;
            p.Texto.Desenhar(dc, Lang.T("Conquistas", "Achievements"), new Vector2(0, 0), 15, Cor.Branco);
            // the hours are in the tile right under it: the line says only how many are unlocked
            string sum = Lang.T($"{unlocked.Count} desbloqueadas", $"{unlocked.Count} unlocked");
            var ss = p.Texto.Medir(sum, 12, FontWeight.Normal);
            p.Texto.Desenhar(dc, sum, new Vector2(L - MathF.Min(ss.X, 240), 3), 12, Cor.Secundario, FontWeight.Normal, largura: 240);

            // the total: five medals in a tile
            Icone.Retangulo(dc, 0, 26, L, 52, 14, new Color4(1, 1, 1, 0.07f));
            p.Texto.Desenhar(dc, Lang.T("Todos os jogos", "All games"), new Vector2(14, 34), 13, Cor.Branco);
            p.Texto.Desenhar(dc, GamesOverview.Hours(totalMin), new Vector2(14, 52), 11.5f, Cor.Secundario, FontWeight.Normal);
            for (int i = 0; i < Trophies.TotalTiers.Length; i++)
            {
                bool got = unlocked.ContainsKey($"total:{Trophies.TotalTiers[i]}");
                var center = new Vector2(L - 24 - (Trophies.TotalTiers.Length - 1 - i) * 40, 46);
                Medal(dc, fab, center, 12, i, got);
                string label = Trophies.TotalTiers[i] >= 1000 ? $"{Trophies.TotalTiers[i] / 1000}k" : Trophies.TotalTiers[i].ToString();
                var ls = p.Texto.Medir(label, 9.5f, FontWeight.SemiBold);
                p.Texto.Desenhar(dc, label, new Vector2(center.X - ls.X / 2, 60), 9.5f, got ? Cor.Branco : Cor.Fraco, FontWeight.SemiBold);
            }

            float y = 88;
            foreach (var g in games)
            {
                Marks.Game(dc, fab, g.Key, new Vector2(15, y + ROW / 2), 28, Cor.Secundario);
                p.Texto.Desenhar(dc, g.Name, new Vector2(38, y + 4), 13, Cor.Branco, largura: 170);
                p.Texto.Desenhar(dc, GamesOverview.Hours(g.Minutes), new Vector2(38, y + 22), 11, Cor.Secundario, FontWeight.Normal);
                // the bar to the next medal, under the hours
                if (Trophies.Next(g.Minutes) is { } next)
                {
                    Icone.Retangulo(dc, 92, y + 27, 84, 4, 2, Cor.Fraco);
                    Icone.Retangulo(dc, 92, y + 27, MathF.Max(4, 84 * next.Progress), 4, 2, Trophies.TierColor(next.Tier));
                    p.Texto.Desenhar(dc, $"{next.Hours} h", new Vector2(182, y + 22), 10.5f, Cor.Secundario, FontWeight.Normal);
                }
                for (int i = 0; i < Trophies.GameTiers.Length; i++)
                    Medal(dc, fab, new Vector2(L - 12 - (Trophies.GameTiers.Length - 1 - i) * 26, y + ROW / 2), 9, i,
                        unlocked.ContainsKey($"jogo:{g.Key}:{Trophies.GameTiers[i]}"));
                y += ROW;
            }
        });
        return c;
    }

    /// <summary>A medal: a disc in the tier's color with a star, or a faint ring when not earned yet.</summary>
    public static void Medal(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float r, int tier, bool earned)
    {
        if (!earned)
        {
            using var b = dc.CreateSolidColorBrush(Cor.Fraco);
            dc.DrawEllipse(new Ellipse(c, r - 0.8f, r - 0.8f), b, 1.6f);
            return;
        }
        var color = Trophies.TierColor(tier);
        Icone.Circulo(dc, c, r, color);
        Icone.Circulo(dc, c, r * 0.78f, new Color4(color.R * 0.8f, color.G * 0.8f, color.B * 0.8f, 1));
        Star(dc, f, c, r * 0.55f, new Color4(1, 1, 1, 0.92f));
    }

    public static void Star(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float r, Color4 color)
    {
        using var g = f.CreatePathGeometry();
        using (var s = g.Open())
        {
            for (int i = 0; i < 10; i++)
            {
                float a = -MathF.PI / 2 + i * MathF.PI / 5, rr = i % 2 == 0 ? r : r * 0.45f;
                var pt = c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * rr;
                if (i == 0) s.BeginFigure(pt, FigureBegin.Filled); else s.AddLine(pt);
            }
            s.EndFigure(FigureEnd.Closed);
            s.Close();
        }
        using var b = dc.CreateSolidColorBrush(color);
        dc.FillGeometry(g, b);
    }

    /// <summary>
    /// The menu icon (3.6.0, his request for a better one): a gold cup in a gradient with its handles, a stem on a dark
    /// base with a gold plate, a white star and a shine down its left side. Drawn in a 24-unit box.
    /// </summary>
    public static void MenuIcon(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r)
    {
        float u = r.Width / 24f;
        Vector2 P(float x, float y) => new(r.X + x * u, r.Y + y * u);
        var gold = new Color4(0.98f, 0.78f, 0.22f, 1);
        var deep = new Color4(0.82f, 0.52f, 0.08f, 1);
        // the handles behind the bowl
        using (var hb = dc.CreateSolidColorBrush(deep))
            foreach (float side in new[] { -1f, 1f })
                dc.DrawEllipse(new Ellipse(P(12 + side * 7.4f, 6.6f), 2.6f * u, 2.9f * u), hb, 1.5f * u);
        // the bowl
        using (var g = f.CreatePathGeometry())
        {
            using (var s = g.Open())
            {
                s.BeginFigure(P(5.6f, 2.6f), FigureBegin.Filled);
                s.AddLine(P(18.4f, 2.6f));
                s.AddBezier(new BezierSegment { Point1 = P(18.4f, 9.6f), Point2 = P(15.4f, 12.8f), Point3 = P(12, 12.9f) });
                s.AddBezier(new BezierSegment { Point1 = P(8.6f, 12.8f), Point2 = P(5.6f, 9.6f), Point3 = P(5.6f, 2.6f) });
                s.EndFigure(FigureEnd.Closed);
                s.Close();
            }
            using var stops = dc.CreateGradientStopCollection(new[]
            {
                new GradientStop { Position = 0, Color = new Color4(1, 0.92f, 0.55f, 1) },
                new GradientStop { Position = 0.45f, Color = gold },
                new GradientStop { Position = 1, Color = deep },
            });
            using var b = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = P(6, 2), EndPoint = P(18, 13) }, stops);
            dc.FillGeometry(g, b);
        }
        // the rim, the shine and the star
        Icone.Retangulo(dc, P(5, 0).X, P(0, 1.8f).Y, 14 * u, 1.6f * u, 0.8f * u, new Color4(1, 0.86f, 0.4f, 1));
        Icone.Retangulo(dc, P(7.2f, 0).X, P(0, 4).Y, 1.4f * u, 5.2f * u, 0.7f * u, new Color4(1, 1, 1, 0.45f));
        Star(dc, f, P(12.6f, 6.8f), 3.1f * u, new Color4(1, 1, 1, 0.95f));
        // the stem, the knot and the base with its plate
        Icone.Retangulo(dc, P(11, 0).X, P(0, 12.6f).Y, 2 * u, 3.4f * u, 0.4f * u, deep);
        Icone.Retangulo(dc, P(9.4f, 0).X, P(0, 15.6f).Y, 5.2f * u, 1.6f * u, 0.8f * u, gold);
        Icone.Retangulo(dc, P(6.6f, 0).X, P(0, 17.4f).Y, 10.8f * u, 4.8f * u, 1.2f * u, new Color4(0.24f, 0.16f, 0.12f, 1));
        Icone.Retangulo(dc, P(9.2f, 0).X, P(0, 18.7f).Y, 5.6f * u, 2.2f * u, 0.5f * u, gold);
    }

    /// <summary>A trophy cup (the unlock animation).</summary>
    public static void Cup(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float s, Color4 color)
    {
        using var b = dc.CreateSolidColorBrush(color);
        using var g = f.CreatePathGeometry();
        using (var p = g.Open())
        {
            p.BeginFigure(c + new Vector2(-0.5f, -0.55f) * s, FigureBegin.Filled);
            p.AddLine(c + new Vector2(0.5f, -0.55f) * s);
            p.AddBezier(new BezierSegment { Point1 = c + new Vector2(0.5f, 0.05f) * s, Point2 = c + new Vector2(0.25f, 0.2f) * s, Point3 = c + new Vector2(0.08f, 0.22f) * s });
            p.AddLine(c + new Vector2(0.08f, 0.4f) * s);
            p.AddLine(c + new Vector2(0.28f, 0.45f) * s);
            p.AddLine(c + new Vector2(0.28f, 0.55f) * s);
            p.AddLine(c + new Vector2(-0.28f, 0.55f) * s);
            p.AddLine(c + new Vector2(-0.28f, 0.45f) * s);
            p.AddLine(c + new Vector2(-0.08f, 0.4f) * s);
            p.AddLine(c + new Vector2(-0.08f, 0.22f) * s);
            p.AddBezier(new BezierSegment { Point1 = c + new Vector2(-0.25f, 0.2f) * s, Point2 = c + new Vector2(-0.5f, 0.05f) * s, Point3 = c + new Vector2(-0.5f, -0.55f) * s });
            p.EndFigure(FigureEnd.Closed);
            p.Close();
        }
        dc.FillGeometry(g, b);
        // the handles
        foreach (float side in new[] { -1f, 1f })
            dc.DrawEllipse(new Ellipse(c + new Vector2(side * 0.52f, -0.3f) * s, 0.16f * s, 0.18f * s), b, MathF.Max(1.2f, s * 0.07f));
    }
}
