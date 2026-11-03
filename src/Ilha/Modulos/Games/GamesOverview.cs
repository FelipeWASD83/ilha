using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Games;

/// <summary>
/// The "Games" card from the menu: the match on now (a click opens its score), then each game with its icon, the hours
/// played (Steam's own count) and the last five results.
/// </summary>
internal sealed class GamesOverview : Atividade
{
    private const float L = 340, ROW = 46;

    private readonly Servicos S;
    private readonly Func<Match?> _live;
    private readonly Action _openLive;
    private readonly Func<string, List<Outcome>> _last;
    private readonly Func<IEnumerable<string>> _played;
    private Dictionary<int, int> _hours = new();
    private DateTime _hoursRead;

    public override string Tipo => "jogos-menu";
    public override string Nome => Lang.T("Jogos", "Games");
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    public GamesOverview(Servicos s, Func<Match?> live, Action openLive, Func<string, List<Outcome>> last, Func<IEnumerable<string>> played)
    {
        S = s;
        _live = live;
        _openLive = openLive;
        _last = last;
        _played = played;
    }

    public void Open()
    {
        // Steam rewrites its file when a game closes: read it again when the card opens, at most once a minute
        if (DateTime.Now - _hoursRead > TimeSpan.FromMinutes(1))
        {
            _hours = Playtime.Steam();
            _hoursRead = DateTime.Now;
        }
        MudouTamanho();
        S.Ilha.Expandir(this);
    }

    /// <summary>The games on the card: the three with a score source, then any other with a finished match.</summary>
    private List<string> Games() => new[] { "cs2", "rl", "bf6" }.Concat(_played()).Distinct().ToList();

    public static string Hours(int minutes) =>
        minutes <= 0 ? "–" : minutes < 60 ? Lang.T($"{minutes} min", $"{minutes} min") : (minutes / 60.0).ToString(minutes < 600 ? "0.0" : "0", Lang.Culture) + " h";

