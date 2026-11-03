using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Ilha.Focus;

/// <summary>The focus symbols (one color each, like the iPhone's) and the Do Not Disturb bell with its red slash.</summary>
internal static class FocusIcons
{
    public static readonly (string Id, string Pt, string En)[] Names =
    {
        ("lua", "Lua", "Moon"), ("maleta", "Maleta", "Briefcase"), ("controle", "Controle", "Controller"), ("cama", "Cama", "Bed"),
        ("livro", "Livro", "Book"), ("coracao", "Coração", "Heart"), ("fone", "Fone", "Headphones"), ("codigo", "Código", "Code"),
    };

    public static readonly (string Id, string Pt, string En, Color4 Color)[] Colors =
    {
        ("indigo", "Índigo", "Indigo", new Color4(0.42f, 0.4f, 0.96f, 1)),
        ("azul", "Azul", "Blue", new Color4(0.2f, 0.56f, 1f, 1)),
        ("ciano", "Ciano", "Cyan", new Color4(0.25f, 0.8f, 0.9f, 1)),
        ("verde", "Verde", "Green", new Color4(0.25f, 0.8f, 0.42f, 1)),
        ("amarelo", "Amarelo", "Yellow", new Color4(1f, 0.8f, 0.2f, 1)),
        ("laranja", "Laranja", "Orange", new Color4(1f, 0.58f, 0.2f, 1)),
        ("vermelho", "Vermelho", "Red", new Color4(1f, 0.32f, 0.32f, 1)),
        ("rosa", "Rosa", "Pink", new Color4(1f, 0.4f, 0.66f, 1)),
        ("roxo", "Roxo", "Purple", new Color4(0.72f, 0.42f, 0.95f, 1)),
    };

    public static Color4 ColorOf(string id)
    {
        foreach (var c in Colors) if (c.Id == id) return c.Color;
        return Colors[0].Color;
    }

    private static ID2D1PathGeometry Path(ID2D1Factory1 f, Action<ID2D1GeometrySink> build)
    {
        var g = f.CreatePathGeometry();
        using var s = g.Open();
        build(s);
        s.Close();
        return g;
    }

    private static void Fill(ID2D1DeviceContext dc, ID2D1Geometry g, Color4 c)
    {
        using var b = dc.CreateSolidColorBrush(c);
        dc.FillGeometry(g, b);
    }

