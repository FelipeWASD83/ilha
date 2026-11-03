using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Sports;

/// <summary>
/// The "Sports" card from the menu: what is live now, then today's and the coming games of the followed teams and
/// leagues, then the latest results. One row per game (a race: its session and the leader).
/// </summary>
internal sealed class SportsCard : Atividade
{
    private const float L = 360, ROW = 34, MAX_ROWS = 12;

    private readonly Servicos S;
    private readonly Func<List<(string Title, List<SportEvent> Events)>> _sections;
    private readonly Action _refresh;

    public override string Tipo => "esportes-menu";
    public override string Nome => Lang.T("Esportes", "Sports");
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    public SportsCard(Servicos s, Func<List<(string, List<SportEvent>)>> sections, Action refresh)
    {
        S = s;
        _sections = sections;
        _refresh = refresh;
    }

    public void Open()
    {
        _refresh();
        MudouTamanho();
        S.Ilha.Expandir(this);
    }

    /// <summary>New data arrived: rebuild if open.</summary>
    public void Changed()
    {
        if (TemCenaViva) MudouTamanho();
    }

    private void CrestArrived() => S.NaInterface(Redesenhar);

    protected override Cena Montar(Palco p, Forma f)
    {
        // the sections cut to the rows that fit, keeping each section's title with at least one row
        var rows = new List<(string? Title, SportEvent? E)>();
        foreach (var (title, events) in _sections())
        {
            if (events.Count == 0 || rows.Count + 2 > MAX_ROWS) continue;
            rows.Add((title, null));
            foreach (var e in events)
            {
                if (rows.Count >= MAX_ROWS) break;
                rows.Add((null, e));
            }
        }
        float h = 26 + (rows.Count == 0 ? 40 : rows.Sum(r => r.Title != null ? 22 : ROW));
        var c = p.NovaCena(new Vector2(L, h));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            p.Texto.Desenhar(dc, Lang.T("Esportes", "Sports"), new Vector2(0, 0), 15, Cor.Branco);
            if (rows.Count == 0)
            {
                p.Texto.Desenhar(dc, Lang.T("Nada hoje nas ligas que você segue (configurações > Esportes).", "Nothing today in the leagues you follow (settings > Sports)."),
                    new Vector2(0, 30), 12, Cor.Secundario, FontWeight.Normal, largura: L);
                return;
            }
            float y = 26;
            foreach (var (title, e) in rows)
            {
                if (title != null)
                {
                    p.Texto.Desenhar(dc, title, new Vector2(0, y + 3), 11.5f, Cor.Secundario, FontWeight.SemiBold);
                    y += 22;
                    continue;
                }
                Row(dc, p, e!, y);
                y += ROW;
            }
        });
        return c;
    }

    private void Row(ID2D1DeviceContext dc, Palco p, SportEvent e, float y)
    {
        string status = SportDraw.Status(e);
        var ss = p.Texto.Medir(status, 12, FontWeight.SemiBold, tabular: true);
        p.Texto.Desenhar(dc, status, new Vector2(L - MathF.Min(ss.X, 80), y + 8), 12, e.State == EventState.Live ? Cores.Live : Cor.Secundario,
            FontWeight.SemiBold, tabular: true, largura: 80);
        if (e.League.Racing)
        {
            string lead = e.Competitors.FirstOrDefault()?.Name ?? "";
            p.Texto.Desenhar(dc, $"{e.Name} · {SportDraw.Session(e)}", new Vector2(0, y + 1), 13, Cor.Branco, largura: L - 90);
            if (lead.Length > 0 && e.State != EventState.Pre)
                p.Texto.Desenhar(dc, "P1 " + lead, new Vector2(0, y + 17), 11, Cor.Secundario, FontWeight.Normal, largura: L - 90);
            return;
        }
        // home crest, name, score, name, away crest
        float mid = (L - 80) / 2;
        SportDraw.Crest(dc, e.Home, new Vector2(10, y + ROW / 2), 18, CrestArrived);
        SportDraw.Crest(dc, e.Away, new Vector2(L - 92, y + ROW / 2), 18, CrestArrived);
        string score = e.State == EventState.Pre ? "x" : $"{e.Home?.Score ?? 0} : {e.Away?.Score ?? 0}";
        var sc = p.Texto.Medir(score, 14, FontWeight.Bold, tabular: true);
        p.Texto.Desenhar(dc, score, new Vector2(mid - sc.X / 2, y + 7), 14, e.State == EventState.Pre ? Cor.Secundario : Cor.Branco, FontWeight.Bold, tabular: true);
        float nameW = mid - sc.X / 2 - 30;
        string home = e.Home?.Short ?? "", away = e.Away?.Short ?? "";
        var hs = p.Texto.Medir(home, 12.5f, FontWeight.Normal);
        p.Texto.Desenhar(dc, home, new Vector2(mid - sc.X / 2 - 8 - MathF.Min(hs.X, nameW), y + 8), 12.5f, Cor.Branco, FontWeight.Normal, largura: nameW);
        p.Texto.Desenhar(dc, away, new Vector2(mid + sc.X / 2 + 8, y + 8), 12.5f, Cor.Branco, FontWeight.Normal, largura: nameW);
    }

    /// <summary>The menu icon: the sport that is live, or the next one today (a soccer ball when nothing is on).</summary>
    public static void Icon(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r, string sport) =>
        SportIcons.Draw(dc, f, r, sport);
}
