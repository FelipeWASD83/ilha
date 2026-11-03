using System;
using System.Numerics;
using Ilha.Games;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Windows.UI.Composition;

namespace Ilha.Extras;

/// <summary>
/// The card for a device that just started charging (3.5.0, his request): its photo on the left, the name and "Charging"
/// sliding in, and on the right a battery that fills up to the real level while a lightning bolt pops over it and pulses.
/// Drawn once and moved only in the compositor, like the game celebrations.
/// </summary>
internal static class ChargingCard
{
    public const float W = 380, H = 60;

    /// <summary>
    /// A new charging reading of one battery: true only when it goes from not charging to charging. The first reading of
    /// a battery never counts (opening the island with the headset on the cable is not news).
    /// </summary>
    public static bool Started(System.Collections.Generic.Dictionary<string, bool> seen, string key, bool charging)
    {
        bool known = seen.TryGetValue(key, out bool was);
        seen[key] = charging;
        return known && !was && charging;
    }
    private static readonly Color4 Green = new(0.3f, 0.86f, 0.42f, 1);

    /// <param name="image">Draws the device photo (or its icon) into a box of the card's height.</param>
    /// <param name="percent">The level now, when the device says it (null fills the battery all the way).</param>
    public static Cena Create(Palco p, float imageBox, Action<ID2D1DeviceContext, Vector2> image, string name, int? percent, string? part)
    {
        var c = p.NovaCena(new Vector2(W, H));
        var cc = p.C;
        var f = p.G.Fabrica;
        const float BW = 40, BH = 20, BX = W - 58, BY = (H - BH) / 2;
        float level = Math.Clamp((percent ?? 100) / 100f, 0.06f, 1f);

        var photo = Celebrations.Layer(p, c, Vector2.Zero, new Vector2(imageBox + 8, H), (dc, s) => image(dc, s));
        string sub = (part == null ? "" : part + " · ") + Lang.T("Carregando", "Charging") + (percent is int n ? $" · {n}%" : "");
        float textX = imageBox + 12, textW = BX - textX - 10;
        var text = Celebrations.Layer(p, c, new Vector2(textX, 8), new Vector2(textW, H - 12), (dc, _) =>
        {
            p.Texto.Desenhar(dc, name, Vector2.Zero, 16, Cor.Branco, largura: textW);
            p.Texto.Desenhar(dc, sub, new Vector2(0, 24), 12.5f, Green, FontWeight.Normal, largura: textW);
        });

        // the battery: its outline and nub, the fill growing from the left, and the bolt over it
        Celebrations.Layer(p, c, new Vector2(BX - 2, BY - 2), new Vector2(BW + 8, BH + 4), (dc, _) =>
        {
            using var b = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 0.55f));
            dc.DrawRoundedRectangle(new RoundedRectangle(new Vortice.RawRectF(2.8f, 2.8f, BW + 1.2f, BH + 1.2f), 5, 5), b, 1.6f);
            Icone.Retangulo(dc, BW + 2.6f, 2 + BH / 2 - 3.5f, 2.6f, 7, 1.2f, new Color4(1, 1, 1, 0.55f));
        });
        var fill = Celebrations.Layer(p, c, new Vector2(BX + 2.5f, BY + 2.5f), new Vector2(BW - 5, BH - 5), (dc, s) =>
            Icone.Retangulo(dc, 0, 0, s.X, s.Y, 3, Green));
        fill.CenterPoint = new Vector3(0, fill.Size.Y / 2, 0);
        var glow = Celebrations.Layer(p, c, new Vector2(BX - 10, BY - 10), new Vector2(BW + 20, BH + 20), (dc, s) =>
        {
            using var stops = dc.CreateGradientStopCollection(new[]
            {
                new GradientStop { Position = 0, Color = new Color4(Green.R, Green.G, Green.B, 0.45f) },
                new GradientStop { Position = 1, Color = new Color4(Green.R, Green.G, Green.B, 0) },
            });
            using var r = dc.CreateRadialGradientBrush(new RadialGradientBrushProperties { Center = s / 2, RadiusX = s.X / 2, RadiusY = s.Y / 2 }, stops);
            dc.FillEllipse(new Ellipse(s / 2, s.X / 2, s.Y / 2), r);
        });
        var bolt = Celebrations.Layer(p, c, new Vector2(BX + BW / 2 - 9, BY + BH / 2 - 11), new Vector2(18, 22), (dc, s) =>
        {
            Bolt(dc, f, s / 2 + new Vector2(0.6f, 0.8f), 9.5f, new Color4(0, 0, 0, 0.5f));
            Bolt(dc, f, s / 2, 9.5f, new Color4(1, 1, 1, 1));
        });

        Celebrations.Fade(cc, photo, 0, 200);
        Celebrations.Scale(cc, photo, 0, 420, new[] { (0f, 0.7f), (1f, 1f) });
        Celebrations.Slide(cc, text, 120, 360, new Vector2(14, 0));
        Celebrations.Fade(cc, text, 120, 220);
        // the fill runs up from empty, settles at the level and breathes once more
        Celebrations.Scale3(cc, fill, 200, 1100, new[] { (0f, new Vector2(0.02f, 1)), (0.85f, new Vector2(level, 1)), (1f, new Vector2(level, 1)) });
        Celebrations.Scale(cc, bolt, 650, 520, new[] { (0f, 0.1f), (0.6f, 1.35f), (0.8f, 0.92f), (1f, 1f) });
        Celebrations.Fade(cc, bolt, 650, 120);
        Celebrations.Opacity(cc, glow, 650, 1600, new[] { (0f, 0f), (0.2f, 1f), (0.55f, 0.35f), (0.8f, 0.9f), (1f, 0.3f) });
        return c;
    }

    /// <summary>A lightning bolt (also the charging mark elsewhere could use it).</summary>
    public static void Bolt(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float r, Color4 color)
    {
        using var g = f.CreatePathGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(c + new Vector2(0.18f, -1) * r, FigureBegin.Filled);
            s.AddLine(c + new Vector2(-0.62f, 0.14f) * r);
            s.AddLine(c + new Vector2(-0.04f, 0.14f) * r);
            s.AddLine(c + new Vector2(-0.2f, 1) * r);
            s.AddLine(c + new Vector2(0.62f, -0.18f) * r);
            s.AddLine(c + new Vector2(0.04f, -0.18f) * r);
            s.EndFigure(FigureEnd.Closed);
            s.Close();
        }
        using var b = dc.CreateSolidColorBrush(color);
        dc.FillGeometry(g, b);
    }
}
