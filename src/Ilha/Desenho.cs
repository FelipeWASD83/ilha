using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha;

/// <summary>Cores da ilha. Pílula preta pura; o resto é branco com transparência e o acento de cada atividade.</summary>
internal static class Cor
{
    public static readonly Color4 Branco = new(1f, 1f, 1f, 1f);
    public static readonly Color4 Secundario = new(1f, 1f, 1f, 0.58f);
    public static readonly Color4 Fraco = new(1f, 1f, 1f, 0.16f);
    public static readonly Color4 Laranja = new(1f, 0.62f, 0.04f, 1f);
    public static readonly Color4 Verde = new(0.19f, 0.82f, 0.35f, 1f);
    public static readonly Color4 Vermelho = new(1f, 0.27f, 0.23f, 1f);
    public static readonly Color4 Azul = new(0.04f, 0.52f, 1f, 1f);
    public static readonly Color4 Coral = new(0.85f, 0.47f, 0.34f, 1f);

    public static Windows.UI.Color Wr(Color4 c) =>
        Windows.UI.Color.FromArgb((byte)(c.A * 255), (byte)(c.R * 255), (byte)(c.G * 255), (byte)(c.B * 255));
}

/// <summary>Texto pelo DirectWrite: Segoe UI Variable, números tabulares quando pedido (relógio não pula).</summary>
internal sealed class Escrita
{
    private readonly Grafico _g;
    private readonly Dictionary<(float, FontWeight), IDWriteTextFormat> _formatos = new();
    private IDWriteTypography? _tabular;

    public Escrita(Grafico g) => _g = g;

    private IDWriteTextFormat Formato(float tamanho, FontWeight peso)
    {
        if (_formatos.TryGetValue((tamanho, peso), out var f)) return f;
        string familia = tamanho >= 20 ? "Segoe UI Variable Display" : "Segoe UI Variable Text";
        f = _g.Escrita.CreateTextFormat(familia, null!, peso, FontStyle.Normal, FontStretch.Normal, tamanho, "pt-br");
        f.WordWrapping = WordWrapping.NoWrap;
        _formatos[(tamanho, peso)] = f;
        return f;
    }

    public IDWriteTextLayout Layout(string texto, float tamanho, FontWeight peso = FontWeight.SemiBold, bool tabular = false, float largura = 4000)
    {
        var l = _g.Escrita.CreateTextLayout(texto, Formato(tamanho, peso), largura, tamanho * 2);
        if (largura < 4000)
        {
            // corta com reticências o que não couber
            l.MaxWidth = largura;
            using var sinal = _g.Escrita.CreateEllipsisTrimmingSign(Formato(tamanho, peso));
            l.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, sinal);
        }
        if (tabular)
        {
            if (_tabular == null)
            {
                _tabular = _g.Escrita.CreateTypography();
                _tabular.AddFontFeature(new FontFeature { NameTag = FontFeatureTag.TabularFigures, Parameter = 1 });
            }
            l.SetTypography(_tabular, new TextRange(0, (uint)texto.Length));
        }
        return l;
    }

    public Vector2 Medir(string texto, float tamanho, FontWeight peso = FontWeight.SemiBold, bool tabular = false)
    {
        using var l = Layout(texto, tamanho, peso, tabular);
        var m = l.Metrics;
        return new Vector2(MathF.Ceiling(m.WidthIncludingTrailingWhitespace), MathF.Ceiling(m.Height));
    }

    /// <summary>
    /// Campo de texto da Entrada: o texto (ou bolinhas, se for senha) com o cursor, rolando para o cursor ficar visível
    /// quando o texto passa da largura. `vazio` aparece apagado quando não tem nada escrito.
    /// </summary>
    public void DesenharCampo(ID2D1DeviceContext dc, string texto, int cursor, bool senha, Vector2 pos, float largura,
        float tamanho, Color4 cor, string vazio = "", bool comCursor = true)
    {
        string mostrado = senha ? new string('•', texto.Length) : texto;
        cursor = Math.Clamp(cursor, 0, mostrado.Length);
        if (mostrado.Length == 0 && vazio.Length > 0)
        {
            Desenhar(dc, vazio, pos, tamanho, new Color4(cor.R, cor.G, cor.B, 0.35f), FontWeight.Normal, largura: largura);
            if (comCursor) Icone.Retangulo(dc, pos.X, pos.Y + tamanho * 0.15f, 1.6f, tamanho * 1.15f, 0.8f, cor);
            return;
        }
        using var l = Layout(mostrado, tamanho, FontWeight.Normal);
        l.HitTestTextPosition((uint)cursor, false, out float cx, out _, out _);
        float rolagem = MathF.Max(0, cx - largura + 4);
        dc.PushAxisAlignedClip(new System.Drawing.RectangleF(pos.X, pos.Y - 2, largura, tamanho * 1.6f), AntialiasMode.Aliased);
        using (var b = dc.CreateSolidColorBrush(cor)) dc.DrawTextLayout(new Vector2(pos.X - rolagem, pos.Y), l, b, DrawTextOptions.EnableColorFont);
        if (comCursor) Icone.Retangulo(dc, pos.X + cx - rolagem, pos.Y + tamanho * 0.15f, 1.6f, tamanho * 1.15f, 0.8f, cor);
        dc.PopAxisAlignedClip();
    }

    /// <summary>Desenha com a linha de base calculada para o texto ficar centrado na altura dada.</summary>
    public void Desenhar(ID2D1DeviceContext dc, string texto, Vector2 pos, float tamanho, Color4 cor,
        FontWeight peso = FontWeight.SemiBold, bool tabular = false, float largura = 4000)
    {
        using var l = Layout(texto, tamanho, peso, tabular, largura);
        using var b = dc.CreateSolidColorBrush(cor);
        dc.DrawTextLayout(pos, l, b, DrawTextOptions.EnableColorFont);
    }
}

