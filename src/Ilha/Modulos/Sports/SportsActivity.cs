using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ilha.Games;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Sports;

/// <summary>Drawing what the sports pieces share: a crest (or a dot until it downloads) and an event's status.</summary>
internal static class SportDraw
{
    public static void Crest(ID2D1DeviceContext dc, Competitor? c, Vector2 center, float side, Action arrived)
    {
        if (c != null && Crests.Get(c.Logo, arrived) is { } px) GameArt.Draw(dc, px, center.X - side / 2, center.Y - side / 2, side);
        else Icone.Circulo(dc, center, side * 0.22f, Cor.Fraco);
    }

    /// <summary>"67'", "Intervalo", "Encerrado", "21:30" or "qua 21:30".</summary>
    public static string Status(SportEvent e)
    {
        if (e.State == EventState.Live) return e.Detail.Length > 0 ? e.Detail : e.Clock;
        if (e.State == EventState.Post) return e.League.Racing ? Lang.T("Final", "Final") : Lang.T("Encerrado", "Full time");
        var today = DateTime.Today;
        return e.Start.Date == today ? e.Start.ToString("HH:mm", Lang.Culture)
            : e.Start.Date == today.AddDays(1) ? Lang.T("amanhã ", "tomorrow ") + e.Start.ToString("HH:mm", Lang.Culture)
            : e.Start.ToString("ddd HH:mm", Lang.Culture);
    }

    public static string Session(SportEvent e) => e.Session switch
    {
        "FP1" => Lang.T("Treino 1", "Practice 1"),
        "FP2" => Lang.T("Treino 2", "Practice 2"),
        "FP3" => Lang.T("Treino 3", "Practice 3"),
        "Qual" => Lang.T("Classificação", "Qualifying"),
        "Sprint" or "SR" => "Sprint",
        "Race" => Lang.T("Corrida", "Race"),
        _ => e.Session,
    };
}

/// <summary>
/// The followed game on the pill: home crest, score, away crest and the minute. The card adds the league, the names
/// and the status. For a race: the session, the leader and the followed driver's place.
/// </summary>
internal sealed class SportsActivity : Atividade
{
    private const float LC = 214, HC = 24, L = 320;
    private readonly Servicos S;
    private SportEvent? _e;
    private Func<IEnumerable<string>> _drivers;
    private string _layout = "";

    public override string Tipo => "esporte";
    public override string Nome => Lang.T("Esporte ao vivo", "Live sport");
    public override bool AnunciaChegada => true;
    public override float? LarguraCompactaMinima => LC;

    public SportEvent? Current => _e;

    public SportsActivity(Servicos s, Func<IEnumerable<string>> drivers)
    {
        S = s;
        _drivers = drivers;
    }

    public void Show(SportEvent? e)
    {
        _e = e;
        if (e == null) { Desligar(); return; }
        string layout = $"{e.Id}|{e.League.Racing}";
        Ligar();
        if (layout != _layout) { _layout = layout; MudouTamanho(); } else Redesenhar();
    }

    public void CrestArrived() => S.NaInterface(Redesenhar);

