using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Windows.UI.Composition;

// não é Ilha.Modulos.Midia: o núcleo tem a classe estática Ilha.Modulos e o namespace colidiria com ela
namespace Ilha.Midia;

/// <summary>
/// O que a mídia mostra: bolha (capa), compacta (capa e visualizador) e cartão (capa grande, título, artista, barra
/// arrastável com os tempos, anterior/tocar/próxima). O módulo põe os dados aqui; a atividade só desenha, liga o
/// visualizador enquanto as barras estão na tela e tocando, e anda a barra 4x por segundo com o cartão aberto.
/// </summary>
internal sealed class AtividadeMidia : Atividade
{
    public override string Tipo => "midia";
    public override string Nome => Lang.T("Mídia tocando", "Now playing");

    private const float LARGURA = 340f;
    private const float Y_LINHA = 78f, A_LINHA = 26f;
    private const float Y_BOTOES = 106f, A_BOTOES = 30f;
    private const float MARGEM_BARRA = 4f;
    private const float BAIXA = 0.2f;
    // pílula: capa na ponta esquerda, visualizador na direita, meio vazio (o núcleo põe a hora ali)
    internal const float COMPACTA_L = 170f, CAPA_COMPACTA = 24f, FOLGA_MEIO = 6f;
    internal static float InicioDoVisualizador => COMPACTA_L - 4 * 5.5f + 2.5f;
    private static readonly Color4 CorPadrao = new(1f, 1f, 1f, 0.85f);

    // dados que o módulo põe
    private string _titulo = "", _artista = "";
    private Capa? _capa;
    private Color4 _acento = CorPadrao;
    public bool Tocando { get; private set; }
    private bool _podeAnterior, _podeProxima, _podeTocarPausar, _podeBuscar;
    public Linha Linha { get; private set; } = new();
    public bool VisualizadorLigado = true;
    public float PassoVolume = 0.02f;

    public Action? AoAnterior, AoTocarPausar, AoProxima;

    public override (float De, float Ate)? MeioLivreCompacta => MeioLivre();

    /// <summary>
    /// O vão entre a capa e o visualizador, encolhido por igual dos dois lados para a hora ficar no centro da pílula
    /// (a cena é centralizada nela).
    /// </summary>
    internal static (float De, float Ate) MeioLivre()
    {
        float meio = COMPACTA_L / 2;
        float metade = MathF.Min(meio - CAPA_COMPACTA - FOLGA_MEIO, InicioDoVisualizador - FOLGA_MEIO - meio);
        return (meio - metade, meio + metade);
    }
    public Action<TimeSpan>? AoBuscar;

    // cenas vivas
    private sealed class Barras
    {
        public required Cena Cena;
        public required SpriteVisual[] Visuais;
    }
    private readonly List<Barras> _barras = new();
    private readonly List<Superficie> _linhas = new();
    private ImplicitAnimationCollection? _implicitas;

    private readonly Loopback _loop = new();
    private readonly float[] _bandas = new float[Espectro.Bandas];
    private readonly System.Windows.Forms.Timer _tiqueBarras = new() { Interval = 33 };
    private readonly System.Windows.Forms.Timer _tiqueLinha = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _desligarVis = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _fimVolume = new() { Interval = 1300 };

    private float? _arraste;
    private int _restoRoda;
    private float? _volumeMostrado;

    public AtividadeMidia()
    {
        _tiqueBarras.Tick += (_, _) => AtualizarBarras();
        _tiqueLinha.Tick += (_, _) => RedesenharLinha();
        _desligarVis.Tick += (_, _) =>
        {
            _desligarVis.Stop();
            if (!DeveVisualizar) DesligarVisualizador();
        };
        _fimVolume.Tick += (_, _) =>
        {
            _fimVolume.Stop();
            _volumeMostrado = null;
            RedesenharLinha();
        };
    }

    // ------------------------------------------------------------------ o que o módulo chama

    public void Mostrar(bool ligada)
    {
        if (ligada) Ligar(); else Desligar();
    }

    public void Definir(string titulo, string artista, Capa? capa, Color4? cor)
    {
        _titulo = titulo;
        _artista = artista;
        _capa = capa;
        _acento = cor ?? CorPadrao;
        foreach (var b in _barras)
            foreach (var v in b.Visuais)
                if (v.Brush is CompositionColorBrush cb) cb.Color = Cor.Wr(_acento);
        Redesenhar();
    }

