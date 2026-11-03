using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Ilha.Sports;

/// <summary>
/// The Sports menu icon, one per sport (3.5.0, his request: it follows what is live, or the next game today): a soccer
/// ball, an F1 car, a tennis racket with its ball, a basketball and an American football. Drawn in a 24-unit box.
/// </summary>
internal static class SportIcons
{
    public static void Draw(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r, string sport)
    {
        float u = r.Width / 24f;
        Vector2 P(float x, float y) => new(r.X + x * u, r.Y + y * u);
        switch (sport)
        {
            case "racing": Car(dc, f, P, u); break;
            case "tennis": Tennis(dc, f, P, u); break;
            case "basketball": Basketball(dc, f, P, u); break;
            case "football": Football(dc, f, P, u); break;
            default: Soccer(dc, f, P, u); break;
        }
    }

    private static readonly Color4 Ink = new(0.1f, 0.1f, 0.11f, 1);

    private static ID2D1PathGeometry Poly(ID2D1Factory1 f, params Vector2[] pts)
    {
        var g = f.CreatePathGeometry();
        using var s = g.Open();
        s.BeginFigure(pts[0], FigureBegin.Filled);
        for (int i = 1; i < pts.Length; i++) s.AddLine(pts[i]);
        s.EndFigure(FigureEnd.Closed);
        s.Close();
        return g;
    }