    protected override Cena Montar(Palco p, Forma f)
    {
        var live = _live();
        var games = Games();
        float top = live != null ? 64 : 0;
        var c = p.NovaCena(new Vector2(L, 24 + top + games.Count * ROW));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            p.Texto.Desenhar(dc, Lang.T("Jogos", "Games"), new Vector2(0, 0), 15, Cor.Branco);
            if (live != null)
            {
                Icone.Retangulo(dc, 0, 24, L, 54, 14, new Color4(1, 1, 1, 0.07f));
                Marks.Game(dc, p.G.Fabrica, live.Game, new Vector2(28, 51), 30, Cor.Branco);
                p.Texto.Desenhar(dc, Lang.T("Agora", "Now") + " · " + (live.Idle ? live.GameName : string.Join(" · ", new[] { live.Mode, live.Map }.Where(s => s.Length > 0))),
                    new Vector2(52, 30), 12, Cor.Secundario, FontWeight.Normal, largura: L - 140);
                string score = live.Idle ? live.Stage : $"{live.Us?.ToString() ?? "–"} : {live.Them?.ToString() ?? "–"}";
                p.Texto.Desenhar(dc, score, new Vector2(52, 47), 18, Cor.Branco, FontWeight.Bold, tabular: true, largura: L - 140);
                if (live.ClockAt(DateTime.Now) is double clock)
                {
                    string tx = Match.Time(clock);
                    var sz = p.Texto.Medir(tx, 13, FontWeight.SemiBold, tabular: true);
                    p.Texto.Desenhar(dc, tx, new Vector2(L - 14 - sz.X, 43), 13, Cor.Secundario, FontWeight.SemiBold, tabular: true);
                }
            }
            float y = 24 + top;
            foreach (var g in games)
            {
                Marks.Game(dc, p.G.Fabrica, g, new Vector2(16, y + ROW / 2), 30, Cor.Secundario);
                p.Texto.Desenhar(dc, Match.NameOf(g), new Vector2(40, y + 5), 14, Cor.Branco, largura: 170);
                int min = Playtime.SteamApp(g) is int app && _hours.TryGetValue(app, out var m) ? m : 0;
                p.Texto.Desenhar(dc, min > 0 ? Lang.T("jogadas: ", "played: ") + Hours(min) : Lang.T("horas: sem dado", "hours: no data"),
                    new Vector2(40, y + 24), 11.5f, Cor.Secundario, FontWeight.Normal, largura: 170);
                var last = _last(g);
                for (int i = 0; i < 5; i++)
                {
                    var center = new Vector2(L - 5 * 22 + i * 22 + 9, y + ROW / 2);
                    if (i < last.Count)
                        Icone.Circulo(dc, center, 7.5f, last[i] == Outcome.Win ? TeamColors.Win : last[i] == Outcome.Loss ? TeamColors.Loss : Cor.Fraco);
                    else
                    {
                        using var b = dc.CreateSolidColorBrush(Cor.Fraco);
                        dc.DrawEllipse(new Ellipse(center, 6.5f, 6.5f), b, 1.4f);
                    }
                }
                y += ROW;
            }
        });
        if (live != null) c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(0, 24, L, 54), Acao = _openLive });
        return c;
    }

    /// <summary>
    /// The menu icon (3.5.0, his request for a better one): a light controller with grips, two sticks, the cross and four
    /// colored buttons. <paramref name="color"/> is unused since then (kept for the callers).
    /// </summary>
    public static void Icon(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r, Color4 color)
    {
        var c = new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2 - 0.6f * r.Width / 24f);
        float u = r.Width / 24f;
        using var g = f.CreatePathGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(c + new Vector2(-6, -5.6f) * u, FigureBegin.Filled);
            s.AddBezier(new BezierSegment { Point1 = c + new Vector2(-2, -6.4f) * u, Point2 = c + new Vector2(2, -6.4f) * u, Point3 = c + new Vector2(6, -5.6f) * u });
            s.AddBezier(new BezierSegment { Point1 = c + new Vector2(10.6f, -5.4f) * u, Point2 = c + new Vector2(12.6f, 5) * u, Point3 = c + new Vector2(11.2f, 8.4f) * u });
            s.AddBezier(new BezierSegment { Point1 = c + new Vector2(10, 10.6f) * u, Point2 = c + new Vector2(7, 9.6f) * u, Point3 = c + new Vector2(5.2f, 5.6f) * u });
            s.AddLine(c + new Vector2(-5.2f, 5.6f) * u);
            s.AddBezier(new BezierSegment { Point1 = c + new Vector2(-7, 9.6f) * u, Point2 = c + new Vector2(-10, 10.6f) * u, Point3 = c + new Vector2(-11.2f, 8.4f) * u });
            s.AddBezier(new BezierSegment { Point1 = c + new Vector2(-12.6f, 5) * u, Point2 = c + new Vector2(-10.6f, -5.4f) * u, Point3 = c + new Vector2(-6, -5.6f) * u });
            s.EndFigure(FigureEnd.Closed);
            s.Close();
        }
        using (var stops = dc.CreateGradientStopCollection(new[]
               {
                   new GradientStop { Position = 0, Color = new Color4(0.98f, 0.98f, 0.99f, 1) },
                   new GradientStop { Position = 1, Color = new Color4(0.74f, 0.74f, 0.78f, 1) },
               }))
        using (var body = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = c + new Vector2(0, -6) * u, EndPoint = c + new Vector2(0, 9) * u }, stops))
            dc.FillGeometry(g, body);
        var ink = new Color4(0.14f, 0.14f, 0.16f, 1);
        // the sticks: the left one up, the right one down
        foreach (var at in new[] { new Vector2(-6.2f, -1.4f), new Vector2(3.2f, 2.6f) })
        {
            Icone.Circulo(dc, c + at * u, 2.3f * u, ink);
            Icone.Circulo(dc, c + at * u, 1.4f * u, new Color4(0.36f, 0.36f, 0.4f, 1));
        }
        // the cross, down on the left
        var d = c + new Vector2(-3.2f, 2.6f) * u;
        Icone.Retangulo(dc, d.X - 2.1f * u, d.Y - 0.7f * u, 4.2f * u, 1.4f * u, 0.4f * u, ink);
        Icone.Retangulo(dc, d.X - 0.7f * u, d.Y - 2.1f * u, 1.4f * u, 4.2f * u, 0.4f * u, ink);
        // four buttons in a diamond, up on the right
        var b = c + new Vector2(6.2f, -1.4f) * u;
        Icone.Circulo(dc, b + new Vector2(0, -1.75f) * u, 1f * u, new Color4(0.98f, 0.76f, 0.12f, 1));
        Icone.Circulo(dc, b + new Vector2(1.75f, 0) * u, 1f * u, new Color4(0.9f, 0.2f, 0.22f, 1));
        Icone.Circulo(dc, b + new Vector2(0, 1.75f) * u, 1f * u, new Color4(0.3f, 0.75f, 0.3f, 1));
        Icone.Circulo(dc, b + new Vector2(-1.75f, 0) * u, 1f * u, new Color4(0.2f, 0.5f, 0.92f, 1));
    }
}
