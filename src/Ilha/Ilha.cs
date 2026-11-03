using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace Ilha;

internal enum Estado { Oculta, Ociosa, Compacta, Expandida, Alerta }

/// <summary>
/// A pílula: forma, molas, conteúdo e mouse. Tudo que anima é animação do compositor (roda no DWM, na taxa
/// do monitor); este código só decide o alvo, troca as cenas e ajusta a janela e a região de toque.
/// </summary>
internal sealed class Ilha : IDisposable
{
    // medidas em DIP (multiplicadas pela escala do monitor e pelo "tamanho" da config)
    // ALTURA_MAX bounds the shape visuals: a card taller than it lost the bottom of its black body (the chat card at 380
    // DIP did, on 01/11/2026); the window itself still follows the content. 880 since 3.9.0: the menu got a sixth row
    // (weather and the private server, back then) and its last row lost the black body at 640
    private const float LARGURA_JANELA = 760, ALTURA_MAX = 880;
    private const float OCIOSA_L = 128, OCIOSA_A = 32;
    private const float COMPACTA_A = 36, COMPACTA_FOLGA = 12, COMPACTA_MIN = 168;
    private const float PREVIA_L = 14, PREVIA_A = 4;
    private const float BOLHA = 36, VAO = 14;
    private const float CARTAO_FOLGA = 18, CARTAO_MIN = 340, RAIO_EXPANDIDA = 38, RAIO_ALERTA = 28;
    private const float OMBRO = 7;
    // room under the shape for the wide shadow layer (offset + 3 sigma)
    private const float SOMBRA = 56;
    private const float PONTO = 7, PONTO_VAO = 5;

    private readonly Grafico _g;
    private readonly Palco _p;
    private readonly JanelaVisual _vis;
    private readonly JanelaToque _toque;
    private Config _cfg;
    private DesktopWindowTarget? _alvo;
    private CompositionEffectFactory? _desfoque;

    // árvore
    private ContainerVisual _raiz = null!;
    private ContainerVisual _camada = null!;
    private CompositionRoundedRectangleGeometry _geoCorpo = null!, _geoBolha = null!, _geoBolhaE = null!;
    // the left bubble (3.7.0, his request): a third activity, like the iPhone 18 Pro's island
    private ShapeVisual _formaBolha = null!, _formaBolhaE = null!;
    private readonly List<SpriteVisual> _ombros = new();
    private readonly List<Superficie> _fixas = new();
    private Vaga _vagaCorpo = null!, _vagaBolha = null!, _vagaBolhaE = null!;

    // the column of balls at the right of the open menu (4.0.0, his request): every activity running, one ball each,
    // each with its own shape, shadow, border, clip and click, like the bubble next to the pill
    private const int COLUNA_MAX = 6;
    private const float COLUNA_VAO = 10;
    private sealed class BolaDaColuna
    {
        public required CompositionRoundedRectangleGeometry Geo;
        public required ShapeVisual Forma;
        public Vaga Vaga = null!;
    }
    private readonly List<BolaDaColuna> _coluna = new();
    private List<Atividade> _naColuna = new();
    private readonly List<System.Drawing.RectangleF> _rColuna = new();

    // situação
    private Contexto _ctx = new();
    private readonly List<Atividade> _atividades = new();
    private readonly Queue<Alerta> _fila = new();
    // every change of the alert in front goes through here: the one leaving stops its looping sound
    private Alerta? _alertaAtual;
    private Alerta? _alerta
    {
        get => _alertaAtual;
        set
        {
            if (ReferenceEquals(_alertaAtual, value)) return;
            _alertaAtual?.PararSom();
            _alertaAtual = value;
        }
    }
    private readonly System.Windows.Forms.Timer _relogioAlerta = new();
    private readonly System.Windows.Forms.Timer _relogioSaida = new() { Interval = 320 };
    private readonly System.Windows.Forms.Timer _vigiaClique = new() { Interval = 35 };
    private bool _sobre, _expandida, _porAtalho;
    private Atividade? _foco;
    private Atividade? _painel;
    private Indicadores? _indicadores;
    private ContainerVisual _pontos = null!;
    private SpriteVisual _pontoCamera = null!, _pontoMic = null!, _pontoGravando = null!;
    private bool _pontosVisiveis;
    private float _extraPontos;

    // borda clara: aparece só com fundo escuro em volta (a sonda lê poucas vezes por segundo, só com a ilha visível)
    private ShapeVisual? _borda, _bordaBolha, _bordaBolhaE;
    private readonly SondaDeFundo _sonda = new();
    private int _sondaFalhas;
    private readonly System.Windows.Forms.Timer _relogioSonda = new() { Interval = 500 };
    private bool _bordaLigada;
    private long _ultimoLogDaSonda;

    // hora no meio da compacta: as superfícies vivas, redesenhadas quando o minuto vira
    private readonly List<Superficie> _relogios = new();
    private readonly System.Windows.Forms.Timer _minutoRelogio = new() { Interval = 5_000 };
    private int _minutoDesenhado = -1;

    // o mouse: posição do cursor quando a região mudou (movimento "falso" do Windows nesse ponto não conta como entrar)
    private Nativo.POINT? _cursorNaMudanca;
    private readonly System.Windows.Forms.Timer _vigiaSobre = new() { Interval = 150 };
    private System.Drawing.Rectangle _regiaoAplicada;
    // região que espera a mola crescer: um relógio próprio aplica (o fim do lote de animação se perdia quando outro
    // Aplicar com o mesmo plano chegava no meio, e a região do cartão nunca crescia)
    private Action? _regiaoPendente;
    private readonly System.Windows.Forms.Timer _relogioRegiao = new() { Interval = 380 };
    private AlvoDeSoltar? _alvoDeSoltar;

    /// <summary>O campo de texto da ilha (o núcleo usa para o Esc: a ilha só ouve o teclado depois de um clique nela).</summary>
    public Entrada? Entrada { get; set; }

    /// <summary>Quem recebe arquivos soltos em cima da ilha (a prateleira).</summary>
    public IReceptorDeArquivos? Receptor
    {
        get => _receptor;
        set
        {
            _receptor = value;
            if (value != null) _vigiaArraste.Start();
            else { _vigiaArraste.Stop(); _zonaDeSoltar = false; }
        }
    }
    private IReceptorDeArquivos? _receptor;

    // zona de soltar: com a ilha escondida não existe pílula para receber o arquivo. Com o botão segurado perto do topo
    // do meio da tela, a pílula vazia aparece para o arraste cair nela (só enquanto o botão estiver apertado)
    private readonly System.Windows.Forms.Timer _vigiaArraste = new() { Interval = 125 };
    // conteúdo em tela cheia esconde a ilha; o mouse na borda de cima, perto do meio, traz de volta enquanto estiver perto
    private readonly System.Windows.Forms.Timer _vigiaTopo = new() { Interval = 150 };
    private bool _revelada;
    private long _longeDesde;
    private bool _zonaDeSoltar;
    private long _apertadoDesde;

    /// <summary>Toca o som de cada alerta e chegada (null = sem som).</summary>
    public Sons? Sons { get; set; }

    /// <summary>Câmera e microfone em uso: a ilha desenha as bolinhas verde e laranja no canto da pílula.</summary>
    public Indicadores? Indicadores
    {
        get => _indicadores;
        set
        {
            _indicadores = value;
            if (value != null) value.Mudou += () =>
            {
                // the ring around the time is drawn in the clock surfaces
                foreach (var s in _relogios.ToArray()) s.Redesenhar();
                Aplicar();
            };
        }
    }
    private Estado _estado = Estado.Oculta;
    private bool _janelasVisiveis;

    // geometria atual em pixels, relativa à janela
    private int _janX, _janY, _janL, _janA;
    private System.Drawing.RectangleF _rCorpo, _rBolha, _rBolhaE;
    private bool _temBolha, _temBolhaE;
    private float _alturaAlvoPx;
    private int _geracao;

    public event Action<Estado>? EstadoMudou;
    public Estado EstadoAtual => _estado;
    public IntPtr JanelaVisual => _vis.Handle;

    // ---- para o --teste-mouse
    public IntPtr HandleDoToque => _toque.Handle;
    public float Escala => _g.Escala;
    public System.Drawing.RectangleF CorpoNaTela => new(_rCorpo.X + _janX, _rCorpo.Y + _janY, _rCorpo.Width, _rCorpo.Height);
    public string Diagnostico => $"estado={_estado} sobre={_sobre} alerta={(_alerta != null)} expandida={_expandida} porAtalho={_porAtalho} corpo={CorpoNaTela}";
    public void TesteClicarNoCorpo()
    {
        int x = (int)(_rCorpo.Left + _rCorpo.Width / 2), y = (int)(_rCorpo.Top + _rCorpo.Height / 2);
        Apertou(x, y);
        Clicou(x, y);
    }

    private sealed class Vaga
    {
        public required ContainerVisual Host;
        public Quadro? Atual;
        public string? Chave;
    }

    private sealed class Quadro
    {
        public required SpriteVisual Sprite;
        public required CompositionVisualSurface Vs;
        public required CompositionSurfaceBrush Pincel;
        public required Cena Cena;
        public CompositionEffectBrush? Efeito;
        public bool Saindo, Limpo;
    }

    public Ilha(Grafico g, Config cfg, JanelaVisual vis, JanelaToque toque)
    {
        _g = g;
        _p = new Palco(g);
        _cfg = cfg;
        _vis = vis;
        _toque = toque;

        _toque.Moveu += (x, y) => { _mouse = (x, y); Entrou(); };
        _toque.Saiu += Saiu;
        _toque.Clicou += Clicou;
        _toque.Apertou += Apertou;
        _toque.Arrastando += (x, y) =>
        {
            if (_arraste == null) return;
            var l = Local(_vagaCorpo, x, y);
            if (Vector2.Distance(l, _inicioArraste) > 4) _arrastou = true;
            _arraste(l.X, l.Y, false);
        };
        _toque.Rolou += Rolou;
        _relogioAlerta.Tick += (_, _) => { _relogioAlerta.Stop(); FimDoAlerta(); };
        _relogioSaida.Interval = Math.Max(1, cfg.AtrasoRecolherMs);
        _relogioSaida.Tick += (_, _) => { _relogioSaida.Stop(); if (!_sobre && _expandida && !_porAtalho) Recolher(); };
        _vigiaClique.Tick += (_, _) => VigiarCliqueFora();
        _relogioSonda.Tick += (_, _) => Sondar();
        _minutoRelogio.Tick += (_, _) =>
        {
            if (_relogios.Count == 0) { _minutoRelogio.Stop(); return; }
            if (DateTime.Now.Minute == _minutoDesenhado) return;
            foreach (var s in _relogios.ToArray()) s.Redesenhar();
        };
        _vigiaSobre.Tick += (_, _) => VigiarSobre();
        _relogioRegiao.Tick += (_, _) => { _relogioRegiao.Stop(); var r = _regiaoPendente; _regiaoPendente = null; r?.Invoke(); };
        _vigiaArraste.Tick += (_, _) => VigiarArraste();
        _vigiaTopo.Tick += (_, _) => VigiarTopo();
        _alvoDeSoltar = AlvoDeSoltar.Registrar(_toque.Handle, () => Receptor);

        Reposicionar(forcar: true);
    }

    public Palco Palco => _p;

    /// <summary>
    /// Set by a module that needs the island to keep private text off the screen (a lock that is closed, for example).
    /// While it returns true, modules that show other people's text (notifications) show only that something arrived.
    /// </summary>
    public static Func<bool>? Discreto { get; set; }

    /// <summary>The active focus mode's rule for alerts (Modulos/Foco): true = this alert does not show.</summary>
    public static Func<Alerta, bool>? FiltroDoFoco { get; set; }

    private static bool FocoBarra(Alerta a)
    {
        try { return FiltroDoFoco?.Invoke(a) == true; }
        catch (Exception) { return false; }
    }

    /// <summary>The discreet hook says so; a hook that throws counts as discreet.</summary>
    public static bool Discreta
    {
        get
        {
            try { return Discreto?.Invoke() == true; }
            catch { return true; }
        }
    }

    // ------------------------------------------------------------------ montagem

