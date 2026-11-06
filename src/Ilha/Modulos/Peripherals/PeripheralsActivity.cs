using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Peripherals;

/// <summary>
/// The "Periféricos" card (4.3.0): Gear Hub's active profile, its saved profiles as buttons (a press applies it),
/// the devices it sees with status and battery, the Snap Tap state and a button that opens Gear Hub. Also draws the
/// notices on the pill.
/// </summary>
internal sealed class PeripheralsActivity : Atividade
{
    private const float L = 352, HEADER = 38, PROFILE = 54, BTN_H = 32, BTN_GAP = 8, ROW = 26, OPEN_H = 34;
    private const int COLS = 3, MAX_PROFILES = 12, MAX_DEVICES = 6;

    /// <summary>The orange of Gear Hub's icon (#ff8a3d) and the colors of its three knobs.</summary>
    internal static readonly Color4 Orange = new(1f, 0.54f, 0.24f, 1f);
    private static readonly Color4[] Knobs = { new(1f, 0.70f, 0.36f, 1f), new(0.19f, 0.82f, 0.35f, 1f), new(0.48f, 0.72f, 1f, 1f) };
    private static readonly Color4 SnapOn = new(0.32f, 0.86f, 0.5f, 1);

    private readonly Servicos S;
    private readonly PeripheralsModule _m;

    public override string Tipo => "perifericos";
    public override string Nome => Lang.T("Periféricos", "Peripherals");
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    public PeripheralsActivity(Servicos s, PeripheralsModule m)
    {
        S = s;
        _m = m;
    }

    public void Open()
    {
        MudouTamanho();
        S.Ilha.Expandir(this);
    }

    public void Changed()
    {
        if (S.Ilha.Expandida == this) MudouTamanho();
    }

    // ------------------------------------------------------------------ glyphs (shapes only)

    /// <summary>Gear Hub's mark: three slider tracks, each knob at its own height (like its icon).</summary>
    public static void Sliders(ID2D1DeviceContext dc, Vector2 c, float side, Color4 color, bool coloredKnobs)
    {
        float u = side / 24f;
        float[] xs = { -7f, 0f, 7f }, knob = { 1f, -4.5f, 4.5f };
        var dim = new Color4(color.R, color.G, color.B, color.A * 0.32f);
        for (int i = 0; i < 3; i++)
        {
            float x = c.X + xs[i] * u;
            Icone.Retangulo(dc, x - 1.3f * u, c.Y - 10 * u, 2.6f * u, 20 * u, 1.3f * u, dim);
            float ky = c.Y + knob[i] * u;
            Icone.Retangulo(dc, x - 1.3f * u, ky, 2.6f * u, c.Y + 10 * u - ky, 1.3f * u, color);
            Icone.Circulo(dc, new Vector2(x, ky), 3.6f * u, color);
            Icone.Circulo(dc, new Vector2(x, ky), 1.5f * u, coloredKnobs ? Knobs[i] : new Color4(0, 0, 0, 1));
        }
    }

    /// <summary>A memory stick: the board, the chips and the pins.</summary>
    private static void Memory(ID2D1DeviceContext dc, Vector2 c, float side, Color4 color)
    {
        float u = side / 24f;
        Icone.Retangulo(dc, c.X - 11 * u, c.Y - 5 * u, 22 * u, 9 * u, 1.2f * u, color);
        var hole = new Color4(0, 0, 0, 1);
        for (int i = 0; i < 4; i++) Icone.Retangulo(dc, c.X + (-9 + i * 4.8f) * u, c.Y - 3.2f * u, 3.4f * u, 4.2f * u, 0.5f * u, hole);
        for (int i = 0; i < 8; i++) Icone.Retangulo(dc, c.X + (-10 + i * 2.7f) * u, c.Y + 4.6f * u, 1.6f * u, 2.4f * u, 0.3f * u, color);
    }

