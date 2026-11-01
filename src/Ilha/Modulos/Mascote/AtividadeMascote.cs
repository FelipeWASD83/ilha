using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Windows.UI.Composition;

namespace Ilha.Mascote;

/// <summary>
/// O mascote dentro da ilha: prioridade mais baixa que tudo ("mascote" fica fora da lista de prioridades), então
/// sozinho na área de trabalho ele ocupa a pílula, e com música ou o Claude Code na frente ele vai para a bolha.
/// Não abre cartão, não anuncia chegada e some no modo competitivo. Cada estado é uma cena: trocar de estado refaz a
/// cena e a ilha faz a transição.
/// </summary>
internal sealed class AtividadeMascote : Atividade
{
    public override string Tipo => "mascote";
    public override string Nome => "Mascote";
    public override bool TemExpandida => false;
    public override bool AnunciaChegada => false;
    // sozinho na área de trabalho, a pílula fica do tamanho da pílula vazia de antes, não da de uma atividade
    public override float? LarguraCompactaMinima => 128;

    private const float ALTURA = 24, LARGURA_MAX = 120, BOLHA = 24;

    private Biblioteca? _bib;
    public EstadoMascote Estado { get; private set; } = EstadoMascote.Parado;
    // tiras já ampliadas: (animação, largura, altura) -> pixels
    private readonly Dictionary<(Animacao, int, int, bool), byte[]> _tiras = new();

    public void Folhas(Biblioteca? bib)
    {
        if (ReferenceEquals(bib, _bib)) return;
        _bib = bib;
        _tiras.Clear();
        if (Ativa) MudouTamanho();
    }

    public void Atualizar(EstadoMascote estado, bool aparece)
    {
        bool mudou = estado != Estado;
        Estado = estado;
        if (aparece && !Ativa) { Ligar(); return; }
        if (!aparece && Ativa) { Desligar(); return; }
        if (mudou && Ativa) MudouTamanho();
    }

    public void Parar() => Desligar();

    protected override Cena Montar(Palco p, Forma f)
    {
        bool bolha = f == Forma.Bolha;
        float e = p.Escala;
        var (tag, ef) = _bib == null ? (null, Efeito.Nenhum) : Escolha.Tag(_bib.Nomes, Estado);
        var an = tag == null ? null : _bib![tag];
        if (an == null) return p.NovaCena(new Vector2(BOLHA, BOLHA));

        int maxA = (int)p.Px(bolha ? BOLHA : ALTURA), maxL = (int)p.Px(bolha ? BOLHA : LARGURA_MAX);
        var arr = Escolha.Arranjar(an.Largura, an.Altura, maxL, maxA, ef, bolha);
        var cena = p.NovaCena(new Vector2(arr.CenaL / e, arr.CenaA / e));
        var c = p.C;

        // dormindo sem tag própria: o primeiro quadro parado
        bool parado = ef == Efeito.Sono || an.Quadros.Count == 1;
        var quadros = parado ? new List<byte[]> { an.Quadros[an.Sequencia[0]] } : an.Quadros;
        var chave = (an, arr.L, arr.A, parado);
        if (!_tiras.TryGetValue(chave, out var tira)) _tiras[chave] = tira = Montagem.Tira(quadros, an.Largura, an.Altura, arr.L, arr.A);
        var sup = Pixels(p, cena, tira, quadros.Count * arr.L, arr.A);

        var pincel = c.CreateSurfaceBrush(sup.Superf);
        pincel.Stretch = CompositionStretch.None;
        pincel.HorizontalAlignmentRatio = 0;
        pincel.VerticalAlignmentRatio = 0;
        var sprite = c.CreateSpriteVisual();
        sprite.Size = new Vector2(arr.L, arr.A);
        sprite.Offset = new Vector3(arr.X, arr.Y, 0);
        sprite.Brush = pincel;
        // pivô do balanço e da respiração: o pé, no meio do corpo (a âncora da folha)
        sprite.CenterPoint = new Vector3(Math.Clamp(an.Ancora.X / an.Largura, 0, 1) * arr.L, Math.Clamp(an.Ancora.Y / an.Altura, 0, 1) * arr.A, 0);
        cena.Raiz.Children.InsertAtTop(sprite);

        if (!parado) TocarQuadros(c, pincel, an, arr.L);
        if (ef == Efeito.Danca) Dancar(c, sprite, arr);
        if (ef == Efeito.Sono) Dormir(p, cena, sprite, arr);
        return cena;
    }

    private static Superficie Pixels(Palco p, Cena cena, byte[] px, int l, int a)
    {
        var s = p.G.NovaSuperficie(new Vector2(l, a), (dc, _) => Desenhar(dc, px, l, a), emPixels: true);
        cena.Superficies.Add(s);
        return s;
    }

