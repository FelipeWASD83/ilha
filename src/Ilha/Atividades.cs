using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct2D1;
using Windows.UI.Composition;

namespace Ilha;

internal enum Forma { Compacta, Bolha, Expandida, Alerta }

/// <summary>Área clicável dentro de uma cena, em DIP a partir do canto de cima da cena.</summary>
internal sealed class Botao
{
    public System.Drawing.RectangleF Area;
    public Action Acao = () => { };
    /// <summary>
    /// Arrastável (barra de progresso, miniatura): recebe a posição em DIP na cena ao apertar, a cada movimento e ao
    /// soltar (último argumento true). Com Arrastar, o clique não chama Acao.
    /// </summary>
    public Action<float, float, bool>? Arrastar;
}

/// <summary>
/// O que uma atividade mostra numa forma: uma árvore de visuais do compositor, do tamanho dado. A ilha põe a
/// cena dentro da pílula e cuida da entrada e da saída (fade, escala e desfoque).
/// </summary>
internal sealed class Cena
{
    public required ContainerVisual Raiz;
    public required Vector2 Tamanho;
    public List<Botao> Botoes { get; } = new();
    internal readonly List<Superficie> Superficies = new();
    internal Action? AoSoltar;

    public void Soltar()
    {
        AoSoltar?.Invoke();
        // fecha já a memória nativa (superfícies, pincéis, visuais) em vez de esperar o coletor de lixo
        foreach (var s in Superficies) s.Dispose();
        Superficies.Clear();
        foreach (var v in System.Linq.Enumerable.ToList(Raiz.Children))
        {
            if (v is SpriteVisual sv) sv.Brush?.Dispose();
            v.Dispose();
        }
        Raiz.Dispose();
    }
}

/// <summary>Ajuda as atividades a montar cenas: tudo medido em DIP, convertido para pixel aqui.</summary>
internal sealed class Palco
{
    public Grafico G { get; }
    public Escrita Texto { get; }
    public Compositor C => G.Compositor;
    public float Escala => G.Escala;

    public Faixas Faixas { get; }

    public Palco(Grafico g)
    {
        G = g;
        Texto = new Escrita(g);
        Faixas = new Faixas(g);
    }

    /// <summary>Uma logo animada dentro da cena (pos e lado em DIP). Para de encadear quando a cena some.</summary>
    public LogoAnimada Logo(Cena cena, Vector2 pos, float lado)
    {
        var l = new LogoAnimada(this, lado);
        l.Visual.Offset = new Vector3(Px(pos.X), Px(pos.Y), 0);
        cena.Raiz.Children.InsertAtTop(l.Visual);
        cena.AoSoltar += l.Soltar;
        return l;
    }

    public float Px(float dip) => MathF.Round(dip * Escala);
    public Vector2 Px(Vector2 dip) => new(Px(dip.X), Px(dip.Y));

    public Cena NovaCena(Vector2 tamanhoDip)
    {
        var raiz = C.CreateContainerVisual();
        raiz.Size = Px(tamanhoDip);
        return new Cena { Raiz = raiz, Tamanho = tamanhoDip };
    }

    /// <summary>Um pedaço desenhado em Direct2D dentro da cena, na posição dada (DIP).</summary>
    public Superficie Desenho(Cena cena, Vector2 pos, Vector2 tamanho, Action<ID2D1DeviceContext, Vector2> desenho)
    {
        var s = G.NovaSuperficie(tamanho, desenho);
        var v = C.CreateSpriteVisual();
        v.Size = s.TamanhoPx;
        v.Offset = new Vector3(Px(pos.X), Px(pos.Y), 0);
        v.Brush = s.Pincel;
        cena.Raiz.Children.InsertAtTop(v);
        cena.Superficies.Add(s);
        return s;
    }

    public SpriteVisual Bloco(Cena cena, Vector2 pos, Vector2 tamanho, Vortice.Mathematics.Color4 cor)
    {
        var v = C.CreateSpriteVisual();
        v.Size = Px(tamanho);
        v.Offset = new Vector3(Px(pos.X), Px(pos.Y), 0);
        v.Brush = C.CreateColorBrush(Cor.Wr(cor));
        cena.Raiz.Children.InsertAtTop(v);
        return v;
    }
}

/// <summary>Um evento pontual que abre a ilha por alguns segundos e volta.</summary>
internal sealed class Alerta
{
    public required string Tipo;
    public required Func<Palco, Cena> Criar;
    public bool PermitidoNoCompetitivo;
    // passa pelo Não Incomodar (alarme de timer, como o próprio Windows deixa)
    public bool Importante;
    public float? Segundos;
    public Atividade? Dono;
    // som que toca quando o alerta aparece (null = o próprio Tipo; "" = sem som)
    public string? Som;
}

/// <summary>
/// Uma coisa em andamento que a ilha mostra (mídia, timer, Claude Code...). Cada forma pede uma cena nova;
/// quando o conteúdo muda sem mudar de tamanho, a atividade só redesenha as superfícies das cenas vivas.
/// </summary>
internal abstract class Atividade
{
    public abstract string Tipo { get; }
    public abstract string Nome { get; }
    public virtual bool PermitidaNoCompetitivo => false;
    public virtual bool TemExpandida => true;
    /// <summary>Ferramenta (calculadora, tradutor, IA): abre expandida pelo atalho mesmo sem estar "em andamento".</summary>
    public virtual bool EhFerramenta => false;
    /// <summary>Largura mínima da pílula compacta (null = a padrão); a de privacidade é só as bolinhas.</summary>
    public virtual float? LarguraCompactaMinima => null;
    /// <summary>Quando liga, aparece expandindo por um instante (o volume e a privacidade só aparecem).</summary>
    public virtual bool AnunciaChegada => TemExpandida;
    /// <summary>Rodinha do mouse em cima da ilha com esta atividade na frente (delta 120 por dente; true = tratou).</summary>
    public virtual bool Rolar(int delta) => false;
    public bool Ativa { get; private set; }
    public DateTime Desde { get; private set; }

    /// <summary>Ligou, desligou ou mudou de tamanho: a ilha refaz o layout.</summary>
    public event Action<Atividade>? Mudou;
    public event Action<Alerta>? Alertou;

    private readonly List<Cena> _vivas = new();

    protected void Ligar()
    {
        if (Ativa) return;
        Ativa = true;
        Desde = DateTime.Now;
        Mudou?.Invoke(this);
    }

    protected void Desligar()
    {
        if (!Ativa) return;
        Ativa = false;
        Mudou?.Invoke(this);
    }

    /// <summary>Sobe quando a cena precisa ser refeita (tamanho ou layout mudou).</summary>
    public int Versao { get; private set; }

    protected void MudouTamanho() { Versao++; Mudou?.Invoke(this); }
    protected void Alertar(Alerta a) { a.Dono = this; Alertou?.Invoke(a); }

    public Cena Criar(Palco p, Forma f)
    {
        var c = Montar(p, f);
        _vivas.Add(c);
        c.AoSoltar += () => _vivas.Remove(c);
        return c;
    }

    protected abstract Cena Montar(Palco p, Forma f);

    protected bool TemCenaViva => _vivas.Count > 0;

    /// <summary>Redesenha o que as cenas vivas mostram (o relógio andou, a música trocou...).</summary>
    protected void Redesenhar()
    {
        foreach (var c in _vivas.ToArray())
            foreach (var s in c.Superficies) s.Redesenhar();
    }

    public virtual void Dispose() { }
}