    /// <summary>A webcam: the round head with its lens on a small stand.</summary>
    private static void Camera(ID2D1DeviceContext dc, Vector2 c, float side, Color4 color)
    {
        float u = side / 24f;
        Icone.Circulo(dc, new Vector2(c.X, c.Y - 2 * u), 8 * u, color);
        Icone.Circulo(dc, new Vector2(c.X, c.Y - 2 * u), 3.6f * u, new Color4(0, 0, 0, 1));
        Icone.Circulo(dc, new Vector2(c.X + 1.1f * u, c.Y - 3.1f * u), 1f * u, color);
        Icone.Retangulo(dc, c.X - 1.3f * u, c.Y + 5.5f * u, 2.6f * u, 4 * u, 0.6f * u, color);
        Icone.Retangulo(dc, c.X - 6 * u, c.Y + 9 * u, 12 * u, 2.2f * u, 1.1f * u, color);
    }

    /// <summary>A light bulb: the glass, the neck and two lines of the base.</summary>
    private static void Bulb(ID2D1DeviceContext dc, Vector2 c, float side, Color4 color)
    {
        float u = side / 24f;
        Icone.Circulo(dc, new Vector2(c.X, c.Y - 3 * u), 7.5f * u, color);
        Icone.Retangulo(dc, c.X - 4 * u, c.Y + 1 * u, 8 * u, 5 * u, 1.5f * u, color);
        Icone.Retangulo(dc, c.X - 4 * u, c.Y + 7 * u, 8 * u, 1.8f * u, 0.9f * u, color);
        Icone.Retangulo(dc, c.X - 3 * u, c.Y + 9.6f * u, 6 * u, 1.8f * u, 0.9f * u, color);
    }

    /// <summary>The icon for a device kind (or Gear Hub's mark for "gear").</summary>
    public static void KindGlyph(ID2D1DeviceContext dc, ID2D1Factory1 f, string kind, Vector2 c, float side, Color4 color)
    {
        switch (kind)
        {
            case "gear": Sliders(dc, c, side, color, coloredKnobs: true); break;
            case "keyboard": global::Ilha.Extras.Desenhos.Teclado(dc, f, c, side, color); break;
            case "mouse": global::Ilha.Extras.Desenhos.Mouse(dc, f, c, side * 0.85f, color); break;
            case "headset": global::Ilha.Extras.Desenhos.Fone(dc, f, c, side, color); break;
            case "controller": global::Ilha.Extras.Desenhos.Controle(dc, f, c, side, color); break;
            case "memory": Memory(dc, c, side, color); break;
            case "camera": Camera(dc, c, side, color); break;
            case "light": Bulb(dc, c, side, color); break;
            default: Sliders(dc, c, side, color, coloredKnobs: false); break;
        }
    }

    // ------------------------------------------------------------------ the card

    private static int ProfileRows(int n) => (Math.Min(n, MAX_PROFILES) + COLS - 1) / COLS;

