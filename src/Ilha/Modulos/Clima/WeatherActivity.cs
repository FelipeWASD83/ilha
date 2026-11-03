using System;
using System.Linq;
using System.Numerics;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Weather;

/// <summary>
/// The weather card from the menu (3.9.0): the city, the temperature now with the sky and the feeling, today's high
/// and low, and the next hours (time, symbol, temperature, chance of rain). Never on the pill: a change of sky comes
/// as a notice instead.
/// </summary>
internal sealed class WeatherActivity : Atividade
{
    private const float L = 352, COL = 44;
    private const int HOURS = 8;

    private readonly Servicos S;
    private readonly WeatherModule _m;

    public override string Tipo => "clima";
    public override string Nome => Lang.T("Clima", "Weather");
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    public WeatherActivity(Servicos s, WeatherModule m)
    {
        S = s;
        _m = m;
    }

    public void Open()
    {
        MudouTamanho();
        S.Ilha.Expandir(this);
        _m.RefreshIfOld();
    }

    public void Changed()
    {
        if (S.Ilha.Expandida == this) MudouTamanho();
    }

    internal static string Deg(double t) => Math.Round(t).ToString("0", Lang.Culture) + "°";

    protected override Cena Montar(Palco p, Forma f)
    {
        var w = _m.Now;
        if (w == null)
        {
            var e = p.NovaCena(new Vector2(L, 52));
            string msg = _m.Problem ?? Lang.T("Lendo o clima…", "Reading the weather…");
            p.Desenho(e, Vector2.Zero, e.Tamanho, (dc, t) =>
            {
                p.Texto.Desenhar(dc, Lang.T("Clima", "Weather"), Vector2.Zero, 15, Cor.Branco);
                p.Texto.Desenhar(dc, msg, new Vector2(0, 26), 12.5f, _m.Problem != null ? Cor.Laranja : Cor.Secundario, FontWeight.Normal, largura: L);
            });
            return e;
        }
        var c = p.NovaCena(new Vector2(L, 176));
        var place = _m.PlaceName;
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var fab = p.G.Fabrica;
            p.Texto.Desenhar(dc, place, Vector2.Zero, 15, Cor.Branco, largura: L - 90);
            string read = w.ReadAt.ToString("HH:mm", Lang.Culture);
            var rs = p.Texto.Medir(read, 11.5f, FontWeight.Normal);
            p.Texto.Desenhar(dc, read, new Vector2(L - rs.X, 3), 11.5f, Cor.Fraco, FontWeight.Normal);
            // now: the big symbol, the temperature, the sky and the feeling
            WeatherIcons.Draw(dc, fab, new System.Drawing.RectangleF(0, 28, 52, 52), w.Sky, w.Day, Cor.Branco);
            string temp = Deg(w.Temp);
            var ts = p.Texto.Medir(temp, 38, FontWeight.SemiBold, tabular: true);
            p.Texto.Desenhar(dc, temp, new Vector2(62, 26), 38, Cor.Branco, FontWeight.SemiBold, tabular: true);
            float x = 62 + ts.X + 14;
            p.Texto.Desenhar(dc, SkyInfo.Name(w.Sky, w.Day), new Vector2(x, 34), 14, SkyInfo.Tint(w.Sky, w.Day), largura: L - x);
            var parts = new System.Collections.Generic.List<string>();
            if (w.Feels is double fl) parts.Add(Lang.T("sensação ", "feels ") + Deg(fl));
            if (w.Max is double mx && w.Min is double mn) parts.Add($"↑{Deg(mx)} ↓{Deg(mn)}");
            p.Texto.Desenhar(dc, string.Join(" · ", parts), new Vector2(x, 55), 12, Cor.Secundario, FontWeight.Normal, tabular: true, largura: L - x);
            // the next hours
            Icone.Retangulo(dc, 0, 94, L, 82, 14, new Color4(1, 1, 1, 0.06f));
            float gap = (L - HOURS * COL) / (HOURS + 1);
            int i = 0;
            foreach (var h in w.Hours.Take(HOURS))
            {
                float cx = gap + i * (COL + gap) + COL / 2;
                string hh = h.Time.ToString("HH", Lang.Culture) + "h";
                var hs = p.Texto.Medir(hh, 11, FontWeight.Normal, tabular: true);
                p.Texto.Desenhar(dc, hh, new Vector2(cx - hs.X / 2, 100), 11, Cor.Secundario, FontWeight.Normal, tabular: true);
                WeatherIcons.Draw(dc, fab, new System.Drawing.RectangleF(cx - 12, 117, 24, 24), SkyInfo.Of(h.Code), h.Day, Cor.Branco);
                string ht = Deg(h.Temp);
                var hts = p.Texto.Medir(ht, 13, FontWeight.SemiBold, tabular: true);
                p.Texto.Desenhar(dc, ht, new Vector2(cx - hts.X / 2, 143), 13, Cor.Branco, FontWeight.SemiBold, tabular: true);
                if (h.Rain is int rp && rp >= 20)
                {
                    string rt = rp + "%";
                    var rts = p.Texto.Medir(rt, 10, FontWeight.SemiBold, tabular: true);
                    p.Texto.Desenhar(dc, rt, new Vector2(cx - rts.X / 2, 160), 10, new Color4(0.4f, 0.7f, 1f, 1), FontWeight.SemiBold, tabular: true);
                }
                i++;
            }
        });
        return c;
    }

    /// <summary>The notice when the sky changes: old symbol, arrow, new symbol, "Nublado → Chuva" and the temperature.</summary>
    public static Cena ChangeCard(Palco p, Sky from, Sky to, bool day, double temp)
    {
        string title = $"{SkyInfo.Name(from, day)} → {SkyInfo.Name(to, day)}";
        string sub = Deg(temp) + Lang.T(" agora", " now");
        var ts = p.Texto.Medir(title, 15, FontWeight.Bold);
        float w = MathF.Min(ts.X + 104, 380);
        var c = p.NovaCena(new Vector2(w, 46));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var fab = p.G.Fabrica;
            WeatherIcons.Draw(dc, fab, new System.Drawing.RectangleF(0, 9, 28, 28), from, day, new Color4(1, 1, 1, 0.55f));
            Icone.Linha(dc, new Vector2(32, 23), new Vector2(40, 23), 1.6f, Cor.Secundario, fab);
            Icone.Linha(dc, new Vector2(37, 20), new Vector2(40, 23), 1.6f, Cor.Secundario, fab);
            Icone.Linha(dc, new Vector2(37, 26), new Vector2(40, 23), 1.6f, Cor.Secundario, fab);
            WeatherIcons.Draw(dc, fab, new System.Drawing.RectangleF(44, 5, 36, 36), to, day, Cor.Branco);
            p.Texto.Desenhar(dc, title, new Vector2(90, 3), 15, Cor.Branco, FontWeight.Bold, largura: w - 92);
            p.Texto.Desenhar(dc, sub, new Vector2(90, 24), 12.5f, SkyInfo.Tint(to, day), FontWeight.Normal, largura: w - 92);
        });
        return c;
    }
}
