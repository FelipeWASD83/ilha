using System;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Keyboard;

/// <summary>
/// The keyboard card from the menu (4.2.0): the profile in use, the live Snap Tap with its key pairs and a button to
/// turn it on or off, and the profiles Synapse has (the one in use marked). Switching profiles stays with Synapse and
/// the keyboard's own shortcut: the card says so.
/// </summary>
internal sealed class KeyboardActivity : Atividade
{
    private const float L = 352;
    private static readonly Color4 On = new(0.32f, 0.86f, 0.5f, 1), Off = new(1, 1, 1, 0.45f), Razer = new(0.27f, 0.84f, 0.17f, 1);

    private readonly Servicos S;
    private readonly KeyboardModule _m;

    public override string Tipo => "teclado";
    public override string Nome => Lang.T("Teclado", "Keyboard");
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    public KeyboardActivity(Servicos s, KeyboardModule m)
    {
        S = s;
        _m = m;
    }

    public void Open()
    {
        MudouTamanho();
        S.Ilha.Expandir(this);
        _m.ReadSnap(notify: false);
    }

    public void Changed()
    {
        if (S.Ilha.Expandida == this) MudouTamanho();
    }

    /// <summary>A small keyboard: the outline and three rows of keys.</summary>
    public static void Glyph(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r, Color4 color)
    {
        float w = r.Width, h = r.Height * 0.62f, x = r.X, y = r.Y + (r.Height - h) / 2;
        using var b = dc.CreateSolidColorBrush(color);
        dc.DrawRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x + 1, y + 1, w - 2, h - 2), h * 0.18f, h * 0.18f), b, MathF.Max(1.4f, w * 0.07f));
        float k = w * 0.1f, gap = (w - 2 - 6 * k) / 7;
        for (int row = 0; row < 2; row++)
            for (int i = 0; i < 6; i++)
                Icone.Retangulo(dc, x + 1 + gap + i * (k + gap), y + h * (0.24f + row * 0.26f), k, k * 0.8f, k * 0.2f, color);
        Icone.Retangulo(dc, x + w * 0.28f, y + h * 0.74f, w * 0.44f, k * 0.8f, k * 0.2f, color);
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        var prof = _m.Profiles;
        var snap = _m.Snap;
        int listed = prof?.Profiles.Count ?? 0;
        float listH = listed > 0 ? 30 + ((listed + 1) / 2) * 26 : 0;
        float h = 34 + 62 + 92 + listH + 22;
        var c = p.NovaCena(new Vector2(L, h));
        bool busy = _m.Busy;
        string? problem = _m.Problem;
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var fab = p.G.Fabrica;
            Glyph(dc, fab, new System.Drawing.RectangleF(0, 2, 26, 22), Razer);
            p.Texto.Desenhar(dc, "Huntsman V3 HE", new Vector2(34, 2), 15, Cor.Branco, FontWeight.SemiBold);
            // the profile in use
            p.Texto.Desenhar(dc, Lang.T("Perfil", "Profile"), new Vector2(0, 38), 11.5f, Cor.Secundario, FontWeight.Normal);
            p.Texto.Desenhar(dc, prof?.ActiveName ?? Lang.T("Abra o Synapse para ver o perfil", "Open Synapse to see the profile"),
                new Vector2(0, 54), prof?.ActiveName != null ? 20 : 13, prof?.ActiveName != null ? Cor.Branco : Cor.Fraco,
                prof?.ActiveName != null ? FontWeight.Bold : FontWeight.Normal, largura: L);
            // Snap Tap: state, pairs and the button
            float y = 96;
            Icone.Retangulo(dc, 0, y, L, 80, 14, new Color4(1, 1, 1, 0.06f));
            p.Texto.Desenhar(dc, "Snap Tap", new Vector2(14, y + 10), 14, Cor.Branco, FontWeight.SemiBold);
            string state = snap == null ? Lang.T("lendo…", "reading…") : snap.Enabled ? Lang.T("Ligado", "On") : Lang.T("Desligado", "Off");
            var ss = p.Texto.Medir("Snap Tap", 14, FontWeight.SemiBold);
            Icone.Circulo(dc, new Vector2(14 + ss.X + 14, y + 19), 4.5f, snap?.Enabled == true ? On : Off);
            p.Texto.Desenhar(dc, state, new Vector2(14 + ss.X + 24, y + 11), 13, snap?.Enabled == true ? On : Cor.Secundario, FontWeight.SemiBold);
            string pairs = snap == null ? "" : snap.Used.Count == 0 ? Lang.T("sem par de teclas", "no key pair")
                : string.Join("  ·  ", snap.Used.Select(x => RazerProtocol.PairText(x) + ", " + RazerProtocol.ModeName(x.Mode)));
            p.Texto.Desenhar(dc, problem ?? pairs, new Vector2(14, y + 36), 12, problem != null ? Cor.Laranja : Cor.Secundario, FontWeight.Normal, largura: L - 130);
            p.Texto.Desenhar(dc, Lang.T("vale até trocar de perfil", "lasts until the profile changes"), new Vector2(14, y + 56), 10.5f, Cor.Fraco, FontWeight.Normal, largura: L - 130);
            // button
            var br = new System.Drawing.RectangleF(L - 112, y + 22, 98, 36);
            Icone.Retangulo(dc, br.X, br.Y, br.Width, br.Height, 18, snap?.Enabled == true ? new Color4(1, 1, 1, 0.12f) : new Color4(On.R, On.G, On.B, 0.22f));
            string bt = busy ? "…" : snap?.Enabled == true ? Lang.T("Desligar", "Turn off") : Lang.T("Ligar", "Turn on");
            var bs = p.Texto.Medir(bt, 13, FontWeight.SemiBold);
            p.Texto.Desenhar(dc, bt, new Vector2(br.X + (br.Width - bs.X) / 2, br.Y + 9), 13, Cor.Branco, FontWeight.SemiBold);
            // Synapse's profiles, two columns, the one in use marked
            if (listed > 0)
            {
                float ly = y + 96;
                p.Texto.Desenhar(dc, Lang.T("Perfis no Synapse", "Profiles in Synapse"), new Vector2(0, ly), 11.5f, Cor.Secundario, FontWeight.Normal);
                int i = 0;
                foreach (var (guid, name) in prof!.Profiles)
                {
                    float px = i % 2 == 0 ? 0 : L / 2 + 4, py = ly + 22 + (i / 2) * 26;
                    bool active = guid == prof.Active;
                    if (active) Icone.Circulo(dc, new Vector2(px + 5, py + 9), 4, Razer);
                    p.Texto.Desenhar(dc, name, new Vector2(px + 14, py), 13, active ? Cor.Branco : Cor.Secundario, active ? FontWeight.SemiBold : FontWeight.Normal, largura: L / 2 - 20);
                    i++;
                }
            }
            p.Texto.Desenhar(dc, Lang.T("Troque o perfil no Synapse ou pelo atalho do teclado.", "Switch profiles in Synapse or with the keyboard's shortcut."),
                new Vector2(0, h - 18), 10.5f, Cor.Fraco, FontWeight.Normal, largura: L);
        });
        c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(L - 112, 96 + 22, 98, 36), Acao = () => { if (!_m.Busy) _m.Toggle(); } });
        return c;
    }

    /// <summary>The notice for a profile change ("SnapTapLess · Snap Tap desligado") or a Snap Tap change.</summary>
    public static Cena NoticeCard(Palco p, string? profile, SnapTapState? snap)
    {
        string title = profile ?? "Snap Tap";
        string sub = snap == null ? "" : "Snap Tap " + (snap.Enabled ? Lang.T("ligado", "on") : Lang.T("desligado", "off"))
            + (snap.Enabled && snap.Used.Count > 0 ? " · " + RazerProtocol.PairText(snap.Used[0]) : "");
        var ts = p.Texto.Medir(title, 15, FontWeight.Bold);
        var us = p.Texto.Medir(sub, 12.5f, FontWeight.Normal);
        float w = MathF.Min(MathF.Max(ts.X, us.X) + 52, 380);
        var c = p.NovaCena(new Vector2(w, 44));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            Glyph(dc, p.G.Fabrica, new System.Drawing.RectangleF(0, 8, 34, 28), Razer);
            p.Texto.Desenhar(dc, title, new Vector2(46, 2), 15, Cor.Branco, FontWeight.Bold, largura: w - 48);
            p.Texto.Desenhar(dc, sub, new Vector2(46, 23), 12.5f, snap?.Enabled == true ? On : Cor.Secundario, FontWeight.Normal, largura: w - 48);
        });
        return c;
    }

    /// <summary>The CS2 guard's notice: Snap Tap is on in a competitive match (or the island just turned it off).</summary>
    public static Cena GuardCard(Palco p, bool turnedOff)
    {
        string title = turnedOff ? Lang.T("Snap Tap desligado para a competitiva", "Snap Tap off for the competitive match")
            : Lang.T("Snap Tap ligado na competitiva", "Snap Tap is on in competitive");
        string sub = turnedOff ? Lang.T("volta quando a partida acabar", "back on when the match ends")
            : Lang.T("a Valve expulsa da partida: troque de perfil", "Valve kicks you for it: switch profiles");
        var ts = p.Texto.Medir(title, 15, FontWeight.Bold);
        float w = MathF.Min(ts.X + 52, 400);
        var c = p.NovaCena(new Vector2(w, 44));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            Glyph(dc, p.G.Fabrica, new System.Drawing.RectangleF(0, 8, 34, 28), turnedOff ? Razer : Cor.Laranja);
            p.Texto.Desenhar(dc, title, new Vector2(46, 2), 15, Cor.Branco, FontWeight.Bold, largura: w - 48);
            p.Texto.Desenhar(dc, sub, new Vector2(46, 23), 12.5f, turnedOff ? Cor.Secundario : Cor.Laranja, FontWeight.Normal, largura: w - 48);
        });
        return c;
    }
}