    protected override Cena Montar(Palco p, Forma f)
    {
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(24, 24));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    if (_e is not { } e) return;
                    if (e.League.Racing) { SportDraw.Crest(dc, null, t / 2, 20, CrestArrived); return; }
                    string s = $"{e.Home?.Score ?? 0}:{e.Away?.Score ?? 0}";
                    var sz = p.Texto.Medir(s, 10, FontWeight.Bold, tabular: true);
                    p.Texto.Desenhar(dc, s, (t - sz) / 2, 10, Cor.Branco, FontWeight.Bold, tabular: true);
                });
                return c;
            }
            case Forma.Compacta:
            {
                var c = p.NovaCena(new Vector2(LC, HC));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) => { if (_e is { } e) Compact(dc, p, e, t); });
                return c;
            }
            default:
                return Card(p);
        }
    }

    private void Compact(ID2D1DeviceContext dc, Palco p, SportEvent e, Vector2 t)
    {
        string status = SportDraw.Status(e);
        var ss = p.Texto.Medir(status, 13, FontWeight.SemiBold, tabular: true);
        p.Texto.Desenhar(dc, status, new Vector2(t.X - MathF.Min(ss.X, 70), (t.Y - ss.Y) / 2), 13, Cor.Secundario, FontWeight.SemiBold, tabular: true, largura: 70);
        if (e.League.Racing)
        {
            string line = Race(e);
            p.Texto.Desenhar(dc, line, new Vector2(0, (t.Y - 17) / 2), 13, Cor.Branco, FontWeight.SemiBold, largura: t.X - 78);
            return;
        }
        float mid = 92;
        string a = e.Home?.Score?.ToString() ?? "–", b = e.Away?.Score?.ToString() ?? "–";
        var sa = p.Texto.Medir(a, 16, FontWeight.Bold, tabular: true);
        float y = (t.Y - sa.Y) / 2;
        p.Texto.Desenhar(dc, a, new Vector2(mid - 7 - sa.X, y), 16, Cor.Branco, FontWeight.Bold, tabular: true);
        p.Texto.Desenhar(dc, b, new Vector2(mid + 7, y), 16, Cor.Branco, FontWeight.Bold, tabular: true);
        var col = p.Texto.Medir(":", 15, FontWeight.Bold);
        p.Texto.Desenhar(dc, ":", new Vector2(mid - col.X / 2, y - 1), 15, Cor.Secundario, FontWeight.Bold);
        var sb = p.Texto.Medir(b, 16, FontWeight.Bold, tabular: true);
        SportDraw.Crest(dc, e.Home, new Vector2(mid - 7 - sa.X - 15, t.Y / 2), 20, CrestArrived);
        SportDraw.Crest(dc, e.Away, new Vector2(mid + 7 + sb.X + 15, t.Y / 2), 20, CrestArrived);
    }

    /// <summary>"P1 Leclerc · P4 Hamilton" (the leader, then each followed driver that is not leading).</summary>
    private string Race(SportEvent e)
    {
        var parts = new List<string>();
        if (e.Competitors.FirstOrDefault() is { } lead) parts.Add("P1 " + Surname(lead.Name));
        var drivers = _drivers().ToList();
        for (int i = 1; i < e.Competitors.Count; i++)
            if (Espn.Follows(e.Competitors[i], drivers)) parts.Add($"P{i + 1} {Surname(e.Competitors[i].Name)}");
        return string.Join(" · ", parts);
    }

    private static string Surname(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? name;

    private Cena Card(Palco p)
    {
        var e = _e;
        bool racing = e?.League.Racing == true;
        var c = p.NovaCena(new Vector2(L, racing ? 150 : 132));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            if (_e is not { } x) return;
            string head = x.League.Name + (racing ? " · " + SportDraw.Session(x) : "");
            p.Texto.Desenhar(dc, head, new Vector2(0, 1), 13, Cor.Secundario, largura: L - 90);
            string st = SportDraw.Status(x);
            var ss = p.Texto.Medir(st, 13, FontWeight.SemiBold, tabular: true);
            p.Texto.Desenhar(dc, st, new Vector2(L - MathF.Min(ss.X, 90), 1), 13, x.State == EventState.Live ? Cores.Live : Cor.Secundario, FontWeight.SemiBold, tabular: true, largura: 90);
            if (racing)
            {
                p.Texto.Desenhar(dc, x.Name, new Vector2(0, 22), 16, Cor.Branco, largura: L);
                var drivers = _drivers().ToList();
                for (int i = 0; i < Math.Min(5, x.Competitors.Count); i++)
                {
                    var d = x.Competitors[i];
                    bool mine = Espn.Follows(d, drivers);
                    p.Texto.Desenhar(dc, $"{i + 1}", new Vector2(0, 50 + i * 19), 13, Cor.Secundario, FontWeight.SemiBold, tabular: true);
                    p.Texto.Desenhar(dc, d.Name, new Vector2(22, 50 + i * 19), 13, mine ? Cores.Live : Cor.Branco, mine ? FontWeight.Bold : FontWeight.Normal, largura: L - 22);
                }
                return;
            }
            float mid = L / 2, top = 30;
            SportDraw.Crest(dc, x.Home, new Vector2(30, top + 26), 50, CrestArrived);
            SportDraw.Crest(dc, x.Away, new Vector2(L - 30, top + 26), 50, CrestArrived);
            string a = x.Home?.Score?.ToString() ?? "–", b = x.Away?.Score?.ToString() ?? "–";
            var sa = p.Texto.Medir(a, 38, FontWeight.Bold, tabular: true);
            p.Texto.Desenhar(dc, a, new Vector2(mid - 14 - sa.X, top + 2), 38, Cor.Branco, FontWeight.Bold, tabular: true);
            p.Texto.Desenhar(dc, b, new Vector2(mid + 14, top + 2), 38, Cor.Branco, FontWeight.Bold, tabular: true);
            var colon = p.Texto.Medir(":", 32, FontWeight.Bold);
            p.Texto.Desenhar(dc, ":", new Vector2(mid - colon.X / 2, top + 3), 32, Cor.Secundario, FontWeight.Bold);
            Name(dc, p, x.Home?.Short ?? "", new Vector2(0, top + 62), true);
            Name(dc, p, x.Away?.Short ?? "", new Vector2(L, top + 62), false);
        });
        return c;
    }

    private static void Name(ID2D1DeviceContext dc, Palco p, string s, Vector2 at, bool left)
    {
        var m = p.Texto.Medir(s, 13, FontWeight.SemiBold);
        float w = MathF.Min(m.X, 140);
        p.Texto.Desenhar(dc, s, new Vector2(left ? at.X : at.X - w, at.Y), 13, Cor.Branco, FontWeight.SemiBold, largura: 140);
    }
}

internal static class Cores
{
    public static readonly Color4 Live = new(1f, 0.33f, 0.3f, 1f);
}