    public void DefinirEstado(bool tocando, bool podeAnterior, bool podeProxima, bool podeTocarPausar, bool podeBuscar)
    {
        bool mudou = tocando != Tocando || podeAnterior != _podeAnterior || podeProxima != _podeProxima
            || podeTocarPausar != _podeTocarPausar || podeBuscar != _podeBuscar;
        Tocando = tocando;
        _podeAnterior = podeAnterior;
        _podeProxima = podeProxima;
        _podeTocarPausar = podeTocarPausar;
        _podeBuscar = podeBuscar;
        if (!podeBuscar) _arraste = null;
        if (!mudou) return;
        if (!tocando) BaixarBarras();
        Redesenhar();
        Avaliar();
    }

    /// <summary>Sessão nova: linha nova. Mesma sessão: só redesenha (o módulo já mexeu nela).</summary>
    public void DefinirLinha(Linha l)
    {
        Linha = l;
        RedesenharLinha();
        Avaliar();
    }

    public void LinhaMudou()
    {
        RedesenharLinha();
        Avaliar();
    }

    public void ConfigMudou() => Avaliar();

    /// <summary>Volta da suspensão: a captura antiga pode ter morrido calada.</summary>
    public void ReiniciarVisualizador() => _loop.Reiniciar();

    public override void Dispose()
    {
        _tiqueBarras.Stop();
        _tiqueLinha.Stop();
        _desligarVis.Stop();
        _fimVolume.Stop();
        _loop.Desligar();
        _tiqueBarras.Dispose();
        _tiqueLinha.Dispose();
        _desligarVis.Dispose();
        _fimVolume.Dispose();
    }

    // ------------------------------------------------------------------ rodinha = volume

    public override bool Rolar(int delta)
    {
        _restoRoda += delta;
        int dentes = _restoRoda / 120;
        if (dentes == 0) return true;
        _restoRoda -= dentes * 120;
        float? novo;
        try { novo = VolumeDoSistema.Somar(dentes * PassoVolume); }
        catch (Exception e) { Log.Escrever("volume pela rodinha: " + e.Message); novo = null; }
        if (novo is { } n)
        {
            _volumeMostrado = n;
            _fimVolume.Stop();
            _fimVolume.Start();
            RedesenharLinha();
        }
        return true;
    }

    // ------------------------------------------------------------------ visualizador e relógio da barra

    private bool DeveVisualizar => VisualizadorLigado && Tocando && _barras.Count > 0;

    private void Avaliar()
    {
        if (DeveVisualizar)
        {
            _desligarVis.Stop();
            if (!_loop.Ligado) _loop.Ligar();
            if (!_tiqueBarras.Enabled) _tiqueBarras.Start();
        }
        else if (_loop.Ligado && !_desligarVis.Enabled)
        {
            // espera um pouco: troca de cena (compacta para cartão) e troca de faixa não derrubam a captura
            _desligarVis.Start();
        }
        bool andar = _linhas.Count > 0 && (Tocando || Linha.Segurando);
        if (andar != _tiqueLinha.Enabled) _tiqueLinha.Enabled = andar;
    }

    private void DesligarVisualizador()
    {
        _loop.Desligar();
        _tiqueBarras.Stop();
        BaixarBarras();
    }

    private void AtualizarBarras()
    {
        if (!Tocando || !_loop.Ligado) return;
        _loop.Ler(_bandas);
        foreach (var b in _barras)
            for (int i = 0; i < b.Visuais.Length; i++)
                b.Visuais[i].Scale = new Vector3(1, BAIXA + (1 - BAIXA) * _bandas[i % _bandas.Length], 1);
    }

    private void BaixarBarras()
    {
        foreach (var b in _barras)
            foreach (var v in b.Visuais)
                v.Scale = new Vector3(1, BAIXA, 1);
    }

    private void RedesenharLinha()
    {
        foreach (var s in _linhas.ToArray()) s.Redesenhar();
        if (_tiqueLinha.Enabled && !(Tocando || Linha.Segurando)) _tiqueLinha.Stop();
    }

