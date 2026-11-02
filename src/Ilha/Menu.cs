using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace Ilha;

/// <summary>Uma entrada do menu principal da ilha (calculadora, IA, ar...). Cada módulo põe as suas.</summary>
internal sealed class ItemDoMenu
{
    public required string Id;
    public required string Nome;
    public required Action Abrir;
    /// <summary>Desenha o ícone no quadrado dado (DIP, já com a posição): só formas, nada de letra.</summary>
    public Action<ID2D1DeviceContext, Palco, System.Drawing.RectangleF>? Icone;
    /// <summary>Linha pequena embaixo do nome (a temperatura da sala no ar, por exemplo); null = nada.</summary>
    public Func<string?>? Detalhe;
    public int Ordem = 100;
    internal string? Dono;

    /// <summary>
    /// A battery entry (headset, mouse): drawn in the menu's bottom-right corner instead of the grid, as the icon inside
    /// a gray-ringed circle next to an iPhone-style battery.
    /// </summary>
    public bool Footer;
    /// <summary>Battery level 0 to 100 for the footer battery; null = nothing read yet.</summary>
    public Func<int?>? Level;
    public Func<bool>? Charging;
}

/// <summary>A lista do menu principal. O núcleo marca o dono enquanto o módulo liga e tira as entradas dele ao desligar.</summary>
internal sealed class MenuDaIlha
{
    private readonly List<ItemDoMenu> _itens = new();

    /// <summary>O módulo que está ligando agora (o núcleo marca).</summary>
    public string? Dono { get; set; }
    public event Action? Mudou;

    public IReadOnlyList<ItemDoMenu> Itens => _itens.OrderBy(i => i.Ordem).ThenBy(i => i.Nome, StringComparer.CurrentCulture).ToList();

    public void Adicionar(ItemDoMenu item)
    {
        item.Dono ??= Dono;
        _itens.RemoveAll(x => x.Id == item.Id);
        _itens.Add(item);
        Mudou?.Invoke();
    }

    public void Remover(string id)
    {
        if (_itens.RemoveAll(x => x.Id == id) > 0) Mudou?.Invoke();
    }

    public void RemoverDoDono(string dono)
    {
        if (_itens.RemoveAll(x => x.Dono == dono) > 0) Mudou?.Invoke();
    }

    /// <summary>O detalhe de alguma entrada mudou (temperatura nova): o menu aberto redesenha.</summary>
    public void Redesenhar() => Mudou?.Invoke();
}

/// <summary>
/// O menu principal: abre pelo atalho da ilha ou pelo clique sem nada em andamento, e o "&lt;" das telas internas
/// volta para ele. Hora e data em cima e uma grade com as entradas dos módulos.
/// </summary>
internal sealed class Menu : Atividade
{
    public override string Tipo => "menu";
    public override string Nome => "Menu";

    private const float COLUNAS = 4, LADO = 84, ALTO = 66, VAO = 8, CABECALHO = 58;
    private readonly MenuDaIlha _menu;
    private readonly System.Windows.Forms.Timer _minuto = new() { Interval = 5_000 };
    private int _desenhado = -1, _quantos = -1;

