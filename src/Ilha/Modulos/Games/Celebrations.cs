using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Windows.UI.Composition;

namespace Ilha.Games;

/// <summary>
/// The moments on the pill: a skull that drops in and gets slashed by two red cuts (a kill), "GOOOOOOOOOL!" lighting up
/// letter by letter with a shine running over it (a goal) and a big "EZ" or "GG" slamming down at the end. Each is a
/// short alert drawn once and animated only in the compositor.
/// </summary>
internal static class Celebrations
{
    private static readonly Color4 Blood = new(1f, 0.16f, 0.2f, 1f);

    // ------------------------------------------------------------------ kill

    public static Cena Kill(Palco p, int streak)
    {
        string text = streak > 1 ? $"x{streak}" : "+1";
        const float S = 46, H = 52;
        var ts = p.Texto.Medir(text, 24, FontWeight.Black, tabular: true);
        var c = p.NovaCena(new Vector2(14 + S + 10 + ts.X + 14, H));
        var at = new Vector2(14, (H - S) / 2);
        var f = p.G.Fabrica;
        var ring = Layer(p, c, at - new Vector2(10, 10), new Vector2(S + 20, S + 20), (dc, t) =>
        {
            using var b = dc.CreateSolidColorBrush(new Color4(Blood.R, Blood.G, Blood.B, 0.9f));
            dc.DrawEllipse(new Ellipse(t / 2, S / 2 + 2, S / 2 + 2), b, 3f);
        });
        // the skull, its flash and the cuts shake together on the hit (the group), while the skull drops on its own
        var hit = Group(p, c);
        var skull = Layer(p, c, at, new Vector2(S, S), (dc, t) => Skull(dc, f, t / 2, S * 0.44f, Cor.Branco), hit);
        // the same skull in red over it, gone in a blink: the hit flash
        var flash = Layer(p, c, at, new Vector2(S, S), (dc, t) => Skull(dc, f, t / 2, S * 0.44f, Blood), hit);
        var cut1 = Layer(p, c, at, new Vector2(S, S), (dc, t) => Cut(dc, f, t, true), hit);
        var cut2 = Layer(p, c, at, new Vector2(S, S), (dc, t) => Cut(dc, f, t, false), hit);
        var label = Layer(p, c, new Vector2(14 + S + 10, (H - ts.Y) / 2), ts + new Vector2(2, 0), (dc, _) =>
            p.Texto.Desenhar(dc, text, Vector2.Zero, 24, streak > 1 ? new Color4(1f, 0.78f, 0.25f, 1f) : Cor.Branco, FontWeight.Black, tabular: true));

        var cc = p.C;
        // drops in, squashes on landing, wobbles back
        Slide(cc, skull, 0, 260, new Vector2(0, -22), bounce: true);
        Fade(cc, skull, 0, 90);
        Scale3(cc, skull, 200, 320, new[] { (0f, new Vector2(1, 1)), (0.3f, new Vector2(1.18f, 0.82f)), (0.65f, new Vector2(0.94f, 1.06f)), (1f, new Vector2(1, 1)) });
        Rotate(cc, skull, 200, 420, new[] { (0f, 0f), (0.3f, -9f), (0.65f, 6f), (1f, 0f) });
        Opacity(cc, flash, 0, 1, new[] { (0f, 0f) });
        // the cuts, one after the other, each drawn from its first end to the other
        Reveal(cc, cut1, 330, 120, S, fromLeft: true);
        Reveal(cc, cut2, 470, 120, S, fromLeft: false);
        Opacity(cc, flash, 330, 320, new[] { (0f, 0f), (0.12f, 0.85f), (1f, 0f) });
        Scale(cc, ring, 330, 420, new[] { (0f, 0.45f), (1f, 1.35f) });
        Opacity(cc, ring, 330, 420, new[] { (0f, 0f), (0.1f, 0.95f), (1f, 0f) });
        Shake(cc, hit, 340, 220, 2.2f);
        // the count pops in after the second cut
        Scale(cc, label, 520, 300, new[] { (0f, 0f), (0.6f, 1.25f), (1f, 1f) });
        Fade(cc, label, 520, 120);
        return c;
    }

    private static readonly Color4 Gold = new(1f, 0.8f, 0.22f, 1f);