    /// <summary>A mode's symbol in a 24-unit box.</summary>
    public static void Draw(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r, string name, Color4 color)
    {
        float u = r.Width / 24f;
        Vector2 P(float x, float y) => new(r.X + x * u, r.Y + y * u);
        switch (name)
        {
            case "maleta":
                using (var b = dc.CreateSolidColorBrush(color))
                    dc.DrawRoundedRectangle(new RoundedRectangle(new Vortice.RawRectF(P(9, 0).X, P(0, 4.6f).Y, P(15, 0).X, P(0, 9).Y), 1.4f * u, 1.4f * u), b, 1.7f * u);
                Icone.Retangulo(dc, P(3.5f, 0).X, P(0, 8).Y, 17 * u, 12 * u, 2.4f * u, color);
                Icone.Retangulo(dc, P(3.5f, 0).X, P(0, 12.6f).Y, 17 * u, 1.2f * u, 0, new Color4(0, 0, 0, 0.35f));
                break;
            case "controle":
                using (var g = Path(f, s =>
                {
                    s.BeginFigure(P(7, 7), FigureBegin.Filled);
                    s.AddLine(P(17, 7));
                    s.AddBezier(new BezierSegment { Point1 = P(21, 7), Point2 = P(23, 16), Point3 = P(21, 18.5f) });
                    s.AddBezier(new BezierSegment { Point1 = P(19.5f, 20.5f), Point2 = P(17, 19), Point3 = P(15, 15.5f) });
                    s.AddLine(P(9, 15.5f));
                    s.AddBezier(new BezierSegment { Point1 = P(7, 19), Point2 = P(4.5f, 20.5f), Point3 = P(3, 18.5f) });
                    s.AddBezier(new BezierSegment { Point1 = P(1, 16), Point2 = P(3, 7), Point3 = P(7, 7) });
                    s.EndFigure(FigureEnd.Closed);
                })) Fill(dc, g, color);
                var hole = new Color4(0, 0, 0, 0.45f);
                Icone.Retangulo(dc, P(5.4f, 0).X, P(0, 10.6f).Y, 4.6f * u, 1.5f * u, 0.5f * u, hole);
                Icone.Retangulo(dc, P(7, 0).X, P(0, 9).Y, 1.5f * u, 4.6f * u, 0.5f * u, hole);
                Icone.Circulo(dc, P(16.4f, 10.4f), 1.1f * u, hole);
                Icone.Circulo(dc, P(18.4f, 12.4f), 1.1f * u, hole);
                break;
            case "cama":
                Icone.Retangulo(dc, P(2.5f, 0).X, P(0, 6).Y, 2.4f * u, 14 * u, 1 * u, color);
                Icone.Retangulo(dc, P(2.5f, 0).X, P(0, 13).Y, 19 * u, 4.4f * u, 1.2f * u, color);
                Icone.Retangulo(dc, P(19.1f, 0).X, P(0, 15).Y, 2.4f * u, 5 * u, 1 * u, color);
                Icone.Retangulo(dc, P(5.6f, 0).X, P(0, 9.6f).Y, 5.4f * u, 3 * u, 1.4f * u, color);
                Icone.Retangulo(dc, P(11.6f, 0).X, P(0, 10.2f).Y, 9.8f * u, 3.4f * u, 1.4f * u, new Color4(color.R, color.G, color.B, 0.7f));
                break;
            case "livro":
                foreach (float side in new[] { -1f, 1f })
                    using (var g = Path(f, s =>
                    {
                        s.BeginFigure(P(12 + side * 0.8f, 6.4f), FigureBegin.Filled);
                        s.AddBezier(new BezierSegment { Point1 = P(12 + side * 4, 4.6f), Point2 = P(12 + side * 7.4f, 4.8f), Point3 = P(12 + side * 9.6f, 5.6f) });
                        s.AddLine(P(12 + side * 9.6f, 18.6f));
                        s.AddBezier(new BezierSegment { Point1 = P(12 + side * 7.4f, 17.8f), Point2 = P(12 + side * 4, 17.8f), Point3 = P(12 + side * 0.8f, 19.4f) });
                        s.EndFigure(FigureEnd.Closed);
                    })) Fill(dc, g, side < 0 ? color : new Color4(color.R * 0.85f, color.G * 0.85f, color.B * 0.85f, 1));
                break;
            case "coracao":
                using (var g = Path(f, s =>
                {
                    s.BeginFigure(P(12, 20), FigureBegin.Filled);
                    s.AddBezier(new BezierSegment { Point1 = P(4, 14.5f), Point2 = P(1.6f, 9.6f), Point3 = P(4.4f, 6.4f) });
                    s.AddBezier(new BezierSegment { Point1 = P(7, 3.6f), Point2 = P(10.8f, 4.8f), Point3 = P(12, 7.8f) });
                    s.AddBezier(new BezierSegment { Point1 = P(13.2f, 4.8f), Point2 = P(17, 3.6f), Point3 = P(19.6f, 6.4f) });
                    s.AddBezier(new BezierSegment { Point1 = P(22.4f, 9.6f), Point2 = P(20, 14.5f), Point3 = P(12, 20) });
                    s.EndFigure(FigureEnd.Closed);
                })) Fill(dc, g, color);
                break;
            case "fone":
                using (var g = Path(f, s =>
                {
                    s.BeginFigure(P(4.6f, 14), FigureBegin.Hollow);
                    s.AddArc(new ArcSegment { Point = P(19.4f, 14), Size = new Vortice.Mathematics.Size(7.4f * u, 7.6f * u), SweepDirection = SweepDirection.Clockwise, ArcSize = ArcSize.Small });
                    s.EndFigure(FigureEnd.Open);
                }))
                using (var b = dc.CreateSolidColorBrush(color))
                    dc.DrawGeometry(g, b, 2 * u);
                Icone.Retangulo(dc, P(3, 0).X, P(0, 13).Y, 4.4f * u, 7 * u, 1.8f * u, color);
                Icone.Retangulo(dc, P(16.6f, 0).X, P(0, 13).Y, 4.4f * u, 7 * u, 1.8f * u, color);
                break;
            case "codigo":
                foreach (var (a, b) in new[] { (P(8.6f, 7), P(3.6f, 12)), (P(3.6f, 12), P(8.6f, 17)), (P(15.4f, 7), P(20.4f, 12)), (P(20.4f, 12), P(15.4f, 17)), (P(13.6f, 5.6f), P(10.4f, 18.4f)) })
                    Icone.Linha(dc, a, b, 2.2f * u, color, f);
                break;
            default: // lua: a crescent
                using (var g = Path(f, s =>
                {
                    s.BeginFigure(P(15.2f, 3.6f), FigureBegin.Filled);
                    s.AddArc(new ArcSegment { Point = P(15.2f, 20.4f), Size = new Vortice.Mathematics.Size(8.6f * u, 8.6f * u), SweepDirection = SweepDirection.CounterClockwise, ArcSize = ArcSize.Large });
                    s.AddArc(new ArcSegment { Point = P(15.2f, 3.6f), Size = new Vortice.Mathematics.Size(10.4f * u, 10.4f * u), SweepDirection = SweepDirection.Clockwise, ArcSize = ArcSize.Small });
                    s.EndFigure(FigureEnd.Closed);
                })) Fill(dc, g, color);
                break;
        }
    }

    /// <summary>
    /// The Do Not Disturb control (his request: a bell with a red slash): a white bell, and over it a red line with a
    /// thin dark edge so it reads on any ball.
    /// </summary>
    public static void BellOff(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r, Color4 bell)
    {
        float u = r.Width / 24f;
        global::Ilha.Volume.Desenhos.Sino(dc, f, new Vector2(r.X, r.Y), u, bell);
        var a = new Vector2(r.X + 4 * u, r.Y + 3.4f * u);
        var b = new Vector2(r.X + 20 * u, r.Y + 21 * u);
        Icone.Linha(dc, a, b, 3.6f * u, new Color4(0.08f, 0.08f, 0.09f, 1), f);
        Icone.Linha(dc, a, b, 2f * u, new Color4(1f, 0.25f, 0.25f, 1), f);
    }
}