/// <summary>Ícones simples desenhados em Direct2D (sem arquivo de imagem).</summary>
/// <summary>
/// Draws a whole bitmap into a destination rectangle (DIP). Vortice's DrawBitmap(bitmap, opacity, mode, Rect) takes the
/// SOURCE rectangle and always draws at the origin: every photo with a position came out in the corner, cut (the
/// headphone photos, the chat photos, the mixer icons). Found on 01/11/2026.
/// </summary>
internal static class Bitmaps
{
    public static void Draw(ID2D1DeviceContext dc, ID2D1Bitmap bmp, float x, float y, float w, float h, BitmapInterpolationMode mode) =>
        dc.DrawBitmap(bmp, new Vortice.RawRectF(x, y, x + w, y + h), 1f, mode, null);
}

internal static class Icone
{
    public static void Circulo(ID2D1DeviceContext dc, Vector2 c, float r, Color4 cor)
    {
        using var b = dc.CreateSolidColorBrush(cor);
        dc.FillEllipse(new Ellipse(c, r, r), b);
    }

    public static void Retangulo(ID2D1DeviceContext dc, float x, float y, float w, float h, float raio, Color4 cor)
    {
        using var b = dc.CreateSolidColorBrush(cor);
        var rr = new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), raio, raio);
        dc.FillRoundedRectangle(ref rr, b);
    }

    /// <summary>Anel de progresso (0 a 1) começando no topo, sentido horário.</summary>
    public static void Anel(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float r, float espessura, float progresso, Color4 cor)
    {
        using var fundo = dc.CreateSolidColorBrush(new Color4(cor.R, cor.G, cor.B, 0.25f));
        dc.DrawEllipse(new Ellipse(c, r, r), fundo, espessura);
        progresso = Math.Clamp(progresso, 0f, 1f);
        if (progresso <= 0.001f) return;
        using var b = dc.CreateSolidColorBrush(cor);
        if (progresso >= 0.999f) { dc.DrawEllipse(new Ellipse(c, r, r), b, espessura); return; }
        using var g = f.CreatePathGeometry();
        using (var s = g.Open())
        {
            float a = -MathF.PI / 2 + progresso * MathF.PI * 2;
            s.BeginFigure(new Vector2(c.X, c.Y - r), FigureBegin.Hollow);
            s.AddArc(new ArcSegment
            {
                Point = new Vector2(c.X + r * MathF.Cos(a), c.Y + r * MathF.Sin(a)),
                Size = new Size(r, r),
                SweepDirection = SweepDirection.Clockwise,
                ArcSize = progresso > 0.5f ? ArcSize.Large : ArcSize.Small,
            });
            s.EndFigure(FigureEnd.Open);
            s.Close();
        }
        using var traco = f.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        dc.DrawGeometry(g, b, espessura, traco);
    }

    public static void Triangulo(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 a, Vector2 b2, Vector2 c, Color4 cor)
    {
        using var g = f.CreatePathGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(a, FigureBegin.Filled);
            s.AddLine(b2);
            s.AddLine(c);
            s.EndFigure(FigureEnd.Closed);
            s.Close();
        }
        using var b = dc.CreateSolidColorBrush(cor);
        dc.FillGeometry(g, b);
    }

    public static void Linha(ID2D1DeviceContext dc, Vector2 a, Vector2 b2, float espessura, Color4 cor, ID2D1Factory1 f)
    {
        using var b = dc.CreateSolidColorBrush(cor);
        using var traco = f.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        dc.DrawLine(a, b2, b, espessura, traco);
    }
}