    /// <summary>
    /// An ace (the whole enemy team in one round): "ACE!" in gold slams down, the five skulls light up one after the
    /// other under it, each crossed out, then a shine runs over the word.
    /// </summary>
    public static Cena Ace(Palco p, int skulls = 5)
    {
        const string word = "ACE!";
        const float size = 44, sk = 26, gap = 8;
        var ws = p.Texto.Medir(word, size, FontWeight.Black);
        float rowW = skulls * sk + (skulls - 1) * gap;
        float w = MathF.Max(ws.X, rowW) + 40, h = ws.Y + sk + 22;
        var c = p.NovaCena(new Vector2(w, h));
        var f = p.G.Fabrica;
        var cc = p.C;
        var origin = new Vector2((w - ws.X) / 2, 4);
        var glow = Layer(p, c, origin - new Vector2(14, 14), ws + new Vector2(28, 28), (dc, _) => Glow(dc, p, word, size, Gold, new Vector2(14, 14)));
        Opacity(cc, glow, 0, 1, new[] { (0f, 0f) });
        var big = Layer(p, c, origin, ws + new Vector2(2, 3), (dc, _) =>
        {
            p.Texto.Desenhar(dc, word, new Vector2(0, 2.5f), size, new Color4(0, 0, 0, 0.55f), FontWeight.Black);
            p.Texto.Desenhar(dc, word, Vector2.Zero, size, Gold, FontWeight.Black);
        });
        Scale(cc, big, 0, 420, new[] { (0f, 2.6f), (0.6f, 0.92f), (0.8f, 1.05f), (1f, 1f) });
        Rotate(cc, big, 0, 420, new[] { (0f, -10f), (0.6f, 2f), (1f, 0f) });
        Fade(cc, big, 0, 140);
        Opacity(cc, glow, 300, 1400, new[] { (0f, 0f), (0.2f, 0.9f), (0.5f, 0.45f), (0.75f, 0.9f), (1f, 0.6f) });

        float x0 = (w - rowW) / 2, y0 = ws.Y + 12;
        for (int i = 0; i < skulls; i++)
        {
            var at = new Vector2(x0 + i * (sk + gap), y0);
            var s = Layer(p, c, at, new Vector2(sk, sk), (dc, t) => Skull(dc, f, t / 2, sk * 0.44f, Cor.Branco));
            var x = Layer(p, c, at, new Vector2(sk, sk), (dc, t) =>
            {
                Icone.Linha(dc, new Vector2(3, 3), t - new Vector2(3, 3), 3.4f, Blood, f);
                Icone.Linha(dc, new Vector2(t.X - 3, 3), new Vector2(3, t.Y - 3), 3.4f, Blood, f);
            });
            int d = 380 + i * 110;
            Scale(cc, s, d, 260, new[] { (0f, 0f), (0.6f, 1.3f), (1f, 1f) });
            Fade(cc, s, d, 80);
            Scale(cc, x, d + 120, 160, new[] { (0f, 2f), (1f, 1f) });
            Fade(cc, x, d + 120, 80);
        }
        Shine(p, c, word, size, origin, ws, 380 + skulls * 110 + 150, 2);
        return c;
    }