    private static ID2D1LinearGradientBrush Linear(ID2D1DeviceContext dc, Vector2 a, Vector2 b, Color4 from, Color4 to)
    {
        using var stops = dc.CreateGradientStopCollection(new[] { new GradientStop { Position = 0, Color = from }, new GradientStop { Position = 1, Color = to } });
        return dc.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = a, EndPoint = b }, stops);
    }

    private static void Fill(ID2D1DeviceContext dc, ID2D1Geometry g, Color4 c)
    {
        using var b = dc.CreateSolidColorBrush(c);
        dc.FillGeometry(g, b);
    }

    /// <summary>Draws inside a ball only (the seams and patches that run over its edge).</summary>
    private static void InBall(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float rad, Action draw)
    {
        using var ball = f.CreateEllipseGeometry(new Ellipse(c, rad, rad));
        dc.PushLayer(new LayerParameters1
        {
            ContentBounds = new Vortice.RawRectF(c.X - rad - 1, c.Y - rad - 1, c.X + rad + 1, c.Y + rad + 1),
            GeometricMask = ball, Opacity = 1, MaskTransform = Matrix3x2.Identity,
        }, null!);
        draw();
        dc.PopLayer();
    }

    private static Vector2[] Pentagon(Vector2 c, float rad, float turn)
    {
        var pts = new Vector2[5];
        for (int i = 0; i < 5; i++)
        {
            float a = turn + i * MathF.Tau / 5;
            pts[i] = c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * rad;
        }
        return pts;
    }

    private static void Soccer(ID2D1DeviceContext dc, ID2D1Factory1 f, Func<float, float, Vector2> P, float u)
    {
        var c = P(12, 12);
        float rad = 9.4f * u;
        Icone.Circulo(dc, c, rad, new Color4(0.97f, 0.97f, 0.98f, 1));
        InBall(dc, f, c, rad, () =>
        {
            var mid = Pentagon(c, 3.4f * u, -MathF.PI / 2);
            using (var g = Poly(f, mid)) Fill(dc, g, Ink);
            for (int i = 0; i < 5; i++)
            {
                float a = -MathF.PI / 2 + i * MathF.Tau / 5;
                var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
                // a seam out of each corner to a patch cut by the edge
                Icone.Linha(dc, mid[i], c + dir * 6.4f * u, 0.8f * u, Ink, f);
                using var g = Poly(f, Pentagon(c + dir * 9.6f * u, 3.2f * u, a + MathF.PI / 5));
                Fill(dc, g, Ink);
            }
            // a little shade on the lower right
            Icone.Circulo(dc, c + new Vector2(2.6f, 2.6f) * u, rad, new Color4(0, 0, 0, 0.08f));
        });
        using var rim = dc.CreateSolidColorBrush(new Color4(0.55f, 0.55f, 0.58f, 1));
        dc.DrawEllipse(new Ellipse(c, rad, rad), rim, 0.45f * u);
    }

    private static void Basketball(ID2D1DeviceContext dc, ID2D1Factory1 f, Func<float, float, Vector2> P, float u)
    {
        var c = P(12, 12);
        float rad = 9.4f * u;
        Icone.Circulo(dc, c, rad, new Color4(0.93f, 0.47f, 0.17f, 1));
        InBall(dc, f, c, rad, () =>
        {
            Icone.Circulo(dc, c + new Vector2(-2.4f, -2.8f) * u, 5 * u, new Color4(1, 1, 1, 0.12f));
            var seam = new Color4(0.2f, 0.09f, 0.03f, 1);
            Icone.Linha(dc, c + new Vector2(0, -rad), c + new Vector2(0, rad), 0.9f * u, seam, f);
            Icone.Linha(dc, c + new Vector2(-rad, 0), c + new Vector2(rad, 0), 0.9f * u, seam, f);
            using var b = dc.CreateSolidColorBrush(seam);
            dc.DrawEllipse(new Ellipse(c + new Vector2(-11.4f, 0) * u, 7.2f * u, 11 * u), b, 0.9f * u);
            dc.DrawEllipse(new Ellipse(c + new Vector2(11.4f, 0) * u, 7.2f * u, 11 * u), b, 0.9f * u);
        });
    }

    private static void Football(ID2D1DeviceContext dc, ID2D1Factory1 f, Func<float, float, Vector2> P, float u)
    {
        var c = P(12, 12);
        var old = dc.Transform;
        dc.Transform = Matrix3x2.CreateRotation(-MathF.PI / 4.2f, c) * old;
        using (var g = f.CreatePathGeometry())
        {
            using (var s = g.Open())
            {
                s.BeginFigure(c + new Vector2(-11, 0) * u, FigureBegin.Filled);
                s.AddBezier(new BezierSegment { Point1 = c + new Vector2(-6, -7.6f) * u, Point2 = c + new Vector2(6, -7.6f) * u, Point3 = c + new Vector2(11, 0) * u });
                s.AddBezier(new BezierSegment { Point1 = c + new Vector2(6, 7.6f) * u, Point2 = c + new Vector2(-6, 7.6f) * u, Point3 = c + new Vector2(-11, 0) * u });
                s.EndFigure(FigureEnd.Closed);
                s.Close();
            }
            using var brown = Linear(dc, c + new Vector2(0, -6) * u, c + new Vector2(0, 6) * u,
                new Color4(0.62f, 0.33f, 0.17f, 1), new Color4(0.38f, 0.18f, 0.08f, 1));
            dc.FillGeometry(g, brown);
        }
        var white = new Color4(0.97f, 0.96f, 0.93f, 1);
        // the stripes near the tips and the laces on top
        foreach (float side in new[] { -1f, 1f })
            Icone.Linha(dc, c + new Vector2(side * 6.6f, -3.2f) * u, c + new Vector2(side * 6.6f, 3.2f) * u, 0.9f * u, white, f);
        Icone.Linha(dc, c + new Vector2(-3.6f, -2.2f) * u, c + new Vector2(3.6f, -2.2f) * u, 0.9f * u, white, f);
        for (float x = -2.7f; x <= 2.8f; x += 1.8f)
            Icone.Linha(dc, c + new Vector2(x, -3.4f) * u, c + new Vector2(x, -1) * u, 0.8f * u, white, f);
        dc.Transform = old;
    }

    private static void Tennis(ID2D1DeviceContext dc, ID2D1Factory1 f, Func<float, float, Vector2> P, float u)
    {
        var c = P(10.6f, 10.4f);
        var old = dc.Transform;
        dc.Transform = Matrix3x2.CreateRotation(MathF.PI / 4.5f, c) * old;
        var frame = new Color4(0.86f, 0.18f, 0.2f, 1);
        var head = c + new Vector2(0, -3.4f) * u;
        // the handle and the throat
        Icone.Retangulo(dc, c.X - 1.2f * u, c.Y + 4.6f * u, 2.4f * u, 6.6f * u, 0.9f * u, new Color4(0.16f, 0.16f, 0.18f, 1));
        Icone.Linha(dc, head + new Vector2(-3, 5.4f) * u, c + new Vector2(0, 5) * u, 1.2f * u, frame, f);
        Icone.Linha(dc, head + new Vector2(3, 5.4f) * u, c + new Vector2(0, 5) * u, 1.2f * u, frame, f);
        // the strings inside the head
        using (var face = f.CreateEllipseGeometry(new Ellipse(head, 5.2f * u, 6.6f * u)))
        {
            dc.PushLayer(new LayerParameters1
            {
                ContentBounds = new Vortice.RawRectF(head.X - 8 * u, head.Y - 8 * u, head.X + 8 * u, head.Y + 8 * u),
                GeometricMask = face, Opacity = 1, MaskTransform = Matrix3x2.Identity,
            }, null!);
            Icone.Circulo(dc, head, 7 * u, new Color4(1, 1, 1, 0.08f));
            var str = new Color4(0.92f, 0.92f, 0.94f, 0.85f);
            for (float d = -4.2f; d <= 4.3f; d += 1.4f)
            {
                Icone.Linha(dc, head + new Vector2(d, -7) * u, head + new Vector2(d, 7) * u, 0.45f * u, str, f);
                Icone.Linha(dc, head + new Vector2(-6, d * 1.2f) * u, head + new Vector2(6, d * 1.2f) * u, 0.45f * u, str, f);
            }
            dc.PopLayer();
        }
        using (var b = dc.CreateSolidColorBrush(frame))
            dc.DrawEllipse(new Ellipse(head, 5.2f * u, 6.6f * u), b, 1.5f * u);
        dc.Transform = old;
        // the ball, down on the right
        var ball = P(18.4f, 18.2f);
        Icone.Circulo(dc, ball, 3.5f * u, new Color4(0.84f, 0.93f, 0.25f, 1));
        using var seam = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 0.95f));
        InBall(dc, f, ball, 3.5f * u, () =>
        {
            dc.DrawEllipse(new Ellipse(ball + new Vector2(-4.2f, 0) * u, 2.6f * u, 3.6f * u), seam, 0.6f * u);
            dc.DrawEllipse(new Ellipse(ball + new Vector2(4.2f, 0) * u, 2.6f * u, 3.6f * u), seam, 0.6f * u);
        });
    }

    private static void Car(ID2D1DeviceContext dc, ID2D1Factory1 f, Func<float, float, Vector2> P, float u)
    {
        var red = new Color4(0.88f, 0.1f, 0.12f, 1);
        var dark = new Color4(0.12f, 0.12f, 0.13f, 1);
        // the rear wing, behind the body
        Icone.Linha(dc, P(21.6f, 8.2f), P(21.6f, 13.2f), 1.5f * u, dark, f);
        Icone.Retangulo(dc, P(19.4f, 7.4f).X, P(0, 7.4f).Y, 3.6f * u, 1.7f * u, 0.4f * u, red);
        using (var body = Poly(f, P(1, 16.4f), P(1.2f, 14.8f), P(4.6f, 14.2f), P(9.4f, 12.6f), P(11.6f, 11.2f), P(13.2f, 9.8f),
                   P(15.2f, 9.6f), P(16.2f, 8.6f), P(17.8f, 10.6f), P(20.8f, 11.2f), P(20.8f, 13.8f), P(15, 15.8f), P(9.6f, 16.2f)))
        {
            using var b = Linear(dc, P(0, 9), P(0, 16.4f), new Color4(1, 0.3f, 0.3f, 1), red);
            dc.FillGeometry(body, b);
        }
        // the front wing, the helmet and the halo over it
        Icone.Retangulo(dc, P(0.4f, 0).X, P(0, 16).Y, 5.4f * u, 1.3f * u, 0.4f * u, dark);
        Icone.Circulo(dc, P(13.6f, 10.2f), 1.25f * u, new Color4(0.98f, 0.82f, 0.18f, 1));
        Icone.Linha(dc, P(11.4f, 10.6f), P(15, 9.2f), 0.9f * u, dark, f);
        // the wheels, the rear one bigger
        foreach (var (x, y, rr) in new[] { (17.6f, 15.2f, 3.7f), (6.2f, 15.8f, 3.1f) })
        {
            Icone.Circulo(dc, P(x, y), rr * u, dark);
            Icone.Circulo(dc, P(x, y), rr * 0.42f * u, new Color4(0.55f, 0.55f, 0.58f, 1));
        }
    }
}
