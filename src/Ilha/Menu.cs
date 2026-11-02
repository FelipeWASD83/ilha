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

    /// <summary>Size in grid cells (columns x rows); the menu packs every entry into the first free spot, in order.</summary>
    public int Cols = 1, Rows = 1;
    /// <summary>Side of the icon box (DIP). A bigger icon (the limit ring with its number inside) needs no detail line.</summary>
    public float IconSize = 24;
    /// <summary>Draws the whole tile (the sound slider, the batteries) instead of icon and name; the rect is the tile.</summary>
    public Action<ID2D1DeviceContext, Palco, System.Drawing.RectangleF>? Draw;
    /// <summary>
    /// Dragging inside the tile: the position relative to the tile's corner, the tile's size and true on release. A press
    /// and release without moving still opens the entry.
    /// </summary>
    public Action<Vector2, System.Drawing.SizeF, bool>? Drag;
    /// <summary>Mouse wheel over the tile (120 per notch); true = handled.</summary>
    public Func<int, bool>? Wheel;
    /// <summary>Clickable parts of a drawn tile (each device in the batteries tile), relative to the tile.</summary>
    public Func<System.Drawing.SizeF, IReadOnlyList<(System.Drawing.RectangleF Area, Action Open)>>? Parts;
    /// <summary>A round button in the top-right corner, next to the date (settings), instead of a tile.</summary>
    public bool Corner;
    /// <summary>A small color dot before the detail line (the color picker's last color).</summary>
    public Func<Vortice.Mathematics.Color4?>? DetailSwatch;
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
/// volta para ele. Hora e data em cima, as configurações no canto e uma grade de blocos (1x1 com ícone e nome, ou
/// maiores e desenhados pelo módulo, como o volume e as baterias), no estilo da Central de Controle do iPhone.
/// </summary>
internal sealed class Menu : Atividade
{
    public override string Tipo => "menu";
    public override string Nome => "Menu";

    private const int COLUNAS = 4;
    private const float LADO = 84, ALTO = 66, VAO = 8, CABECALHO = 58, CANTO = 34;
    /// <summary>Corner radius of the tiles; the card's own radius is this plus its padding, so the corners line up.</summary>
    internal const float RAIO_BLOCO = 20;
    private static readonly Vortice.Mathematics.Color4 FundoBloco = new(1, 1, 1, 0.085f);
    private static readonly Vortice.Mathematics.Color4 BordaBloco = new(1, 1, 1, 0.07f);

    private readonly MenuDaIlha _menu;
    private readonly System.Windows.Forms.Timer _minuto = new() { Interval = 5_000 };
    private int _desenhado = -1;
    private string _assinatura = "";
    // the tiles of the scene on screen, for the mouse wheel
    private List<(ItemDoMenu Item, System.Drawing.RectangleF Area)> _blocos = new();

    public Menu(MenuDaIlha menu)
    {
        _menu = menu;
        _menu.Mudou += () =>
        {
            // entrou, saiu ou mudou de tamanho um bloco: a grade muda; só o conteúdo mudou: redesenha
            if (Assinatura(_menu.Itens) != _assinatura) MudouTamanho();
            else Redesenhar();
        };
        // só roda enquanto o menu está na tela, e só redesenha quando o minuto vira
        _minuto.Tick += (_, _) =>
        {
            if (!TemCenaViva) { _minuto.Stop(); return; }
            if (DateTime.Now.Minute != _desenhado) Redesenhar();
        };
    }

    private static string Assinatura(IReadOnlyList<ItemDoMenu> itens) =>
        string.Join("|", itens.Select(i => $"{i.Id}:{i.Cols}x{i.Rows}:{(i.Corner ? 1 : 0)}"));

    /// <summary>
    /// Packs the tiles into the 4-column grid: each goes to the first free spot (row by row) where it fits, in the menu's
    /// order. Returns each tile's column and row, and the number of rows used.
    /// </summary>
    internal static (List<(int Col, int Row)> Spots, int Rows) Pack(IReadOnlyList<(int Cols, int Rows)> sizes)
    {
        var used = new List<bool[]>();
        var spots = new List<(int, int)>();
        int rows = 0;
        bool Free(int c, int r, int w, int h)
        {
            for (int y = r; y < r + h; y++)
                for (int x = c; x < c + w; x++)
                    if (y < used.Count && used[y][x]) return false;
            return true;
        }
        foreach (var (cols, height) in sizes)
        {
            int w = Math.Clamp(cols, 1, COLUNAS), h = Math.Clamp(height, 1, 4);
            for (int r = 0; ; r++)
            {
                int c = Enumerable.Range(0, COLUNAS - w + 1).FirstOrDefault(c => Free(c, r, w, h), -1);
                if (c < 0) continue;
                while (used.Count < r + h) used.Add(new bool[COLUNAS]);
                for (int y = r; y < r + h; y++)
                    for (int x = c; x < c + w; x++) used[y][x] = true;
                spots.Add((c, r));
                rows = Math.Max(rows, r + h);
                break;
            }
        }
        return (spots, rows);
    }

