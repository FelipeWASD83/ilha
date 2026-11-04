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
    private readonly Func<List<Record>> _recent;
    private Dictionary<int, int> _hours = new();
    private DateTime _hoursRead;

    public override string Tipo => "jogos-menu";
    public override string Nome => Lang.T("Jogos", "Games");
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    public GamesOverview(Servicos s, Func<Match?> live, Action openLive, Func<string, List<Outcome>> last, Func<IEnumerable<string>> played,
        Func<List<Record>> recent)
    {
        S = s;
        _live = live;
        _openLive = openLive;
        _last = last;
        _played = played;
        _recent = recent;
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

    private const float CLIP_W = (L - 3 * 8) / 4, CLIP_H = CLIP_W * 9 / 16, MATCH_ROW = 56;

    /// <summary>
    /// The mode as shown now: Rocket League matches saved before 3.9.0 keep the old names in the history (and "Playlist N"
    /// for events), so they are read through today's names; an event shows no mode (4.0.1).
    /// </summary>
    internal static string ModeName(string game, string saved)
    {
        if (game != "rl") return saved;
        if (System.Text.RegularExpressions.Regex.IsMatch(saved, @"^Playlist \d+$")) return "";
        int? id = saved switch
        {
            "Duelo" or "Duel" => 1, "Duplas" or "Doubles" => 2, "Padrão" or "Standard" => 3, "Caos" or "Chaos" => 4,
            "Duelo ranqueado" or "Ranked Duel" => 10, "Duplas ranqueado" or "Ranked Doubles" => 11, "Padrão ranqueado" or "Ranked Standard" => 13,
            "Snow Day" => 15, "Hoops" => 17, "Rumble" => 18,
            "Hoops ranqueado" or "Ranked Hoops" => 27, "Rumble ranqueado" or "Ranked Rumble" => 28, "Snow Day ranqueado" or "Ranked Snow Day" => 30,
            _ => null,
        };
        return id is int i ? RocketLeagueState.PlaylistName(i) : saved;
    }

    /// <summary>
    /// One finished match: a card tinted green (win) or red (loss), the game's icon, the result and the mode, the score,
    /// kills and deaths, the MVP (a crown in Rocket League, the round MVP stars in CS2) and when.
    /// </summary>
    private static void MatchCard(ID2D1DeviceContext dc, Palco p, Record r, float y)
    {
        var o = r.Read ?? Outcome.Draw;
        var color = o == Outcome.Win ? TeamColors.Win : o == Outcome.Loss ? TeamColors.Loss : Cor.Secundario;
        float h = MATCH_ROW - 6;
        Icone.Retangulo(dc, 0, y, L, h, 12, new Color4(color.R, color.G, color.B, 0.16f));
        Icone.Retangulo(dc, 0, y, 5, h, 2.5f, color);
        Marks.Game(dc, p.G.Fabrica, r.Game, new Vector2(26, y + h / 2), 26, Cor.Secundario);
        string result = o == Outcome.Win ? Lang.T("Vitória", "Win") : o == Outcome.Loss ? Lang.T("Derrota", "Loss") : Lang.T("Empate", "Draw");
        string mode = ModeName(r.Game, r.Mode) is { Length: > 0 } m ? m : Match.NameOf(r.Game);
        p.Texto.Desenhar(dc, result, new Vector2(46, y + 6), 14, color, FontWeight.Bold);
        var rs = p.Texto.Medir(result, 14, FontWeight.Bold);
        p.Texto.Desenhar(dc, "· " + mode, new Vector2(46 + rs.X + 6, y + 8), 11.5f, Cor.Secundario, FontWeight.Normal, largura: L - 130 - rs.X);
        string score = r.Us is int a && r.Them is int b ? $"{a} : {b}" : "– : –";
        // no kills and deaths known (Rocket League, matches before 3.8.0): only the score, not a "K / D –" placeholder
        string kd = r.Kills != null || r.Deaths != null ? $"   {r.Kills?.ToString() ?? "–"} K / {r.Deaths?.ToString() ?? "–"} D" : "";
        p.Texto.Desenhar(dc, score + kd, new Vector2(46, y + 26), 12, Cor.Branco, FontWeight.SemiBold, tabular: true, largura: L - 130);
        // the right side: MVP and when
        string when = r.End.Date == DateTime.Today ? r.End.ToString("HH:mm") : r.End.ToString("dd/MM");
        var wsz = p.Texto.Medir(when, 11, FontWeight.Normal);
        p.Texto.Desenhar(dc, when, new Vector2(L - 10 - wsz.X, y + 28), 11, Cor.Fraco, FontWeight.Normal);
        // 4.0.3: the crown's right edge on the time's (it stood 2 px in and hung below the result line), and its middle on
        // the result's letters, like the CS2 stars beside it (1 px in: the jewel on the tip sticks out of the box)
        const float CW = 24, CH = 18;
        if (r.Mvp == true) Celebrations.Crown(dc, p.G.Fabrica, new System.Drawing.RectangleF(L - 11 - CW, y + 7, CW, CH));
        else if (r.Stars is int st && st > 0)
        {
            string s = $"MVP ★{st}";
            var ss = p.Texto.Medir(s, 11.5f, FontWeight.SemiBold);
            p.Texto.Desenhar(dc, s, new Vector2(L - 10 - ss.X, y + 7), 11.5f, new Color4(1f, 0.82f, 0.25f, 1f), FontWeight.SemiBold);
        }
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        var live = _live();
        var games = Games();
        float top = live != null ? 64 : 0;
        // the last saved clips (3.7.0): their frames in a row under the games, a click opens the video
        var clips = Clips.ClipModule.Current is { } cm ? Clips.ClipStore.Recent(cm.SavedFolder, 4) : new List<Clips.ClipFile>();
        float clipsH = clips.Count > 0 ? 30 + CLIP_H + 24 : 0;
        // the last five matches of every game, newest first (3.8.0, his request; kept in ~/.ilha/partidas.json)
        var recent = _recent();
        float recentH = recent.Count > 0 ? 30 + recent.Count * MATCH_ROW : 0;
        var c = p.NovaCena(new Vector2(L, 24 + top + games.Count * ROW + recentH + clipsH));
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
                y += ROW;
            }
            if (recent.Count > 0)
            {
                p.Texto.Desenhar(dc, Lang.T("Últimas partidas", "Last matches"), new Vector2(0, y + 8), 12, Cor.Secundario, FontWeight.Normal);
                y += 30;
                foreach (var r in recent)
                {
                    MatchCard(dc, p, r, y);
                    y += MATCH_ROW;
                }
            }
            if (clips.Count > 0)
            {
                p.Texto.Desenhar(dc, Lang.T("Clipes", "Clips"), new Vector2(0, y + 8), 12, Cor.Secundario, FontWeight.Normal);
                string folder = Lang.T("Abrir pasta", "Open folder");
                var fs = p.Texto.Medir(folder, 12, FontWeight.Normal);
                p.Texto.Desenhar(dc, folder, new Vector2(L - fs.X, y + 8), 12, new Color4(0.35f, 0.65f, 1f, 1), FontWeight.Normal);
                for (int i = 0; i < clips.Count; i++)
                {
                    float x = i * (CLIP_W + 8);
                    Clips.ClipStore.Draw(dc, p.G.Fabrica, clips[i].Thumb, x, y + 30, CLIP_W, CLIP_H, 8);
                    p.Texto.Desenhar(dc, clips[i].When.ToString("dd/MM HH:mm"), new Vector2(x, y + 30 + CLIP_H + 3), 10.5f, Cor.Fraco, FontWeight.Normal, largura: CLIP_W);
                }
            }
        });
        if (live != null) c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(0, 24, L, 54), Acao = _openLive });
        if (clips.Count > 0)
        {
            float cy = 24 + top + games.Count * ROW + recentH;
            c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(L - 90, cy + 4, 90, 22), Acao = () => Clips.ClipStore.ShowInFolder(clips[0].Video) });
            for (int i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(i * (CLIP_W + 8), cy + 30, CLIP_W, CLIP_H), Acao = () => Clips.ClipStore.Open(clip.Video) });
            }
        }
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
