using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Windows.UI.Composition;

namespace Ilha.Games;

/// <summary>
/// The moments on the pill: a skull that pops in and gets crossed out by a red X (a kill), "GOOOOOOOOOL!" stretching
/// out (a goal) and a big "EZ" or "GG" slamming down at the end. Each is a short alert drawn once and animated only in
/// the compositor.
/// </summary>
internal static class Celebrations
{
    private static readonly Color4 XRed = new(1f, 0.2f, 0.2f, 1f);

    // ------------------------------------------------------------------ kill

    public static Cena Kill(Palco p, int streak)
    {
        string text = streak > 1 ? $"x{streak}" : "+1";
        float w = 116;
        var c = p.NovaCena(new Vector2(w, 40));
        var skull = Layer(p, c, new Vector2(10, 2), new Vector2(36, 36), (dc, t) => Skull(dc, p.G.Fabrica, t / 2, 15));
        var cross = Layer(p, c, new Vector2(10, 2), new Vector2(36, 36), (dc, t) => Cross(dc, p.G.Fabrica, t / 2, 13));
        var label = Layer(p, c, new Vector2(54, 6), new Vector2(w - 56, 28), (dc, t) =>
            p.Texto.Desenhar(dc, text, new Vector2(0, 0), 22, Cor.Branco, FontWeight.Black, tabular: true));
        var cc = p.C;
        Pop(cc, skull, 0, 330, 0.25f, 1.18f);
        Fade(cc, skull, 0, 160);
        // the X slams onto the skull a beat later
        Scale(cc, cross, 180, 220, new[] { (0f, 2.4f), (1f, 1f) });
        Fade(cc, cross, 180, 120);
        Slide(cc, label, 120, 260, new Vector2(14, 0));
        Fade(cc, label, 120, 200);
        return c;
    }

    private static void Skull(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float r)
    {
        // cranium, jaw, eye sockets, nose and teeth
        Icone.Circulo(dc, c + new Vector2(0, -2.5f), r * 0.82f, Cor.Branco);
        Icone.Retangulo(dc, c.X - r * 0.5f, c.Y + r * 0.2f, r, r * 0.62f, r * 0.18f, Cor.Branco);
        var hole = new Color4(0, 0, 0, 1);
        Icone.Circulo(dc, c + new Vector2(-r * 0.33f, -r * 0.12f), r * 0.24f, hole);
        Icone.Circulo(dc, c + new Vector2(r * 0.33f, -r * 0.12f), r * 0.24f, hole);
        Icone.Triangulo(dc, f, c + new Vector2(0, r * 0.12f), c + new Vector2(-r * 0.1f, r * 0.32f), c + new Vector2(r * 0.1f, r * 0.32f), hole);
        for (int i = -1; i <= 1; i++)
            Icone.Linha(dc, c + new Vector2(i * r * 0.2f, r * 0.55f), c + new Vector2(i * r * 0.2f, r * 0.8f), 1.3f, hole, f);
    }

    private static void Cross(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float r)
    {
        var shadow = new Color4(0, 0, 0, 0.45f);
        foreach (var (a, b) in new[] { (new Vector2(-r, -r), new Vector2(r, r)), (new Vector2(r, -r), new Vector2(-r, r)) })
        {
            Icone.Linha(dc, c + a + new Vector2(0, 1.2f), c + b + new Vector2(0, 1.2f), 5.4f, shadow, f);
            Icone.Linha(dc, c + a, c + b, 4.4f, XRed, f);
        }
    }

    // ------------------------------------------------------------------ goal

    public static Cena Goal(Palco p, bool ours, Color4 color)
    {
        if (!ours)
        {
            string t = Lang.T("Gol do adversário", "Their goal");
            var s = p.Texto.Medir(t, 17, FontWeight.Bold);
            var cc0 = p.NovaCena(new Vector2(s.X + 34, 36));
            Icone_(p, cc0, color);
            var l = Layer(p, cc0, new Vector2(30, (36 - s.Y) / 2), new Vector2(s.X + 2, s.Y), (dc, _) => p.Texto.Desenhar(dc, t, Vector2.Zero, 17, Cor.Secundario, FontWeight.Bold));
            Fade(p.C, l, 0, 220);
            Slide(p.C, l, 0, 260, new Vector2(0, -8));
            return cc0;
        }
        const string goal = "GOOOOOOOOOL!";
        var size = p.Texto.Medir(goal, 34, FontWeight.Black);
        var c = p.NovaCena(new Vector2(size.X + 16, 48));
        var shadowLayer = Layer(p, c, new Vector2(8, (48 - size.Y) / 2 + 2), size, (dc, _) => p.Texto.Desenhar(dc, goal, Vector2.Zero, 34, new Color4(0, 0, 0, 0.5f), FontWeight.Black));
        var text = Layer(p, c, new Vector2(8, (48 - size.Y) / 2), size, (dc, _) =>
        {
            // the O's in the team color, the G, L and ! in white
            float x = 0;
            foreach (char ch in goal)
            {
                string s = ch.ToString();
                p.Texto.Desenhar(dc, s, new Vector2(x, 0), 34, ch == 'O' ? color : Cor.Branco, FontWeight.Black);
                x += p.Texto.Medir(s, 34, FontWeight.Black).X;
            }
        });
        var cc = p.C;
        foreach (var v in new[] { shadowLayer, text })
        {
            // stretches out from the middle, overshoots, then a little shake
            Scale(cc, v, 0, 520, new[] { (0f, 0.12f), (0.55f, 1.08f), (0.75f, 0.97f), (1f, 1f) }, xOnly: true);
            Fade(cc, v, 0, 140);
            Rotate(cc, v, 560, 600, new[] { (0f, 0f), (0.25f, -2.5f), (0.5f, 2.5f), (0.75f, -1.2f), (1f, 0f) });
        }
        return c;
    }