    private static System.Drawing.RectangleF Area(int col, int row, ItemDoMenu i)
    {
        int w = Math.Clamp(i.Cols, 1, COLUNAS), h = Math.Clamp(i.Rows, 1, 4);
        return new(col * (LADO + VAO), CABECALHO + row * (ALTO + VAO), w * LADO + (w - 1) * VAO, h * ALTO + (h - 1) * VAO);
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        _minuto.Start();
        var todos = _menu.Itens;
        _assinatura = Assinatura(todos);
        var cantos = todos.Where(i => i.Corner).ToList();
        var itens = todos.Where(i => !i.Corner).ToList();
        var (lugares, linhas) = Pack(itens.Select(i => (i.Cols, i.Rows)).ToList());
        var blocos = itens.Select((it, k) => (it, Area(lugares[k].Col, lugares[k].Row, it))).ToList();
        _blocos = blocos;
        float largura = COLUNAS * LADO + (COLUNAS - 1) * VAO;
        float altura = CABECALHO + (linhas == 0 ? 20 : linhas * ALTO + (linhas - 1) * VAO);
        var c = p.NovaCena(new Vector2(largura, altura));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var agora = DateTime.Now;
            _desenhado = agora.Minute;
            var cultura = Lang.Culture;
            p.Texto.Desenhar(dc, agora.ToString("HH:mm", cultura), new Vector2(-1, -6), 38, Cor.Branco, FontWeight.Medium, tabular: true);
            float dataAte = largura - cantos.Count * (CANTO + 8);
            p.Texto.Desenhar(dc, agora.ToString(Lang.T("dddd, d 'de' MMMM", "dddd, MMMM d"), cultura), new Vector2(112, 6), 14, Cor.Secundario, FontWeight.Normal, largura: dataAte - 112 - 4);
            for (int k = 0; k < cantos.Count; k++) DesenharCanto(dc, p, cantos[k], AreaDoCanto(k, largura));
            if (itens.Count == 0)
            {
                p.Texto.Desenhar(dc, Lang.T("Nenhum módulo com entrada no menu.", "No module has a menu entry."), new Vector2(0, CABECALHO), 12, Cor.Secundario, FontWeight.Normal);
                return;
            }
            foreach (var (item, r) in blocos)
            {
                Bloco(dc, r, FundoBloco);
                try
                {
                    if (item.Draw != null) item.Draw(dc, p, r);
                    else DesenharPadrao(dc, p, item, r);
                }
                catch (Exception e) { Log.Escrever($"menu: bloco {item.Id}: {e.Message}"); }
            }
        });
        foreach (var (item, r) in blocos)
        {
            if (item.Parts?.Invoke(r.Size) is { } partes)
                foreach (var (area, abrir) in partes)
                    c.Botoes.Add(new Botao { Area = new(r.X + area.X, r.Y + area.Y, area.Width, area.Height), Acao = () => Abrir(item.Id, abrir) });
            var botao = new Botao { Area = r, Acao = () => Abrir(item.Id, item.Abrir) };
            if (item.Drag is { } arrastar)
            {
                var origem = new Vector2(r.X, r.Y);
                var tamanho = r.Size;
                botao.Arrastar = (x, y, soltou) =>
                {
                    try { arrastar(new Vector2(x, y) - origem, tamanho, soltou); }
                    catch (Exception e) { Log.Escrever($"menu: arrastar {item.Id}: {e.Message}"); }
                };
            }
            c.Botoes.Add(botao);
        }
        for (int k = 0; k < cantos.Count; k++)
        {
            var item = cantos[k];
            c.Botoes.Add(new Botao { Area = AreaDoCanto(k, largura), Acao = () => Abrir(item.Id, item.Abrir) });
        }
        return c;
    }

    public override bool Rolar(int delta, Vector2? noCartao)
    {
        if (noCartao is not { } l) return false;
        foreach (var (item, r) in _blocos)
            if (item.Wheel != null && r.Contains(l.X, l.Y))
            {
                try { return item.Wheel(delta); }
                catch (Exception e) { Log.Escrever($"menu: rodinha {item.Id}: {e.Message}"); return false; }
            }
        return false;
    }

    /// <summary>The corner buttons line up from the right edge, in the menu's order (the first one at the edge).</summary>
    private static System.Drawing.RectangleF AreaDoCanto(int k, float largura) =>
        new(largura - CANTO - k * (CANTO + 8), -2, CANTO, CANTO);

    private static void DesenharCanto(ID2D1DeviceContext dc, Palco p, ItemDoMenu item, System.Drawing.RectangleF r)
    {
        var centro = new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2);
        Icone.Circulo(dc, centro, r.Width / 2, new Vortice.Mathematics.Color4(1, 1, 1, 0.12f));
        try { item.Icone?.Invoke(dc, p, new System.Drawing.RectangleF(centro.X - 10, centro.Y - 10, 20, 20)); }
        catch (Exception e) { Log.Escrever($"menu: ícone de {item.Id}: {e.Message}"); }
    }

    /// <summary>The tile's rounded body with a faint edge (the glass rim of the iPhone controls, without the blur).</summary>
    internal static void Bloco(ID2D1DeviceContext dc, System.Drawing.RectangleF r, Vortice.Mathematics.Color4 cor)
    {
        Icone.Retangulo(dc, r.X, r.Y, r.Width, r.Height, RAIO_BLOCO, cor);
        using var b = dc.CreateSolidColorBrush(BordaBloco);
        var rr = new RoundedRectangle(new System.Drawing.RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), RAIO_BLOCO - 0.5f, RAIO_BLOCO - 0.5f);
        dc.DrawRoundedRectangle(rr, b, 1f);
    }

    private static void DesenharPadrao(ID2D1DeviceContext dc, Palco p, ItemDoMenu item, System.Drawing.RectangleF r)
    {
        float ic = Math.Clamp(item.IconSize, 12, 40);
        var area = new System.Drawing.RectangleF(r.X + r.Width / 2 - ic / 2, r.Y + 21 - ic / 2, ic, ic);
        try { item.Icone?.Invoke(dc, p, area); }
        catch (Exception e) { Log.Escrever($"menu: ícone de {item.Id}: {e.Message}"); }
        if (item.Icone == null) Icone.Retangulo(dc, area.X + 3, area.Y + 3, ic - 6, ic - 6, 5, Cor.Fraco);
        string? detalhe = null;
        try { detalhe = item.Detalhe?.Invoke(); } catch { }
        Centrado(dc, p, item.Nome, r, r.Y + (detalhe == null ? 40 : 36), 12, Cor.Branco, FontWeight.Medium);
        if (detalhe == null) return;
        Vortice.Mathematics.Color4? amostra = null;
        try { amostra = item.DetailSwatch?.Invoke(); } catch { }
        Centrado(dc, p, detalhe, r, r.Y + 51, 10.5f, Cor.Secundario, FontWeight.Normal, amostra);
    }

    private static void Centrado(ID2D1DeviceContext dc, Palco p, string texto, System.Drawing.RectangleF r, float y, float tam,
        Vortice.Mathematics.Color4 cor, FontWeight peso, Vortice.Mathematics.Color4? amostra = null)
    {
        const float BOLA = 7, ESPACO = 4;
        float extra = amostra == null ? 0 : BOLA + ESPACO;
        var m = p.Texto.Medir(texto, tam, peso);
        float l = MathF.Min(m.X, r.Width - 8 - extra);
        float x = r.X + (r.Width - l - extra) / 2;
        if (amostra is { } a)
        {
            var centro = new Vector2(x + BOLA / 2, y + m.Y / 2);
            Icone.Circulo(dc, centro, BOLA / 2, a);
            // a dark color on the black card still shows its edge
            using var b = dc.CreateSolidColorBrush(new Vortice.Mathematics.Color4(1, 1, 1, 0.35f));
            dc.DrawEllipse(new Ellipse(centro, BOLA / 2, BOLA / 2), b, 0.8f);
        }
        p.Texto.Desenhar(dc, texto, new Vector2(x + extra, y), tam, cor, peso, largura: r.Width - 8 - extra);
    }

    private static void Abrir(string id, Action abrir)
    {
        try { abrir(); }
        catch (Exception e) { Log.Escrever($"menu: abrir {id}: {e}"); }
    }

    public override void Dispose() => _minuto.Dispose();
}