    /// <summary>An achievement unlocked: the cup in the tier's color pops up with a ring of light, then the text slides in.</summary>
    public static Cena Trophy(Palco p, Trophy t)
    {
        string top = Lang.T("Conquista desbloqueada", "Achievement unlocked") + " · " + Trophies.TierName(t.Tier);
        string title = t.Total ? Lang.T($"{t.Hours} h em todos os jogos", $"{t.Hours} h in all games") : $"{t.Hours} h · {t.GameName}";
        var ts = p.Texto.Medir(title, 16, FontWeight.Bold);
        var ss = p.Texto.Medir(top, 12, FontWeight.SemiBold);
        float w = MathF.Min(MathF.Max(ts.X, ss.X) + 86, 420), h = 62;
        var c = p.NovaCena(new Vector2(w, h));
        var f = p.G.Fabrica;
        var cc = p.C;
        var color = Trophies.TierColor(t.Tier);
        var ring = Layer(p, c, new Vector2(4, 1), new Vector2(60, 60), (dc, s) =>
        {
            using var b = dc.CreateSolidColorBrush(new Color4(color.R, color.G, color.B, 0.8f));
            dc.DrawEllipse(new Ellipse(s / 2, 26, 26), b, 2.5f);
        });
        var cup = Layer(p, c, new Vector2(14, 11), new Vector2(40, 40), (dc, s) =>
        {
            TrophyRoom.Cup(dc, f, s / 2 + new Vector2(0, 1.5f), 34, new Color4(0, 0, 0, 0.45f));
            TrophyRoom.Cup(dc, f, s / 2, 34, color);
            TrophyRoom.Star(dc, f, s / 2 + new Vector2(0, -4), 6, new Color4(1, 1, 1, 0.9f));
        });
        var text = Layer(p, c, new Vector2(70, (h - ts.Y - ss.Y - 2) / 2), new Vector2(w - 74, ts.Y + ss.Y + 4), (dc, _) =>
        {
            p.Texto.Desenhar(dc, top, Vector2.Zero, 12, color, FontWeight.SemiBold, largura: w - 74);
            p.Texto.Desenhar(dc, title, new Vector2(0, ss.Y + 1), 16, Cor.Branco, FontWeight.Bold, largura: w - 74);
        });
        Scale(cc, cup, 0, 520, new[] { (0f, 0.2f), (0.55f, 1.25f), (0.8f, 0.94f), (1f, 1f) });
        Rotate(cc, cup, 0, 520, new[] { (0f, -25f), (0.55f, 8f), (1f, 0f) });
        Fade(cc, cup, 0, 120);
        Scale(cc, ring, 250, 600, new[] { (0f, 0.4f), (1f, 1.5f) });
        Opacity(cc, ring, 250, 600, new[] { (0f, 0f), (0.15f, 1f), (1f, 0f) });
        Slide(cc, text, 220, 360, new Vector2(16, 0));
        Fade(cc, text, 220, 240);
        return c;
    }

