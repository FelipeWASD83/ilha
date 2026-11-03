using System;
using System.Linq;
using System.Numerics;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Focus;

/// <summary>
/// The focus on the island: on the pill, only when nothing else is there, the symbol and name of the mode that is on
/// (like the iPhone's moon); opened from the menu, the list of modes, a tap turning one on or off.
/// </summary>
internal sealed class FocusActivity : Atividade
{
    private const float L = 340, ROW = 50, HC = 24;

    private readonly Servicos S;
    private readonly FocoModule _m;
    private string _layout = "";

    public override string Tipo => "foco";
    public override string Nome => Lang.T("Foco", "Focus");
    public override bool EhFerramenta => true;
    public override bool AoFundo => true;
    public override bool AnunciaChegada => false;

    public FocusActivity(Servicos s, FocoModule m)
    {
        S = s;
        _m = m;
    }

    public void Open()
    {
        MudouTamanho();
        S.Ilha.Expandir(this);
    }

    /// <summary>A mode went on or off, or the modes changed.</summary>
    public void Changed()
    {
        if (_m.Active != null && _m.ShowMark) Ligar(); else Desligar();
        string layout = $"{_m.Active?.Id}|{_m.Active?.Name}|{_m.Modes.Count}";
        if (layout != _layout) { _layout = layout; MudouTamanho(); }
        else Redesenhar();
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        var m = _m.Active;
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(24, 24));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    if (m != null) FocusIcons.Draw(dc, p.G.Fabrica, new System.Drawing.RectangleF(2, 2, 20, 20), m.Icone, FocusIcons.ColorOf(m.Cor));
                });
                return c;
            }
            case Forma.Compacta:
            {
                string name = m?.Name ?? "";
                var ts = p.Texto.Medir(name, 13, FontWeight.SemiBold);
                var c = p.NovaCena(new Vector2(MathF.Min(26 + ts.X, 200), HC));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    if (m == null) return;
                    var color = FocusIcons.ColorOf(m.Cor);
                    FocusIcons.Draw(dc, p.G.Fabrica, new System.Drawing.RectangleF(0, 2, 20, 20), m.Icone, color);
                    p.Texto.Desenhar(dc, name, new Vector2(26, (t.Y - ts.Y) / 2), 13, color, FontWeight.SemiBold, largura: t.X - 26);
                });
                return c;
            }
            default:
                return Card(p);
        }
    }

    /// <summary>When a mode turns on by itself, or what it changes, in a short line under its name.</summary>
    internal static string Describe(FocusMode m, bool on)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (m.Horarios.FirstOrDefault(h => Schedule.Parse(h) != null) is { } h) parts.Add(h.Trim());
        if (m.Apps.Count > 0) parts.Add(Lang.T("com ", "with ") + string.Join(", ", m.Apps.Select(FocusRules.Normalize)));
        if (parts.Count == 0)
        {
            if (m.Silenciar) parts.Add(Lang.T("sem sons", "no sounds"));
            if (m.NaoPerturbeWindows) parts.Add(Lang.T("Não Perturbe do Windows", "Windows Do Not Disturb"));
            parts.Add(FocusAlerts.Normalize(m.Avisos) switch
            {
                FocusAlerts.None => Lang.T("nenhum aviso", "no alerts"),
                FocusAlerts.Important => Lang.T("só avisos importantes", "important alerts only"),
                _ => Lang.T("todos os avisos", "all alerts"),
            });
        }
        return (on ? Lang.T("Ligado · ", "On · ") : "") + string.Join(" · ", parts);
    }

    private Cena Card(Palco p)
    {
        var modes = _m.Modes.ToList();
        var active = _m.Active;
        float h = 30 + modes.Count * ROW + 22;
        var c = p.NovaCena(new Vector2(L, h));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var fab = p.G.Fabrica;
            p.Texto.Desenhar(dc, Lang.T("Foco", "Focus"), Vector2.Zero, 15, Cor.Branco);
            float y = 30;
            foreach (var m in modes)
            {
                bool on = active?.Id == m.Id;
                var color = FocusIcons.ColorOf(m.Cor);
                Icone.Retangulo(dc, 0, y, L, ROW - 6, 14, on ? new Color4(color.R, color.G, color.B, 0.22f) : new Color4(1, 1, 1, 0.06f));
                var center = new Vector2(24, y + (ROW - 6) / 2);
                Icone.Circulo(dc, center, 15, on ? color : new Color4(color.R, color.G, color.B, 0.2f));
                FocusIcons.Draw(dc, fab, new System.Drawing.RectangleF(center.X - 10, center.Y - 10, 20, 20), m.Icone, on ? Cor.Branco : color);
                p.Texto.Desenhar(dc, m.Name, new Vector2(48, y + 5), 14, Cor.Branco, largura: L - 60);
                p.Texto.Desenhar(dc, Describe(m, on), new Vector2(48, y + 24), 11, on ? color : Cor.Secundario, FontWeight.Normal, largura: L - 60);
                y += ROW;
            }
            p.Texto.Desenhar(dc, Lang.T("Os modos se editam nas configurações (Foco).", "Modes are edited in the settings (Focus)."),
                new Vector2(0, y + 2), 11, Cor.Fraco, FontWeight.Normal, largura: L);
        });
        float yy = 30;
        foreach (var m in modes)
        {
            var mode = m;
            c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(0, yy, L, ROW - 6), Acao = () => _m.Toggle(mode) });
            yy += ROW;
        }
        return c;
    }

    /// <summary>The notice when a mode turns on or off: its symbol in its color, the name and "On"/"Off".</summary>
    public static Cena NoticeCard(Palco p, FocusMode m, bool on)
    {
        var color = FocusIcons.ColorOf(m.Cor);
        string title = m.Name;
        string sub = on ? Lang.T("Ligado", "On") : Lang.T("Desligado", "Off");
        var ts = p.Texto.Medir(title, 16, FontWeight.Bold);
        float w = MathF.Min(MathF.Max(ts.X, 90) + 70, 360);
        var c = p.NovaCena(new Vector2(w, 50));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var center = new Vector2(22, t.Y / 2);
            Icone.Circulo(dc, center, 19, on ? color : new Color4(color.R, color.G, color.B, 0.22f));
            FocusIcons.Draw(dc, p.G.Fabrica, new System.Drawing.RectangleF(center.X - 12, center.Y - 12, 24, 24), m.Icone, on ? Cor.Branco : color);
            p.Texto.Desenhar(dc, title, new Vector2(54, 5), 16, Cor.Branco, FontWeight.Bold, largura: w - 56);
            p.Texto.Desenhar(dc, sub, new Vector2(54, 27), 12.5f, on ? color : Cor.Secundario, FontWeight.Normal, largura: w - 56);
        });
        return c;
    }
}
