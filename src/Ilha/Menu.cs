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
    /// <summary>
    /// Draws the tile's content (the sound slider, the batteries) instead of icon and name; the rect is the whole cell
    /// area. The menu has already drawn the gray body: a ball on top for a 1x1, a rounded block for bigger ones.
    /// </summary>
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
    /// <summary>A plain icon in the top-right corner, next to the date (settings, recording), instead of a tile.</summary>
    public bool Corner;
    /// <summary>
    /// A small icon in the card's bottom-right corner, with no ball, name or background (a discreet entry). The icon
    /// draws its own colors, dim ones being the point.
    /// </summary>
    public bool CornerBottom;
    /// <summary>A small color dot before the detail line (the color picker's last color).</summary>
    public Func<Vortice.Mathematics.Color4?>? DetailSwatch;
    /// <summary>A bigger block that draws in the label space under its body, like the balls' names (the batteries).</summary>
    public bool LabelBelow;
}

/// <summary>A lista do menu principal. O núcleo marca o dono enquanto o módulo liga e tira as entradas dele ao desligar.</summary>
internal sealed class MenuDaIlha
{
    private readonly List<ItemDoMenu> _itens = new();

    /// <summary>O módulo que está ligando agora (o núcleo marca).</summary>
    public string? Dono { get; set; }
    public event Action? Mudou;

    public IReadOnlyList<ItemDoMenu> Itens => _itens.OrderBy(i => i.Ordem).ThenBy(i => i.Nome, StringComparer.CurrentCulture).ToList();

    private HashSet<string> _escondidos = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The entries on screen: all but the ones hidden in the settings (`menuEscondido`).</summary>
    public IReadOnlyList<ItemDoMenu> Visiveis => Itens.Where(i => !_escondidos.Contains(i.Id)).ToList();

