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

    /// <summary>A trophy cup (the menu icon and the unlock animation).</summary>
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