    private ImplicitAnimationCollection Implicitas(Compositor c)
    {
        if (_implicitas != null) return _implicitas;
        // cada Scale novo (30 por segundo) é interpolado no compositor: a barra anda liso a 250 Hz sem o app redesenhar
        var a = c.CreateVector3KeyFrameAnimation();
        a.Target = "Scale";
        a.InsertExpressionKeyFrame(1f, "this.FinalValue");
        a.Duration = TimeSpan.FromMilliseconds(70);
        var col = c.CreateImplicitAnimationCollection();
        col["Scale"] = a;
        return _implicitas = col;
    }

    private void Visualizador(Palco p, Cena c, Vector2 pos, float altura)
    {
        var visuais = new SpriteVisual[Espectro.Bandas];
        for (int i = 0; i < visuais.Length; i++)
        {
            var v = p.Bloco(c, new Vector2(pos.X + i * 5.5f, pos.Y), new Vector2(3, altura), _acento);
            v.CenterPoint = new Vector3(v.Size.X / 2, v.Size.Y / 2, 0);
            v.Scale = new Vector3(1, BAIXA, 1);
            v.ImplicitAnimations = Implicitas(p.C);
            visuais[i] = v;
        }
        var b = new Barras { Cena = c, Visuais = visuais };
        _barras.Add(b);
        c.AoSoltar += () => { _barras.Remove(b); Avaliar(); };
        Avaliar();
    }

    // ------------------------------------------------------------------ cenas

