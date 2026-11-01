using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Windows.UI.Composition;

namespace Ilha;

/// <summary>
/// As animações das logos e das bolinhas (feitas pelo ferramentas/preparar_logos.py): cada uma é uma faixa
/// horizontal de quadros a 23,976 fps. Aqui cada faixa é reamostrada uma vez para o tamanho exato em pixels em
/// que vai aparecer (sem borrar) e guardada.
/// </summary>
internal sealed class Faixas
{
    public sealed record Info(int Quadros, bool Loop);

    public double Fps { get; } = 24000.0 / 1001;
    private readonly Dictionary<string, Info> _info = new();
    private readonly Dictionary<string, float> _zoom = new();
    private readonly Grafico _g;
    private readonly Dictionary<(string, int), Superficie?> _cache = new();

    public static string Pasta => Path.Combine(AppContext.BaseDirectory, "imagens", "logos");

    public Faixas(Grafico g)
    {
        _g = g;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "animacoes.json")));
            Fps = doc.RootElement.GetProperty("fps").GetDouble();
            if (doc.RootElement.TryGetProperty("zoom", out var z))
                foreach (var f in z.EnumerateObject()) _zoom[f.Name] = (float)f.Value.GetDouble();
            foreach (var a in doc.RootElement.GetProperty("animacoes").EnumerateObject())
                _info[a.Name] = new Info(a.Value.GetProperty("quadros").GetInt32(), a.Value.GetProperty("loop").GetBoolean());
        }
        catch (Exception e) { Log.Escrever("animacoes.json não carregou: " + e.Message); }
    }

    public Info? Sobre(string nome) => _info.TryGetValue(nome, out var i) ? i : null;

    /// <summary>Quanto ampliar a família (o "claude" de "claude-loop" ou de "claude.png") para as logos terem o mesmo tamanho.</summary>
    public float Zoom(string nome)
    {
        string familia = Path.GetFileNameWithoutExtension(nome).Split('-')[0];
        return _zoom.TryGetValue(familia, out var z) ? z : 1f;
    }

    /// <summary>A faixa de `nome` com cada quadro em `px` pixels (null se a imagem não existe, ex.: clone do git).</summary>
    public Superficie? Faixa(string nome, int px)
    {
        if (_cache.TryGetValue((nome, px), out var s)) return s;
        s = null;
        var info = Sobre(nome);
        string arq = Path.Combine(Pasta, nome + ".png");
        if (info != null && File.Exists(arq))
        {
            var dados = Reamostrar(arq, info.Quadros * px, px);
            if (dados != null) s = Criar(dados, info.Quadros * px, px);
        }
        _cache[(nome, px)] = s;
        return s;
    }

    /// <summary>Uma imagem parada (claude.png, chatgpt.png) em `px` pixels.</summary>
    public Superficie? Parada(string arquivo, int px)
    {
        if (_cache.TryGetValue((arquivo, px), out var s)) return s;
        string arq = Path.Combine(Pasta, arquivo);
        var dados = File.Exists(arq) ? Reamostrar(arq, px, px) : null;
        s = dados == null ? null : Criar(dados, px, px);
        _cache[(arquivo, px)] = s;
        return s;
    }

    private Superficie Criar(byte[] dados, int w, int h) =>
        _g.NovaSuperficie(new Vector2(w, h), (dc, t) => Desenhar(dc, dados, w, h), emPixels: true);

    private static unsafe void Desenhar(ID2D1DeviceContext dc, byte[] dados, int w, int h)
    {
        var props = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96f, 96f, BitmapOptions.None);
        fixed (byte* p = dados)
        {
            using var bmp = dc.CreateBitmap(new SizeI(w, h), (IntPtr)p, (uint)(w * 4), props);
            dc.DrawBitmap(bmp, 1f, BitmapInterpolationMode.NearestNeighbor, new Rect(0, 0, w, h));
        }
    }

    // GDI+ com bicúbica de alta qualidade, uma vez por tamanho, em BGRA pré-multiplicado
    private static byte[]? Reamostrar(string arquivo, int w, int h)
    {
        try
        {
            using var original = new System.Drawing.Bitmap(arquivo);
            using var nova = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = System.Drawing.Graphics.FromImage(nova))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                using var atr = new System.Drawing.Imaging.ImageAttributes();
                atr.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY); // sem borda escura nas pontas
                g.DrawImage(original, new System.Drawing.Rectangle(0, 0, w, h), 0, 0, original.Width, original.Height, System.Drawing.GraphicsUnit.Pixel, atr);
            }
            var bd = nova.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var dados = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, dados, y * w * 4, w * 4);
            nova.UnlockBits(bd);
            return dados;
        }
        catch (Exception e) { Log.Escrever($"faixa {Path.GetFileName(arquivo)} não carregou: {e.Message}"); return null; }
    }
}

