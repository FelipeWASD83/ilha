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
        var itens = _menu.Itens;
        _quantos = itens.Count;
        int linhas = (int)Math.Ceiling(itens.Count / COLUNAS);
        float largura = COLUNAS * LADO + (COLUNAS - 1) * VAO;
        float altura = CABECALHO + (linhas == 0 ? 20 : linhas * ALTO + (linhas - 1) * VAO);
        var c = p.NovaCena(new Vector2(largura, altura));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var agora = DateTime.Now;
            _desenhado = agora.Minute;
            var cultura = new System.Globalization.CultureInfo("pt-BR");
            p.Texto.Desenhar(dc, agora.ToString("HH:mm", cultura), new Vector2(-1, -6), 38, Cor.Branco, FontWeight.Medium, tabular: true);
            p.Texto.Desenhar(dc, agora.ToString("dddd, d 'de' MMMM", cultura), new Vector2(112, 6), 14, Cor.Secundario, FontWeight.Normal, largura: largura - 112);
            if (itens.Count == 0)
            {
                p.Texto.Desenhar(dc, "Nenhum módulo com entrada no menu.", new Vector2(0, CABECALHO), 12, Cor.Secundario, FontWeight.Normal);
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
        return c;
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
