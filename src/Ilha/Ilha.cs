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
    private const float LARGURA_JANELA = 760, ALTURA_MAX = 400;
    private const float OCIOSA_L = 128, OCIOSA_A = 32;
    private const float COMPACTA_A = 36, COMPACTA_FOLGA = 12, COMPACTA_MIN = 168;
    private const float PREVIA_L = 14, PREVIA_A = 4;
    private const float BOLHA = 36, VAO = 14;
    private const float CARTAO_FOLGA = 18, CARTAO_MIN = 340, RAIO_EXPANDIDA = 30, RAIO_ALERTA = 28;
    private const float OMBRO = 7;
    private const float SOMBRA = 30;
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
    private CompositionRoundedRectangleGeometry _geoCorpo = null!, _geoBolha = null!;
    private ShapeVisual _formaBolha = null!;
    private readonly List<SpriteVisual> _ombros = new();
    private readonly List<Superficie> _fixas = new();
    private Vaga _vagaCorpo = null!, _vagaBolha = null!;

    // situação
    private Contexto _ctx = new();
    private readonly List<Atividade> _atividades = new();
    private readonly Queue<Alerta> _fila = new();
    private Alerta? _alerta;
    private readonly System.Windows.Forms.Timer _relogioAlerta = new();
    private readonly System.Windows.Forms.Timer _relogioSaida = new() { Interval = 320 };
    private readonly System.Windows.Forms.Timer _vigiaClique = new() { Interval = 35 };
    private bool _sobre, _expandida, _porAtalho;
    private Atividade? _foco;
    private Atividade? _painel;
    private Indicadores? _indicadores;
    private ContainerVisual _pontos = null!;
    private SpriteVisual _pontoCamera = null!, _pontoMic = null!;
    private bool _pontosVisiveis;
    private float _extraPontos;

    /// <summary>Toca o som de cada alerta e chegada (null = sem som).</summary>
    public Sons? Sons { get; set; }

    /// <summary>Câmera e microfone em uso: a ilha desenha as bolinhas verde e laranja no canto da pílula.</summary>
    public Indicadores? Indicadores
    {
        get => _indicadores;
        set
        {
            _indicadores = value;
            if (value != null) value.Mudou += () => Aplicar();
        }
    }
    private Estado _estado = Estado.Oculta;
    private bool _janelasVisiveis;

    // geometria atual em pixels, relativa à janela
    private int _janX, _janY, _janL, _janA;
    private System.Drawing.RectangleF _rCorpo, _rBolha;
    private bool _temBolha;
    private float _alturaAlvoPx;
    private int _geracao;

    public event Action<Estado>? EstadoMudou;
    public Estado EstadoAtual => _estado;
    public IntPtr JanelaVisual => _vis.Handle;

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

        _toque.Moveu += (_, _) => Entrou();
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
        _relogioSaida.Tick += (_, _) => { _relogioSaida.Stop(); if (!_sobre && _expandida && !_porAtalho) Recolher(); };
        _vigiaClique.Tick += (_, _) => VigiarCliqueFora();

        Reposicionar(forcar: true);
    }

    public Palco Palco => _p;

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

        if (_cfg.Sombra) Sombras(c);
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
            };
            for (int i = 0; i < 4; i++)
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
                v.StartAnimation("Offset", e);
                if (i >= 2)
                {
                    var op = c.CreateExpressionAnimation("fb.Opacity");
                    op.SetReferenceParameter("fb", _formaBolha);
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

        _vagaCorpo = new Vaga { Host = hostCorpo };
        _vagaBolha = new Vaga { Host = hostBolha };

        // bolinhas de câmera (verde) e microfone (laranja), como as do iPhone
        _pontos = c.CreateContainerVisual();
        _pontos.Opacity = 0;
        _pontoCamera = Ponto(c, Cor.Verde);
        _pontoMic = Ponto(c, Cor.Laranja);
        _pontos.Children.InsertAtTop(_pontoCamera);
        _pontos.Children.InsertAtTop(_pontoMic);
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
        var camadas = new (float sigma, float alfa, float dy)[] { (2.5f, 0.30f, 1f), (11f, 0.26f, 7f) };
        const float R = 18;
        foreach (var (sigma, alfa, dy) in camadas)
        {
            float M = MathF.Ceiling(sigma * 3);
            float lado = 2 * (M + R) + 2;
            var sup = _g.NovaSuperficie(new Vector2(lado), (dc, t) => Borrao(dc, t, M, R, sigma, alfa));
            _fixas.Add(sup);
            float m = _p.Px(M), ins = _p.Px(M + R), d = _p.Px(dy);
            foreach (var (geo, ligada) in new[] { (_geoCorpo, (Visual?)null), (_geoBolha, _formaBolha) })
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
        foreach (var v in new[] { _vagaCorpo, _vagaBolha })
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
        _raiz?.Dispose();
        _ombros.Clear();
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
        _cfg = cfg;
        if (forma) Reposicionar(forcar: true);
        else Aplicar();
    }

    private void AjustarJanelas()
    {
        foreach (var h in new[] { _vis.Handle, _toque.Handle })
            Nativo.SetWindowPos(h, Nativo.HWND_TOPMOST, _janX, _janY, _janL, _janA, Nativo.SWP_NOACTIVATE);
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
        else _sobre = false;
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
    public void DefinirPainel(Atividade a) => _painel = a;

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
        if (trocouFrente && _expandida && _porAtalho) _expandida = false;
        ReafirmarTopo();
        Aplicar();
    }

    private bool Permitida(Atividade a) => !_ctx.Competitivo || a.PermitidaNoCompetitivo;

    private int Prioridade(string tipo)
    {
        int i = _cfg.Prioridades.FindIndex(p => string.Equals(p, tipo, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 100 : i;
    }

    private List<Atividade> Visiveis() => _atividades
        .Where(a => a.Ativa && Permitida(a))
        .OrderBy(a => Prioridade(a.Tipo))
        .ThenByDescending(a => a.Desde)
        .ToList();

    // ------------------------------------------------------------------ alertas

    public void Alertar(Alerta a)
    {
        if (_cfg.Pausada) return;
        if (a.Dono != null && a.Dono != _painel && !_atividades.Contains(a.Dono)) return;
        if (_ctx.Competitivo && !a.PermitidoNoCompetitivo) return;
        if (_ctx.NaoPerturbe && _cfg.RespeitarNaoPerturbe && !a.Importante) return;
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
        !_cfg.Pausada && (!_ctx.Competitivo || a.PermitidoNoCompetitivo) && !(_ctx.NaoPerturbe && _cfg.RespeitarNaoPerturbe && !a.Importante);

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
            if (som.Length > 0) Sons?.Tocar(som, _alerta.Importante);
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
        _relogioSaida.Stop();
        if (_sobre) return;
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
        _sobre = false;
        if (_expandida && !_porAtalho) _relogioSaida.Start();
        else if (_estado is Estado.Compacta or Estado.Ociosa) Aplicar();
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
        var o = v.Atual.Sprite.Offset;
        var host = v.Host.Offset;
        return new Vector2((x - host.X - o.X) / _g.Escala, (y - host.Y - o.Y) / _g.Escala);
    }

    private void Apertou(int x, int y)
    {
        _arraste = null;
        _cenaApertada = _vagaCorpo.Atual == null ? null : SemVersao(_vagaCorpo.Chave);
        if (_vagaCorpo.Atual == null) return;
        var l = Local(_vagaCorpo, x, y);
        foreach (var b in _vagaCorpo.Atual.Cena.Botoes)
            if (b.Arrastar != null && b.Area.Contains(l.X, l.Y))
            {
                _arraste = b.Arrastar;
                _inicioArraste = l;
                _arrastou = false;
                _arraste(l.X, l.Y, false);
                return;
            }
    }

    private void Rolou(int delta)
    {
        var a = _estado == Estado.Expandida ? _foco : _estado == Estado.Compacta ? Visiveis().FirstOrDefault() : null;
        try { a?.Rolar(delta); } catch (Exception e) { Log.Escrever("rolar: " + e); }
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
        if (_estado == Estado.Expandida)
        {
            if (mesmaCena) ClicarBotao(_vagaCorpo, x, y);
            return;
        }
        var vis = Visiveis();
        var daBolha = DaBolha(vis);
        if (_temBolha && _rBolha.Contains(x, y) && daBolha != null) _foco = daBolha;
        else if (_estado == Estado.Compacta && _pontosVisiveis && _extraPontos > 0
                 && x >= _rCorpo.Right - _p.Px(COMPACTA_FOLGA + _extraPontos)
                 && vis.FirstOrDefault(a => a.Tipo == "privacidade") is { } privacidade) _foco = privacidade;
        else if (vis.Count > 0) _foco = vis[0];
        else _foco = _ctx.Competitivo ? null : _painel;
        if (_foco == null || !_foco.TemExpandida) return;
        _expandida = true;
        _porAtalho = false;
        Aplicar();
    }

    private bool ClicarBotao(Vaga v, int x, int y)
    {
        if (v.Atual == null) return false;
        var o = v.Atual.Sprite.Offset;
        var host = v.Host.Offset;
        float lx = (x - host.X - o.X) / _g.Escala, ly = (y - host.Y - o.Y) / _g.Escala;
        foreach (var b in v.Atual.Cena.Botoes)
            if (b.Area.Contains(lx, ly)) { b.Acao(); return true; }
        return false;
    }

    /// <summary>Atalho global: abre a atividade principal (ou o painel) expandida; de novo, recolhe.</summary>
    public void Atalho()
    {
        if (_cfg.Pausada) return;
        if (_expandida) { Recolher(); return; }
        // o clique no ícone da bandeja já recolheu a ilha pelo "clique fora": não reabrir no mesmo clique
        if (Environment.TickCount64 - _recolheuPorCliqueFora < 400) return;
        if (_alerta != null) { _relogioAlerta.Stop(); _fila.Clear(); _alerta = null; }
        var vis = Visiveis();
        _foco = vis.FirstOrDefault(a => a.TemExpandida) ?? (_ctx.Competitivo ? null : _painel);
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
        _foco = null;
        Aplicar();
    }

    private long _recolheuPorCliqueFora;

    private void VigiarCliqueFora()
    {
        if (!_expandida || _sobre) return;
        // 0x8001: apertado agora ou desde a última consulta (um clique rápido entre duas leituras também conta)
        bool apertou = (Nativo.GetAsyncKeyState(0x01) & 0x8001) != 0 || (Nativo.GetAsyncKeyState(0x02) & 0x8001) != 0;
        if (!apertou) return;
        Nativo.GetCursorPos(out var c);
        float x = c.X - _janX, y = c.Y - _janY;
        if (!_rCorpo.Contains(x, y)) { _recolheuPorCliqueFora = Environment.TickCount64; Recolher(); }
    }

    // ------------------------------------------------------------------ estado e layout

    private sealed record Plano(Estado Estado, Vector2 Corpo, float Raio, string? ChaveCorpo, Func<Cena>? CenaCorpo,
        bool Bolha, string? ChaveBolha, Func<Cena>? CenaBolha);

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
            var al = _alerta;
            return Cartao(Estado.Alerta, $"alerta:{al.GetHashCode()}:{al.Dono?.Versao ?? 0}", () => al.Criar(_p), RAIO_ALERTA);
        }

        bool avulsa = _foco == _painel || _foco?.EhFerramenta == true;
        if (_expandida && _foco != null && (_foco.Ativa || avulsa) && (Permitida(_foco) || avulsa && !_ctx.Competitivo))
        {
            var f = _foco;
            return Cartao(Estado.Expandida, $"exp:{f.GetHashCode()}:{f.Versao}", () => f.Criar(_p, Forma.Expandida), RAIO_EXPANDIDA);
        }
        _expandida = false;

        if (vis.Count > 0)
        {
            var a = vis[0];
            string ch = $"cmp:{a.GetHashCode()}:{a.Versao}";
            var tam = TamanhoDaCena(_vagaCorpo, ch, () => a.Criar(_p, Forma.Compacta), out var criar);
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
            return new Plano(Estado.Compacta, new Vector2(l, h), h / 2, ch, criar, bolha, cb, fb);
        }

        if (_ctx.Desktop && !_ctx.Competitivo && _cfg.OciosaNaAreaDeTrabalho)
        {
            float l = OCIOSA_L, h = OCIOSA_A;
            if (_sobre) { l += PREVIA_L * 0.6f; h += PREVIA_A * 0.6f; }
            return new Plano(Estado.Ociosa, new Vector2(l, h), h / 2, null, null, false, null, null);
        }

        return new Plano(Estado.Oculta, Vector2.Zero, 0, null, null, false, null, null);
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
        var assinatura = (pl.Estado, pl.Corpo, pl.Raio, pl.ChaveCorpo, pl.Bolha, pl.ChaveBolha, _janelasVisiveis || pl.Estado == Estado.Oculta,
            (_indicadores?.Camera ?? false) && PontosDevemAparecer(pl.Estado, _foco), (_indicadores?.Microfone ?? false) && PontosDevemAparecer(pl.Estado, _foco));
        if (animar && pl.Estado == _estado && assinatura.Equals(_ultimoPlano) && pl.CenaCorpo == null)
        {
            if (_expandida && _porAtalho) LigarVigia(); else _vigiaClique.Stop();
            return;
        }
        _ultimoPlano = assinatura;
        var anterior = _estado;
        _estado = pl.Estado;
        AvisarCartao(_estado == Estado.Expandida ? _foco : null);
        int geracao = ++_geracao;
        var c = _g.Compositor;
        var mola = _cfg.Mola;

        if (pl.Estado == Estado.Oculta)
        {
            _vigiaClique.Stop();
            TrocarCena(_vagaCorpo, null, null, 0, animar);
            TrocarCena(_vagaBolha, null, null, 0, animar);
            AnimarEscalar(_pontos, "Opacity", 0, 120, animar);
            AnimarEscalar(_formaBolha, "Opacity", 0, 150, animar);
            if (!_janelasVisiveis) { _raiz.Offset = new Vector3(0, -_p.Px(ALTURA_MAX), 0); return; }
            var lote = c.CreateScopedBatch(CompositionBatchTypes.Animation);
            float acima = _cfg.Colada ? 0 : _p.Px(_cfg.DistanciaDoTopo);
            Mover(_raiz, new Vector3(0, -(acima + _alturaAlvoPx + _p.Px(SOMBRA) + _p.Px(12)), 0), animar, amortecimento: 0.9f);
            lote.End();
            lote.Completed += (_, _) => { if (geracao == _geracao && _estado == Estado.Oculta) MostrarJanelas(false); };
            Nativo.SetWindowRgn(_toque.Handle, Nativo.CreateRoundRectRgn(0, 0, 0, 0, 0, 0), false);
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

        TrocarCena(_vagaCorpo, pl.ChaveCorpo, pl.CenaCorpo, corpoPx.Y, animar);
        // o conteúdo da compacta desliza para a esquerda quando as bolinhas pedem espaço
        float extra = pl.Estado == Estado.Compacta ? _p.Px(_extraPontos) : 0;
        Mover(_vagaCorpo.Host, new Vector3(cx - MathF.Round(extra / 2), topo, 0), animar);
        PosicionarPontos(pl.Estado, corpoPx, topo, cx, animar);
        TrocarCena(_vagaBolha, pl.Bolha ? pl.ChaveBolha : null, pl.CenaBolha, bolhaPx, animar);
        lote2.End();
        lote2.Completed += (_, _) =>
        {
            if (geracao != _geracao) return;
            int justa = (int)MathF.Ceiling(topo + _alturaAlvoPx * 1.12f + _p.Px(SOMBRA));
            if (justa < _janA) { _janA = justa; AjustarJanelas(); }
        };

        // região de toque: o formato final da pílula (e da bolha)
        _rCorpo = new System.Drawing.RectangleF(cx - corpoPx.X / 2, topo, corpoPx.X, corpoPx.Y);
        _temBolha = pl.Bolha;
        _rBolha = new System.Drawing.RectangleF(cx + corpoPx.X / 2 + _p.Px(VAO), topo, bolhaPx, bolhaPx);
        int r2 = (int)(raioPx * 2);
        var rgn = Nativo.CreateRoundRectRgn((int)_rCorpo.Left, (int)(topo - (colada ? raioPx : 0)), (int)_rCorpo.Right + 1, (int)_rCorpo.Bottom + 1, r2, r2);
        if (pl.Bolha)
        {
            int b2 = (int)bolhaPx;
            var rb = Nativo.CreateRoundRectRgn((int)_rBolha.Left, (int)(topo - (colada ? bolhaPx / 2 : 0)), (int)_rBolha.Right + 1, (int)_rBolha.Bottom + 1, b2, b2);
            Nativo.CombineRgn(rgn, rgn, rb, Nativo.RGN_OR);
            Nativo.DeleteObject(rb);
        }
        Nativo.SetWindowRgn(_toque.Handle, rgn, false);

        if (_expandida && _porAtalho) LigarVigia(); else _vigiaClique.Stop();
        if (anterior != _estado) EstadoMudou?.Invoke(_estado);
    }

    private (Estado, Vector2, float, string?, bool, string?, bool, bool, bool) _ultimoPlano;

    /// <summary>Quem vai para a bolha: a segunda da fila, menos a privacidade (as bolinhas dela já ficam na pílula).</summary>
    private static Atividade? DaBolha(List<Atividade> vis) => vis.Skip(1).FirstOrDefault(a => a.Tipo != "privacidade");

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
        int n = (_indicadores?.Camera == true ? 1 : 0) + (_indicadores?.Microfone == true ? 1 : 0);
        return n == 0 ? 0 : n * PONTO + (n - 1) * PONTO_VAO;
    }

    // a própria atividade de privacidade já mostra as bolinhas; no modo competitivo elas ficam escondidas
    private bool PontosDevemAparecer(Estado e, Atividade? principal) =>
        _indicadores?.Algum == true && !_ctx.Competitivo && !_cfg.Pausada
        && e is Estado.Compacta or Estado.Expandida or Estado.Alerta
        && (e != Estado.Compacta || Visiveis().FirstOrDefault()?.Tipo != "privacidade")
        && !(e == Estado.Expandida && principal?.Tipo == "privacidade");

    private void PosicionarPontos(Estado e, Vector2 corpoPx, float topo, float cx, bool animar)
    {
        var vis = Visiveis();
        bool mostrar = PontosDevemAparecer(e, e == Estado.Expandida ? _foco : vis.FirstOrDefault());
        _pontosVisiveis = mostrar;
        AnimarEscalar(_pontos, "Opacity", mostrar ? 1 : 0, 160, animar);
        if (!mostrar) return;
        float d = _p.Px(PONTO), vao = _p.Px(PONTO_VAO);
        float x = 0;
        foreach (var (ponto, ligado) in new[] { (_pontoMic, _indicadores!.Microfone), (_pontoCamera, _indicadores.Camera) })
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

    private Vector3 Posicao(Cena cena, float alturaPx)
    {
        var px = _p.Px(cena.Tamanho);
        return new Vector3(-MathF.Round(px.X / 2), MathF.Round((alturaPx - px.Y) / 2), 0);
    }

    private Quadro Entrar(Vaga v, Cena cena, float alturaPx, bool animar)
    {
        var c = _g.Compositor;
        var px = _p.Px(cena.Tamanho);
        var vs = c.CreateVisualSurface();
        vs.SourceVisual = cena.Raiz;
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
        _relogioAlerta.Dispose();
        _relogioSaida.Dispose();
        _vigiaClique.Dispose();
        Desmontar();
        _alvo?.Dispose();
        _desfoque?.Dispose();
    }
}
