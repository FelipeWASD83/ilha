using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ilha.Games;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.SteamDownloads;

/// <summary>
/// Steam downloads on the island: the pill with the game's icon, its name, the percentage and a thin bar; the bubble a
/// ring; the card every download with speed and time left. Comes in quietly (downloads are long), redrawn by the module.
/// </summary>
internal sealed class DownloadsActivity : Atividade
{
    private const float L = 340, ROW = 50, HC = 24, LC = 230;
    private static readonly Color4 SteamBlue = new(0.40f, 0.75f, 1f, 1f);

    private readonly Servicos S;
    private List<(Download D, double? Speed)> _items = new();
    private string _layout = "";

    public override string Tipo => "download-steam";
    public override string Nome => Lang.T("Downloads da Steam", "Steam downloads");
    public override bool AnunciaChegada => false;
    public override float? LarguraCompactaMinima => LC;

    public DownloadsActivity(Servicos s) => S = s;

    public void Show(List<(Download D, double? Speed)> items)
    {
        _items = items;
        if (items.Count == 0) { Desligar(); return; }
        Ligar();
        string layout = string.Join("|", items.Select(i => i.D.Key));
        if (layout != _layout) { _layout = layout; MudouTamanho(); }
        else Redesenhar();
    }

    private (Download D, double? Speed) Main => _items.OrderBy(i => i.D.Paused).ThenByDescending(i => i.D.Total).First();