    private static void Icone_(Palco p, Cena c, Color4 color) =>
        p.Desenho(c, new Vector2(6, 8), new Vector2(20, 20), (dc, t) =>
        {
            using var b = dc.CreateSolidColorBrush(color);
            dc.DrawEllipse(new Ellipse(t / 2, 8, 8), b, 2);
            Icone.Circulo(dc, t / 2, 3, color);
        });

    // ------------------------------------------------------------------ end of the match

    /// <summary>"EZ" for a win by a wide margin, "GG" for a close one, a quieter "GG" for a loss.</summary>
    public static string EndWord(Outcome o, int? us, int? them)
    {
        if (o != Outcome.Win || us is not int a || them is not int b) return "GG";
        return a - b >= Math.Max(3, a * 0.5) ? "EZ" : "GG";
    }

    public static Cena End(Palco p, Outcome o, int? us, int? them)
    {
        string word = EndWord(o, us, them);
        Color4 color = o == Outcome.Win ? TeamColors.Win : o == Outcome.Loss ? Cor.Branco : Cor.Secundario;
        string sub = (o == Outcome.Win ? Lang.T("Vitória", "Victory") : o == Outcome.Loss ? Lang.T("Derrota", "Defeat") : Lang.T("Empate", "Draw"))
                     + (us is int a && them is int b ? $"  {a} : {b}" : "");
        var ws = p.Texto.Medir(word, 46, FontWeight.Black);
        var ss = p.Texto.Medir(sub, 14, FontWeight.SemiBold, tabular: true);
        float w = MathF.Max(ws.X + 24, ss.X + 24), h = 84;
        var c = p.NovaCena(new Vector2(MathF.Max(w, 150), h));
        float cw = c.Tamanho.X;
        var big = Layer(p, c, new Vector2((cw - ws.X) / 2, 2), ws, (dc, _) => p.Texto.Desenhar(dc, word, Vector2.Zero, 46, color, FontWeight.Black));
        var small = Layer(p, c, new Vector2((cw - ss.X) / 2, h - ss.Y - 4), ss, (dc, _) => p.Texto.Desenhar(dc, sub, Vector2.Zero, 14, Cor.Secundario, FontWeight.SemiBold, tabular: true));
        var cc = p.C;
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

    // ------------------------------------------------------------------ composition helpers

    private static SpriteVisual Layer(Palco p, Cena c, Vector2 pos, Vector2 size, Action<ID2D1DeviceContext, Vector2> draw)
    {
        var s = p.Desenho(c, pos, size, draw);
        SpriteVisual? v = null;
        foreach (var x in c.Raiz.Children)
            if (x is SpriteVisual sv && ReferenceEquals(sv.Brush, s.Pincel)) v = sv;
        v!.CenterPoint = new Vector3(v.Size.X / 2, v.Size.Y / 2, 0);
        return v;
    }

    private static CompositionEasingFunction Out(Compositor c) => c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

    private static void Scale(Compositor c, Visual v, int delay, int ms, (float At, float S)[] keys, bool xOnly = false)
    {
        var a = c.CreateVector3KeyFrameAnimation();
        foreach (var (at, s) in keys) a.InsertKeyFrame(at, xOnly ? new Vector3(s, MathF.Min(1, 0.6f + s * 0.4f), 1) : new Vector3(s, s, 1), Out(c));
        Run(v, "Scale", a, delay, ms);
    }

    private static void Pop(Compositor c, Visual v, int delay, int ms, float from, float peak) =>
        Scale(c, v, delay, ms, new[] { (0f, from), (0.6f, peak), (1f, 1f) });

    private static void Fade(Compositor c, Visual v, int delay, int ms)
    {
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0f, 0f);
        a.InsertKeyFrame(1f, 1f, Out(c));
        Run(v, "Opacity", a, delay, ms);
    }

    private static void Slide(Compositor c, Visual v, int delay, int ms, Vector2 from)
    {
        var a = c.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(0f, v.Offset + new Vector3(from, 0));
        a.InsertKeyFrame(1f, v.Offset, Out(c));
        Run(v, "Offset", a, delay, ms);
    }

    private static void Rotate(Compositor c, Visual v, int delay, int ms, (float At, float Deg)[] keys)
    {
        var a = c.CreateScalarKeyFrameAnimation();
        foreach (var (at, d) in keys) a.InsertKeyFrame(at, d);
        Run(v, "RotationAngleInDegrees", a, delay, ms);
    }

    private static void Run(Visual v, string property, KeyFrameAnimation a, int delay, int ms)
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
