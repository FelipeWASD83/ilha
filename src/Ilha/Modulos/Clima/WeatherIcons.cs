using System;
using System.Numerics;
using Ilha.Focus;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Ilha.Weather;

/// <summary>The weather groups the island talks about (WMO codes grouped so a small change does not count).</summary>
internal enum Sky { Clear, Cloudy, Fog, Drizzle, Rain, Snow, Storm }

internal static class SkyInfo
{
    /// <summary>Open-Meteo's WMO weather code into a group. 1 (mainly clear) is still clear; 2 and 3 are clouds.</summary>
    public static Sky Of(int code) => code switch
    {
        0 or 1 => Sky.Clear,
        2 or 3 => Sky.Cloudy,
        45 or 48 => Sky.Fog,
        >= 51 and <= 57 => Sky.Drizzle,
        (>= 61 and <= 67) or (>= 80 and <= 82) => Sky.Rain,
        (>= 71 and <= 77) or 85 or 86 => Sky.Snow,
        >= 95 => Sky.Storm,
        _ => Sky.Cloudy,
    };

    public static string Name(Sky s, bool day = true) => s switch
    {
        Sky.Clear => day ? Lang.T("Céu limpo", "Clear") : Lang.T("Noite limpa", "Clear night"),
        Sky.Cloudy => Lang.T("Nublado", "Cloudy"),
        Sky.Fog => Lang.T("Neblina", "Fog"),
        Sky.Drizzle => Lang.T("Garoa", "Drizzle"),
        Sky.Rain => Lang.T("Chuva", "Rain"),
        Sky.Snow => Lang.T("Neve", "Snow"),
        _ => Lang.T("Tempestade", "Thunderstorm"),
    };

    public static Color4 Tint(Sky s, bool day) => s switch
    {
        Sky.Clear => day ? new Color4(1f, 0.8f, 0.25f, 1) : new Color4(0.75f, 0.78f, 1f, 1),
        Sky.Rain or Sky.Drizzle => new Color4(0.4f, 0.7f, 1f, 1),
        Sky.Storm => new Color4(1f, 0.82f, 0.3f, 1),
        Sky.Snow => new Color4(0.85f, 0.93f, 1f, 1),
        _ => new Color4(0.82f, 0.85f, 0.9f, 1),
    };
}

/// <summary>Weather symbols made of shapes, in a 24-unit box (menu ball, pill, card).</summary>
internal static class WeatherIcons
{
    private static readonly Color4 Sun = new(1f, 0.8f, 0.25f, 1), Drop = new(0.4f, 0.7f, 1f, 1), Bolt = new(1f, 0.82f, 0.3f, 1);

    /// <summary>`color` is the cloud's color (white on the island); sun, rain and lightning keep their own.</summary>
    public static void Draw(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r, Sky sky, bool day, Color4 color)
    {
        float u = r.Width / 24f;
        Vector2 P(float x, float y) => new(r.X + x * u, r.Y + y * u);
        switch (sky)
        {
            case Sky.Clear:
                if (day) SunAt(dc, f, P(12, 12), 4.6f * u, u);
                else FocusIcons.Draw(dc, f, r, "lua", new Color4(0.85f, 0.87f, 1f, 1));
                break;
            case Sky.Cloudy:
                // a little sun behind in the day, as weather apps do for "partly cloudy"
                if (day) SunAt(dc, f, P(7.5f, 8), 3.2f * u, u * 0.75f);
                Cloud(dc, P(13, 13.5f), u, color);
                break;
            case Sky.Fog:
                Cloud(dc, P(12, 10), u * 0.85f, color);
                for (int i = 0; i < 3; i++)
                    Icone.Linha(dc, P(4 + i * 1.5f, 16.5f + i * 2.6f), P(20 - (2 - i) * 1.5f, 16.5f + i * 2.6f), 1.6f * u, new Color4(color.R, color.G, color.B, 0.75f), f);
                break;
            case Sky.Drizzle:
            case Sky.Rain:
            {
                Cloud(dc, P(12, 9.5f), u, color);
                int n = sky == Sky.Rain ? 3 : 2;
                for (int i = 0; i < n; i++)
                {
                    float x = sky == Sky.Rain ? 7.5f + i * 4.5f : 9.5f + i * 5;
                    Icone.Linha(dc, P(x + 1, 16.5f), P(x - 0.8f, 21.5f), 1.7f * u, Drop, f);
                }
                break;
            }
            case Sky.Snow:
                Cloud(dc, P(12, 9.5f), u, color);
                foreach (var (x, y) in new[] { (8f, 17.5f), (12f, 20.5f), (16f, 17.5f) }) Icone.Circulo(dc, P(x, y), 1.5f * u, color);
                break;
            default:
                Cloud(dc, P(12, 9.5f), u, color);
                using (var g = f.CreatePathGeometry())
                {
                    using (var s = g.Open())
                    {
                        s.BeginFigure(P(13, 13.5f), FigureBegin.Filled);
                        s.AddLine(P(8.5f, 19));
                        s.AddLine(P(11.8f, 19));
                        s.AddLine(P(10.2f, 23));
                        s.AddLine(P(15.5f, 17));
                        s.AddLine(P(12.3f, 17));
                        s.AddLine(P(14.2f, 13.5f));
                        s.EndFigure(FigureEnd.Closed);
                        s.Close();
                    }
                    using var b = dc.CreateSolidColorBrush(Bolt);
                    dc.FillGeometry(g, b);
                }
                break;
        }
    }

    private static void SunAt(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float radius, float u)
    {
        Icone.Circulo(dc, c, radius, Sun);
        for (int i = 0; i < 8; i++)
        {
            float a = i * MathF.PI / 4;
            var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
            Icone.Linha(dc, c + d * (radius + 2.2f * u), c + d * (radius + 4.4f * u), 1.6f * u, Sun, f);
        }
    }

    /// <summary>A cloud centered on `c`, about 16 units wide: three bumps over a rounded base, all opaque.</summary>
    private static void Cloud(ID2D1DeviceContext dc, Vector2 c, float u, Color4 color)
    {
        Icone.Retangulo(dc, c.X - 8 * u, c.Y - 0.5f * u, 16 * u, 6 * u, 3 * u, color);
        Icone.Circulo(dc, new Vector2(c.X - 4 * u, c.Y + 0.5f * u), 3.6f * u, color);
        Icone.Circulo(dc, new Vector2(c.X + 1 * u, c.Y - 1.8f * u), 4.8f * u, color);
        Icone.Circulo(dc, new Vector2(c.X + 5 * u, c.Y + 1.2f * u), 3.3f * u, color);
    }
}