    protected override Cena Montar(Palco p, Forma f)
    {
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(24, 24));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    if (_items.Count == 0) return;
                    var d = Main.D;
                    Icone.Anel(dc, p.G.Fabrica, new Vector2(12, 12), 10, 2.5f, 1, new Color4(1, 1, 1, 0.15f));
                    Icone.Anel(dc, p.G.Fabrica, new Vector2(12, 12), 10, 2.5f, (float)(d.Fraction ?? 0.25), d.Paused ? Cor.Secundario : SteamBlue);
                    Arrow(dc, p.G.Fabrica, new Vector2(12, 12), 5, Cor.Branco);
                });
                return c;
            }
            case Forma.Compacta:
            {
                var c = p.NovaCena(new Vector2(LC, HC));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    if (_items.Count == 0) return;
                    var (d, speed) = Main;
                    DrawIcon(dc, d, 0, 2, 20);
                    string pct = Percent(d);
                    var ps = p.Texto.Medir(pct, 13, FontWeight.SemiBold, tabular: true);
                    float right = t.X - ps.X;
                    p.Texto.Desenhar(dc, pct, new Vector2(right, (t.Y - ps.Y) / 2 - 2), 13, d.Paused ? Cor.Secundario : SteamBlue, FontWeight.SemiBold, tabular: true);
                    string name = _items.Count > 1 ? $"{d.Name} +{_items.Count - 1}" : d.Name;
                    p.Texto.Desenhar(dc, name, new Vector2(28, 1), 13, Cor.Branco, FontWeight.SemiBold, largura: right - 36);
                    // the bar under the name
                    float bw = right - 36;
                    Icone.Retangulo(dc, 28, 19, bw, 3, 1.5f, new Color4(1, 1, 1, 0.14f));
                    if (d.Fraction is double fr) Icone.Retangulo(dc, 28, 19, MathF.Max(3, bw * (float)fr), 3, 1.5f, d.Paused ? Cor.Secundario : SteamBlue);
                });
                return c;
            }
            default:
                return Card(p);
        }
    }

    private static string Percent(Download d) =>
        d.Paused ? Lang.T("pausado", "paused") : d.Fraction is double f ? $"{Math.Floor(f * 100):0}%" : SteamLibrary.Bytes(d.Done);

    private void DrawIcon(ID2D1DeviceContext dc, Download d, float x, float y, float side)
    {
        if (GameArt.IconFor("steam:" + d.App, d.App) is { } icon) GameArt.Draw(dc, icon, x, y, side);
        else
        {
            Icone.Retangulo(dc, x, y, side, side, side * 0.22f, new Color4(1, 1, 1, 0.12f));
            Arrow(dc, S.Palco.G.Fabrica, new Vector2(x + side / 2, y + side / 2), side * 0.3f, Cor.Branco);
        }
    }

    /// <summary>A down arrow into a tray, centered on `c`.</summary>
    internal static void Arrow(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float r, Color4 color)
    {
        Icone.Linha(dc, new Vector2(c.X, c.Y - r), new Vector2(c.X, c.Y + r * 0.45f), r * 0.32f, color, f);
        Icone.Linha(dc, new Vector2(c.X - r * 0.55f, c.Y - r * 0.05f), new Vector2(c.X, c.Y + r * 0.5f), r * 0.32f, color, f);
        Icone.Linha(dc, new Vector2(c.X + r * 0.55f, c.Y - r * 0.05f), new Vector2(c.X, c.Y + r * 0.5f), r * 0.32f, color, f);
        Icone.Linha(dc, new Vector2(c.X - r * 0.8f, c.Y + r * 0.95f), new Vector2(c.X + r * 0.8f, c.Y + r * 0.95f), r * 0.32f, color, f);
    }

    private Cena Card(Palco p)
    {
        var items = _items.ToList();
        var c = p.NovaCena(new Vector2(L, 30 + Math.Max(1, items.Count) * ROW - 6));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            p.Texto.Desenhar(dc, Lang.T("Downloads da Steam", "Steam downloads"), Vector2.Zero, 15, Cor.Branco);
            float y = 30;
            foreach (var (d, speed) in items)
            {
                Icone.Retangulo(dc, 0, y, L, ROW - 6, 14, new Color4(1, 1, 1, 0.06f));
                DrawIcon(dc, d, 10, y + 6, 32);
                float x = 52, w = L - x - 10;
                string title = d.Workshop ? Lang.T($"Oficina: {d.Name}", $"Workshop: {d.Name}") : d.Name;
                string pct = Percent(d);
                var ps = p.Texto.Medir(pct, 13, FontWeight.SemiBold, tabular: true);
                p.Texto.Desenhar(dc, title, new Vector2(x, y + 4), 13.5f, Cor.Branco, largura: w - ps.X - 8);
                p.Texto.Desenhar(dc, pct, new Vector2(L - 10 - ps.X, y + 5), 13, d.Paused ? Cor.Secundario : SteamBlue, FontWeight.SemiBold, tabular: true);
                Icone.Retangulo(dc, x, y + 24, w, 4, 2, new Color4(1, 1, 1, 0.14f));
                if (d.Fraction is double fr) Icone.Retangulo(dc, x, y + 24, MathF.Max(4, w * (float)fr), 4, 2, d.Paused ? Cor.Secundario : SteamBlue);
                p.Texto.Desenhar(dc, Detail(d, speed), new Vector2(x, y + 30), 11, Cor.Secundario, FontWeight.Normal, tabular: true, largura: w);
                y += ROW;
            }
        });
        return c;
    }

    /// <summary>"1,2 GB de 4,5 GB · 32 MB/s · 2 min" (the parts it knows).</summary>
    internal static string Detail(Download d, double? speed)
    {
        var parts = new List<string>();
        parts.Add(d.Total > 0 ? $"{SteamLibrary.Bytes(d.Done)} {Lang.T("de", "of")} {SteamLibrary.Bytes(d.Total)}" : SteamLibrary.Bytes(d.Done));
        if (d.Paused) parts.Add(Lang.T("pausado", "paused"));
        else if (speed is double s && s > 1000)
        {
            parts.Add(SteamLibrary.Bytes(s) + "/s");
            if (d.Total > d.Done) parts.Add(SteamLibrary.Eta((d.Total - d.Done) / s));
        }
        return string.Join(" · ", parts);
    }

    /// <summary>The notice when a game finished downloading.</summary>
    public static Cena DoneCard(Palco p, Download d)
    {
        string title = d.Name;
        string sub = d.Workshop ? Lang.T("Itens da Oficina baixados", "Workshop items downloaded") : Lang.T("Pronto para jogar", "Ready to play");
        var ts = p.Texto.Medir(title, 15, FontWeight.Bold);
        float w = MathF.Min(MathF.Max(ts.X, 120) + 66, 360);
        var c = p.NovaCena(new Vector2(w, 46));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            if (GameArt.IconFor("steam:" + d.App, d.App) is { } icon) GameArt.Draw(dc, icon, 2, 5, 36);
            else Arrow(dc, p.G.Fabrica, new Vector2(20, 23), 12, SteamBlue);
            p.Texto.Desenhar(dc, title, new Vector2(50, 3), 15, Cor.Branco, FontWeight.Bold, largura: w - 52);
            p.Texto.Desenhar(dc, sub, new Vector2(50, 24), 12.5f, SteamBlue, FontWeight.Normal, largura: w - 52);
        });
        return c;
    }
}