    protected override Cena Montar(Palco p, Forma f)
    {
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(22, 22));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) => Capa.Desenhar(dc, p, _capa, 0, 0, 22, 6));
                return c;
            }
            case Forma.Compacta:
            {
                var c = p.NovaCena(new Vector2(COMPACTA_L, 24));
                p.Desenho(c, Vector2.Zero, new Vector2(CAPA_COMPACTA, 24), (dc, t) => Capa.Desenhar(dc, p, _capa, 1, 1, 22, 6));
                Visualizador(p, c, new Vector2(InicioDoVisualizador, 4), 16);
                return c;
            }
            default:
                return MontarCartao(p);
        }
    }

    private Cena MontarCartao(Palco p)
    {
        var c = p.NovaCena(new Vector2(LARGURA, Y_BOTOES + A_BOTOES));
        p.Desenho(c, Vector2.Zero, new Vector2(LARGURA, 72), (dc, t) =>
        {
            Capa.Desenhar(dc, p, _capa, 0, 0, 64, 14);
            string titulo = _titulo.Length > 0 ? _titulo : Lang.T("Sem título", "Untitled");
            p.Texto.Desenhar(dc, titulo, new Vector2(78, 7), 16, Cor.Branco, largura: LARGURA - 78 - 32);
            if (_artista.Length > 0)
                p.Texto.Desenhar(dc, _artista, new Vector2(78, 31), 13, Cor.Secundario, FontWeight.Normal, largura: LARGURA - 78);
        });
        Visualizador(p, c, new Vector2(LARGURA - 4 * 5.5f + 2.5f, 10), 18);

        var linha = p.Desenho(c, new Vector2(0, Y_LINHA), new Vector2(LARGURA, A_LINHA), (dc, t) => DesenharLinha(p, dc, t));
        _linhas.Add(linha);
        c.AoSoltar += () => { _linhas.Remove(linha); Avaliar(); };

        p.Desenho(c, new Vector2(0, Y_BOTOES), new Vector2(LARGURA, A_BOTOES), (dc, t) => DesenharBotoes(p, dc, t));

        c.Botoes.Add(new Botao { Area = new(0, Y_LINHA - 4, LARGURA, A_LINHA), Arrastar = Arrastou });
        float mx = LARGURA / 2;
        c.Botoes.Add(new Botao { Area = new(mx - 56 - 22, Y_BOTOES - 4, 44, A_BOTOES + 4), Acao = () => { if (_podeAnterior) AoAnterior?.Invoke(); } });
        c.Botoes.Add(new Botao { Area = new(mx - 22, Y_BOTOES - 4, 44, A_BOTOES + 4), Acao = () => { if (_podeTocarPausar) AoTocarPausar?.Invoke(); } });
        c.Botoes.Add(new Botao { Area = new(mx + 56 - 22, Y_BOTOES - 4, 44, A_BOTOES + 4), Acao = () => { if (_podeProxima) AoProxima?.Invoke(); } });
        Avaliar();
        return c;
    }

    private void Arrastou(float x, float y, bool soltou)
    {
        if (!_podeBuscar || !Linha.Atual.TemDuracao) { _arraste = null; return; }
        float f = Math.Clamp((x - MARGEM_BARRA) / (LARGURA - MARGEM_BARRA * 2), 0f, 1f);
        if (!soltou)
        {
            _arraste = f;
            RedesenharLinha();
            return;
        }
        _arraste = null;
        var i = Linha.Atual;
        var alvo = i.Inicio + TimeSpan.FromTicks((long)(i.Duracao.Ticks * (double)f));
        AoBuscar?.Invoke(alvo);
        RedesenharLinha();
        Avaliar();
    }

    private void DesenharLinha(Palco p, ID2D1DeviceContext dc, Vector2 t)
    {
        var i = Linha.Atual;
        var agora = DateTimeOffset.Now;
        float largura = t.X - MARGEM_BARRA * 2;
        if (i.TemDuracao)
        {
            float f = _arraste ?? (float)Linha.Fracao(agora);
            float esp = _arraste != null ? 6 : 4;
            float y = 5 - esp / 2;
            Icone.Retangulo(dc, MARGEM_BARRA, y, largura, esp, esp / 2, Cor.Fraco);
            if (f > 0) Icone.Retangulo(dc, MARGEM_BARRA, y, MathF.Max(esp, largura * f), esp, esp / 2, _podeBuscar ? Cor.Branco : Cor.Secundario);
            if (_arraste != null) Icone.Circulo(dc, new Vector2(MARGEM_BARRA + largura * f, 5), 5.5f, Cor.Branco);
            var pos = _arraste != null ? i.Inicio + TimeSpan.FromTicks((long)(i.Duracao.Ticks * (double)f)) : Linha.Posicao(agora);
            string esq = Rotulo.Tempo(pos - i.Inicio), dir = "-" + Rotulo.Tempo(i.Fim - pos);
            p.Texto.Desenhar(dc, esq, new Vector2(MARGEM_BARRA, 11), 11, Cor.Secundario, FontWeight.Normal, tabular: true);
            var td = p.Texto.Medir(dir, 11, FontWeight.Normal, true);
            p.Texto.Desenhar(dc, dir, new Vector2(t.X - MARGEM_BARRA - td.X, 11), 11, Cor.Secundario, FontWeight.Normal, tabular: true);
        }
        if (_volumeMostrado is { } v)
        {
            string tv = $"Volume {MathF.Round(v * 100):0}%";
            var tam = p.Texto.Medir(tv, 11, FontWeight.SemiBold, true);
            p.Texto.Desenhar(dc, tv, new Vector2((t.X - tam.X) / 2, 11), 11, Cor.Branco, FontWeight.SemiBold, tabular: true);
        }
    }

    private void DesenharBotoes(Palco p, ID2D1DeviceContext dc, Vector2 t)
    {
        var F = p.G.Fabrica;
        float cy = t.Y / 2, mx = t.X / 2;
        var apagado = new Color4(1f, 1f, 1f, 0.3f);
        var ca = _podeAnterior ? Cor.Branco : apagado;
        var cp = _podeTocarPausar ? Cor.Branco : apagado;
        var cn = _podeProxima ? Cor.Branco : apagado;
        // anterior
        float ax = mx - 56;
        Icone.Triangulo(dc, F, new Vector2(ax - 10, cy), new Vector2(ax, cy - 7), new Vector2(ax, cy + 7), ca);
        Icone.Triangulo(dc, F, new Vector2(ax, cy), new Vector2(ax + 10, cy - 7), new Vector2(ax + 10, cy + 7), ca);
        // tocar ou pausar
        if (Tocando)
        {
            Icone.Retangulo(dc, mx - 7, cy - 9, 5, 18, 1.5f, cp);
            Icone.Retangulo(dc, mx + 2, cy - 9, 5, 18, 1.5f, cp);
        }
        else Icone.Triangulo(dc, F, new Vector2(mx - 6, cy - 10), new Vector2(mx + 9, cy), new Vector2(mx - 6, cy + 10), cp);
        // próxima
        float px = mx + 56;
        Icone.Triangulo(dc, F, new Vector2(px + 10, cy), new Vector2(px, cy - 7), new Vector2(px, cy + 7), cn);
        Icone.Triangulo(dc, F, new Vector2(px, cy), new Vector2(px - 10, cy - 7), new Vector2(px - 10, cy + 7), cn);
    }
}