    /// <summary>The ids hidden from the menu (the settings window's "Menu" section); the menu redraws.</summary>
    public void Esconder(IEnumerable<string>? ids)
    {
        var novo = new HashSet<string>(ids ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (novo.SetEquals(_escondidos)) return;
        _escondidos = novo;
        Mudou?.Invoke();
    }

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
/// volta para ele. Hora e data em cima, os ícones do canto (configurações, gravação) e uma grade no estilo da Central
/// de Controle do iPhone: cada entrada 1x1 é uma bola com o nome embaixo, as maiores (volume, brilho, baterias) são
/// blocos de cantos redondos desenhados pelo módulo.
/// </summary>
internal sealed class Menu : Atividade
{
    public override string Tipo => "menu";
    public override string Nome => "Menu";

    private const int COLUNAS = 4;
    private const float LADO = 80, ALTO = 80, VAO = 6, CABECALHO = 48, BOLA = 54, CANTO = 32, CANTO_ICONE = 24, CANTO_BAIXO = 14;
    /// <summary>The header's center line: the time's digits, the date, the dots and the corner icons sit on it.</summary>
    private const float LINHA = 18;
    /// <summary>How far the header stays from the card's sides (the time and the corner icons were almost in the corners).</summary>
    private const float MARGEM = 6;
    /// <summary>The icons are drawn for a 24 DIP box and scaled up as a whole (some draw in fixed units).</summary>
    private const float ESCALA_ICONE = 28f / 24f;
    /// <summary>Corner radius of the bigger blocks: the balls' radius, so everything has the same curve.</summary>
    internal const float RAIO_BLOCO = BOLA / 2;
    private static readonly Vortice.Mathematics.Color4 FundoBloco = new(1, 1, 1, 0.06f);

    private readonly MenuDaIlha _menu;
    private readonly Indicadores? _indicadores;
    private readonly System.Windows.Forms.Timer _minuto = new() { Interval = 5_000 };
    private int _desenhado = -1;
    private string _assinatura = "";
    // the tiles of the scene on screen, for the mouse wheel
    private List<(ItemDoMenu Item, System.Drawing.RectangleF Area)> _blocos = new();

    public Menu(MenuDaIlha menu, Indicadores? indicadores = null)
    {
        _menu = menu;
        _indicadores = indicadores;
        _menu.Mudou += () =>
        {
            // entrou, saiu ou mudou de tamanho um bloco: a grade muda; só o conteúdo mudou: redesenha
            if (Assinatura(_menu.Visiveis) != _assinatura) MudouTamanho();
            else Redesenhar();
        };
        if (_indicadores != null) _indicadores.Mudou += Redesenhar;
        // só roda enquanto o menu está na tela, e só redesenha quando o minuto vira
        _minuto.Tick += (_, _) =>
        {
            if (!TemCenaViva) { _minuto.Stop(); return; }
            if (DateTime.Now.Minute != _desenhado) Redesenhar();
        };
    }

    private static string Assinatura(IReadOnlyList<ItemDoMenu> itens) =>
        string.Join("|", itens.Select(i => $"{i.Id}:{i.Cols}x{i.Rows}:{(i.Corner ? 1 : 0)}{(i.CornerBottom ? 2 : 0)}"));

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

    private static bool Simples(ItemDoMenu i) => i.Cols <= 1 && i.Rows <= 1;

    /// <summary>The ball of a 1x1 tile: centered at the top of its cell, with the name under it.</summary>
    internal static System.Drawing.RectangleF Bola(System.Drawing.RectangleF celula) =>
        new(celula.X + (celula.Width - BOLA) / 2, celula.Y, BOLA, BOLA);

    /// <summary>
    /// The gray body of a tile from its cells: the ball for a 1x1; for a bigger one, from the left edge of its first
    /// column's ball to the right edge of its last one, and from the top of its first row's balls to the bottom of its
    /// last row's (the label space under it stays free), so every edge lines up with the balls around it.
    /// </summary>
    internal static System.Drawing.RectangleF Corpo(System.Drawing.RectangleF celula)
    {
        if (celula.Width <= LADO + 0.5f && celula.Height <= ALTO + 0.5f) return Bola(celula);
        float lado = (LADO - BOLA) / 2;
        return new(celula.X + lado, celula.Y, celula.Width - 2 * lado, celula.Height - (ALTO - BOLA));
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        _minuto.Start();
        var todos = _menu.Visiveis;
        _assinatura = Assinatura(todos);
        // the first corner entry sits at the right edge: settings (the highest order) last in the menu, first here
        var cantos = todos.Where(i => i.Corner).OrderByDescending(i => i.Ordem).ToList();
        var embaixo = todos.Where(i => i.CornerBottom && !i.Corner).OrderByDescending(i => i.Ordem).ToList();
        var itens = todos.Where(i => !i.Corner && !i.CornerBottom).ToList();
        var (lugares, linhas) = Pack(itens.Select(i => (i.Cols, i.Rows)).ToList());
        var blocos = itens.Select((it, k) => (it, Area(lugares[k].Col, lugares[k].Row, it))).ToList();
        _blocos = blocos;
        float largura = COLUNAS * LADO + (COLUNAS - 1) * VAO;
        // the card ends where the last thing drawn ends: a bottom row of blocks has no names under it, so its label
        // space would only be an empty band above the card's edge
        float altura = linhas == 0 ? CABECALHO + 20
            : blocos.Max(b => Simples(b.it) || b.it.LabelBelow ? b.Item2.Bottom : Corpo(b.Item2).Bottom);
        // the discreet icons sit in the bottom-right corner: inside the last row when its right cells are empty, else on
        // a short strip of their own under the grid
        if (embaixo.Count > 0)
        {
            int livres = linhas == 0 ? 0 : CelulasLivresNaDireita(itens, lugares, linhas - 1);
            float precisa = embaixo.Count * (CANTO_BAIXO + 8);
            if (livres * (LADO + VAO) < precisa) altura += CANTO_BAIXO + 10;
        }
        var areasDeBaixo = Enumerable.Range(0, embaixo.Count).Select(k => AreaDeBaixo(k, largura, altura)).ToList();
        var c = p.NovaCena(new Vector2(largura, altura));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            DesenharCabecalho(dc, p, largura, cantos);
            for (int k = 0; k < embaixo.Count; k++)
            {
                var r = areasDeBaixo[k];
                var centro = new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2);
                var antes = dc.Transform;
                dc.Transform = Matrix3x2.CreateScale(CANTO_BAIXO / 24f, centro) * antes;
                try { embaixo[k].Icone?.Invoke(dc, p, new System.Drawing.RectangleF(centro.X - 12, centro.Y - 12, 24, 24)); }
                catch (Exception e) { Log.Escrever($"menu: ícone de {embaixo[k].Id}: {e.Message}"); }
                finally { dc.Transform = antes; }
            }
            if (itens.Count == 0)
            {
                p.Texto.Desenhar(dc, Lang.T("Nenhum módulo com entrada no menu.", "No module has a menu entry."), new Vector2(0, CABECALHO), 12, Cor.Secundario, FontWeight.Normal);
                return;
            }
            foreach (var (item, r) in blocos)
            {
                Fundo(dc, Corpo(r), Simples(item));
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
        for (int k = 0; k < embaixo.Count; k++)
        {
            var item = embaixo[k];
            var r = areasDeBaixo[k];
            // a little more than the icon takes the click, so a small icon is not fiddly to hit
            c.Botoes.Add(new Botao { Area = new(r.X - 5, r.Y - 5, r.Width + 10, r.Height + 10), Acao = () => Abrir(item.Id, item.Abrir) });
        }
        return c;
    }

    /// <summary>How many cells of a row are free from its right edge leftwards (no tile covers them).</summary>
    private static int CelulasLivresNaDireita(IReadOnlyList<ItemDoMenu> itens, List<(int Col, int Row)> lugares, int linha)
    {
        var ocupada = new bool[COLUNAS];
        for (int k = 0; k < itens.Count; k++)
        {
            int w = Math.Clamp(itens[k].Cols, 1, COLUNAS), h = Math.Clamp(itens[k].Rows, 1, 4);
            var (col, row) = lugares[k];
            if (linha < row || linha >= row + h) continue;
            for (int x = col; x < col + w; x++) ocupada[x] = true;
        }
        int livres = 0;
        for (int x = COLUNAS - 1; x >= 0 && !ocupada[x]; x--) livres++;
        return livres;
    }

    /// <summary>The bottom-right icons line up from the right edge of the grid's last ball, the first one at the edge.</summary>
    private static System.Drawing.RectangleF AreaDeBaixo(int k, float largura, float altura) =>
        new(largura - (LADO - BOLA) / 2 - CANTO_BAIXO - k * (CANTO_BAIXO + 8), altura - CANTO_BAIXO, CANTO_BAIXO, CANTO_BAIXO);

    /// <summary>
    /// Time, the camera and microphone dots, date and the corner icons, all centered on one line (the digits' middle,
    /// not the text boxes': a text box has room above and below the glyphs).
    /// </summary>
    private void DesenharCabecalho(ID2D1DeviceContext dc, Palco p, float largura, IReadOnlyList<ItemDoMenu> cantos)
    {
        var agora = DateTime.Now;
        _desenhado = agora.Minute;
        var cultura = Lang.Culture;
        const float HORA = 32, DATA = 13.5f;
        string hora = agora.ToString("HH:mm", cultura);
        var mh = p.Texto.Medir(hora, HORA, FontWeight.SemiBold, tabular: true);
        p.Texto.Desenhar(dc, hora, new Vector2(MARGEM, LINHA - Meio(HORA)), HORA, Cor.Branco, FontWeight.SemiBold, tabular: true);
        float x = MARGEM + mh.X + 10;
        // camera (green) and microphone (orange) in use: dots right after the time
        if (_indicadores is { Algum: true } ind)
        {
            const float PONTO = 8, VAO_PONTO = 5;
            if (ind.Microfone) { Icone.Circulo(dc, new Vector2(x + PONTO / 2, LINHA), PONTO / 2, Cor.Laranja); x += PONTO + VAO_PONTO; }
            if (ind.Camera) { Icone.Circulo(dc, new Vector2(x + PONTO / 2, LINHA), PONTO / 2, Cor.Verde); x += PONTO + VAO_PONTO; }
            x += 5;
        }
        float dataAte = largura - MARGEM - cantos.Count * (CANTO + 2) - 6;
        // the short weekday ("seg., 2 de novembro"): the long one got cut by the corner icons
        p.Texto.Desenhar(dc, agora.ToString(Lang.T("ddd, d 'de' MMMM", "ddd, MMMM d"), cultura), new Vector2(x, LINHA - Meio(DATA, minusculas: true)),
            DATA, Cor.Secundario, FontWeight.Normal, largura: MathF.Max(20, dataAte - x));
        for (int k = 0; k < cantos.Count; k++)
        {
            var r = AreaDoCanto(k, largura);
            var icone = new System.Drawing.RectangleF(r.X + (r.Width - CANTO_ICONE) / 2, LINHA - CANTO_ICONE / 2, CANTO_ICONE, CANTO_ICONE);
            try { cantos[k].Icone?.Invoke(dc, p, icone); }
            catch (Exception e) { Log.Escrever($"menu: ícone de {cantos[k].Id}: {e.Message}"); }
        }
    }

    /// <summary>
    /// From the top of a text box to the middle of its glyphs (Segoe UI Variable: baseline at 1.08 em; digits and capitals
    /// 0.70 em tall, lowercase 0.50 em, so a mostly lowercase line has its middle a bit lower).
    /// </summary>
    private static float Meio(float tamanho, bool minusculas = false) => tamanho * (1.08f - (minusculas ? 0.29f : 0.35f));

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

    /// <summary>The corner icons line up from the right edge (the first one at the edge); the area is what takes the click.</summary>
    private static System.Drawing.RectangleF AreaDoCanto(int k, float largura) =>
        new(largura - MARGEM - CANTO - k * (CANTO + 2), LINHA - CANTO / 2, CANTO, CANTO);

    /// <summary>The gray body: a ball for a 1x1 tile, a block with the balls' curve for a bigger one.</summary>
    internal static void Fundo(ID2D1DeviceContext dc, System.Drawing.RectangleF r, bool bola)
    {
        if (bola) { Icone.Circulo(dc, new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, FundoBloco); return; }
        Icone.Retangulo(dc, r.X, r.Y, r.Width, r.Height, MathF.Min(RAIO_BLOCO, MathF.Min(r.Width, r.Height) / 2), FundoBloco);
    }

    private static void DesenharPadrao(ID2D1DeviceContext dc, Palco p, ItemDoMenu item, System.Drawing.RectangleF r)
    {
        float ic = Math.Clamp(item.IconSize, 12, (BOLA - 10) / ESCALA_ICONE);
        // the icon in the middle of the ball (1x1) or of the block; the name under it
        var corpo = Corpo(r);
        var centro = new Vector2(corpo.X + corpo.Width / 2, corpo.Y + corpo.Height / 2);
        var area = new System.Drawing.RectangleF(centro.X - ic / 2, centro.Y - ic / 2, ic, ic);
        var antes = dc.Transform;
        dc.Transform = Matrix3x2.CreateScale(ESCALA_ICONE, centro) * antes;
        try { item.Icone?.Invoke(dc, p, area); }
        catch (Exception e) { Log.Escrever($"menu: ícone de {item.Id}: {e.Message}"); }
        finally { dc.Transform = antes; }
        if (item.Icone == null) Icone.Retangulo(dc, area.X + 3, area.Y + 3, ic - 6, ic - 6, 5, Cor.Fraco);
        string? detalhe = null;
        try { detalhe = item.Detalhe?.Invoke(); } catch { }
        float y = corpo.Bottom + 4;
        Centrado(dc, p, item.Nome, r, y, 12, Cor.Branco, FontWeight.Medium);
        if (detalhe == null) return;
        Vortice.Mathematics.Color4? amostra = null;
        try { amostra = item.DetailSwatch?.Invoke(); } catch { }
        Centrado(dc, p, detalhe, r, y + 14, 10.5f, Cor.Secundario, FontWeight.Normal, amostra);
    }

    private static void Centrado(ID2D1DeviceContext dc, Palco p, string texto, System.Drawing.RectangleF r, float y, float tam,
        Vortice.Mathematics.Color4 cor, FontWeight peso, Vortice.Mathematics.Color4? amostra = null)
    {
        const float BOLINHA = 7, ESPACO = 4;
        float extra = amostra == null ? 0 : BOLINHA + ESPACO;
        var m = p.Texto.Medir(texto, tam, peso);
        float l = MathF.Min(m.X, r.Width - 4 - extra);
        float x = r.X + (r.Width - l - extra) / 2;
        if (amostra is { } a)
        {
            var centro = new Vector2(x + BOLINHA / 2, y + m.Y / 2);
            Icone.Circulo(dc, centro, BOLINHA / 2, a);
            // a dark color on the black card still shows its edge
            using var b = dc.CreateSolidColorBrush(new Vortice.Mathematics.Color4(1, 1, 1, 0.35f));
            dc.DrawEllipse(new Ellipse(centro, BOLINHA / 2, BOLINHA / 2), b, 0.8f);
        }
        p.Texto.Desenhar(dc, texto, new Vector2(x + extra, y), tam, cor, peso, largura: r.Width - 4 - extra);
    }

    private static void Abrir(string id, Action abrir)
    {
        try { abrir(); }
        catch (Exception e) { Log.Escrever($"menu: abrir {id}: {e}"); }
    }

    public override void Dispose() => _minuto.Dispose();
}

/// <summary>
/// The tall iPhone slider of the menu (sound, brightness): fills white from the bottom up to the level (gray when it is
/// off, like muted), with the icon and the number near the bottom, dark over the fill and white over the empty part.
/// Dragged relative to where it was, so a tap changes nothing.
/// </summary>
internal sealed class MenuSlider
{
    private float _y, _start;
    private bool _on, _moved;

    /// <summary>A drag is going on (from the press to the release).</summary>
    public bool Ativo => _on;

    public static void Draw(ID2D1DeviceContext dc, Palco p, System.Drawing.RectangleF r, float level, bool dim,
        Action<ID2D1DeviceContext, Vector2, Vortice.Mathematics.Color4> icon, string text)
    {
        r = Menu.Corpo(r);
        float top = r.Bottom - r.Height * Math.Clamp(level, 0, 1);
        float radius = MathF.Min(Menu.RAIO_BLOCO, r.Width / 2);
        if (r.Bottom - top > 0.5f)
        {
            dc.PushAxisAlignedClip(new System.Drawing.RectangleF(r.X, top, r.Width, r.Bottom - top), AntialiasMode.PerPrimitive);
            Icone.Retangulo(dc, r.X, r.Y, r.Width, r.Height, radius, dim ? new Vortice.Mathematics.Color4(1, 1, 1, 0.45f) : Cor.Branco);
            Content(dc, p, r, new Vortice.Mathematics.Color4(0, 0, 0, 0.82f), icon, text);
            dc.PopAxisAlignedClip();
        }
        dc.PushAxisAlignedClip(new System.Drawing.RectangleF(r.X, r.Y, r.Width, top - r.Y), AntialiasMode.PerPrimitive);
        Content(dc, p, r, Cor.Branco, icon, text);
        dc.PopAxisAlignedClip();
    }

    private static void Content(ID2D1DeviceContext dc, Palco p, System.Drawing.RectangleF r, Vortice.Mathematics.Color4 color,
        Action<ID2D1DeviceContext, Vector2, Vortice.Mathematics.Color4> icon, string text)
    {
        const float ICON = 24, SIZE = 11f;
        icon(dc, new Vector2(r.X + (r.Width - ICON) / 2, r.Bottom - 56), color);
        var m = p.Texto.Medir(text, SIZE, FontWeight.Bold, tabular: true);
        p.Texto.Desenhar(dc, text, new Vector2(r.X + (r.Width - m.X) / 2, r.Bottom - 20 - SIZE * 0.73f), SIZE, color, FontWeight.Bold, tabular: true);
    }

    /// <summary>The new level for this drag position, or null when it should not change (a tap, or no move yet).</summary>
    public float? Drag(Vector2 pos, float height, bool released, float current)
    {
        if (!_on)
        {
            _on = true;
            _moved = false;
            _y = pos.Y;
            _start = current;
        }
        // the dead zone only guards a tap: once it moved, coming back to the starting point gives the starting level
        _moved |= MathF.Abs(pos.Y - _y) > 2;
        float level = Volume.Logica.Arrastado(_start, _y - pos.Y, height);
        if (released) _on = false;
        return _moved && MathF.Abs(level - current) > 0.001f ? level : null;
    }
}