    /// <summary>A skull: domed cranium, cheekbones, deep eye sockets, the nose and a row of teeth.</summary>
    public static void Skull(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float r, Color4 bone)
    {
        var shade = new Color4(bone.R * 0.72f, bone.G * 0.72f, bone.B * 0.74f, bone.A);
        using var head = f.CreatePathGeometry();
        using (var s = head.Open())
        {
            s.BeginFigure(c + new Vector2(-r * 0.62f, r * 0.42f), FigureBegin.Filled);
            // left cheek up to the dome, over it, down the right cheek
            s.AddBezier(new BezierSegment { Point1 = c + new Vector2(-r * 1.02f, r * 0.1f), Point2 = c + new Vector2(-r * 1.08f, -r * 1.02f), Point3 = c + new Vector2(0, -r * 1.02f) });
            s.AddBezier(new BezierSegment { Point1 = c + new Vector2(r * 1.08f, -r * 1.02f), Point2 = c + new Vector2(r * 1.02f, r * 0.1f), Point3 = c + new Vector2(r * 0.62f, r * 0.42f) });
            // jaw
            s.AddLine(c + new Vector2(r * 0.46f, r * 0.62f));
            s.AddBezier(new BezierSegment { Point1 = c + new Vector2(r * 0.44f, r * 0.98f), Point2 = c + new Vector2(-r * 0.44f, r * 0.98f), Point3 = c + new Vector2(-r * 0.46f, r * 0.62f) });
            s.EndFigure(FigureEnd.Closed);
            s.Close();
        }
        using (var fill = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = c + new Vector2(0, -r), EndPoint = c + new Vector2(0, r) },
                   dc.CreateGradientStopCollection(new[] { new GradientStop(0f, bone), new GradientStop(1f, shade) })))
            dc.FillGeometry(head, fill);

        var hole = new Color4(0.05f, 0.03f, 0.04f, bone.A);
        // eye sockets, a little slanted inward
        foreach (float side in new[] { -1f, 1f })
        {
            using var eye = f.CreatePathGeometry();
            using (var s = eye.Open())
            {
                var e = c + new Vector2(side * r * 0.4f, -r * 0.08f);
                s.BeginFigure(e + new Vector2(-side * r * 0.3f, -r * 0.1f), FigureBegin.Filled);
                s.AddBezier(new BezierSegment { Point1 = e + new Vector2(-side * r * 0.05f, -r * 0.34f), Point2 = e + new Vector2(side * r * 0.34f, -r * 0.18f), Point3 = e + new Vector2(side * r * 0.3f, r * 0.08f) });
                s.AddBezier(new BezierSegment { Point1 = e + new Vector2(side * r * 0.24f, r * 0.3f), Point2 = e + new Vector2(-side * r * 0.3f, r * 0.24f), Point3 = e + new Vector2(-side * r * 0.3f, -r * 0.1f) });
                s.EndFigure(FigureEnd.Closed);
                s.Close();
            }
            using var hb = dc.CreateSolidColorBrush(hole);
            dc.FillGeometry(eye, hb);
        }
        // the nose, an upside-down heart
        Icone.Triangulo(dc, f, c + new Vector2(0, r * 0.16f), c + new Vector2(-r * 0.13f, r * 0.4f), c + new Vector2(r * 0.13f, r * 0.4f), hole);
        // teeth: dark gaps on the jaw
        for (int i = -2; i <= 2; i++)
            Icone.Linha(dc, c + new Vector2(i * r * 0.17f, r * 0.58f), c + new Vector2(i * r * 0.17f, r * 0.84f), MathF.Max(1f, r * 0.07f), hole, f);
        Icone.Linha(dc, c + new Vector2(-r * 0.42f, r * 0.6f), c + new Vector2(r * 0.42f, r * 0.6f), MathF.Max(1f, r * 0.06f), hole, f);
    }

    /// <summary>One slash across the skull: a red blade with a hot white core.</summary>
    private static void Cut(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 size, bool down)
    {
        var a = down ? new Vector2(size.X * 0.12f, size.Y * 0.12f) : new Vector2(size.X * 0.88f, size.Y * 0.12f);
        var b = down ? new Vector2(size.X * 0.88f, size.Y * 0.88f) : new Vector2(size.X * 0.12f, size.Y * 0.88f);
        Icone.Linha(dc, a + new Vector2(0, 1.5f), b + new Vector2(0, 1.5f), 7f, new Color4(0, 0, 0, 0.5f), f);
        Icone.Linha(dc, a, b, 6f, Blood, f);
        Icone.Linha(dc, a + (b - a) * 0.08f, b - (b - a) * 0.08f, 1.6f, new Color4(1f, 0.85f, 0.85f, 0.95f), f);
    }

    // ------------------------------------------------------------------ goal

    public static Cena Goal(Palco p, bool ours, Color4 color)
    {
        if (!ours)
        {
            string t = Lang.T("Gol do adversário", "Their goal");
            var s = p.Texto.Medir(t, 17, FontWeight.Bold);
            var cc0 = p.NovaCena(new Vector2(s.X + 34, 36));
            Layer(p, cc0, new Vector2(6, 8), new Vector2(20, 20), (dc, tt) =>
            {
                using var b = dc.CreateSolidColorBrush(color);
                dc.DrawEllipse(new Ellipse(tt / 2, 8, 8), b, 2);
                Icone.Circulo(dc, tt / 2, 3, color);
            });
            var l = Layer(p, cc0, new Vector2(30, (36 - s.Y) / 2), new Vector2(s.X + 2, s.Y), (dc, _) => p.Texto.Desenhar(dc, t, Vector2.Zero, 17, Cor.Secundario, FontWeight.Bold));
            Fade(p.C, l, 0, 220);
            Slide(p.C, l, 0, 260, new Vector2(0, -8));
            return cc0;
        }

        return Shout(p, "GOOOOOOOOOL!", color, (word, i) => word[i] == 'O');
    }

    /// <summary>Rocket League's epic save (3.7.0, his request): "WHAT A SAVE!" lit like the goal, SAVE in the team color.</summary>
    public static Cena Save(Palco p, Color4 color) => Shout(p, "WHAT A SAVE!", color, (word, i) => i >= 7 && i <= 10);

    /// <summary>
    /// A word lit letter by letter from the left (each pops in big with a white flash), then blinking with a glow and a
    /// shine running over it twice. `tinted` picks the letters in the color; the others are white.
    /// </summary>
    public static Cena Shout(Palco p, string goal, Color4 color, Func<string, int, bool> tinted)
    {
        const float size = 36;
        var full = p.Texto.Medir(goal, size, FontWeight.Black);
        float pad = 16, h = 60;
        var c = p.NovaCena(new Vector2(full.X + pad * 2, h));
        var origin = new Vector2(pad, (h - full.Y) / 2);
        var cc = p.C;

        // the glow behind: the word in the team color, blurred, breathing after the letters are in
        var glow = Layer(p, c, origin - new Vector2(14, 14), full + new Vector2(28, 28), (dc, _) => Glow(dc, p, goal, size, color, new Vector2(14, 14)));
        Opacity(cc, glow, 0, 1, new[] { (0f, 0f) });

        // each letter on its own layer, lit one after the other from the left; the blink is on their group
        var letters = Group(p, c);
        var glyphs = new List<(SpriteVisual V, float X)>();
        float x = 0;
        int n = goal.Length;
        for (int i = 0; i < n; i++)
        {
            string ch = goal[i].ToString();
            var cs = p.Texto.Medir(ch, size, FontWeight.Black);
            var col = tinted(goal, i) ? color : Cor.Branco;
            var v = Layer(p, c, origin + new Vector2(x - 2, 0), cs + new Vector2(4, 2), (dc, _) =>
            {
                p.Texto.Desenhar(dc, ch, new Vector2(2, 2.5f), size, new Color4(0, 0, 0, 0.55f), FontWeight.Black);
                p.Texto.Desenhar(dc, ch, new Vector2(2, 0), size, col, FontWeight.Black);
            }, letters);
            var white = Layer(p, c, origin + new Vector2(x - 2, 0), cs + new Vector2(4, 2), (dc, _) =>
                p.Texto.Desenhar(dc, ch, new Vector2(2, 0), size, Cor.Branco, FontWeight.Black), letters);
            int delay = i * 62;
            Fade(cc, v, delay, 70);
            Scale(cc, v, delay, 260, new[] { (0f, 1.7f), (0.55f, 0.9f), (1f, 1f) });
            Slide(cc, v, delay, 260, new Vector2(0, -9));
            // a white flash as it lands, cooling into its color
            Opacity(cc, white, delay, 300, new[] { (0f, 0f), (0.15f, 1f), (1f, 0f) });
            Scale(cc, white, delay, 260, new[] { (0f, 1.7f), (0.55f, 0.9f), (1f, 1f) });
            glyphs.Add((v, x));
            x += cs.X;
        }
        int lit = n * 62 + 200;

        // then it blinks and a shine runs over it from left to right, twice
        Opacity(cc, letters, lit, 900, new[] { (0f, 1f), (0.15f, 0.45f), (0.3f, 1f), (0.45f, 0.45f), (0.6f, 1f), (1f, 1f) });
        Opacity(cc, glow, lit - 150, 1600, new[] { (0f, 0f), (0.15f, 0.95f), (0.3f, 0.5f), (0.45f, 0.95f), (0.6f, 0.5f), (0.8f, 0.85f), (1f, 0.7f) });
        Shine(p, c, goal, size, origin, full, lit, 2);
        return c;
    }

    /// <summary>The word blurred in its color (Direct2D's gaussian blur over a command list of the text).</summary>
    private static void Glow(ID2D1DeviceContext dc, Palco p, string text, float size, Color4 color, Vector2 at)
    {
        using var list = dc.CreateCommandList();
        var old = dc.Target;
        dc.Target = list;
        p.Texto.Desenhar(dc, text, at, size, color, FontWeight.Black);
        dc.Target = old;
        list.Close();
        using var blur = new Vortice.Direct2D1.Effects.GaussianBlur(dc);
        blur.SetInput(0, list, true);
        blur.StandardDeviation = 7f;
        blur.BorderMode = BorderMode.Soft;
        dc.DrawImage(blur);
        old?.Dispose();
    }

    /// <summary>
    /// A bright band sweeping over the letters only: a gradient brush masked by the word, its offset animated across.
    /// </summary>
    private static void Shine(Palco p, Cena c, string text, float size, Vector2 origin, Vector2 full, int delay, int times)
    {
        var cc = p.C;
        var mask = p.G.NovaSuperficie(full + new Vector2(4, 2), (dc, _) => p.Texto.Desenhar(dc, text, new Vector2(2, 0), size, Cor.Branco, FontWeight.Black));
        c.Superficies.Add(mask);
        var band = cc.CreateLinearGradientBrush();
        band.MappingMode = CompositionMappingMode.Absolute;
        float bw = p.Px(70);
        band.StartPoint = new Vector2(0, 0);
        band.EndPoint = new Vector2(bw, p.Px(18));
        band.ColorStops.Add(cc.CreateColorGradientStop(0f, Windows.UI.Color.FromArgb(0, 255, 255, 255)));
        band.ColorStops.Add(cc.CreateColorGradientStop(0.5f, Windows.UI.Color.FromArgb(235, 255, 255, 255)));
        band.ColorStops.Add(cc.CreateColorGradientStop(1f, Windows.UI.Color.FromArgb(0, 255, 255, 255)));
        band.ExtendMode = CompositionGradientExtendMode.Clamp;
        var masked = cc.CreateMaskBrush();
        masked.Source = band;
        masked.Mask = mask.Pincel;
        var v = cc.CreateSpriteVisual();
        v.Size = mask.TamanhoPx;
        v.Offset = new Vector3(p.Px(origin.X - 2), p.Px(origin.Y), 0);
        v.Brush = masked;
        c.Raiz.Children.InsertAtTop(v);
        var a = cc.CreateVector2KeyFrameAnimation();
        a.InsertKeyFrame(0f, new Vector2(-bw, 0));
        a.InsertKeyFrame(1f, new Vector2(v.Size.X + bw * 0.2f, 0), cc.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.55f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(620);
        a.DelayTime = TimeSpan.FromMilliseconds(delay);
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        a.IterationBehavior = AnimationIterationBehavior.Count;
        a.IterationCount = times;
        band.StartAnimation("Offset", a);
        c.AoSoltar += () => { masked.Dispose(); band.Dispose(); };
    }

    // ------------------------------------------------------------------ end of the match

    /// <summary>"EZ" for a win by a wide margin, "GG" for a close one, a quieter "GG" for a loss.</summary>
    public static string EndWord(Outcome o, int? us, int? them)
    {
        if (o != Outcome.Win || us is not int a || them is not int b) return "GG";
        return a - b >= Math.Max(3, a * 0.5) ? "EZ" : "GG";
    }

    private static readonly Color4 MvpGold = new(1f, 0.82f, 0.25f, 1f);

    /// <param name="mvp">
    /// He was the match's MVP (3.8.0, his request): a crown drops above the word, the word turns gold with a glow and a
    /// shine runs over it.
    /// </param>
    public static Cena End(Palco p, Outcome o, int? us, int? them, bool mvp = false)
    {
        string word = EndWord(o, us, them);
        Color4 color = mvp ? MvpGold : o == Outcome.Win ? TeamColors.Win : o == Outcome.Loss ? Cor.Branco : Cor.Secundario;
        string sub = (mvp ? "MVP · " : "") + (o == Outcome.Win ? Lang.T("Vitória", "Victory") : o == Outcome.Loss ? Lang.T("Derrota", "Defeat") : Lang.T("Empate", "Draw"))
                     + (us is int a && them is int b ? $"  {a} : {b}" : "");
        var ws = p.Texto.Medir(word, 46, FontWeight.Black);
        var ss = p.Texto.Medir(sub, 14, FontWeight.SemiBold, tabular: true);
        float crown = mvp ? 30 : 0;
        float w = MathF.Max(ws.X + 24, ss.X + 24), h = 84 + crown;
        var c = p.NovaCena(new Vector2(MathF.Max(w, 150), h));
        float cw = c.Tamanho.X;
        var origin = new Vector2((cw - ws.X) / 2, 2 + crown);
        SpriteVisual? glow = null;
        if (mvp)
        {
            glow = Layer(p, c, origin - new Vector2(14, 14), ws + new Vector2(28, 28), (dc, _) => Glow(dc, p, word, 46, MvpGold, new Vector2(14, 14)));
            Opacity(p.C, glow, 0, 1, new[] { (0f, 0f) });
        }
        var big = Layer(p, c, origin, ws, (dc, _) => p.Texto.Desenhar(dc, word, Vector2.Zero, 46, color, FontWeight.Black));
        var small = Layer(p, c, new Vector2((cw - ss.X) / 2, h - ss.Y - 4), ss, (dc, _) => p.Texto.Desenhar(dc, sub, Vector2.Zero, 14, mvp ? MvpGold : Cor.Secundario, FontWeight.SemiBold, tabular: true));
        var cc = p.C;
        if (mvp)
        {
            var king = Layer(p, c, new Vector2(cw / 2 - 20, 0), new Vector2(40, 32), (dc, s) => Crown(dc, p.G.Fabrica, new System.Drawing.RectangleF(2, 2, s.X - 4, s.Y - 4)));
            Fade(cc, king, 380, 140);
            Slide(cc, king, 380, 520, new Vector2(0, -24), bounce: true);
            Rotate(cc, king, 380, 520, new[] { (0f, -18f), (0.7f, 6f), (1f, 0f) });
            Opacity(cc, glow!, 600, 1800, new[] { (0f, 0f), (0.2f, 1f), (0.45f, 0.45f), (0.7f, 1f), (1f, 0.7f) });
            Shine(p, c, word, 46, origin, ws, 700, 2);
        }
        if (o == Outcome.Win)
        {
            Scale(cc, big, 0, 420, new[] { (0f, 2.8f), (0.6f, 0.9f), (0.8f, 1.06f), (1f, 1f) });
            Rotate(cc, big, 0, 420, new[] { (0f, -14f), (0.6f, 3f), (1f, 0f) });
        }
        else Scale(cc, big, 0, 380, new[] { (0f, 0.6f), (1f, 1f) });
        Fade(cc, big, 0, 160);
        Fade(cc, small, 280, 260);
        Slide(cc, small, 280, 300, new Vector2(0, 10));
        return c;
    }

    /// <summary>A gold crown: five points with jewels on the tips and a band with three stones.</summary>
    public static void Crown(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r, float alpha = 1)
    {
        float W = r.Width, H = r.Height;
        Vector2 P(float x, float y) => new(r.X + x * W, r.Y + y * H);
        using var g = f.CreatePathGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(P(0.06f, 0.82f), FigureBegin.Filled);
            s.AddLine(P(0.02f, 0.26f));
            s.AddLine(P(0.27f, 0.55f));
            s.AddLine(P(0.5f, 0.12f));
            s.AddLine(P(0.73f, 0.55f));
            s.AddLine(P(0.98f, 0.26f));
            s.AddLine(P(0.94f, 0.82f));
            s.EndFigure(FigureEnd.Closed);
            s.Close();
        }
        using (var stops = dc.CreateGradientStopCollection(new[]
               {
                   new GradientStop { Position = 0, Color = new Color4(1f, 0.93f, 0.55f, alpha) },
                   new GradientStop { Position = 1, Color = new Color4(0.86f, 0.56f, 0.08f, alpha) },
               }))
        using (var b = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = P(0, 0.1f), EndPoint = P(0, 0.85f) }, stops))
            dc.FillGeometry(g, b);
        Icone.Retangulo(dc, P(0.06f, 0).X, P(0, 0.74f).Y, W * 0.88f, H * 0.18f, H * 0.06f, new Color4(0.8f, 0.5f, 0.06f, alpha));
        foreach (var (x, y, col) in new[] { (0.02f, 0.24f, MvpGold), (0.5f, 0.1f, MvpGold), (0.98f, 0.24f, MvpGold) })
            Icone.Circulo(dc, P(x, y), H * 0.08f, new Color4(col.R, col.G, col.B, alpha));
        foreach (var (x, col) in new[] { (0.3f, new Color4(0.25f, 0.6f, 1f, alpha)), (0.5f, new Color4(1f, 0.3f, 0.35f, alpha)), (0.7f, new Color4(0.25f, 0.85f, 0.45f, alpha)) })
            Icone.Circulo(dc, P(x, 0.83f), H * 0.055f, col);
    }

    // ------------------------------------------------------------------ composition helpers

    /// <summary>
    /// A drawn layer. With `group` it goes inside that container instead of the scene's root: two animations of the
    /// same property on one visual replace each other, so a second motion goes on the group.
    /// </summary>
    internal static SpriteVisual Layer(Palco p, Cena c, Vector2 pos, Vector2 size, Action<ID2D1DeviceContext, Vector2> draw, ContainerVisual? group = null)
    {
        var s = p.Desenho(c, pos, size, draw);
        SpriteVisual? v = null;
        foreach (var x in c.Raiz.Children)
            if (x is SpriteVisual sv && ReferenceEquals(sv.Brush, s.Pincel)) v = sv;
        v!.CenterPoint = new Vector3(v.Size.X / 2, v.Size.Y / 2, 0);
        if (group != null)
        {
            c.Raiz.Children.Remove(v);
            group.Children.InsertAtTop(v);
        }
        return v;
    }

    /// <summary>An empty container the size of the scene, on top of what is already there.</summary>
    internal static ContainerVisual Group(Palco p, Cena c)
    {
        var g = p.C.CreateContainerVisual();
        g.Size = c.Raiz.Size;
        g.CenterPoint = new Vector3(g.Size / 2, 0);
        c.Raiz.Children.InsertAtTop(g);
        return g;
    }

    internal static CompositionEasingFunction Out(Compositor c) => c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

    internal static void Scale(Compositor c, Visual v, int delay, int ms, (float At, float S)[] keys)
    {
        var a = c.CreateVector3KeyFrameAnimation();
        foreach (var (at, s) in keys) a.InsertKeyFrame(at, new Vector3(s, s, 1), Out(c));
        Run(v, "Scale", a, delay, ms);
    }

    internal static void Scale3(Compositor c, Visual v, int delay, int ms, (float At, Vector2 S)[] keys)
    {
        var a = c.CreateVector3KeyFrameAnimation();
        foreach (var (at, s) in keys) a.InsertKeyFrame(at, new Vector3(s, 1), Out(c));
        Run(v, "Scale", a, delay, ms);
    }

    internal static void Fade(Compositor c, Visual v, int delay, int ms) => Opacity(c, v, delay, ms, new[] { (0f, 0f), (1f, 1f) });

    internal static void Opacity(Compositor c, Visual v, int delay, int ms, (float At, float O)[] keys)
    {
        if (keys.Length == 1) { v.Opacity = keys[0].O; return; }
        var a = c.CreateScalarKeyFrameAnimation();
        foreach (var (at, o) in keys) a.InsertKeyFrame(at, o, c.CreateLinearEasingFunction());
        Run(v, "Opacity", a, delay, ms);
    }

    internal static void Slide(Compositor c, Visual v, int delay, int ms, Vector2 from, bool bounce = false)
    {
        var a = c.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(0f, v.Offset + new Vector3(from, 0));
        if (bounce)
        {
            var fall = c.CreateCubicBezierEasingFunction(new Vector2(0.5f, 0f), new Vector2(1f, 1f));
            a.InsertKeyFrame(0.75f, v.Offset, fall);
            a.InsertKeyFrame(0.88f, v.Offset + new Vector3(0, -3, 0), Out(c));
            a.InsertKeyFrame(1f, v.Offset, fall);
        }
        else a.InsertKeyFrame(1f, v.Offset, Out(c));
        Run(v, "Offset", a, delay, ms);
    }

    /// <summary>A quick jitter around where the visual already is.</summary>
    internal static void Shake(Compositor c, Visual v, int delay, int ms, float amount)
    {
        var a = c.CreateVector3KeyFrameAnimation();
        var o = v.Offset;
        float[] xs = { 0, -1, 0.8f, -0.6f, 0.4f, 0 };
        for (int i = 0; i < xs.Length; i++)
            a.InsertKeyFrame(i / (float)(xs.Length - 1), o + new Vector3(xs[i] * amount, (i % 2 == 0 ? 0.5f : -0.5f) * amount * (i == xs.Length - 1 ? 0 : 1), 0));
        Run(v, "Offset", a, delay, ms);
    }

    /// <summary>Uncovers the visual from one side to the other (an inset clip running across it).</summary>
    internal static void Reveal(Compositor c, Visual v, int delay, int ms, float sizeDip, bool fromLeft)
    {
        var clip = c.CreateInsetClip();
        float w = v.Size.X;
        if (fromLeft) clip.RightInset = w; else clip.LeftInset = w;
        v.Clip = clip;
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0f, w);
        a.InsertKeyFrame(1f, 0f, c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.6f), new Vector2(0.35f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(ms);
        a.DelayTime = TimeSpan.FromMilliseconds(delay);
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        clip.StartAnimation(fromLeft ? "RightInset" : "LeftInset", a);
    }

    internal static void Rotate(Compositor c, Visual v, int delay, int ms, (float At, float Deg)[] keys)
    {
        var a = c.CreateScalarKeyFrameAnimation();
        foreach (var (at, d) in keys) a.InsertKeyFrame(at, d);
        Run(v, "RotationAngleInDegrees", a, delay, ms);
    }

    internal static void Run(Visual v, string property, KeyFrameAnimation a, int delay, int ms)
    {
        a.Duration = TimeSpan.FromMilliseconds(ms);
        if (delay > 0)
        {
            a.DelayTime = TimeSpan.FromMilliseconds(delay);
            a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
        v.StartAnimation(property, a);
    }
}