    public Menu(MenuDaIlha menu)
    {
        _menu = menu;
        _menu.Mudou += () =>
        {
            // entrou ou saiu entrada: a grade muda de altura; só o detalhe mudou: redesenha
            if (_menu.Itens.Count != _quantos) MudouTamanho();
            else Redesenhar();
        };
        // só roda enquanto o menu está na tela, e só redesenha quando o minuto vira
        _minuto.Tick += (_, _) =>
        {
            if (!TemCenaViva) { _minuto.Stop(); return; }
            if (DateTime.Now.Minute != _desenhado) Redesenhar();
        };
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        _minuto.Start();
        var todos = _menu.Itens;
        _quantos = todos.Count;
        var itens = todos.Where(i => !i.Footer).ToList();
        var rodape = todos.Where(i => i.Footer).ToList();
        int linhas = (int)Math.Ceiling(itens.Count / COLUNAS);
        float largura = COLUNAS * LADO + (COLUNAS - 1) * VAO;
        float altura = CABECALHO + (linhas == 0 ? 20 : linhas * ALTO + (linhas - 1) * VAO);
        float yRodape = altura + FOOT_TOP;
        if (rodape.Count > 0) altura = yRodape + FOOT_H;
        var c = p.NovaCena(new Vector2(largura, altura));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var agora = DateTime.Now;
            _desenhado = agora.Minute;
            var cultura = Lang.Culture;
            p.Texto.Desenhar(dc, agora.ToString("HH:mm", cultura), new Vector2(-1, -6), 38, Cor.Branco, FontWeight.Medium, tabular: true);
            p.Texto.Desenhar(dc, agora.ToString(Lang.T("dddd, d 'de' MMMM", "dddd, MMMM d"), cultura), new Vector2(112, 6), 14, Cor.Secundario, FontWeight.Normal, largura: largura - 112);
            for (int i = 0; i < rodape.Count; i++)
            {
                try { DrawFooter(dc, p, rodape[i], FooterX(i, rodape.Count, largura), yRodape); }
                catch (Exception e) { Log.Escrever($"menu: rodapé de {rodape[i].Id}: {e.Message}"); }
            }
            if (itens.Count == 0)
            {
                p.Texto.Desenhar(dc, Lang.T("Nenhum módulo com entrada no menu.", "No module has a menu entry."), new Vector2(0, CABECALHO), 12, Cor.Secundario, FontWeight.Normal);
                return;
            }
            for (int i = 0; i < itens.Count; i++)
            {
                var (x, y) = Posicao(i);
                Icone.Retangulo(dc, x, y, LADO, ALTO, 14, new Vortice.Mathematics.Color4(1, 1, 1, 0.07f));
                var area = new System.Drawing.RectangleF(x + LADO / 2 - 12, y + 9, 24, 24);
                try { itens[i].Icone?.Invoke(dc, p, area); }
                catch (Exception e) { Log.Escrever($"menu: ícone de {itens[i].Id}: {e.Message}"); }
                if (itens[i].Icone == null) Icone.Retangulo(dc, area.X + 3, area.Y + 3, 18, 18, 5, Cor.Fraco);
                string? detalhe = null;
                try { detalhe = itens[i].Detalhe?.Invoke(); } catch { }
                Centrado(dc, p, itens[i].Nome, x, y + (detalhe == null ? 40 : 36), 12, Cor.Branco, FontWeight.Medium);
                if (detalhe != null) Centrado(dc, p, detalhe, x, y + 51, 10.5f, Cor.Secundario, FontWeight.Normal);
            }
        });
        for (int i = 0; i < itens.Count; i++)
        {
            var (x, y) = Posicao(i);
            var item = itens[i];
            c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(x, y, LADO, ALTO), Acao = () => Abrir(item) });
        }
        for (int i = 0; i < rodape.Count; i++)
        {
            var item = rodape[i];
            c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(FooterX(i, rodape.Count, largura), yRodape, FOOT_W, FOOT_H), Acao = () => Abrir(item) });
        }
        return c;
    }

    // ------------------------------------------------------------------ battery footer

    private const float FOOT_TOP = 12, FOOT_H = 32, FOOT_GAP = 16, PILL_W = 34, PILL_H = 16, NUB = 2.5f;
    private const float FOOT_W = FOOT_H + 7 + PILL_W + 1 + NUB;

    private static readonly Vortice.Mathematics.Color4 Ring = new(0.47f, 0.47f, 0.5f, 1f);
    private static readonly Vortice.Mathematics.Color4 BatteryGreen = new(0.2f, 0.78f, 0.35f, 1f);
    private static readonly Vortice.Mathematics.Color4 BatteryYellow = new(1f, 0.8f, 0f, 1f);
    private static readonly Vortice.Mathematics.Color4 BatteryRed = new(1f, 0.23f, 0.19f, 1f);

    /// <summary>Right-aligned: the last entry touches the right edge.</summary>
    private static float FooterX(int i, int n, float largura) => largura - (n - i) * FOOT_W - (n - 1 - i) * FOOT_GAP;

    internal static Vortice.Mathematics.Color4 BatteryColor(int level, bool charging) =>
        charging || level > 50 ? BatteryGreen : level > 25 ? BatteryYellow : BatteryRed;

    private static void DrawFooter(ID2D1DeviceContext dc, Palco p, ItemDoMenu item, float x, float y)
    {
        float raio = FOOT_H / 2;
        var centro = new Vector2(x + raio, y + raio);
        Icone.Circulo(dc, centro, raio, new Vortice.Mathematics.Color4(1, 1, 1, 0.1f));
        const float pad = 4.5f;
        item.Icone?.Invoke(dc, p, new System.Drawing.RectangleF(x + pad, y + pad, FOOT_H - 2 * pad, FOOT_H - 2 * pad));
        // the ring goes over the photo, so a dark device on the black card still has an outline
        using (var b = dc.CreateSolidColorBrush(Ring)) dc.DrawEllipse(new Vortice.Direct2D1.Ellipse(centro, raio - 0.6f, raio - 0.6f), b, 1.2f);
        int? level = null;
        bool charging = false;
        try { level = item.Level?.Invoke(); charging = item.Charging?.Invoke() ?? false; } catch { }
        DrawBattery(dc, p, x + FOOT_H + 7, y + (FOOT_H - PILL_H) / 2, level, charging);
    }

    /// <summary>
    /// The iPhone battery with the number inside: a gray track filled from the left in green, yellow (50% and below) or
    /// red (25% and below), always green while charging. The number is dark over the fill and white over the empty part.
    /// </summary>
    private static void DrawBattery(ID2D1DeviceContext dc, Palco p, float x, float y, int? level, bool charging)
    {
        const float r = 5f, tam = 11f;
        Icone.Retangulo(dc, x + PILL_W + 1, y + PILL_H / 2 - 3, NUB, 6, 1.2f, new Vortice.Mathematics.Color4(1, 1, 1, 0.35f));
        Icone.Retangulo(dc, x, y, PILL_W, PILL_H, r, new Vortice.Mathematics.Color4(1, 1, 1, 0.24f));
        string texto = level is int lv ? Math.Clamp(lv, 0, 100).ToString() : "–";
        var m = p.Texto.Medir(texto, tam, FontWeight.Bold, tabular: true);
        var pos = new Vector2(x + (PILL_W - m.X) / 2, y + (PILL_H - m.Y) / 2 - 0.5f);
        float cheio = level is int v ? PILL_W * Math.Clamp(v, 0, 100) / 100f : 0;
        if (cheio > 0)
        {
            dc.PushAxisAlignedClip(new System.Drawing.RectangleF(x, y, cheio, PILL_H), AntialiasMode.PerPrimitive);
            Icone.Retangulo(dc, x, y, PILL_W, PILL_H, r, BatteryColor(level!.Value, charging));
            p.Texto.Desenhar(dc, texto, pos, tam, new Vortice.Mathematics.Color4(0, 0, 0, 0.88f), FontWeight.Bold, tabular: true);
            dc.PopAxisAlignedClip();
        }
        if (cheio < PILL_W)
        {
            dc.PushAxisAlignedClip(new System.Drawing.RectangleF(x + cheio, y - 2, PILL_W - cheio + 1, PILL_H + 4), AntialiasMode.PerPrimitive);
            p.Texto.Desenhar(dc, texto, pos, tam, Cor.Branco, FontWeight.Bold, tabular: true);
            dc.PopAxisAlignedClip();
        }
    }

    private static (float x, float y) Posicao(int i) =>
        (i % (int)COLUNAS * (LADO + VAO), CABECALHO + i / (int)COLUNAS * (ALTO + VAO));

    private static void Centrado(ID2D1DeviceContext dc, Palco p, string texto, float x, float y, float tam, Vortice.Mathematics.Color4 cor, FontWeight peso)
    {
        var m = p.Texto.Medir(texto, tam, peso);
        float l = MathF.Min(m.X, LADO - 8);
        p.Texto.Desenhar(dc, texto, new Vector2(x + (LADO - l) / 2, y), tam, cor, peso, largura: LADO - 8);
    }

    private static void Abrir(ItemDoMenu item)
    {
        try { item.Abrir(); }
        catch (Exception e) { Log.Escrever($"menu: abrir {item.Id}: {e}"); }
    }

    public override void Dispose() => _minuto.Dispose();
}