    private static unsafe void Desenhar(ID2D1DeviceContext dc, byte[] px, int l, int a)
    {
        var props = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96f, 96f, BitmapOptions.None);
        fixed (byte* d = px)
        {
            using var bmp = dc.CreateBitmap(new SizeI(l, a), (IntPtr)d, (uint)(l * 4), props);
            dc.DrawBitmap(bmp, 1f, BitmapInterpolationMode.NearestNeighbor, new Rect(0, 0, l, a));
        }
    }

    /// <summary>
    /// O compositor desliza o pincel pela tira em degraus, cada quadro no tempo que o Aseprite diz (keyframes com
    /// degrau, como a LogoAnimada, mas com a duração de cada quadro). Roda no DWM, para sempre, sem acordar o app.
    /// </summary>
    private static void TocarQuadros(Compositor c, CompositionSurfaceBrush pincel, Animacao an, int l)
    {
        var ms = an.Ms.Select(x => Math.Max(16, x)).ToArray();
        double total = ms.Sum();
        var a = c.CreateVector2KeyFrameAnimation();
        var degrau = c.CreateStepEasingFunction(1);
        double t = 0;
        for (int i = 0; i < an.Sequencia.Length; i++)
        {
            a.InsertKeyFrame((float)(t / total), new Vector2(-an.Sequencia[i] * l, 0), degrau);
            t += ms[i];
        }
        a.InsertKeyFrame(1f, new Vector2(-an.Sequencia[^1] * l, 0), degrau);
        a.Duration = TimeSpan.FromMilliseconds(total);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        pincel.StartAnimation("Offset", a);
    }

    /// <summary>Dança feita no compositor: pula no ritmo (~130 bpm) e balança de um lado para o outro a cada dois pulos.</summary>
    private static void Dancar(Compositor c, SpriteVisual v, Arranjo arr)
    {
        var sobe = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.6f), new Vector2(0.4f, 1f));
        var desce = c.CreateCubicBezierEasingFunction(new Vector2(0.6f, 0f), new Vector2(0.8f, 0.4f));
        var pulo = c.CreateScalarKeyFrameAnimation();
        pulo.InsertKeyFrame(0f, arr.Y);
        pulo.InsertKeyFrame(0.45f, arr.Y - arr.Pulo, sobe);
        pulo.InsertKeyFrame(1f, arr.Y, desce);
        pulo.Duration = TimeSpan.FromMilliseconds(460);
        pulo.IterationBehavior = AnimationIterationBehavior.Forever;
        v.StartAnimation("Offset.Y", pulo);

        var suave = c.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.55f, 1f));
        var balanco = c.CreateScalarKeyFrameAnimation();
        balanco.InsertKeyFrame(0f, 0f);
        balanco.InsertKeyFrame(0.25f, -8f, suave);
        balanco.InsertKeyFrame(0.75f, 8f, suave);
        balanco.InsertKeyFrame(1f, 0f, suave);
        balanco.Duration = TimeSpan.FromMilliseconds(920);
        balanco.IterationBehavior = AnimationIterationBehavior.Forever;
        v.StartAnimation("RotationAngleInDegrees", balanco);
    }

    // um "z" de pixel art, 4x4
    private static readonly string[] Z = { "####", "..#.", ".#..", "####" };

    /// <summary>Sono feito no compositor: respira devagar, meio apagado, e dois "z" sobem e somem, um depois do outro.</summary>
    private static void Dormir(Palco p, Cena cena, SpriteVisual v, Arranjo arr)
    {
        var c = p.C;
        v.Opacity = 0.72f;
        var suave = c.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.55f, 1f));
        var resp = c.CreateVector3KeyFrameAnimation();
        resp.InsertKeyFrame(0f, Vector3.One);
        resp.InsertKeyFrame(0.5f, new Vector3(1.04f, 0.92f, 1f), suave);
        resp.InsertKeyFrame(1f, Vector3.One, suave);
        resp.Duration = TimeSpan.FromMilliseconds(3200);
        resp.IterationBehavior = AnimationIterationBehavior.Forever;
        v.StartAnimation("Scale", resp);

        int z = arr.ZEscala, lado = 4 * z;
        var px = PixelsDoZ(z, new Color4(1f, 1f, 1f, 0.85f));
        float x0 = Math.Min(arr.CenaL - lado, arr.X + arr.L - lado / 2f), y0 = Math.Max(0, arr.Y - lado / 2f);
        float x1 = Math.Min(arr.CenaL - lado, x0 + lado * 1.2f), y1 = 0;
        for (int i = 0; i < 2; i++)
        {
            var s = Pixels(p, cena, px, lado, lado);
            var zv = c.CreateSpriteVisual();
            zv.Size = new Vector2(lado, lado);
            zv.Brush = s.Pincel;
            zv.Opacity = 0;
            zv.Offset = new Vector3(x0, y0, 0);
            cena.Raiz.Children.InsertAtTop(zv);
            var atraso = TimeSpan.FromMilliseconds(i * 1600);
            var sobe = c.CreateVector3KeyFrameAnimation();
            sobe.InsertKeyFrame(0f, new Vector3(x0, y0, 0));
            sobe.InsertKeyFrame(1f, new Vector3(x1, y1, 0), c.CreateLinearEasingFunction());
            sobe.Duration = TimeSpan.FromMilliseconds(3200);
            sobe.DelayTime = atraso;
            sobe.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            sobe.IterationBehavior = AnimationIterationBehavior.Forever;
            zv.StartAnimation("Offset", sobe);
            var some = c.CreateScalarKeyFrameAnimation();
            some.InsertKeyFrame(0f, 0f);
            some.InsertKeyFrame(0.2f, 1f);
            some.InsertKeyFrame(0.65f, 1f);
            some.InsertKeyFrame(1f, 0f);
            some.Duration = TimeSpan.FromMilliseconds(3200);
            some.DelayTime = atraso;
            some.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            some.IterationBehavior = AnimationIterationBehavior.Forever;
            zv.StartAnimation("Opacity", some);
        }
    }

    /// <summary>O "z" ampliado `z` vezes, BGRA pré-multiplicado.</summary>
    public static byte[] PixelsDoZ(int z, Color4 cor)
    {
        int lado = 4 * z;
        var px = new byte[lado * lado * 4];
        byte a = (byte)(cor.A * 255);
        for (int y = 0; y < lado; y++)
            for (int x = 0; x < lado; x++)
            {
                if (Z[y / z][x / z] != '#') continue;
                int d = (y * lado + x) * 4;
                px[d] = (byte)(cor.B * a);
                px[d + 1] = (byte)(cor.G * a);
                px[d + 2] = (byte)(cor.R * a);
                px[d + 3] = a;
            }
        return px;
    }
}