/// <summary>
/// Imagens de arquivo (as logos): reduzidas uma vez pelo GDI+ para o tamanho exato em pixels e guardadas,
/// depois desenhadas 1:1 pelo Direct2D, sem borrar.
/// </summary>
internal static class Imagem
{
    private sealed record Pixels(byte[] Dados, int Lado);
    private static readonly Dictionary<(string, int), Pixels?> _cache = new();

    private static Pixels? Carregar(string arquivo, int lado)
    {
        if (_cache.TryGetValue((arquivo, lado), out var p)) return p;
        p = null;
        try
        {
            string caminho = System.IO.Path.Combine(AppContext.BaseDirectory, "imagens", arquivo);
            using var original = new System.Drawing.Bitmap(caminho);
            using var reduzida = new System.Drawing.Bitmap(lado, lado, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = System.Drawing.Graphics.FromImage(reduzida))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.Clear(System.Drawing.Color.Transparent);
                g.DrawImage(original, 0, 0, lado, lado);
            }
            var bd = reduzida.LockBits(new System.Drawing.Rectangle(0, 0, lado, lado), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var dados = new byte[lado * lado * 4];
            for (int y = 0; y < lado; y++)
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, dados, y * lado * 4, lado * 4);
            reduzida.UnlockBits(bd);
            p = new Pixels(dados, lado);
        }
        catch (Exception e) { Log.Escrever($"imagem {arquivo} não carregou: {e.Message}"); }
        _cache[(arquivo, lado)] = p;
        return p;
    }

    /// <summary>Desenha a imagem quadrada com lado em DIP.</summary>
    public static unsafe void Desenhar(ID2D1DeviceContext dc, Palco p, string arquivo, float x, float y, float ladoDip)
    {
        int lado = (int)MathF.Round(ladoDip * p.Escala);
        var px = Carregar(arquivo, lado);
        if (px == null) return;
        var props = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96f * p.Escala, 96f * p.Escala, BitmapOptions.None);
        // destino em pixel inteiro e com exatamente `lado` pixels: o NearestNeighbor vira cópia 1:1 (com posição
        // ou lado fracionário a 125% ele duplicava e sumia com linhas da logo)
        float e = p.Escala;
        float x0 = MathF.Round(x * e) / e, y0 = MathF.Round(y * e) / e, l = lado / e;
        fixed (byte* d = px.Dados)
        {
            using var bmp = dc.CreateBitmap(new SizeI(lado, lado), (IntPtr)d, (uint)(lado * 4), props);
            Bitmaps.Draw(dc, bmp, x0, y0, l, l, BitmapInterpolationMode.NearestNeighbor);
        }
    }
}