    private void Montar()
    {
        var c = _g.Compositor;
        _alvo ??= _g.Alvo(_vis.Handle);
        _desfoque ??= c.CreateEffectFactory(new Desfoque(new CompositionEffectSourceParameter("src")), new[] { "Blur.BlurAmount" });

        float L = _p.Px(LARGURA_JANELA), A = _p.Px(ALTURA_MAX);
        float cx = MathF.Round(L / 2);
        bool colada = _cfg.Colada;
        float topo = colada ? 0 : _p.Px(_cfg.DistanciaDoTopo);

        _raiz = c.CreateContainerVisual();
        _raiz.Size = new Vector2(L, A);
        _alvo.Root = _raiz;

        _camada = c.CreateContainerVisual();
        _camada.Size = new Vector2(L, A);
        _raiz.Children.InsertAtTop(_camada);

        var preto = c.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));

        _geoCorpo = c.CreateRoundedRectangleGeometry();
        _geoCorpo.Size = _p.Px(new Vector2(OCIOSA_L * 0.6f, OCIOSA_A * 0.6f));
        _geoCorpo.CornerRadius = _p.Px(new Vector2(OCIOSA_A * 0.3f));
        var formaCorpo = c.CreateShapeVisual();
        formaCorpo.Size = new Vector2(L, A);
        var sc = c.CreateSpriteShape(_geoCorpo);
        sc.FillBrush = preto;
        formaCorpo.Shapes.Add(sc);
        _camada.Children.InsertAtTop(formaCorpo);

        _geoBolha = c.CreateRoundedRectangleGeometry();
        _geoBolha.Size = _p.Px(new Vector2(BOLHA * 0.5f));
        _geoBolha.CornerRadius = _p.Px(new Vector2(BOLHA * 0.25f));
        _formaBolha = c.CreateShapeVisual();
        _formaBolha.Size = new Vector2(L, A);
        _formaBolha.Opacity = 0;
        var sb = c.CreateSpriteShape(_geoBolha);
        sb.FillBrush = preto;
        _formaBolha.Shapes.Add(sb);
        _camada.Children.InsertAtTop(_formaBolha);

        _geoBolhaE = c.CreateRoundedRectangleGeometry();
        _geoBolhaE.Size = _p.Px(new Vector2(BOLHA * 0.5f));
        _geoBolhaE.CornerRadius = _p.Px(new Vector2(BOLHA * 0.25f));
        _formaBolhaE = c.CreateShapeVisual();
        _formaBolhaE.Size = new Vector2(L, A);
        _formaBolhaE.Opacity = 0;
        var sbe = c.CreateSpriteShape(_geoBolhaE);
        sbe.FillBrush = preto;
        _formaBolhaE.Shapes.Add(sbe);
        _camada.Children.InsertAtTop(_formaBolhaE);

        // centraliza a pílula e cola o topo dela acima da borda (os cantos de cima ficam fora da tela)
        float g = colada ? 1 : 0;
        var centro = c.CreateExpressionAnimation("Vector2(cx - this.Target.Size.X / 2, topo - this.Target.CornerRadius.Y * g)");
        centro.SetScalarParameter("cx", cx);
        centro.SetScalarParameter("topo", topo);
        centro.SetScalarParameter("g", g);
        _geoCorpo.StartAnimation("Offset", centro);

        float vao = _p.Px(VAO);
        var lado = c.CreateExpressionAnimation("Vector2(cx + corpo.Size.X / 2 + vao, topo - this.Target.CornerRadius.Y * g)");
        lado.SetScalarParameter("cx", cx);
        lado.SetScalarParameter("vao", vao);
        lado.SetScalarParameter("topo", topo);
        lado.SetScalarParameter("g", g);
        lado.SetReferenceParameter("corpo", _geoCorpo);
        _geoBolha.StartAnimation("Offset", lado);
        var ladoE = c.CreateExpressionAnimation("Vector2(cx - corpo.Size.X / 2 - vao - this.Target.Size.X, topo - this.Target.CornerRadius.Y * g)");
        ladoE.SetScalarParameter("cx", cx);
        ladoE.SetScalarParameter("vao", vao);
        ladoE.SetScalarParameter("topo", topo);
        ladoE.SetScalarParameter("g", g);
        ladoE.SetReferenceParameter("corpo", _geoCorpo);
        _geoBolhaE.StartAnimation("Offset", ladoE);

        _coluna.Clear();
        float bolhaCheia = _p.Px(BOLHA), passo = _p.Px(BOLHA + COLUNA_VAO);
        for (int k = 0; k < COLUNA_MAX; k++)
        {
            var geo = c.CreateRoundedRectangleGeometry();
            geo.Size = _p.Px(new Vector2(BOLHA * 0.4f));
            geo.CornerRadius = _p.Px(new Vector2(BOLHA * 0.2f));
            // grows from its own center: each ball pops in place, down the right side of the card
            var off = c.CreateExpressionAnimation("Vector2(cx + corpo.Size.X / 2 + vao + (b - this.Target.Size.X) / 2, topo + k * passo + (b - this.Target.Size.Y) / 2)");
            off.SetScalarParameter("cx", cx);
            off.SetScalarParameter("vao", vao);
            off.SetScalarParameter("topo", topo);
            off.SetScalarParameter("b", bolhaCheia);
            off.SetScalarParameter("passo", passo);
            off.SetScalarParameter("k", k);
            off.SetReferenceParameter("corpo", _geoCorpo);
            geo.StartAnimation("Offset", off);
            var forma = c.CreateShapeVisual();
            forma.Size = new Vector2(L, A);
            forma.Opacity = 0;
            var s = c.CreateSpriteShape(geo);
            s.FillBrush = preto;
            forma.Shapes.Add(s);
            _camada.Children.InsertAtTop(forma);
            _coluna.Add(new BolaDaColuna { Geo = geo, Forma = forma });
        }

        if (_cfg.Sombra) Sombras(c);
        _borda = _bordaBolha = _bordaBolhaE = null;
        if (_cfg.BordaAdaptativa && !colada) Borda(c, L, A);
        // os "ombros": curvas côncavas pretas que emendam a pílula na borda da tela, como se ela saísse dela
        _ombros.Clear();
        if (colada)
        {
            float o = _p.Px(OMBRO);
            var esq = _g.NovaSuperficie(new Vector2(OMBRO), (dc, t) => Ombro(dc, t.X, esquerdo: true));
            var dir = _g.NovaSuperficie(new Vector2(OMBRO), (dc, t) => Ombro(dc, t.X, esquerdo: false));
            _fixas.Add(esq);
            _fixas.Add(dir);
            string[] posicoes =
            {
                "Vector3(cx - corpo.Size.X / 2 - o, 0, 0)",
                "Vector3(cx + corpo.Size.X / 2, 0, 0)",
                "Vector3(cx + corpo.Size.X / 2 + vao - o, 0, 0)",
                "Vector3(cx + corpo.Size.X / 2 + vao + bolha.Size.X, 0, 0)",
                "Vector3(cx - corpo.Size.X / 2 - vao - bolhaE.Size.X - o, 0, 0)",
                "Vector3(cx - corpo.Size.X / 2 - vao, 0, 0)",
            };
            for (int i = 0; i < 6; i++)
            {
                var v = c.CreateSpriteVisual();
                v.Size = esq.TamanhoPx;
                v.Brush = i % 2 == 0 ? esq.Pincel : dir.Pincel;
                var e = c.CreateExpressionAnimation(posicoes[i]);
                e.SetScalarParameter("cx", cx);
                e.SetScalarParameter("o", o);
                e.SetScalarParameter("vao", vao);
                e.SetReferenceParameter("corpo", _geoCorpo);
                e.SetReferenceParameter("bolha", _geoBolha);
                e.SetReferenceParameter("bolhaE", _geoBolhaE);
                v.StartAnimation("Offset", e);
                if (i >= 2)
                {
                    var op = c.CreateExpressionAnimation("fb.Opacity");
                    op.SetReferenceParameter("fb", i >= 4 ? _formaBolhaE : _formaBolha);
                    v.StartAnimation("Opacity", op);
                }
                _camada.Children.InsertAtTop(v);
                _ombros.Add(v);
            }
        }

        var hostCorpo = c.CreateContainerVisual();
        hostCorpo.Offset = new Vector3(cx, topo, 0);
        Recortado(c, _geoCorpo, L, A).Children.InsertAtTop(hostCorpo);
        var hostBolha = c.CreateContainerVisual();
        var pos = c.CreateExpressionAnimation("Vector3(cx + corpo.Size.X / 2 + vao + bolha.Size.X / 2, topo, 0)");
        pos.SetScalarParameter("cx", cx);
        pos.SetScalarParameter("vao", vao);
        pos.SetScalarParameter("topo", topo);
        pos.SetReferenceParameter("corpo", _geoCorpo);
        pos.SetReferenceParameter("bolha", _geoBolha);
        hostBolha.StartAnimation("Offset", pos);
        var op2 = c.CreateExpressionAnimation("fb.Opacity");
        op2.SetReferenceParameter("fb", _formaBolha);
        hostBolha.StartAnimation("Opacity", op2);
        Recortado(c, _geoBolha, L, A).Children.InsertAtTop(hostBolha);
        var hostBolhaE = c.CreateContainerVisual();
        var posE = c.CreateExpressionAnimation("Vector3(cx - corpo.Size.X / 2 - vao - bolha.Size.X / 2, topo, 0)");
        posE.SetScalarParameter("cx", cx);
        posE.SetScalarParameter("vao", vao);
        posE.SetScalarParameter("topo", topo);
        posE.SetReferenceParameter("corpo", _geoCorpo);
        posE.SetReferenceParameter("bolha", _geoBolhaE);
        hostBolhaE.StartAnimation("Offset", posE);
        var op3 = c.CreateExpressionAnimation("fb.Opacity");
        op3.SetReferenceParameter("fb", _formaBolhaE);
        hostBolhaE.StartAnimation("Opacity", op3);
        Recortado(c, _geoBolhaE, L, A).Children.InsertAtTop(hostBolhaE);

        _vagaCorpo = new Vaga { Host = hostCorpo };
        _vagaBolha = new Vaga { Host = hostBolha };
        _vagaBolhaE = new Vaga { Host = hostBolhaE };
        for (int k = 0; k < _coluna.Count; k++)
        {
            var bola = _coluna[k];
            var host = c.CreateContainerVisual();
            var hp = c.CreateExpressionAnimation("Vector3(cx + corpo.Size.X / 2 + vao + b / 2, topo + k * passo, 0)");
            hp.SetScalarParameter("cx", cx);
            hp.SetScalarParameter("vao", vao);
            hp.SetScalarParameter("topo", topo);
            hp.SetScalarParameter("b", bolhaCheia);
            hp.SetScalarParameter("passo", passo);
            hp.SetScalarParameter("k", k);
            hp.SetReferenceParameter("corpo", _geoCorpo);
            host.StartAnimation("Offset", hp);
            var ho = c.CreateExpressionAnimation("fb.Opacity");
            ho.SetReferenceParameter("fb", bola.Forma);
            host.StartAnimation("Opacity", ho);
            Recortado(c, bola.Geo, L, A).Children.InsertAtTop(host);
            bola.Vaga = new Vaga { Host = host };
        }

        // bolinhas de câmera (verde) e microfone (laranja), como as do iPhone
        _pontos = c.CreateContainerVisual();
        _pontos.Opacity = 0;
        _pontoCamera = Ponto(c, Cor.Verde);
        _pontoMic = Ponto(c, Cor.Laranja);
        _pontoGravando = Ponto(c, Cor.Vermelho);
        _pontos.Children.InsertAtTop(_pontoCamera);
        _pontos.Children.InsertAtTop(_pontoMic);
        _pontos.Children.InsertAtTop(_pontoGravando);
        _raiz.Children.InsertAtTop(_pontos);
        // começa escondida acima da tela
        _raiz.Offset = new Vector3(0, -_p.Px(ALTURA_MAX), 0);
    }

    /// <summary>
    /// Sombra em duas camadas, como a da Apple: uma curta e mais firme rente à forma e outra larga, difusa e
    /// puxada para baixo. Cada uma é um desfoque gaussiano de verdade, desenhado uma vez e esticado em nove
    /// partes atrás da pílula e da bolha. (A sombra viva do compositor refazia o desfoque a cada quadro e, com a
    /// GPU ocupada, derrubava a animação para uns 96 fps.)
    /// </summary>
    private void Sombras(Compositor c)
    {
        // (desvio do desfoque, opacidade, deslocamento para baixo), em DIP
        // 1.8.0: darker and wider, his request (the 0.30/0.26 pair barely showed on a light background)
        var camadas = new (float sigma, float alfa, float dy)[] { (3f, 0.46f, 1.5f), (15f, 0.44f, 10f) };
        const float R = 18;
        foreach (var (sigma, alfa, dy) in camadas)
        {
            float M = MathF.Ceiling(sigma * 3);
            float lado = 2 * (M + R) + 2;
            var sup = _g.NovaSuperficie(new Vector2(lado), (dc, t) => Borrao(dc, t, M, R, sigma, alfa));
            _fixas.Add(sup);
            float m = _p.Px(M), ins = _p.Px(M + R), d = _p.Px(dy);
            var formas = new List<(CompositionRoundedRectangleGeometry, Visual?)> { (_geoCorpo, null), (_geoBolha, _formaBolha), (_geoBolhaE, _formaBolhaE) };
            foreach (var bola in _coluna) formas.Add((bola.Geo, bola.Forma));
            foreach (var (geo, ligada) in formas)
            {
                var nove = c.CreateNineGridBrush();
                nove.Source = sup.Pincel;
                nove.SetInsets(ins);
                var v = c.CreateSpriteVisual();
                v.Brush = nove;
                var tam = c.CreateExpressionAnimation("Vector2(g.Size.X + 2 * m, g.Size.Y + 2 * m)");
                tam.SetReferenceParameter("g", geo);
                tam.SetScalarParameter("m", m);
                v.StartAnimation("Size", tam);
                var pos = c.CreateExpressionAnimation("Vector3(g.Offset.X - m, g.Offset.Y - m + dy, 0)");
                pos.SetReferenceParameter("g", geo);
                pos.SetScalarParameter("m", m);
                pos.SetScalarParameter("dy", d);
                v.StartAnimation("Offset", pos);
                if (ligada != null)
                {
                    var op = c.CreateExpressionAnimation("fb.Opacity");
                    op.SetReferenceParameter("fb", ligada);
                    v.StartAnimation("Opacity", op);
                }
                _raiz.Children.InsertAtBottom(v);
            }
        }
    }

    /// <summary>Retângulo arredondado preto passado pelo desfoque gaussiano do Direct2D.</summary>
    private static unsafe void Borrao(ID2D1DeviceContext dc, Vector2 t, float m, float r, float sigma, float alfa)
    {
        var transf = dc.Transform;
        using var lista = dc.CreateCommandList();
        var alvo = dc.Target;
        dc.Target = lista;
        dc.Transform = Matrix3x2.Identity;
        using (var b = dc.CreateSolidColorBrush(new Vortice.Mathematics.Color4(0, 0, 0, alfa)))
        {
            var rr = new RoundedRectangle(new System.Drawing.RectangleF(m, m, t.X - 2 * m, t.Y - 2 * m), r, r);
            dc.FillRoundedRectangle(ref rr, b);
        }
        lista.Close();
        dc.Target = alvo;
        alvo.Dispose();
        dc.Transform = transf;
        using var fx = new ID2D1Effect(dc.CreateEffect(new Guid("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5")));
        fx.SetInput(0, lista, true);
        float s = sigma;
        fx.SetValue(0, PropertyType.Float, &s, 4);
        using var saida = fx.Output;
        dc.DrawImage(saida, null, null, InterpolationMode.Linear, CompositeMode.SourceOver);
    }
    /// <summary>
    /// Traço fino e claro em volta da pílula e da bolha, por fora da forma (a geometria copia a dela por expressão,
    /// 0,5 traço maior). Começa apagado; a sonda acende e apaga com uma transição curta.
    /// </summary>
    private void Borda(Compositor c, float L, float A)
    {
        float esp = MathF.Max(1, MathF.Round(_g.Escala));
        var pincel = c.CreateColorBrush(Windows.UI.Color.FromArgb(92, 255, 255, 255));
        ShapeVisual Contorno(CompositionRoundedRectangleGeometry forma)
        {
            var geo = c.CreateRoundedRectangleGeometry();
            foreach (var (prop, expr) in new[] { ("Size", "f.Size + Vector2(e, e)"), ("Offset", "f.Offset - Vector2(e / 2, e / 2)"), ("CornerRadius", "f.CornerRadius + Vector2(e / 2, e / 2)") })
            {
                var x = c.CreateExpressionAnimation(expr);
                x.SetReferenceParameter("f", forma);
                x.SetScalarParameter("e", esp);
                geo.StartAnimation(prop, x);
            }
            var s = c.CreateSpriteShape(geo);
            s.StrokeBrush = pincel;
            s.StrokeThickness = esp;
            var v = c.CreateShapeVisual();
            v.Size = new Vector2(L, A);
            v.Shapes.Add(s);
            return v;
        }
        _borda = Contorno(_geoCorpo);
        _borda.Opacity = _bordaLigada ? 1 : 0;
        _camada.Children.InsertAtTop(_borda);
        _bordaBolha = Contorno(_geoBolha);
        var op = c.CreateExpressionAnimation("fb.Opacity * b.Opacity");
        op.SetReferenceParameter("fb", _formaBolha);
        op.SetReferenceParameter("b", _borda);
        _bordaBolha.StartAnimation("Opacity", op);
        _camada.Children.InsertAtTop(_bordaBolha);
        _bordaBolhaE = Contorno(_geoBolhaE);
        var opE = c.CreateExpressionAnimation("fb.Opacity * b.Opacity");
        opE.SetReferenceParameter("fb", _formaBolhaE);
        opE.SetReferenceParameter("b", _borda);
        _bordaBolhaE.StartAnimation("Opacity", opE);
        _camada.Children.InsertAtTop(_bordaBolhaE);
        foreach (var bola in _coluna)
        {
            var cb = Contorno(bola.Geo);
            var oc = c.CreateExpressionAnimation("fb.Opacity * b.Opacity");
            oc.SetReferenceParameter("fb", bola.Forma);
            oc.SetReferenceParameter("b", _borda);
            cb.StartAnimation("Opacity", oc);
            _camada.Children.InsertAtTop(cb);
        }
    }

    private void LigarSonda()
    {
        // cada leitura espera o compositor (~10 ms medidos): com tela cheia na frente (jogo) não lê nada
        bool rodar = SondaPodeRodar();
        if (rodar && !_relogioSonda.Enabled) { _relogioSonda.Start(); Sondar(); }
        else if (!rodar) { _relogioSonda.Stop(); ApagarBorda(); }
    }

    // on the desktop the island is always plain dark, with no border (his choice)
    private bool SondaPodeRodar() =>
        _borda != null && _janelasVisiveis && _estado != Estado.Oculta && !_ctx.TelaCheia && !_ctx.D3DExclusivo && !_ctx.Desktop;

    /// <summary>A paused probe cannot keep a border that was lit for a background that is no longer there.</summary>
    private void ApagarBorda()
    {
        if (!_bordaLigada || _borda == null) return;
        _bordaLigada = false;
        AnimarEscalar(_borda, "Opacity", 0, 260, true);
    }

    /// <summary>
    /// Lê faixas finas logo fora da pílula e da sombra (dos lados e embaixo): atrás dela não dá, a própria ilha cobre.
    /// Medido em 01/11/2026: ~10 ms de espera por leitura e +0,01% de processador a 4 por segundo; ficou em 2 por segundo.
    /// Histerese para não piscar: acende abaixo de 0,22 de luminância e apaga acima de 0,30.
    /// </summary>
    private void Sondar()
    {
        if (!SondaPodeRodar()) { _relogioSonda.Stop(); ApagarBorda(); return; }
        var r = CorpoNaTela;
        int fora = (int)_p.Px(40), faixa = Math.Max(2, (int)_p.Px(4)), abaixo = (int)_p.Px(46);
        float direita = _rColuna.Count > 0 ? _rColuna[0].Right + _janX : _temBolha ? _rBolha.Right + _janX : r.Right;
        float esquerda = _temBolhaE ? _rBolhaE.Left + _janX : r.Left;
        var faixas = new[]
        {
            new System.Drawing.Rectangle((int)esquerda - fora - faixa, (int)r.Top, faixa, Math.Max(1, (int)r.Height)),
            new System.Drawing.Rectangle((int)direita + fora, (int)r.Top, faixa, Math.Max(1, (int)r.Height)),
            new System.Drawing.Rectangle((int)r.Left, (int)r.Bottom + abaixo, Math.Max(1, (int)r.Width), faixa),
        };
        var lum = _sonda.Ler(faixas);
        if (lum == null && ++_sondaFalhas == 20) Log.Escrever($"borda: 20 leituras seguidas falharam (faixas {string.Join(" ", faixas)})");
        if (lum is double l)
        {
            _sondaFalhas = 0;
            bool ligar = _bordaLigada ? l < 0.30 : l < 0.22;
            if (ligar != _bordaLigada)
            {
                _bordaLigada = ligar;
                AnimarEscalar(_borda!, "Opacity", ligar ? 1 : 0, 260, true);
            }
        }
        if (Environment.TickCount64 - _ultimoLogDaSonda > 60_000)
        {
            _ultimoLogDaSonda = Environment.TickCount64;
            var (n, ms) = _sonda.Custo();
            if (n > 0) Log.Escrever($"borda: {n} leituras no último minuto, {ms:F2} ms cada (média)");
        }
    }

    /// <summary>
    /// Camada do tamanho da janela recortada no formato atual da pílula (ou da bolha): o conteúdo que entra e
    /// sai nunca aparece fora dela, nem no meio da mola. A geometria do recorte copia a da forma por expressão.
    /// </summary>
    private ContainerVisual Recortado(Compositor c, CompositionRoundedRectangleGeometry forma, float L, float A)
    {
        var geo = c.CreateRoundedRectangleGeometry();
        foreach (var prop in new[] { "Size", "Offset", "CornerRadius" })
        {
            var e = c.CreateExpressionAnimation("f." + prop);
            e.SetReferenceParameter("f", forma);
            geo.StartAnimation(prop, e);
        }
        var v = c.CreateContainerVisual();
        v.Size = new Vector2(L, A);
        v.Clip = c.CreateGeometricClip(geo);
        _raiz.Children.InsertAtTop(v);
        return v;
    }

    private SpriteVisual Ponto(Compositor c, Vortice.Mathematics.Color4 cor)
    {
        float d = _p.Px(PONTO);
        var v = c.CreateSpriteVisual();
        v.Size = new Vector2(d, d);
        v.Brush = c.CreateColorBrush(Cor.Wr(cor));
        var g = c.CreateEllipseGeometry();
        g.Center = new Vector2(d / 2);
        g.Radius = new Vector2(d / 2);
        v.Clip = c.CreateGeometricClip(g);
        return v;
    }

    private static void Ombro(ID2D1DeviceContext dc, float t, bool esquerdo)
    {
        // quadrado preto sem um quarto de círculo: a curva fica virada para fora da pílula
        using var f = dc.Factory.QueryInterface<ID2D1Factory1>();
        using var geo = f.CreatePathGeometry();
        using (var s = geo.Open())
        {
            if (esquerdo)
            {
                s.BeginFigure(new Vector2(0, 0), FigureBegin.Filled);
                s.AddLine(new Vector2(t, 0));
                s.AddLine(new Vector2(t, t));
                s.AddArc(new ArcSegment { Point = new Vector2(0, 0), Size = new Vortice.Mathematics.Size(t, t), SweepDirection = SweepDirection.CounterClockwise, ArcSize = ArcSize.Small });
            }
            else
            {
                s.BeginFigure(new Vector2(t, 0), FigureBegin.Filled);
                s.AddLine(new Vector2(0, 0));
                s.AddLine(new Vector2(0, t));
                s.AddArc(new ArcSegment { Point = new Vector2(t, 0), Size = new Vortice.Mathematics.Size(t, t), SweepDirection = SweepDirection.Clockwise, ArcSize = ArcSize.Small });
            }
            s.EndFigure(FigureEnd.Closed);
            s.Close();
        }
        using var b = dc.CreateSolidColorBrush(new Vortice.Mathematics.Color4(0, 0, 0, 1));
        dc.FillGeometry(geo, b);
    }

    private void Desmontar()
    {
        foreach (var v in new[] { _vagaCorpo, _vagaBolha, _vagaBolhaE }.Concat(_coluna.Select(b => b.Vaga)))
        {
            if (v?.Atual == null) continue;
            Limpar(v.Atual);
            v.Atual = null;
            v.Chave = null;
        }
        foreach (var q in _saindo.ToArray()) Limpar(q);
        _saindo.Clear();
        foreach (var s in _fixas) s.Dispose();
        _fixas.Clear();
        // the target lets go of the old root before it is closed: a closed root left on it made the next
        // Root = new one fail with E_INVALIDARG, and the island kept animating the closed tree (toggling notch style)
        if (_alvo != null) _alvo.Root = null;
        _raiz?.Dispose();
        _raiz = null!;
        _ombros.Clear();
        _coluna.Clear();
        _rColuna.Clear();
        _naColuna = new();
        _ultimoPlano = default;
    }

    // ------------------------------------------------------------------ posição

    /// <summary>Monitor principal, DPI e posição. Refaz a árvore se a escala mudou.</summary>
    public void Reposicionar(bool forcar = false)
    {
        var mon = Nativo.MonitorFromPoint(new Nativo.POINT(), Nativo.MONITOR_DEFAULTTOPRIMARY);
        var mi = new Nativo.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Nativo.MONITORINFO>() };
        Nativo.GetMonitorInfoW(mon, ref mi);
        Nativo.GetDpiForMonitor(mon, 0, out uint dpi, out _);
        float escala = dpi / 96f * _cfg.Tamanho;
        bool refazer = forcar || MathF.Abs(escala - _g.Escala) > 0.001f || _raiz == null;
        if (refazer)
        {
            Desmontar();
            _g.Escala = escala;
            Montar();
        }
        int largMon = mi.rcMonitor.Right - mi.rcMonitor.Left;
        _janL = (int)_p.Px(LARGURA_JANELA);
        _janX = mi.rcMonitor.Left + (largMon - _janL) / 2 + (int)_p.Px(_cfg.DeslocamentoX);
        _janY = mi.rcMonitor.Top + (int)_p.Px(_cfg.DeslocamentoY);
        if (_janA == 0) _janA = (int)_p.Px(80);
        AjustarJanelas();
        Log.Escrever($"posição: monitor {largMon}x{mi.rcMonitor.Bottom - mi.rcMonitor.Top} dpi {dpi} escala {escala:F2} janela {_janX},{_janY} {_janL}x{_janA}");
        if (refazer) Aplicar(animar: false);
    }

    public void TrocarConfig(Config cfg)
    {
        bool forma = cfg.Colada != _cfg.Colada || MathF.Abs(cfg.DistanciaDoTopo - _cfg.DistanciaDoTopo) > 0.01f || cfg.Sombra != _cfg.Sombra || MathF.Abs(cfg.Tamanho - _cfg.Tamanho) > 0.001f
                     || cfg.DeslocamentoX != _cfg.DeslocamentoX || cfg.DeslocamentoY != _cfg.DeslocamentoY;
        forma |= cfg.BordaAdaptativa != _cfg.BordaAdaptativa;
        _cfg = cfg;
        _relogioSaida.Interval = Math.Max(1, cfg.AtrasoRecolherMs);
        if (forma) Reposicionar(forcar: true);
        else Aplicar();
    }

    private void AjustarJanelas()
    {
        foreach (var h in new[] { _vis.Handle, _toque.Handle })
            Nativo.SetWindowPos(h, Nativo.HWND_TOPMOST, _janX, _janY, _janL, _janA, Nativo.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Keeps the island out of every screen capture (WDA_EXCLUDEFROMCAPTURE) while the recorder records the screen, so
    /// the pill with the recording time never ends up in the video. False gives it back to captures and screenshots.
    /// </summary>
    private readonly HashSet<string> _foraDasCapturas = new();

    /// <summary>
    /// Keeps the island out of screen captures while any owner asks for it (the recorder recording, the game score
    /// reading a game's HUD that the island sits on).
    /// </summary>
    public void ForaDasCapturas(bool sim, string quem = "gravador")
    {
        bool antes = _foraDasCapturas.Count > 0;
        if (sim) _foraDasCapturas.Add(quem); else _foraDasCapturas.Remove(quem);
        sim = _foraDasCapturas.Count > 0;
        if (sim == antes) return;
        foreach (var h in new[] { _vis.Handle, _toque.Handle })
            if (!Nativo.SetWindowDisplayAffinity(h, sim ? Nativo.WDA_EXCLUDEFROMCAPTURE : Nativo.WDA_NONE))
                Log.Escrever($"fora das capturas ({sim}): erro {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }

    /// <summary>Jogo sem borda costuma se pôr no topo ao ganhar foco; chamado a cada troca de janela da frente.</summary>
    public void ReafirmarTopo()
    {
        if (!_janelasVisiveis) return;
        foreach (var h in new[] { _vis.Handle, _toque.Handle })
            Nativo.SetWindowPos(h, Nativo.HWND_TOPMOST, 0, 0, 0, 0, Nativo.SWP_NOMOVE | Nativo.SWP_NOSIZE | Nativo.SWP_NOACTIVATE);
    }

    private bool _jaMostrou;

    /// <summary>
    /// Esconder é "encobrir" pelo DWM (cloak), não ShowWindow: a janela encoberta não é composta (nada fica por
    /// cima do jogo) e volta na hora. Com SW_HIDE/SW_SHOW a ilha levava uns 70 ms para começar a aparecer.
    /// </summary>
    private void MostrarJanelas(bool sim)
    {
        if (sim == _janelasVisiveis && _jaMostrou) return;
        _janelasVisiveis = sim;
        int encobrir = sim ? 0 : 1;
        foreach (var h in new[] { _vis.Handle, _toque.Handle })
        {
            Nativo.DwmSetWindowAttribute(h, Nativo.DWMWA_CLOAK, ref encobrir, 4);
            if (!_jaMostrou) Nativo.ShowWindow(h, Nativo.SW_SHOWNOACTIVATE);
        }
        _jaMostrou = true;
        if (sim) ReafirmarTopo();
        else { _sobre = false; _vigiaSobre.Stop(); }
        LigarSonda();
    }

    // ------------------------------------------------------------------ atividades e contexto

    private readonly Dictionary<Atividade, string> _donos = new();
    private Atividade? _cartaoAvisado;

    /// <summary>O módulo que está ligando agora (o núcleo marca); as atividades adicionadas ficam em nome dele.</summary>
    public string? DonoAtual { get; set; }

    /// <summary>As atividades em andamento que podem aparecer agora, da mais importante para a menos.</summary>
    public IReadOnlyList<Atividade> Ativas => Visiveis();

    public void Adicionar(Atividade a)
    {
        if (DonoAtual != null) _donos[a] = DonoAtual;
        _atividades.Add(a);
        a.Mudou += AtividadeMudou;
        a.Alertou += Alertar;
    }

    /// <summary>Tira a atividade da ilha (o módulo dela foi desligado).</summary>
    public void Remover(Atividade a)
    {
        if (!_atividades.Remove(a)) return;
        a.Mudou -= AtividadeMudou;
        a.Alertou -= Alertar;
        _donos.Remove(a);
        _jaVistas.Remove(a);
        var resto = _fila.Where(x => x.Dono != a).ToList();
        _fila.Clear();
        foreach (var x in resto) _fila.Enqueue(x);
        if (_foco == a) { _foco = null; _expandida = false; }
        if (_cartaoAvisado == a) _cartaoAvisado = null;
        if (_alerta?.Dono == a) { _relogioAlerta.Stop(); ProximoAlerta(); return; }
        Aplicar();
    }

    public void RemoverDoDono(string dono)
    {
        foreach (var (a, d) in _donos.ToList())
            if (d == dono) Remover(a);
    }

    /// <summary>Tira um alerta da tela (ou da fila) sem mexer nos outros.</summary>
    public void FecharAlerta(Alerta a)
    {
        if (_alerta == a) { _relogioAlerta.Stop(); ProximoAlerta(); return; }
        var resto = _fila.Where(x => x != a).ToList();
        _fila.Clear();
        foreach (var x in resto) _fila.Enqueue(x);
    }

    /// <summary>Solta o arraste em andamento (depois de um arrastar para outro app, que engole o botão solto).</summary>
    public void SoltarArraste()
    {
        _arraste = null;
        _toque.SoltarCaptura();
    }

    /// <summary>A atividade que o atalho abre quando não tem nada em andamento.</summary>
    public void DefinirPainel(Atividade a)
    {
        _painel = a;
        // the menu is not in the activities' list, so its size changes (an entry in or out, the public mode) had no one
        // listening: the open menu only reflowed when opened again (3.7.1)
        a.Mudou += x => { if (x == _painel && _expandida && _foco == _painel) Aplicar(); };
    }

    private readonly HashSet<Atividade> _jaVistas = new();

    private void AtividadeMudou(Atividade a)
    {
        // um evento atrasado (tarefa que terminou depois do Parar) de quem já saiu da ilha não traz ela de volta
        if (!_atividades.Contains(a)) return;
        if (a.Ativa && _jaVistas.Add(a))
        {
            // algo novo aparece expandindo por um instante (não no Não Incomodar)
            if (!(_ctx.NaoPerturbe && _cfg.RespeitarNaoPerturbe) && a.AnunciaChegada && Permitida(a))
                Alertar(new Alerta { Tipo = "chegada", Som = "chegada", Criar = p => a.Criar(p, Forma.Expandida), PermitidoNoCompetitivo = a.PermitidaNoCompetitivo, Segundos = _cfg.AlertaSegundos * 0.7f, Dono = a });
        }
        if (!a.Ativa && _jaVistas.Remove(a))
        {
            if (_foco == a && !a.EhFerramenta) { _foco = null; _expandida = false; }
            // a chegada dela não faz mais sentido: sai da fila e, se estiver na tela, sai também
            // (os outros alertas dela, como o "timer acabou", ficam)
            var resto = _fila.Where(x => !(x.Tipo == "chegada" && x.Dono == a)).ToList();
            _fila.Clear();
            foreach (var x in resto) _fila.Enqueue(x);
            if (_alerta is { Tipo: "chegada" } al && al.Dono == a) { _relogioAlerta.Stop(); ProximoAlerta(); return; }
        }
        Aplicar();
    }

    public void Contexto(Contexto ctx)
    {
        bool trocouFrente = ctx.Janela != _ctx.Janela;
        _ctx = ctx;
        if (ctx.ConteudoEmTelaCheia && !ctx.Competitivo) { if (!_vigiaTopo.Enabled) _vigiaTopo.Start(); }
        else { _vigiaTopo.Stop(); _revelada = false; }
        if (trocouFrente && _expandida && _porAtalho) _expandida = false;
        ReafirmarTopo();
        Aplicar();
        LigarSonda();
    }

    // in the live score mode the other activities stay (the score goes ahead of them); their arrival alerts and sounds
    // still do not show over the game
    private bool Permitida(Atividade a) => !_ctx.Competitivo || a.PermitidaNoCompetitivo || _cfg.ModoCompetitivo == ModosCompetitivos.Placar;

    /// <summary>
    /// The menu (and the "&lt;" back to it) is shut in a competitive game, except in score mode, where the island is
    /// meant to be there: he could open the score but not get back to the menu (03/11/2026).
    /// </summary>
    private bool MenuFechado => _ctx.Competitivo && _cfg.ModoCompetitivo != ModosCompetitivos.Placar;

    private int Prioridade(string tipo)
    {
        int i = _cfg.Prioridades.FindIndex(p => string.Equals(p, tipo, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 100 : i;
    }

    private List<Atividade> Visiveis() => _atividades
        .Where(a => a.Ativa && Permitida(a))
        .OrderByDescending(a => a.NaFrente)
        .ThenBy(a => Prioridade(a.Tipo))
        .ThenBy(a => a.AoFundo ? 1 : 0)
        .ThenByDescending(a => a.Desde)
        .ToList();

    // ------------------------------------------------------------------ alertas

    public void Alertar(Alerta a)
    {
        if (_cfg.Pausada) return;
        if (a.Dono != null && a.Dono != _painel && !_atividades.Contains(a.Dono)) return;
        if (_ctx.Competitivo && !a.PermitidoNoCompetitivo) return;
        if (_ctx.NaoPerturbe && _cfg.RespeitarNaoPerturbe && !a.Importante) return;
        if (FocoBarra(a)) return;
        if (a.Importante)
        {
            // alarme (timer acabou) fura a fila
            var resto = _fila.ToList();
            _fila.Clear();
            _fila.Enqueue(a);
            foreach (var x in resto) _fila.Enqueue(x);
        }
        else _fila.Enqueue(a);
        if (_alerta == null) ProximoAlerta();
    }

    /// <summary>O alerta apareceria agora (não pausada, não barrado pelo modo competitivo nem pelo Não Incomodar)?</summary>
    public bool PodeMostrarAlerta(Alerta a) => PodeMostrar(a);

    private bool PodeMostrar(Alerta a) =>
        !_cfg.Pausada && (!_ctx.Competitivo || a.PermitidoNoCompetitivo) && !(_ctx.NaoPerturbe && _cfg.RespeitarNaoPerturbe && !a.Importante)
        && !(_ctx.ConteudoEmTelaCheia && !_revelada && !a.Importante)
        && !(a.Privado && Discreta) && !FocoBarra(a);

    private void ProximoAlerta()
    {
        // o que estava na fila e deixou de poder aparecer (entrou num jogo competitivo, ligou o Não Incomodar) é descartado
        do _alerta = _fila.Count > 0 ? _fila.Dequeue() : null;
        while (_alerta != null && !PodeMostrar(_alerta));
        if (_alerta != null)
        {
            _relogioAlerta.Interval = Math.Max(1, (int)(1000 * (_alerta.Segundos ?? _cfg.AlertaSegundos)));
            _relogioAlerta.Start();
            string som = _alerta.Som ?? _alerta.Tipo;
            if (_alerta.SomEmLoop is { } loop && Sons?.TocarEmLoop(loop, _alerta.Importante) is { } parar) _alerta.Parar = parar;
            else if (som.Length > 0) Sons?.Tocar(som, _alerta.Importante);
        }
        Aplicar();
    }

    private void FimDoAlerta()
    {
        // com o mouse em cima, espera ele sair
        if (_sobre) { _relogioAlerta.Interval = 600; _relogioAlerta.Start(); return; }
        ProximoAlerta();
    }

    // ------------------------------------------------------------------ mouse e atalho

    private void Entrou()
    {
        // a região cresceu embaixo de um cursor parado: o Windows manda um movimento sem o mouse ter mexido. Isso não é
        // "entrar" (senão um alerta que nasce embaixo do cursor nunca mais some)
        Nativo.GetCursorPos(out var cur);
        if (!_sobre && !_relogioSaida.Enabled && _cursorNaMudanca is { } m && m.X == cur.X && m.Y == cur.Y) return;
        _cursorNaMudanca = null;
        _relogioSaida.Stop();
        if (_sobre) return;
        _vigiaSobre.Start();
        _sobre = true;
        if (_expandida) _porAtalho = false;
        if (_estado == Estado.Compacta && !_expandida && Visiveis().FirstOrDefault() is { Tipo: "privacidade" } priv)
        {
            _foco = priv;
            _expandida = true;
            Aplicar();
            return;
        }
        if (_estado is Estado.Compacta or Estado.Ociosa) Aplicar();
    }

    private void Saiu()
    {
        _vigiaSobre.Stop();
        if (!_sobre) return;
        _sobre = false;
        if (_expandida && !_porAtalho) _relogioSaida.Start();
        else if (_estado is Estado.Compacta or Estado.Ociosa) Aplicar();
    }

    /// <summary>
    /// Reserva do aviso de saída (ele pode se perder: captura do mouse, arrastar para outro app, região que encolheu):
    /// enquanto a ilha acha que o mouse está em cima, confere se a janela embaixo do cursor ainda é a dela.
    /// </summary>
    private void VigiarSobre()
    {
        if (!_sobre) { _vigiaSobre.Stop(); return; }
        if (_toque.Apertado) return;
        Nativo.GetCursorPos(out var c);
        if (Nativo.WindowFromPoint(c) == _toque.Handle) return;
        _toque.EsquecerRastreio();
        Saiu();
    }

    private void VigiarArraste()
    {
        bool pode = _receptor != null && !_cfg.Pausada && !_ctx.Competitivo && !_ctx.TelaCheia && !_ctx.D3DExclusivo;
        // com o cartão aberto pelo atalho quem lê o botão é o vigia do clique fora (ler aqui comeria o "apertou" dele)
        if (!pode || _vigiaClique.Enabled)
        {
            if (_zonaDeSoltar) { _zonaDeSoltar = false; Aplicar(); }
            _apertadoDesde = 0;
            return;
        }
        bool apertado = (Nativo.GetAsyncKeyState(0x01) & 0x8000) != 0;
        if (!apertado)
        {
            _apertadoDesde = 0;
            if (_zonaDeSoltar && _alvoDeSoltar?.EmCima != true) { _zonaDeSoltar = false; Aplicar(); }
            return;
        }
        if (_zonaDeSoltar || _estado != Estado.Oculta) return;
        long agora = Environment.TickCount64;
        Nativo.GetCursorPos(out var c);
        if (_apertadoDesde == 0) { _apertadoDesde = agora; _inicioDoAperto = c; return; }
        if (agora - _apertadoDesde < 200 || !NaZona(c) || NaZona(_inicioDoAperto)) return;
        // só um arraste de arquivo de verdade: o DoDragDrop do OLE prende o mouse numa janela "CLIPBRDWNDCLASS"; janela
        // sendo movida (barra de título, Snap) e aba de navegador não abrem a pílula
        var gti = new Nativo.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Nativo.GUITHREADINFO>() };
        if (!Nativo.GetGUIThreadInfo(0, ref gti) || (gti.flags & 0x2) != 0 || gti.hwndCapture == IntPtr.Zero) return;
        string classe = Nativo.Classe(gti.hwndCapture);
        if (classe != "CLIPBRDWNDCLASS")
        {
            if (classe != _ultimaClasseDeCaptura) { _ultimaClasseDeCaptura = classe; Log.Escrever("zona de soltar: captura com " + classe + " (não é arraste de arquivo)"); }
            return;
        }
        _zonaDeSoltar = true;
        Aplicar();
    }

    private Nativo.POINT _inicioDoAperto;
    private string _ultimaClasseDeCaptura = "";

    private bool NaZona(Nativo.POINT c)
    {
        float cx = _janX + _janL / 2f, dy = c.Y - _janY;
        return dy >= 0 && dy < _p.Px(140) && MathF.Abs(c.X - cx) < _p.Px(300);
    }

    private void VigiarTopo()
    {
        if (!_ctx.ConteudoEmTelaCheia || _ctx.Competitivo || _cfg.Pausada) { _vigiaTopo.Stop(); _revelada = false; return; }
        Nativo.GetCursorPos(out var c);
        float cx = _janX + _janL / 2f, dy = c.Y - _janY;
        bool noMeio = MathF.Abs(c.X - cx) < _p.Px(420);
        if (!_revelada)
        {
            // encostou na borda de cima
            if (noMeio && dy >= 0 && dy <= _p.Px(4)) { _revelada = true; _longeDesde = 0; Aplicar(); }
            return;
        }
        // fica enquanto o mouse estiver em cima ou perto (a faixa de cima até um pouco abaixo da ilha), ou com o cartão aberto
        float alcance = (_rCorpo.Height > 0 ? _rCorpo.Bottom : _p.Px(60)) + _p.Px(48);
        bool perto = _sobre || _expandida || noMeio && dy >= 0 && dy <= alcance;
        if (perto) { _longeDesde = 0; return; }
        long agora = Environment.TickCount64;
        if (_longeDesde == 0) { _longeDesde = agora; return; }
        if (agora - _longeDesde < 900) return;
        _revelada = false;
        Aplicar();
    }

    private Action<float, float, bool>? _arraste;
    private Vector2 _inicioArraste;
    private bool _arrastou;
    private string? _cenaApertada;

    /// <summary>A chave da cena sem a versão do fim: o mesmo cartão ou alerta redesenhado continua sendo a mesma cena.</summary>
    private static string? SemVersao(string? chave) =>
        chave is null ? null : chave.LastIndexOf(':') is var i and > 0 ? chave[..i] : chave;

    private Vector2 Local(Vaga v, int x, int y)
    {
        if (v.Atual == null) return Vector2.Zero;
        var o = OrigemDaCena(v.Atual);
        var host = v.Host.Offset;
        return new Vector2((x - host.X - o.X) / _g.Escala, (y - host.Y - o.Y) / _g.Escala);
    }

    private void Apertou(int x, int y)
    {
        _arraste = null;
        _cenaApertada = _vagaCorpo.Atual == null ? null : SemVersao(_vagaCorpo.Chave);
        if (_vagaCorpo.Atual == null) return;
        var l = Local(_vagaCorpo, x, y);
        foreach (var (b, d) in BotoesDe(_vagaCorpo.Atual.Cena))
            if (b.Arrastar is { } arrastar && b.Area.Contains(l.X - d.X, l.Y - d.Y))
            {
                _arraste = (ax, ay, soltou) => arrastar(ax - d.X, ay - d.Y, soltou);
                _inicioArraste = l;
                _arrastou = false;
                _arraste(l.X, l.Y, false);
                return;
            }
    }

    /// <summary>Os botões da cena e, se ela embrulha outra (o "&lt;" do menu), os da de dentro com o deslocamento dela.</summary>
    private static IEnumerable<(Botao b, Vector2 desl)> BotoesDe(Cena c)
    {
        foreach (var b in c.Botoes.ToArray()) yield return (b, Vector2.Zero);
        if (c.Interna is { } i)
            foreach (var (b, d) in BotoesDe(i)) yield return (b, d + c.DeslocamentoInterno);
    }

    // the last mouse position over the island (window pixels), for the wheel over a menu tile
    private (int X, int Y) _mouse;

    private void Rolou(int delta)
    {
        var a = _estado == Estado.Expandida ? _foco : _estado == Estado.Compacta ? Visiveis().FirstOrDefault() : null;
        Vector2? noCartao = _estado == Estado.Expandida && _vagaCorpo.Atual != null ? Local(_vagaCorpo, _mouse.X, _mouse.Y) : null;
        try { a?.Rolar(delta, noCartao); } catch (Exception e) { Log.Escrever("rolar: " + e); }
    }

    private void Clicou(int x, int y)
    {
        if (_arraste != null)
        {
            var l = Local(_vagaCorpo, x, y);
            var fim = _arraste;
            _arraste = null;
            fim(l.X, l.Y, true);
            // apertou e soltou sem sair do lugar: vale como clique (o botão, ou fechar o alerta)
            if (_arrastou || Vector2.Distance(l, _inicioArraste) > 4) return;
        }
        bool mesmaCena = _cenaApertada != null && _vagaCorpo.Atual != null && _cenaApertada == SemVersao(_vagaCorpo.Chave);
        _cenaApertada = null;
        if (_estado == Estado.Alerta)
        {
            // apertou noutra coisa e o alerta apareceu antes de soltar: nem aprova nem fecha nada
            if (!mesmaCena) return;
            if (!ClicarBotao(_vagaCorpo, x, y)) { _relogioAlerta.Stop(); ProximoAlerta(); }
            return;
        }
        if (_estado == Estado.Expandida && _foco == _painel && ColunaEm(x, y) is { } daColuna)
        {
            _clicada = true;
            _foco = daColuna;
            // held like a card opened by the hotkey (closes on a click outside or Esc): the new card is usually narrower
            // than the menu and the cursor, still where the ball was, ends up outside it; held by hover, the island
            // closed 0.6 s after the click (4.0.1, seen with a real click)
            _porAtalho = true;
            Aplicar();
            FocarTeclado();
            return;
        }
        if (_estado == Estado.Expandida)
        {
            _clicada = true;
            var frenteAntes = Nativo.GetForegroundWindow();
            _noClique = true;
            try { if (mesmaCena) ClicarBotao(_vagaCorpo, x, y); }
            finally { _noClique = false; }
            // clicou na ilha: ela pega o teclado (só para o Esc), se o botão não fechou o cartão, não abriu um campo e
            // não trouxe outra janela para a frente (as configurações)
            if (_expandida && Nativo.GetForegroundWindow() == frenteAntes) FocarTeclado();
            return;
        }
        var vis = Visiveis();
        var daBolha = DaBolha(vis);
        var daBolhaE = DaBolhaE(vis);
        if (_temBolha && _rBolha.Contains(x, y) && daBolha != null) _foco = daBolha;
        else if (_temBolhaE && _rBolhaE.Contains(x, y) && daBolhaE != null) _foco = daBolhaE;
        else if (_estado == Estado.Compacta && _pontosVisiveis && _extraPontos > 0
                 && x >= _rCorpo.Right - _p.Px(COMPACTA_FOLGA + _extraPontos)
                 && vis.FirstOrDefault(a => a.Tipo == "privacidade") is { } privacidade) _foco = privacidade;
        else if (vis.Count > 0) _foco = vis[0];
        else _foco = MenuFechado ? null : _painel;
        if (_foco == null || !_foco.TemExpandida) return;
        _expandida = true;
        _porAtalho = false;
        _clicada = true;
        Aplicar();
        FocarTeclado();
    }

    /// <summary>The activity whose ball of the menu's column is under the point, if any.</summary>
    private Atividade? ColunaEm(int x, int y)
    {
        for (int k = 0; k < _rColuna.Count && k < _naColuna.Count; k++)
            if (_rColuna[k].Contains(x, y)) return _naColuna[k].TemExpandida ? _naColuna[k] : null;
        return null;
    }

    private bool ClicarBotao(Vaga v, int x, int y)
    {
        if (v.Atual == null) return false;
        var o = OrigemDaCena(v.Atual);
        var host = v.Host.Offset;
        float lx = (x - host.X - o.X) / _g.Escala, ly = (y - host.Y - o.Y) / _g.Escala;
        foreach (var (b, d) in BotoesDe(v.Atual.Cena))
            if (b.Area.Contains(lx - d.X, ly - d.Y)) { b.Acao(); return true; }
        return false;
    }

    private void FocarTeclado()
    {
        if (Entrada == null || Entrada.Aberta || _ctx.Competitivo) return;
        try { Entrada.Focar(); } catch (Exception e) { Log.Escrever("foco: " + e.Message); }
    }

    /// <summary>
    /// O Esc com a ilha focada: numa tela interna volta para o menu (e a ilha segue com o teclado); no menu, recolhe e
    /// o foco volta para onde estava. Num alerta, fecha o alerta.
    /// </summary>
    public bool VoltarPeloEsc()
    {
        if (_estado == Estado.Alerta && _alerta != null) { _relogioAlerta.Stop(); ProximoAlerta(); return _expandida; }
        if (_estado != Estado.Expandida) return false;
        if (_clicada && _foco != null && _foco != _painel && _painel != null && !MenuFechado) { AbrirMenu(); return true; }
        Recolher();
        return false;
    }

    /// <summary>O "&lt;" e o Esc: volta para o menu principal.</summary>
    public void AbrirMenu()
    {
        if (_painel == null || _cfg.Pausada) return;
        // o "fechou" do módulo pode chamar Recolher (que zera estas marcas): sem o mouse em cima, o menu segue no modo do
        // atalho (o clique fora e a troca de janela recolhem)
        bool porAtalho = _porAtalho || !_sobre, clicada = _clicada;
        // o campo do módulo fecha (ele recebe o "fechou"), mas a ilha segue ouvindo o Esc
        if (Entrada is { Aberta: true, SoTeclas: false }) Entrada.ModoTeclado();
        if (_alerta != null) { _relogioAlerta.Stop(); _alerta = null; }
        _foco = _painel;
        _expandida = true;
        _porAtalho = porAtalho;
        _clicada = clicada;
        Aplicar();
    }

    /// <summary>Atalho global: abre a atividade principal (ou o painel) expandida; de novo, recolhe.</summary>
    public void Atalho()
    {
        if (_cfg.Pausada) return;
        if (_expandida) { Recolher(); return; }
        // o clique no ícone da bandeja já recolheu a ilha pelo "clique fora": não reabrir no mesmo clique
        if (Environment.TickCount64 - _recolheuPorCliqueFora < 400) return;
        if (_alerta != null) { _relogioAlerta.Stop(); _fila.Clear(); _alerta = null; }
        _clicada = false;
        var vis = Visiveis();
        _foco = vis.FirstOrDefault(a => a.TemExpandida) ?? (MenuFechado ? null : _painel);
        if (_foco == null) return;
        _expandida = true;
        _porAtalho = true;
        Aplicar();
    }

    /// <summary>
    /// Mostra as janelas uma vez com tudo acima da tela e um desfoque de mentira, e esconde de novo: a primeira
    /// aparição de verdade começava a animar uns 80 ms atrasada (o DWM montando a janela e o efeito).
    /// </summary>
    public void Aquecer()
    {
        if (_estado != Estado.Oculta || _janelasVisiveis) return;
        var c = _g.Compositor;
        var ef = _desfoque!.CreateBrush();
        ef.SetSourceParameter("src", c.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0)));
        ef.Properties.InsertScalar("Blur.BlurAmount", 4f);
        var v = c.CreateSpriteVisual();
        v.Size = new Vector2(8, 8);
        v.Brush = ef;
        _raiz.Children.InsertAtTop(v);
        MostrarJanelas(true);
        var t = new System.Windows.Forms.Timer { Interval = 300 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            _raiz.Children.Remove(v);
            v.Dispose();
            ef.Dispose();
            if (_estado == Estado.Oculta) MostrarJanelas(false);
        };
        t.Start();
    }

    /// <summary>Só para a medição: estica a pílula de forma linear por 1 s (toda volta da tela muda um pouco).</summary>
    public void TesteLinear()
    {
        var c = _g.Compositor;
        var a = c.CreateVector2KeyFrameAnimation();
        var inicio = _geoCorpo.Size;
        a.InsertKeyFrame(0f, inicio, c.CreateLinearEasingFunction());
        a.InsertKeyFrame(0.5f, inicio + _p.Px(new Vector2(260, 90)), c.CreateLinearEasingFunction());
        a.InsertKeyFrame(1f, inicio, c.CreateLinearEasingFunction());
        a.Duration = TimeSpan.FromMilliseconds(1000);
        _geoCorpo.StartAnimation("Size", a);
    }

    /// <summary>Abre esta atividade expandida (ferramentas pelo atalho delas; qualquer uma pelo módulo).</summary>
    public void Expandir(Atividade a)
    {
        if (_cfg.Pausada) return;
        if (a != _painel && !_atividades.Contains(a)) return;
        if (_alerta != null) { _relogioAlerta.Stop(); _fila.Clear(); _alerta = null; }
        // um bloco do menu clicado abre a ferramenta por aqui: continua valendo como clique
        if (!_noClique) _clicada = false;
        _foco = a;
        _expandida = true;
        _porAtalho = true;
        Aplicar();
    }

    /// <summary>A atividade expandida agora (null se nenhuma).</summary>
    public Atividade? Expandida => _expandida ? _foco : null;

    public void Recolher()
    {
        if (!_expandida) return;
        _expandida = false;
        _porAtalho = false;
        _clicada = false;
        _foco = null;
        Aplicar();
        // recolheu por baixo de um alerta: o plano pode não mudar (o Aplicar sai cedo); o teclado volta agora
        if (Entrada?.Aberta == true)
            Programa.NaInterface(() => { if (!_expandida && _estado != Estado.Expandida) Entrada?.Fechar(); });
    }

    private long _recolheuPorCliqueFora;
    private bool _arrasteDeFora;
    // a ilha foi clicada desde que abriu (só aí o Esc volta para o menu; aberta pelo atalho, o Esc fecha e devolve o foco)
    private bool _clicada, _noClique;

    private void VigiarCliqueFora()
    {
        if (!_expandida || _sobre) return;
        // o botão segurado de um arraste de arquivo (de outro app) não é clique fora, nem quando o arquivo sai do cartão
        // com o botão ainda apertado (quem decide fechar é o módulo que recebe)
        if (_alvoDeSoltar is { } a)
        {
            if (a.EmCima || Environment.TickCount64 - a.Fim < 400) { _arrasteDeFora = true; return; }
            if (_arrasteDeFora)
            {
                if ((Nativo.GetAsyncKeyState(0x01) & 0x8000) == 0) _arrasteDeFora = false;
                return;
            }
        }
        // 0x8001: apertado agora ou desde a última consulta (um clique rápido entre duas leituras também conta)
        bool apertou = (Nativo.GetAsyncKeyState(0x01) & 0x8001) != 0 || (Nativo.GetAsyncKeyState(0x02) & 0x8001) != 0;
        if (!apertou) return;
        Nativo.GetCursorPos(out var c);
        float x = c.X - _janX, y = c.Y - _janY;
        // the balls of the menu's column are the island too (4.0.1: a click on one, with the menu opened by the hotkey,
        // closed everything instead of opening that activity)
        if (!_rCorpo.Contains(x, y) && !_rColuna.Any(r => r.Contains(x, y))) { _recolheuPorCliqueFora = Environment.TickCount64; Recolher(); }
    }

    // ------------------------------------------------------------------ estado e layout

    private sealed record Plano(Estado Estado, Vector2 Corpo, float Raio, string? ChaveCorpo, Func<Cena>? CenaCorpo,
        bool Bolha, string? ChaveBolha, Func<Cena>? CenaBolha, bool BolhaE = false, string? ChaveBolhaE = null, Func<Cena>? CenaBolhaE = null,
        IReadOnlyList<(string Chave, Func<Cena> Criar)>? Coluna = null);

    private Plano Planejar()
    {
        var vis = Visiveis();
        if (_cfg.Pausada)
        {
            _expandida = false;
            _porAtalho = false;
            _foco = null;
            return new Plano(Estado.Oculta, Vector2.Zero, 0, null, null, false, null, null);
        }

        // alerta passa por cima de qualquer atividade (é o primeiro da ordem padrão e não entra na lista de prioridades)
        if (_alerta != null && PodeMostrar(_alerta))
        {
            _naColuna = new();
            var al = _alerta;
            return Cartao(Estado.Alerta, $"alerta:{al.GetHashCode()}:{al.Dono?.Versao ?? 0}", () => al.Criar(_p), RAIO_ALERTA);
        }

        bool avulsa = _foco == _painel || _foco?.EhFerramenta == true;
        if (_expandida && _foco != null && (_foco.Ativa || avulsa) && (Permitida(_foco) || avulsa && !MenuFechado))
        {
            var f = _foco;
            bool voltar = f != _painel && _painel != null && !MenuFechado;
            var cartao = Cartao(Estado.Expandida, $"exp:{f.GetHashCode()}:{(voltar ? 1 : 0)}:{f.Versao}",
                voltar ? () => ComVoltar(f.Criar(_p, Forma.Expandida)) : () => f.Criar(_p, Forma.Expandida), RAIO_EXPANDIDA);
            // the open menu: every activity running goes to a ball at its right (the privacy one has its dots in the
            // menu's header already)
            _naColuna = f == _painel ? vis.Where(a => a.Tipo != "privacidade").Take(COLUNA_MAX).ToList() : new List<Atividade>();
            if (_naColuna.Count == 0) return cartao;
            var col = _naColuna.Select(a => ($"col:{a.GetHashCode()}:{a.Versao}", (Func<Cena>)(() => a.Criar(_p, Forma.Bolha)))).ToList();
            return cartao with { Coluna = col };
        }
        _expandida = false;
        _naColuna = new();

        if (_ctx.ConteudoEmTelaCheia && !_revelada && !_zonaDeSoltar)
            return new Plano(Estado.Oculta, Vector2.Zero, 0, null, null, false, null, null);

        _anelNaPilula = false;
        if (vis.Count == 1 && vis[0].Tipo == "privacidade" && _cfg.Relogio && !_ctx.Desktop && CorDoIndicador() != null)
        {
            // only the camera or microphone in use: the idle pill with the time, the ring around it; a click opens the card
            var a = vis[0];
            float l = OCIOSA_L, h = OCIOSA_A;
            if (_sobre) { l += PREVIA_L * 0.6f; h += PREVIA_A * 0.6f; }
            string ch = $"cmp:privacidade-relogio:{a.Versao}";
            TamanhoDaCena(_vagaCorpo, ch, () => ComRelogio(_p.NovaCena(new Vector2(OCIOSA_L, OCIOSA_A)), (0, OCIOSA_L)), out var criar);
            _anelNaPilula = true;
            _extraPontos = 0;
            return new Plano(Estado.Compacta, new Vector2(l, h), h / 2, ch, criar, false, null, null);
        }

        if (vis.Count > 0)
        {
            var a = vis[0];
            var meio = _cfg.Relogio && !_ctx.Desktop ? a.MeioLivreCompacta : null;
            // with the ring the time needs a little more room; without the room, the dots on the side say it instead
            float precisa = LarguraDoRelogio() + (CorDoIndicador() != null ? ANEL_FOLGA * 2 + 6 : 10);
            bool relogio = meio is { } mv && mv.Ate - mv.De >= precisa;
            _anelNaPilula = relogio && CorDoIndicador() != null;
            string ch = $"cmp:{a.GetHashCode()}:{(relogio ? 1 : 0)}:{a.Versao}";
            var tam = TamanhoDaCena(_vagaCorpo, ch, relogio ? () => ComRelogio(a.Criar(_p, Forma.Compacta), meio!.Value) : () => a.Criar(_p, Forma.Compacta), out var criar);
            float l = MathF.Max(a.LarguraCompactaMinima ?? COMPACTA_MIN, tam.X + COMPACTA_FOLGA * 2), h = COMPACTA_A;
            // com as bolinhas de privacidade na pílula, ela alarga para elas não cobrirem o conteúdo
            _extraPontos = PontosDevemAparecer(Estado.Compacta, a) ? LarguraPontos() + 8 : 0;
            l += _extraPontos;
            if (_sobre) { l += PREVIA_L; h += PREVIA_A; }
            var segunda = DaBolha(vis);
            bool bolha = _cfg.Dividir && segunda != null;
            string? cb = null;
            Func<Cena>? fb = null;
            if (bolha)
            {
                var b = segunda!;
                cb = $"bol:{b.GetHashCode()}:{b.Versao}";
                fb = () => b.Criar(_p, Forma.Bolha);
            }
            var terceira = DaBolhaE(vis);
            bool bolhaE = bolha && terceira != null;
            string? cbe = null;
            Func<Cena>? fbe = null;
            if (bolhaE)
            {
                var b = terceira!;
                cbe = $"bolE:{b.GetHashCode()}:{b.Versao}";
                fbe = () => b.Criar(_p, Forma.Bolha);
            }
            return new Plano(Estado.Compacta, new Vector2(l, h), h / 2, ch, criar, bolha, cb, fb, bolhaE, cbe, fbe);
        }

        if (_zonaDeSoltar || !_ctx.Competitivo && _cfg.PilulaVazia)
        {
            float l = OCIOSA_L, h = OCIOSA_A;
            if (_sobre) { l += PREVIA_L * 0.6f; h += PREVIA_A * 0.6f; }
            // the empty pill shows the time everywhere except on the desktop (and not while it is a drop zone)
            string? chave = null;
            Func<Cena>? criar = null;
            if (_cfg.Relogio && !_ctx.Desktop && !_zonaDeSoltar)
            {
                chave = "ociosa:relogio:0";
                TamanhoDaCena(_vagaCorpo, chave, () => ComRelogio(_p.NovaCena(new Vector2(OCIOSA_L, OCIOSA_A)), (0, OCIOSA_L)), out criar);
            }
            return new Plano(Estado.Ociosa, new Vector2(l, h), h / 2, chave, criar, false, null, null);
        }

        return new Plano(Estado.Oculta, Vector2.Zero, 0, null, null, false, null, null);
    }

    private const float FAIXA_VOLTAR = 28;

    /// <summary>Embrulha o cartão com uma faixa em cima e o "&lt;" que volta para o menu.</summary>
    private Cena ComVoltar(Cena interna)
    {
        var c = _p.NovaCena(new Vector2(MathF.Max(interna.Tamanho.X, 120), interna.Tamanho.Y + FAIXA_VOLTAR));
        interna.Raiz.Offset = new Vector3(0, _p.Px(FAIXA_VOLTAR), 0);
        c.Raiz.Children.InsertAtTop(interna.Raiz);
        c.Interna = interna;
        c.DeslocamentoInterno = new Vector2(0, FAIXA_VOLTAR);
        c.AoSoltar += () => { c.Raiz.Children.Remove(interna.Raiz); interna.Soltar(); };
        _p.Desenho(c, Vector2.Zero, new Vector2(80, 22), (dc, t) =>
        {
            var f = _p.G.Fabrica;
            Icone.Linha(dc, new Vector2(8, 4), new Vector2(2, 11), 2.2f, Cor.Secundario, f);
            Icone.Linha(dc, new Vector2(2, 11), new Vector2(8, 18), 2.2f, Cor.Secundario, f);
            _p.Texto.Desenhar(dc, "Menu", new Vector2(15, 1), 13, Cor.Secundario, Vortice.DirectWrite.FontWeight.Medium);
        });
        // área maior que o desenho, entrando na folga do cartão (mais fácil de acertar)
        c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(-8, -8, 84, 34), Acao = AbrirMenu });
        return c;
    }

    private float LarguraDoRelogio() => _p.Texto.Medir("00:00", 13, Vortice.DirectWrite.FontWeight.SemiBold, tabular: true).X;

    private const float ANEL_FOLGA = 8, ANEL_A = 22;

    /// <summary>The ring's color when the camera (green) or the microphone (orange) is in use; null when none, or hidden.</summary>
    private Vortice.Mathematics.Color4? CorDoIndicador() =>
        _indicadores is { Algum: true } i && !_ctx.Competitivo && !_cfg.Pausada ? (i.Camera ? Cor.Verde : Cor.Laranja) : null;

    /// <summary>The compact pill shows the time with the ring around it (the dots on the side would repeat it).</summary>
    private bool _anelNaPilula;

    /// <summary>Põe a hora no trecho vazio do meio da cena compacta (fonte sem serifa, discreta).</summary>
    private Cena ComRelogio(Cena cena, (float De, float Ate) meio)
    {
        var tam = new Vector2(meio.Ate - meio.De, cena.Tamanho.Y);
        var s = _p.Desenho(cena, new Vector2(meio.De, 0), tam, (dc, t) =>
        {
            var agora = DateTime.Now;
            _minutoDesenhado = agora.Minute;
            string h = agora.ToString("HH:mm");
            var m = _p.Texto.Medir(h, 13, Vortice.DirectWrite.FontWeight.SemiBold, tabular: true);
            var cor = CorDoIndicador();
            if (cor is { } c)
            {
                // camera or microphone in use: the time on a pill of that color (green camera, orange microphone)
                float l = m.X + ANEL_FOLGA * 2, a = ANEL_A;
                Icone.Retangulo(dc, (t.X - l) / 2, (t.Y - a) / 2, l, a, a / 2, c);
            }
            _p.Texto.Desenhar(dc, h, new Vector2((t.X - m.X) / 2, (t.Y - m.Y) / 2), 13, cor != null ? Cor.Branco : Cor.Secundario,
                Vortice.DirectWrite.FontWeight.SemiBold, tabular: true);
        });
        _relogios.Add(s);
        cena.AoSoltar += () => _relogios.Remove(s);
        if (!_minutoRelogio.Enabled) _minutoRelogio.Start();
        return cena;
    }

    private Plano Cartao(Estado e, string chave, Func<Cena> fabrica, float raio)
    {
        var tam = TamanhoDaCena(_vagaCorpo, chave, fabrica, out var criar);
        var corpo = new Vector2(MathF.Max(CARTAO_MIN, tam.X + CARTAO_FOLGA * 2), tam.Y + CARTAO_FOLGA * 2);
        return new Plano(e, corpo, raio, chave, criar, false, null, null);
    }

    // Cria a cena já aqui (o tamanho da pílula depende dela); se a vaga já mostra essa chave, reaproveita.
    private Vector2 TamanhoDaCena(Vaga v, string chave, Func<Cena> fabrica, out Func<Cena>? criar)
    {
        if (v.Chave == chave && v.Atual != null) { criar = null; return v.Atual.Cena.Tamanho; }
        var c = fabrica();
        criar = () => c;
        return c.Tamanho;
    }

    /// <summary>Recalcula o alvo e anima até ele. Chamado a cada mudança (evento), nunca por relógio.</summary>
    public void Aplicar(bool animar = true)
    {
        if (_raiz == null) return;
        var pl = Planejar();
        var assinatura = (pl.Estado, pl.Corpo, pl.Raio, pl.ChaveCorpo, pl.Bolha, pl.ChaveBolha + "|" + pl.ChaveBolhaE + "|" + string.Join(",", pl.Coluna?.Select(x => x.Chave) ?? Enumerable.Empty<string>()), _janelasVisiveis || pl.Estado == Estado.Oculta,
            (_indicadores?.Camera ?? false) && PontosDevemAparecer(pl.Estado, _foco), (_indicadores?.Microfone ?? false) && PontosDevemAparecer(pl.Estado, _foco),
            GravandoNoPlacar && PontosDevemAparecer(pl.Estado, _foco));
        if (animar && pl.Estado == _estado && assinatura.Equals(_ultimoPlano) && pl.CenaCorpo == null)
        {
            if (_expandida && _porAtalho) LigarVigia(); else _vigiaClique.Stop();
            return;
        }
        _ultimoPlano = assinatura;
        var anterior = _estado;
        _estado = pl.Estado;
        AvisarCartao(_estado == Estado.Expandida ? _foco : null);
        // recolheu: o teclado volta para a janela de antes (o campo do módulo fecha junto)
        if (_estado is Estado.Compacta or Estado.Ociosa or Estado.Oculta && Entrada?.Aberta == true)
            Programa.NaInterface(() => { if (_estado is not (Estado.Expandida or Estado.Alerta)) Entrada?.Fechar(); });
        int geracao = ++_geracao;
        var c = _g.Compositor;
        var mola = _cfg.Mola;

        if (pl.Estado == Estado.Oculta)
        {
            _vigiaClique.Stop();
            TrocarCena(_vagaCorpo, null, null, 0, animar);
            TrocarCena(_vagaBolha, null, null, 0, animar);
            TrocarCena(_vagaBolhaE, null, null, 0, animar);
            foreach (var bola in _coluna)
            {
                TrocarCena(bola.Vaga, null, null, 0, animar);
                AnimarEscalar(bola.Forma, "Opacity", 0, 150, animar);
            }
            _rColuna.Clear();
            AnimarEscalar(_pontos, "Opacity", 0, 120, animar);
            AnimarEscalar(_formaBolha, "Opacity", 0, 150, animar);
            AnimarEscalar(_formaBolhaE, "Opacity", 0, 150, animar);
            if (!_janelasVisiveis) { _raiz.Offset = new Vector3(0, -_p.Px(ALTURA_MAX), 0); return; }
            var lote = c.CreateScopedBatch(CompositionBatchTypes.Animation);
            float acima = _cfg.Colada ? 0 : _p.Px(_cfg.DistanciaDoTopo);
            Mover(_raiz, new Vector3(0, -(acima + _alturaAlvoPx + _p.Px(SOMBRA) + _p.Px(12)), 0), animar, amortecimento: 0.9f);
            lote.End();
            lote.Completed += (_, _) => { if (geracao == _geracao && _estado == Estado.Oculta) MostrarJanelas(false); };
            Nativo.SetWindowRgn(_toque.Handle, Nativo.CreateRoundRectRgn(0, 0, 0, 0, 0, 0), false);
            _regiaoAplicada = default;
            _regiaoPendente = null;
            _relogioRegiao.Stop();
            LigarSonda();
            if (anterior != Estado.Oculta) EstadoMudou?.Invoke(_estado);
            return;
        }

        bool colada = _cfg.Colada;
        float topo = colada ? 0 : _p.Px(_cfg.DistanciaDoTopo);
        float cx = MathF.Round(_janL / 2f);
        var corpoPx = _p.Px(pl.Corpo);
        float raioPx = _p.Px(pl.Raio);
        _alturaAlvoPx = corpoPx.Y;

        // janela alta o bastante para o alvo, o overshoot e a sombra; encolhe só depois de assentar
        int alturaNova = (int)MathF.Ceiling(topo + corpoPx.Y * 1.12f + _p.Px(SOMBRA));
        if (alturaNova > _janA) { _janA = alturaNova; AjustarJanelas(); }

        float bolhaPx = _p.Px(BOLHA);
        bool vinhaOculta = !_janelasVisiveis;
        if (vinhaOculta)
        {
            MolaV2(_geoCorpo, "Size", new Vector2(corpoPx.X * 0.6f, corpoPx.Y * 0.6f + (colada ? raioPx * 0.6f : 0)), false, mola);
            MolaV2(_geoCorpo, "CornerRadius", new Vector2(raioPx * 0.6f), false, mola);
            MolaV2(_geoBolha, "Size", new Vector2(bolhaPx * 0.4f), false, mola);
            MolaV2(_geoBolha, "CornerRadius", new Vector2(bolhaPx * 0.2f), false, mola);
            MolaV2(_geoBolhaE, "Size", new Vector2(bolhaPx * 0.4f), false, mola);
            MolaV2(_geoBolhaE, "CornerRadius", new Vector2(bolhaPx * 0.2f), false, mola);
            foreach (var bola in _coluna)
            {
                MolaV2(bola.Geo, "Size", new Vector2(bolhaPx * 0.4f), false, mola);
                MolaV2(bola.Geo, "CornerRadius", new Vector2(bolhaPx * 0.2f), false, mola);
            }
            _raiz.Offset = new Vector3(0, -(topo + corpoPx.Y + _p.Px(SOMBRA)), 0);
            MostrarJanelas(true);
        }

        var lote2 = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        Mover(_raiz, Vector3.Zero, animar);
        MolaV2(_geoCorpo, "Size", new Vector2(corpoPx.X, corpoPx.Y + (colada ? raioPx : 0)), animar, mola);
        MolaV2(_geoCorpo, "CornerRadius", new Vector2(raioPx), animar, mola);

        if (pl.Bolha)
        {
            MolaV2(_geoBolha, "Size", new Vector2(bolhaPx, bolhaPx + (colada ? bolhaPx / 2 : 0)), animar, mola);
            MolaV2(_geoBolha, "CornerRadius", new Vector2(bolhaPx / 2), animar, mola);
            AnimarEscalar(_formaBolha, "Opacity", 1, 160, animar);
        }
        else
        {
            MolaV2(_geoBolha, "Size", new Vector2(bolhaPx * 0.4f, bolhaPx * 0.4f + (colada ? bolhaPx * 0.2f : 0)), animar, mola);
            MolaV2(_geoBolha, "CornerRadius", new Vector2(bolhaPx * 0.2f), animar, mola);
            AnimarEscalar(_formaBolha, "Opacity", 0, 140, animar);
        }
        if (pl.BolhaE)
        {
            MolaV2(_geoBolhaE, "Size", new Vector2(bolhaPx, bolhaPx + (colada ? bolhaPx / 2 : 0)), animar, mola);
            MolaV2(_geoBolhaE, "CornerRadius", new Vector2(bolhaPx / 2), animar, mola);
            AnimarEscalar(_formaBolhaE, "Opacity", 1, 160, animar);
        }
        else
        {
            MolaV2(_geoBolhaE, "Size", new Vector2(bolhaPx * 0.4f, bolhaPx * 0.4f + (colada ? bolhaPx * 0.2f : 0)), animar, mola);
            MolaV2(_geoBolhaE, "CornerRadius", new Vector2(bolhaPx * 0.2f), animar, mola);
            AnimarEscalar(_formaBolhaE, "Opacity", 0, 140, animar);
        }

        TrocarCena(_vagaCorpo, pl.ChaveCorpo, pl.CenaCorpo, corpoPx.Y, animar);
        // o conteúdo da compacta desliza para a esquerda quando as bolinhas pedem espaço
        float extra = pl.Estado == Estado.Compacta ? _p.Px(_extraPontos) : 0;
        Mover(_vagaCorpo.Host, new Vector3(cx - MathF.Round(extra / 2), topo, 0), animar);
        PosicionarPontos(pl.Estado, corpoPx, topo, cx, animar);
        TrocarCena(_vagaBolha, pl.Bolha ? pl.ChaveBolha : null, pl.CenaBolha, bolhaPx, animar);
        TrocarCena(_vagaBolhaE, pl.BolhaE ? pl.ChaveBolhaE : null, pl.CenaBolhaE, bolhaPx, animar);
        _rColuna.Clear();
        for (int k = 0; k < _coluna.Count; k++)
        {
            var bola = _coluna[k];
            bool on = pl.Coluna != null && k < pl.Coluna.Count;
            if (on)
            {
                MolaV2(bola.Geo, "Size", new Vector2(bolhaPx), animar, mola);
                MolaV2(bola.Geo, "CornerRadius", new Vector2(bolhaPx / 2), animar, mola);
                AnimarEscalar(bola.Forma, "Opacity", 1, 160 + k * 40, animar);
                TrocarCena(bola.Vaga, pl.Coluna![k].Chave, pl.Coluna[k].Criar, bolhaPx, animar);
                _rColuna.Add(new System.Drawing.RectangleF(cx + corpoPx.X / 2 + _p.Px(VAO), topo + k * _p.Px(BOLHA + COLUNA_VAO), bolhaPx, bolhaPx));
            }
            else
            {
                MolaV2(bola.Geo, "Size", new Vector2(bolhaPx * 0.4f), animar, mola);
                MolaV2(bola.Geo, "CornerRadius", new Vector2(bolhaPx * 0.2f), animar, mola);
                AnimarEscalar(bola.Forma, "Opacity", 0, 140, animar);
                TrocarCena(bola.Vaga, null, null, bolhaPx, animar);
            }
        }
        lote2.End();

        // região de toque: o formato final da pílula (e da bolha). Encolhendo, vale na hora; crescendo, só quando a mola
        // assentar: antes disso a região maior que o desenho segurava o mouse (e o clique) em cima de outro app
        _rCorpo = new System.Drawing.RectangleF(cx - corpoPx.X / 2, topo, corpoPx.X, corpoPx.Y);
        _temBolha = pl.Bolha;
        _rBolha = new System.Drawing.RectangleF(cx + corpoPx.X / 2 + _p.Px(VAO), topo, bolhaPx, bolhaPx);
        _temBolhaE = pl.BolhaE;
        _rBolhaE = new System.Drawing.RectangleF(cx - corpoPx.X / 2 - _p.Px(VAO) - bolhaPx, topo, bolhaPx, bolhaPx);
        var final = System.Drawing.Rectangle.Round(_rCorpo);
        bool cresce = !_sobre && !_regiaoAplicada.IsEmpty && (final.Width > _regiaoAplicada.Width || final.Height > _regiaoAplicada.Height);
        var regiao = (rc: _rCorpo, rb: _rBolha, bolha: pl.Bolha, raio: raioPx, bolhaPx, topo, colada, rbe: _rBolhaE, bolhaE: pl.BolhaE, col: _rColuna.ToList());
        Action aplicar = () => AplicarRegiao(regiao.rc, regiao.rb, regiao.bolha, regiao.raio, regiao.bolhaPx, regiao.topo, regiao.colada, regiao.rbe, regiao.bolhaE, regiao.col);
        _relogioRegiao.Stop();
        if (cresce) { _regiaoPendente = aplicar; _relogioRegiao.Start(); }
        else { _regiaoPendente = null; aplicar(); }
        lote2.Completed += (_, _) =>
        {
            if (geracao != _geracao) return;
            int justa = (int)MathF.Ceiling(topo + _alturaAlvoPx * 1.12f + _p.Px(SOMBRA));
            if (justa < _janA) { _janA = justa; AjustarJanelas(); }
        };
        LigarSonda();

        if (_expandida && _porAtalho) LigarVigia(); else _vigiaClique.Stop();
        if (anterior != _estado) EstadoMudou?.Invoke(_estado);
    }

    private (Estado, Vector2, float, string?, bool, string?, bool, bool, bool, bool) _ultimoPlano;

    private void AplicarRegiao(System.Drawing.RectangleF rc, System.Drawing.RectangleF rb, bool bolha, float raioPx, float bolhaPx, float topo, bool colada,
        System.Drawing.RectangleF rbe = default, bool bolhaE = false, List<System.Drawing.RectangleF>? coluna = null)
    {
        int r2 = (int)(raioPx * 2);
        var rgn = Nativo.CreateRoundRectRgn((int)rc.Left, (int)(topo - (colada ? raioPx : 0)), (int)rc.Right + 1, (int)rc.Bottom + 1, r2, r2);
        foreach (var (on, b) in new[] { (bolha, rb), (bolhaE, rbe) })
        {
            if (!on) continue;
            int b2 = (int)bolhaPx;
            var r = Nativo.CreateRoundRectRgn((int)b.Left, (int)(topo - (colada ? bolhaPx / 2 : 0)), (int)b.Right + 1, (int)b.Bottom + 1, b2, b2);
            Nativo.CombineRgn(rgn, rgn, r, Nativo.RGN_OR);
            Nativo.DeleteObject(r);
        }
        foreach (var b in coluna ?? new())
        {
            int b2 = (int)bolhaPx;
            var r = Nativo.CreateRoundRectRgn((int)b.Left, (int)b.Top, (int)b.Right + 1, (int)b.Bottom + 1, b2, b2);
            Nativo.CombineRgn(rgn, rgn, r, Nativo.RGN_OR);
            Nativo.DeleteObject(r);
        }
        // movimento que o Windows manda por causa desta troca, com o cursor parado, não conta como entrar
        if (Nativo.GetCursorPos(out var cur)) _cursorNaMudanca = cur;
        Nativo.SetWindowRgn(_toque.Handle, rgn, false);
        _regiaoAplicada = System.Drawing.Rectangle.Round(rc);
    }

    /// <summary>Quem vai para a bolha: a segunda da fila, menos a privacidade (as bolinhas dela já ficam na pílula).</summary>
    private static Atividade? DaBolha(List<Atividade> vis) => vis.Skip(1).FirstOrDefault(a => a.Tipo != "privacidade");

    /// <summary>Who goes to the left bubble: the one after the right bubble's, the privacy one left out the same way.</summary>
    private static Atividade? DaBolhaE(List<Atividade> vis) => vis.Skip(1).Where(a => a.Tipo != "privacidade").Skip(1).FirstOrDefault();

    private void AvisarCartao(Atividade? cartao)
    {
        if (cartao == _cartaoAvisado) return;
        var velho = _cartaoAvisado;
        _cartaoAvisado = cartao;
        // depois do Aplicar (a atividade pode querer mexer na ilha de volta)
        Programa.NaInterface(() =>
        {
            try { velho?.ExpandidaMudou(false); cartao?.ExpandidaMudou(true); }
            catch (Exception e) { Log.Escrever("ExpandidaMudou: " + e); }
        });
    }

    private float LarguraPontos()
    {
        int n = (_indicadores?.Camera == true ? 1 : 0) + (_indicadores?.Microfone == true ? 1 : 0) + (GravandoNoPlacar ? 1 : 0);
        return n == 0 ? 0 : n * PONTO + (n - 1) * PONTO_VAO;
    }

    // a própria atividade de privacidade já mostra as bolinhas. A hora com o anel em volta já avisa (compacta), e o menu
    // põe as bolinhas do lado da hora dele. In a competitive game they show only in score mode (3.9.0, his request: the
    // score pill carries the dots, plus a red one while something records the screen; before, none showed over a game)
    private bool PontosDevemAparecer(Estado e, Atividade? principal) =>
        (_indicadores?.Algum == true || GravandoNoPlacar) && (!_ctx.Competitivo || _cfg.ModoCompetitivo == ModosCompetitivos.Placar) && !_cfg.Pausada
        && e is Estado.Compacta or Estado.Expandida or Estado.Alerta
        && (e != Estado.Compacta || Visiveis().FirstOrDefault()?.Tipo != "privacidade" && !_anelNaPilula)
        && !(e == Estado.Expandida && principal?.Tipo is "privacidade" or "menu");

    /// <summary>The red dot: only over a competitive game in score mode (elsewhere the recording activities show).</summary>
    private bool GravandoNoPlacar => _indicadores?.Gravando == true && _ctx.Competitivo && _cfg.ModoCompetitivo == ModosCompetitivos.Placar;

    private void PosicionarPontos(Estado e, Vector2 corpoPx, float topo, float cx, bool animar)
    {
        var vis = Visiveis();
        bool mostrar = PontosDevemAparecer(e, e == Estado.Expandida ? _foco : vis.FirstOrDefault());
        _pontosVisiveis = mostrar;
        AnimarEscalar(_pontos, "Opacity", mostrar ? 1 : 0, 160, animar);
        if (!mostrar) return;
        float d = _p.Px(PONTO), vao = _p.Px(PONTO_VAO);
        float x = 0;
        foreach (var (ponto, ligado) in new[] { (_pontoGravando, GravandoNoPlacar), (_pontoMic, _indicadores!.Microfone), (_pontoCamera, _indicadores.Camera) })
        {
            ponto.Opacity = ligado ? 1 : 0;
            ponto.Offset = new Vector3(x, 0, 0);
            if (ligado) x += d + vao;
        }
        float largura = x - vao;
        // compacta: centradas na altura, no espaço extra da direita; cartões: no canto de cima, dentro da folga
        float margem = e == Estado.Compacta ? _p.Px(COMPACTA_FOLGA) : _p.Px(CARTAO_FOLGA) * 0.8f;
        float y = e == Estado.Compacta ? (corpoPx.Y - d) / 2 : _p.Px(CARTAO_FOLGA) * 0.4f;
        Mover(_pontos, new Vector3(MathF.Round(cx + corpoPx.X / 2 - margem - largura), MathF.Round(topo + y), 0), animar);
    }

    private void LigarVigia()
    {
        if (_vigiaClique.Enabled) return;
        _arrasteDeFora = false;
        // zera o "apertado desde a última consulta": o clique que abriu a ilha (no ícone da bandeja) não pode fechá-la
        Nativo.GetAsyncKeyState(0x01);
        Nativo.GetAsyncKeyState(0x02);
        _vigiaClique.Start();
    }

    // ------------------------------------------------------------------ cenas

    private void TrocarCena(Vaga v, string? chave, Func<Cena>? criar, float alturaPx, bool animar)
    {
        if (v.Chave == chave)
        {
            // mesma cena: só reposiciona na altura nova (a prévia do hover muda a altura)
            if (v.Atual != null) Mover(v.Atual.Sprite, Posicao(v.Atual.Cena, alturaPx), animar);
            return;
        }
        if (v.Atual != null) Sair(v.Atual, animar);
        v.Atual = null;
        v.Chave = chave;
        if (chave == null || criar == null) return;
        var cena = criar();
        v.Atual = Entrar(v, cena, alturaPx, animar);
    }

    /// <summary>
    /// Folga em volta de toda cena: a superfície pega também o que passa um pouco da cena (a logo do Claude ampliada e
    /// as bolinhas chegam a 56% do lado da caixa delas, e a logo do alerta de permissão fica em posição negativa).
    /// Sem ela o desenho saía cortado na borda da cena; com ela, quem recorta é só o formato da pílula.
    /// </summary>
    private const float FOLGA_CENA = 12;

    private Vector3 Posicao(Cena cena, float alturaPx)
    {
        var px = _p.Px(cena.Tamanho);
        float f = _p.Px(FOLGA_CENA);
        return new Vector3(-MathF.Round(px.X / 2) - f, MathF.Round((alturaPx - px.Y) / 2) - f, 0);
    }

    /// <summary>Onde fica o canto da cena (sem a folga), relativo à vaga.</summary>
    private Vector3 OrigemDaCena(Quadro q) => q.Sprite.Offset + new Vector3(_p.Px(FOLGA_CENA), _p.Px(FOLGA_CENA), 0);

    private Quadro Entrar(Vaga v, Cena cena, float alturaPx, bool animar)
    {
        var c = _g.Compositor;
        float f = _p.Px(FOLGA_CENA);
        var px = _p.Px(cena.Tamanho) + new Vector2(2 * f);
        var vs = c.CreateVisualSurface();
        vs.SourceVisual = cena.Raiz;
        vs.SourceOffset = new Vector2(-f);
        vs.SourceSize = px;
        var pincel = c.CreateSurfaceBrush(vs);
        var sprite = c.CreateSpriteVisual();
        sprite.Size = px;
        sprite.Offset = Posicao(cena, alturaPx);
        sprite.CenterPoint = new Vector3(px.X / 2, px.Y / 2, 0);
        var q = new Quadro { Sprite = sprite, Vs = vs, Pincel = pincel, Cena = cena };
        v.Host.Children.InsertAtTop(sprite);
        if (!animar) { sprite.Brush = pincel; return q; }

        // entra com fade, um pouco de escala e saindo do desfoque; depois troca para o pincel sem efeito
        var ef = _desfoque!.CreateBrush();
        ef.SetSourceParameter("src", pincel);
        ef.Properties.InsertScalar("Blur.BlurAmount", _p.Px(7));
        sprite.Brush = ef;
        q.Efeito = ef;
        sprite.Opacity = 0;
        sprite.Scale = new Vector3(0.88f, 0.88f, 1);
        var lote = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        var facil = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));
        var op = c.CreateScalarKeyFrameAnimation();
        op.InsertKeyFrame(1f, 1f, facil);
        op.Duration = TimeSpan.FromMilliseconds(260);
        op.DelayTime = TimeSpan.FromMilliseconds(50);
        op.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        sprite.StartAnimation("Opacity", op);
        var bl = c.CreateScalarKeyFrameAnimation();
        bl.InsertKeyFrame(1f, 0f, facil);
        bl.Duration = TimeSpan.FromMilliseconds(320);
        ef.StartAnimation("Blur.BlurAmount", bl);
        var es = c.CreateSpringVector3Animation();
        es.FinalValue = Vector3.One;
        es.DampingRatio = 0.72f;
        es.Period = TimeSpan.FromMilliseconds(55);
        sprite.StartAnimation("Scale", es);
        lote.End();
        lote.Completed += (_, _) =>
        {
            if (q.Efeito == null || q.Saindo || q.Limpo) return;
            sprite.Brush = pincel;
            q.Efeito.Dispose();
            q.Efeito = null;
        };
        return q;
    }

    private readonly List<Quadro> _saindo = new();

    private static void Limpar(Quadro q)
    {
        if (q.Limpo) return;
        q.Limpo = true;
        q.Sprite.Parent?.Children.Remove(q.Sprite);
        q.Efeito?.Dispose();
        q.Efeito = null;
        q.Sprite.Dispose();
        q.Pincel.Dispose();
        q.Vs.Dispose();
        q.Cena.Soltar();
    }

    private void Sair(Quadro q, bool animar)
    {
        q.Saindo = true;
        if (!animar) { Limpar(q); return; }
        _saindo.Add(q);
        var c = _g.Compositor;
        var ef = q.Efeito ?? _desfoque!.CreateBrush();
        if (q.Efeito == null) { ef.SetSourceParameter("src", q.Pincel); ef.Properties.InsertScalar("Blur.BlurAmount", 0f); }
        q.Efeito = ef;
        q.Sprite.Brush = ef;
        var lote = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        var facil = c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));
        var op = c.CreateScalarKeyFrameAnimation();
        op.InsertKeyFrame(1f, 0f, facil);
        op.Duration = TimeSpan.FromMilliseconds(150);
        q.Sprite.StartAnimation("Opacity", op);
        var bl = c.CreateScalarKeyFrameAnimation();
        bl.InsertKeyFrame(1f, _p.Px(6), facil);
        bl.Duration = TimeSpan.FromMilliseconds(150);
        ef.StartAnimation("Blur.BlurAmount", bl);
        var sc = c.CreateVector3KeyFrameAnimation();
        sc.InsertKeyFrame(1f, new Vector3(0.92f, 0.92f, 1), facil);
        sc.Duration = TimeSpan.FromMilliseconds(150);
        q.Sprite.StartAnimation("Scale", sc);
        lote.End();
        lote.Completed += (_, _) => { _saindo.Remove(q); Limpar(q); };
    }

    // ------------------------------------------------------------------ animações

    private void MolaV2(CompositionObject o, string prop, Vector2 alvo, bool animar, Mola mola)
    {
        if (!animar)
        {
            o.StopAnimation(prop);
            if (o is CompositionRoundedRectangleGeometry g)
            {
                if (prop == "Size") g.Size = alvo; else g.CornerRadius = alvo;
            }
            return;
        }
        var a = _g.Compositor.CreateSpringVector2Animation();
        a.FinalValue = alvo;
        a.DampingRatio = mola.Amortecimento;
        a.Period = TimeSpan.FromMilliseconds(mola.PeriodoMs);
        o.StartAnimation(prop, a);
    }

    private void Mover(Visual v, Vector3 alvo, bool animar, float? amortecimento = null)
    {
        if (!animar) { v.StopAnimation("Offset"); v.Offset = alvo; return; }
        var a = _g.Compositor.CreateSpringVector3Animation();
        a.FinalValue = alvo;
        a.DampingRatio = amortecimento ?? _cfg.Mola.Amortecimento;
        a.Period = TimeSpan.FromMilliseconds(_cfg.Mola.PeriodoMs);
        v.StartAnimation("Offset", a);
    }

    private void AnimarEscalar(Visual v, string prop, float alvo, int ms, bool animar)
    {
        if (!animar) { v.StopAnimation(prop); v.Opacity = alvo; return; }
        var a = _g.Compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1f, alvo);
        a.Duration = TimeSpan.FromMilliseconds(ms);
        v.StartAnimation(prop, a);
    }

    public void Dispose()
    {
        _alerta?.PararSom();
        _relogioAlerta.Dispose();
        _relogioSaida.Dispose();
        _vigiaClique.Dispose();
        _relogioSonda.Dispose();
        _minutoRelogio.Dispose();
        _vigiaSobre.Dispose();
        _vigiaArraste.Dispose();
        _vigiaTopo.Dispose();
        _relogioRegiao.Dispose();
        _sonda.Dispose();
        if (_alvoDeSoltar != null) AlvoDeSoltar.Revogar(_toque.Handle);
        Desmontar();
        _alvo?.Dispose();
        _desfoque?.Dispose();
    }
}