    protected override Cena Montar(Palco p, Forma f)
    {
        var st = _m.State;
        var saved = st.Saved.Take(MAX_PROFILES).ToList();
        var devices = st.Devices.Take(MAX_DEVICES).ToList();
        int rows = ProfileRows(saved.Count);
        float profilesTop = HEADER + PROFILE;
        float profilesH = saved.Count > 0 ? 22 + rows * BTN_H + (rows - 1) * BTN_GAP + 14 : 0;
        float devicesTop = profilesTop + profilesH;
        float devicesH = devices.Count > 0 ? 22 + devices.Count * ROW + 10 : 0;
        float openTop = devicesTop + devicesH;
        float h = openTop + OPEN_H;
        var c = p.NovaCena(new Vector2(L, h));
        float bw = (L - (COLS - 1) * BTN_GAP) / COLS;
        var chip = new System.Drawing.RectangleF(L - 124, 0, 124, 26);
        string? problem = _m.Problem;

        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var fab = p.G.Fabrica;
            Sliders(dc, new Vector2(12, 13), 24, Orange, coloredKnobs: true);
            p.Texto.Desenhar(dc, "Gear Hub", new Vector2(32, 2), 15, Cor.Branco, FontWeight.SemiBold);
            if (st.Version is { } v)
            {
                var gs = p.Texto.Medir("Gear Hub", 15, FontWeight.SemiBold);
                p.Texto.Desenhar(dc, v, new Vector2(32 + gs.X + 8, 5), 11.5f, Cor.Fraco, FontWeight.Normal);
            }
            if (st.SnapTap is bool on)
            {
                Icone.Retangulo(dc, chip.X, chip.Y, chip.Width, chip.Height, 13, new Color4(1, 1, 1, 0.08f));
                Icone.Circulo(dc, new Vector2(chip.X + 14, chip.Y + 13), 4, on ? SnapOn : new Color4(1, 1, 1, 0.45f));
                p.Texto.Desenhar(dc, GearHubState.SnapText(on), new Vector2(chip.X + 24, chip.Y + 5), 12, on ? SnapOn : Cor.Secundario, FontWeight.SemiBold, largura: chip.Width - 28);
            }

            // the active profile
            p.Texto.Desenhar(dc, Lang.T("Perfil", "Profile"), new Vector2(0, HEADER), 11.5f, Cor.Secundario, FontWeight.Normal);
            p.Texto.Desenhar(dc, st.ProfileDisplay ?? Lang.T("nenhum", "none"), new Vector2(0, HEADER + 16), 20,
                st.ProfileDisplay != null ? Cor.Branco : Cor.Fraco, FontWeight.Bold, largura: L);
            if (problem != null) p.Texto.Desenhar(dc, problem, new Vector2(L / 2, HEADER + 2), 11.5f, Cor.Laranja, FontWeight.Normal, largura: L / 2);

            // saved profiles as buttons
            if (saved.Count > 0)
            {
                p.Texto.Desenhar(dc, Lang.T("Perfis", "Profiles"), new Vector2(0, profilesTop), 11.5f, Cor.Secundario, FontWeight.Normal);
                for (int i = 0; i < saved.Count; i++)
                {
                    var r = ProfileButton(i, profilesTop, bw);
                    bool active = saved[i].Name == st.Profile;
                    Icone.Retangulo(dc, r.X, r.Y, r.Width, r.Height, BTN_H / 2, active ? new Color4(Orange.R, Orange.G, Orange.B, 0.38f) : new Color4(1, 1, 1, 0.08f));
                    string name = saved[i].Display;
                    var ns = p.Texto.Medir(name, 12.5f, FontWeight.SemiBold);
                    float tx = ns.X > r.Width - 16 ? r.X + 8 : r.X + (r.Width - ns.X) / 2;
                    p.Texto.Desenhar(dc, name, new Vector2(tx, r.Y + 8), 12.5f, Cor.Branco, FontWeight.SemiBold, largura: r.Width - 16);
                }
            }

            // devices: icon, name, status and battery
            if (devices.Count > 0)
            {
                p.Texto.Desenhar(dc, Lang.T("Aparelhos", "Devices"), new Vector2(0, devicesTop), 11.5f, Cor.Secundario, FontWeight.Normal);
                for (int i = 0; i < devices.Count; i++)
                {
                    var d = devices[i];
                    float y = devicesTop + 22 + i * ROW;
                    KindGlyph(dc, fab, d.Kind, new Vector2(10, y + 10), 18, Cor.Branco);
                    float right = d.Battery != null ? L - BatteryPill.Total - 8 : L;
                    var ns = p.Texto.Medir(d.Name, 13, FontWeight.Normal);
                    float nameW = MathF.Min(ns.X, right - 28 - 40);
                    p.Texto.Desenhar(dc, d.Name, new Vector2(28, y + 2), 13, Cor.Branco, FontWeight.Normal, largura: nameW + 1);
                    if (d.Status is { Length: > 0 } s && right - 28 - nameW - 10 > 30)
                        p.Texto.Desenhar(dc, s, new Vector2(28 + nameW + 10, y + 3.5f), 11.5f, Cor.Secundario, FontWeight.Normal, largura: right - 28 - nameW - 10);
                    if (d.Battery is int b) BatteryPill.Draw(dc, p, L - BatteryPill.Total, y + 1, b, d.Charging);
                }
            }

            // open Gear Hub
            Icone.Retangulo(dc, 0, openTop, L, OPEN_H, OPEN_H / 2, new Color4(1, 1, 1, 0.07f));
            string open = Lang.T("Abrir o Gear Hub", "Open Gear Hub");
            var os = p.Texto.Medir(open, 13, FontWeight.SemiBold);
            p.Texto.Desenhar(dc, open, new Vector2((L - os.X) / 2, openTop + 8), 13, Cor.Branco, FontWeight.SemiBold);
        });

        if (st.SnapTap != null) c.Botoes.Add(new Botao { Area = chip, Acao = _m.SnapTapPressed });
        for (int i = 0; i < saved.Count; i++)
        {
            string name = saved[i].Name;
            c.Botoes.Add(new Botao { Area = ProfileButton(i, profilesTop, bw), Acao = () => _m.ApplyProfile(name) });
        }
        c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(0, openTop, L, OPEN_H), Acao = () => _m.ShowGearHub() });
        return c;
    }

    private static System.Drawing.RectangleF ProfileButton(int i, float top, float bw) =>
        new(i % COLS * (bw + BTN_GAP), top + 22 + i / COLS * (BTN_H + BTN_GAP), bw, BTN_H);

    // ------------------------------------------------------------------ notices on the pill

    /// <summary>One notice: the icon on the left, the title and the small line under it (a battery ring for low battery).</summary>
    public static Cena NoticeCard(Palco p, GearNotice n)
    {
        bool battery = n.Kind == NoticeKind.Battery;
        Color4 sub = n.Kind switch
        {
            NoticeKind.Battery => n.Percent <= 10 ? Cor.Vermelho : Cor.Laranja,
            NoticeKind.SnapTap => n.On == true ? SnapOn : Cor.Secundario,
            NoticeKind.Profile when n.On is bool on => on ? SnapOn : Cor.Secundario,
            _ => Cor.Secundario,
        };
        Color4 icon = n.Kind switch
        {
            NoticeKind.Device => n.On == true ? Cor.Branco : Cor.Secundario,
            NoticeKind.SnapTap => n.On == true ? SnapOn : Cor.Secundario,
            NoticeKind.Effect => Knobs[0],
            _ => Cor.Branco,
        };
        float extra = battery ? 46 : 0;
        var ts = p.Texto.Medir(n.Title, 15, FontWeight.Bold);
        var us = p.Texto.Medir(n.Sub, 12.5f, FontWeight.Normal);
        float w = MathF.Min(MathF.Max(ts.X, us.X) + 52 + extra, 380);
        var c = p.NovaCena(new Vector2(w, 44));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var fab = p.G.Fabrica;
            string kind = n.Kind is NoticeKind.Profile ? "gear" : n.Kind is NoticeKind.Effect ? "light" : n.Icon;
            KindGlyph(dc, fab, kind, new Vector2(17, 22), 30, kind == "gear" ? Orange : icon);
            float tw = w - 48 - extra;
            p.Texto.Desenhar(dc, n.Title, new Vector2(46, 2), 15, Cor.Branco, FontWeight.Bold, largura: tw);
            p.Texto.Desenhar(dc, n.Sub, new Vector2(46, 23), 12.5f, sub, FontWeight.Normal, largura: tw);
            if (battery && n.Percent is int pct)
            {
                var rc = new Vector2(w - 19, 22);
                Icone.Anel(dc, fab, rc, 15, 3.4f, pct / 100f, sub);
                string num = pct.ToString(Lang.Culture);
                var m = p.Texto.Medir(num, 11, FontWeight.SemiBold, tabular: true);
                p.Texto.Desenhar(dc, num, new Vector2(rc.X - m.X / 2, rc.Y - m.Y / 2), 11, Cor.Branco, tabular: true);
            }
        });
        return c;
    }
}
