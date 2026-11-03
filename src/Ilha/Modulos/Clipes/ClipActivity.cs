using System;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Clips;

/// <summary>
/// The end-of-match question: on the pill, a film strip and "N clips" while it waits; the card shows each clip's frame
/// and why it was cut (a click opens the video), with Save and Discard.
/// </summary>
internal sealed class ClipActivity : Atividade
{
    private const float L = 340, TW = 164, TH = 92, LC = 120, HC = 24;
    private static readonly Color4 Red = new(1f, 0.3f, 0.32f, 1);

    private readonly Servicos S;
    private readonly ClipModule _m;

    public override string Tipo => "clipes";
    public override string Nome => Lang.T("Clipes", "Clips");
    public override bool PermitidaNoCompetitivo => true;
    public override bool AnunciaChegada => false;

    public ClipActivity(Servicos s, ClipModule m)
    {
        S = s;
        _m = m;
    }

    public void Ask()
    {
        Ligar();
        MudouTamanho();
        S.Ilha.Expandir(this);
    }

    public void Done()
    {
        bool open = S.Ilha.Expandida == this;
        Desligar();
        if (open) S.Ilha.Recolher();
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        int n = _m.PendingClips.Count;
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(24, 24));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) => Film(dc, new System.Drawing.RectangleF(2, 4, 20, 16), Red));
                return c;
            }
            case Forma.Compacta:
            {
                var c = p.NovaCena(new Vector2(LC, HC));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    Film(dc, new System.Drawing.RectangleF(0, 4, 20, 16), Red);
                    string s = n == 1 ? Lang.T("1 clipe", "1 clip") : Lang.T($"{n} clipes", $"{n} clips");
                    var ts = p.Texto.Medir(s, 13, FontWeight.SemiBold);
                    p.Texto.Desenhar(dc, s, new Vector2(t.X - ts.X, (t.Y - ts.Y) / 2), 13, Cor.Branco, FontWeight.SemiBold);
                });
                return c;
            }
            default:
                return Card(p);
        }
    }

    private Cena Card(Palco p)
    {
        var clips = _m.PendingClips.ToList();
        int shown = Math.Min(4, clips.Count);
        int rows = (shown + 1) / 2;
        float h = 30 + rows * (TH + 30) + 50;
        var c = p.NovaCena(new Vector2(L, h));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var fab = p.G.Fabrica;
            string title = clips.Count == 1 ? Lang.T("1 clipe desta partida", "1 clip from this match") : Lang.T($"{clips.Count} clipes desta partida", $"{clips.Count} clips from this match");
            p.Texto.Desenhar(dc, title, Vector2.Zero, 15, Cor.Branco, largura: L);
            for (int i = 0; i < shown; i++)
            {
                float x = (i % 2) * (TW + 12), y = 30 + (i / 2) * (TH + 30);
                ClipStore.Draw(dc, fab, clips[i].Thumb, x, y, TW, TH, 10);
                // a play mark over the frame
                Icone.Circulo(dc, new Vector2(x + TW / 2, y + TH / 2), 15, new Color4(0, 0, 0, 0.5f));
                Icone.Triangulo(dc, fab, new Vector2(x + TW / 2 - 4.5f, y + TH / 2 - 7), new Vector2(x + TW / 2 - 4.5f, y + TH / 2 + 7), new Vector2(x + TW / 2 + 7.5f, y + TH / 2), Cor.Branco);
                p.Texto.Desenhar(dc, clips[i].Reason, new Vector2(x, y + TH + 5), 11.5f, Cor.Secundario, FontWeight.Normal, largura: TW);
            }
            float by = 30 + rows * (TH + 30) + 8;
            Icone.Retangulo(dc, 0, by, (L - 12) / 2, 36, 18, new Color4(0.2f, 0.56f, 1f, 1));
            Icone.Retangulo(dc, (L + 12) / 2, by, (L - 12) / 2, 36, 18, new Color4(1, 1, 1, 0.1f));
            string save = Lang.T("Salvar", "Save"), discard = Lang.T("Descartar", "Discard");
            var ss = p.Texto.Medir(save, 14, FontWeight.SemiBold);
            var ds = p.Texto.Medir(discard, 14, FontWeight.SemiBold);
            p.Texto.Desenhar(dc, save, new Vector2((L - 12) / 4 - ss.X / 2, by + (36 - ss.Y) / 2), 14, Cor.Branco, FontWeight.SemiBold);
            p.Texto.Desenhar(dc, discard, new Vector2((L + 12) / 2 + (L - 12) / 4 - ds.X / 2, by + (36 - ds.Y) / 2), 14, Red, FontWeight.SemiBold);
        });
        for (int i = 0; i < shown; i++)
        {
            var clip = clips[i];
            float x = (i % 2) * (TW + 12), y = 30 + (i / 2) * (TH + 30);
            c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(x, y, TW, TH), Acao = () => ClipStore.Open(clip.Video) });
        }
        float bottom = 30 + rows * (TH + 30) + 8;
        c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(0, bottom, (L - 12) / 2, 36), Acao = () => _m.Decide(keep: true) });
        c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF((L + 12) / 2, bottom, (L - 12) / 2, 36), Acao = () => _m.Decide(keep: false) });
        return c;
    }

    /// <summary>A film strip: a rounded frame with sprocket holes along the top and bottom.</summary>
    public static void Film(ID2D1DeviceContext dc, System.Drawing.RectangleF r, Color4 color)
    {
        Icone.Retangulo(dc, r.X, r.Y, r.Width, r.Height, 3, color);
        var hole = new Color4(0, 0, 0, 0.8f);
        float step = r.Width / 4;
        for (int i = 0; i < 4; i++)
        {
            Icone.Retangulo(dc, r.X + i * step + step * 0.3f, r.Y + 1.5f, step * 0.4f, 2.2f, 0.6f, hole);
            Icone.Retangulo(dc, r.X + i * step + step * 0.3f, r.Bottom - 3.7f, step * 0.4f, 2.2f, 0.6f, hole);
        }
        Icone.Retangulo(dc, r.X + 2, r.Y + 5, r.Width - 4, r.Height - 10, 1, new Color4(0, 0, 0, 0.35f));
    }

    /// <summary>A short notice ("Clip saved"), with the clip's frame when there is one.</summary>
    public static Cena NoticeCard(Palco p, string text, ClipFile? clip)
    {
        var ts = p.Texto.Medir(text, 15, FontWeight.Bold);
        float w = MathF.Min(ts.X + (clip != null ? 112 : 44), 380);
        var c = p.NovaCena(new Vector2(w, 52));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            if (clip != null) ClipStore.Draw(dc, p.G.Fabrica, clip.Thumb, 0, 2, 84, 48, 8);
            else Film(dc, new System.Drawing.RectangleF(4, 16, 26, 20), Red);
            float x = clip != null ? 96 : 40;
            p.Texto.Desenhar(dc, text, new Vector2(x, (t.Y - ts.Y) / 2), 15, Cor.Branco, FontWeight.Bold, largura: w - x);
        });
        if (clip != null) c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(0, 0, w, 52), Acao = () => ClipStore.Open(clip.Video) });
        return c;
    }
}
