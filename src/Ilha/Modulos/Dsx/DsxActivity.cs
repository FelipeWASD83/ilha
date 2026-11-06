using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Dsx;

/// <summary>
/// The controller card (4.2.0): the DualSense DSX sees, its battery, light bar colors, the mic LED, adaptive trigger
/// presets for both triggers and "Voltar ao perfil do DSX". Each press goes to DSX right away.
/// </summary>
internal sealed class DsxActivity : Atividade
{
    private const float L = 352;
    private static readonly (int R, int G, int B)[] Colors =
    {
        (0, 64, 255), (255, 0, 40), (0, 220, 90), (150, 0, 255), (255, 140, 0), (255, 255, 255), (0, 0, 0),
    };

    private readonly Servicos S;
    private readonly DsxModule _m;

    public override string Tipo => "dsx";
    public override string Nome => Lang.T("Controle", "Controller");
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    public DsxActivity(Servicos s, DsxModule m)
    {
        S = s;
        _m = m;
    }

    public void Open()
    {
        MudouTamanho();
        S.Ilha.Expandir(this);
        _m.Refresh();
    }

    public void Changed()
    {
        if (S.Ilha.Expandida == this) MudouTamanho();
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        var d = _m.Device;
        if (d == null)
        {
            var e = p.NovaCena(new Vector2(L, 52));
            string msg = _m.Problem ?? Lang.T("Nenhum controle no DSX.", "No controller in DSX.");
            p.Desenho(e, Vector2.Zero, e.Tamanho, (dc, t) =>
            {
                p.Texto.Desenhar(dc, Lang.T("Controle", "Controller"), Vector2.Zero, 15, Cor.Branco);
                p.Texto.Desenhar(dc, msg, new Vector2(0, 26), 12.5f, _m.Problem != null ? Cor.Laranja : Cor.Secundario, FontWeight.Normal, largura: L);
            });
            return e;
        }
        var c = p.NovaCena(new Vector2(L, 268));
        var buttons = new List<Botao>();
        float sw = (L - 6 * 10) / 7f;
        for (int i = 0; i < Colors.Length; i++)
        {
            var col = Colors[i];
            buttons.Add(new Botao { Area = new System.Drawing.RectangleF(i * (sw + 10), 70, sw, 30), Acao = () => _m.SetLight(col.R, col.G, col.B) });
        }
        buttons.Add(new Botao { Area = new System.Drawing.RectangleF(0, 128, L, 34), Acao = _m.CycleMic });
        float tw = (L - 4 * 8) / 5f;
        for (int i = 0; i < DsxClient.TriggerPresets.Length; i++)
        {
            int k = i;
            buttons.Add(new Botao { Area = new System.Drawing.RectangleF(i * (tw + 8), 190, tw, 32), Acao = () => _m.SetTriggers(k) });
        }
        buttons.Add(new Botao { Area = new System.Drawing.RectangleF(0, 234, L, 34), Acao = _m.ResetToDsx });
        string? problem = _m.Problem;
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var fab = p.G.Fabrica;
            global::Ilha.Extras.Desenhos.Controle(dc, fab, new Vector2(14, 14), 26, Cor.Branco);
            p.Texto.Desenhar(dc, d.Name, new Vector2(36, 2), 15, Cor.Branco, FontWeight.SemiBold);
            string sub = string.Join(" · ", new[] { d.Link, d.Battery >= 0 ? d.Battery + "%" : "" }.Where(x => x.Length > 0));
            var ns = p.Texto.Medir(d.Name, 15, FontWeight.SemiBold);
            p.Texto.Desenhar(dc, sub, new Vector2(36 + ns.X + 10, 4), 12.5f, Cor.Secundario, FontWeight.Normal);
            if (problem != null) p.Texto.Desenhar(dc, problem, new Vector2(0, 26), 11.5f, Cor.Laranja, FontWeight.Normal, largura: L);
            // light bar
            p.Texto.Desenhar(dc, Lang.T("Luz", "Light bar"), new Vector2(0, 48), 11.5f, Cor.Secundario, FontWeight.Normal);
            for (int i = 0; i < Colors.Length; i++)
            {
                var (r, g, b) = Colors[i];
                float x = i * (sw + 10);
                bool on = _m.Light is { } l && l[0] == r && l[1] == g && l[2] == b;
                Icone.Retangulo(dc, x, 70, sw, 30, 15, r + g + b == 0 ? new Color4(1, 1, 1, 0.1f) : new Color4(r / 255f, g / 255f, b / 255f, 1));
                if (r + g + b == 0) p.Texto.Desenhar(dc, Lang.T("off", "off"), new Vector2(x + sw / 2 - 9, 76), 11, Cor.Secundario, FontWeight.SemiBold);
                if (on) Icone.Circulo(dc, new Vector2(x + sw / 2, 108), 2.5f, Cor.Branco);
            }
            // mic LED
            Icone.Retangulo(dc, 0, 128, L, 34, 17, new Color4(1, 1, 1, 0.07f));
            string mic = _m.MicMode switch { DsxClient.MicOn => Lang.T("ligado", "on"), DsxClient.MicPulse => Lang.T("piscando", "pulsing"), DsxClient.MicOff => Lang.T("desligado", "off"), _ => Lang.T("do perfil", "from the profile") };
            p.Texto.Desenhar(dc, Lang.T("LED do microfone: ", "Mic LED: ") + mic, new Vector2(14, 136), 13, Cor.Branco, FontWeight.Normal, largura: L - 28);
            // triggers
            p.Texto.Desenhar(dc, Lang.T("Gatilhos", "Triggers") + (_m.Trigger is { } tr ? " · " + tr : "") + (d.Triggers ? "" : Lang.T(" (sem gatilho adaptativo)", " (no adaptive triggers)")),
                new Vector2(0, 170), 11.5f, Cor.Secundario, FontWeight.Normal, largura: L);
            for (int i = 0; i < DsxClient.TriggerPresets.Length; i++)
            {
                var pr = DsxClient.TriggerPresets[i];
                string name = Lang.T(pr.Pt, pr.En);
                bool on = _m.Trigger == name;
                float x = i * (tw + 8);
                Icone.Retangulo(dc, x, 190, tw, 32, 16, on ? new Color4(0.25f, 0.5f, 1f, 0.45f) : new Color4(1, 1, 1, 0.08f));
                var s = p.Texto.Medir(name, 11.5f, FontWeight.SemiBold);
                p.Texto.Desenhar(dc, name, new Vector2(x + (tw - s.X) / 2, 198), 11.5f, Cor.Branco, FontWeight.SemiBold);
            }
            // back to DSX
            Icone.Retangulo(dc, 0, 234, L, 34, 17, new Color4(1, 1, 1, 0.07f));
            string back = Lang.T("Voltar ao perfil do DSX", "Back to the DSX profile");
            var bs = p.Texto.Medir(back, 13, FontWeight.SemiBold);
            p.Texto.Desenhar(dc, back, new Vector2((L - bs.X) / 2, 242), 13, Cor.Branco, FontWeight.SemiBold);
        });
        c.Botoes.AddRange(buttons);
        return c;
    }
}