/// <summary>
/// Um quadrado que toca as faixas: o compositor desliza o pincel pela faixa em degraus, um quadro a cada
/// 1/23,976 s, dentro do DWM (o app não acorda para isso). Toca em sequência: as de uma vez emendam na
/// seguinte; a última que repete fica repetindo.
/// </summary>
internal sealed class LogoAnimada
{
    private readonly Palco _p;
    private readonly int _px;
    private int _pxDesenho;
    private readonly CompositionSurfaceBrush _pincel;
    private readonly SpriteVisual _sprite;
    private int _geracao;
    /// <summary>A caixa do tamanho pedido; o desenho fica dentro, centralizado e ampliado pelo zoom da família.</summary>
    public ContainerVisual Visual { get; }

    public LogoAnimada(Palco p, float ladoDip)
    {
        _p = p;
        _px = Math.Max(4, (int)MathF.Round(ladoDip * p.Escala));
        _pincel = p.C.CreateSurfaceBrush();
        _pincel.Stretch = CompositionStretch.None;
        _pincel.HorizontalAlignmentRatio = 0;
        _pincel.VerticalAlignmentRatio = 0;
        Visual = p.C.CreateContainerVisual();
        Visual.Size = new Vector2(_px, _px);
        _sprite = p.C.CreateSpriteVisual();
        _sprite.Brush = _pincel;
        Visual.Children.InsertAtTop(_sprite);
        Ampliar(1f);
    }

    private void Ampliar(float zoom)
    {
        _pxDesenho = Math.Max(4, (int)MathF.Round(_px * zoom));
        float d = MathF.Round((_px - _pxDesenho) / 2f);
        _sprite.Size = new Vector2(_pxDesenho, _pxDesenho);
        _sprite.Offset = new Vector3(d, d, 0);
    }

    /// <summary>A cena sumiu: o que estiver encadeado não continua (o pincel já foi descartado).</summary>
    public void Soltar() => _geracao++;

    /// <summary>Imagem parada (claude.png ou chatgpt.png).</summary>
    public void Parada(string arquivo)
    {
        _geracao++;
        _pincel.StopAnimation("Offset");
        _pincel.Offset = Vector2.Zero;
        Ampliar(_p.Faixas.Zoom(arquivo));
        _pincel.Surface = _p.Faixas.Parada(arquivo, _pxDesenho)?.Superf;
    }

    /// <summary>Toca as animações na ordem. `repetirTudo` faz até as de uma vez repetirem (usado na galeria).</summary>
    public void Tocar(params string[] nomes) => Tocar(false, nomes);

    public void Tocar(bool repetirTudo, params string[] nomes)
    {
        int g = ++_geracao;
        Passo(g, nomes, 0, repetirTudo);
    }

    private void Passo(int g, string[] nomes, int i, bool repetirTudo)
    {
        if (g != _geracao || i >= nomes.Length) return;
        string nome = nomes[i];
        var info = _p.Faixas.Sobre(nome);
        Ampliar(_p.Faixas.Zoom(nome));
        var faixa = _p.Faixas.Faixa(nome, _pxDesenho);
        if (info == null || faixa == null) { Passo(g, nomes, i + 1, repetirTudo); return; }
        var c = _p.C;
        _pincel.StopAnimation("Offset");
        _pincel.Surface = faixa.Superf;
        int n = info.Quadros;
        var a = c.CreateVector2KeyFrameAnimation();
        var degrau = c.CreateStepEasingFunction(1);
        int w = _pxDesenho;
        for (int k = 0; k < n; k++) a.InsertKeyFrame((float)k / n, new Vector2(-k * w, 0), degrau);
        a.InsertKeyFrame(1f, new Vector2(-(n - 1) * w, 0), degrau);
        a.Duration = TimeSpan.FromSeconds(n / _p.Faixas.Fps);
        bool repete = info.Loop || repetirTudo;
        if (repete)
        {
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            _pincel.StartAnimation("Offset", a);
            return;
        }
        // de uma vez: quando termina, emenda a próxima (o último quadro fica se for a última)
        var lote = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        _pincel.StartAnimation("Offset", a);
        lote.End();
        lote.Completed += (_, _) => Passo(g, nomes, i + 1, repetirTudo);
    }
}
