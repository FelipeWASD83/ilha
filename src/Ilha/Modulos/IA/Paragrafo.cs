using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.IA;

/// <summary>
/// Texto com quebra de linha (o <see cref="Escrita"/> do núcleo é de uma linha só, com reticências). Formatos
/// próprios com WordWrapping ligado, na mesma Segoe UI Variable.
/// </summary>
internal sealed class Paragrafo
{
    private readonly IDWriteFactory _f;
    private readonly Dictionary<(float, FontWeight), IDWriteTextFormat> _formatos = new();

    public Paragrafo(IDWriteFactory f) => _f = f;

    private IDWriteTextFormat Formato(float tam, FontWeight peso)
    {
        if (_formatos.TryGetValue((tam, peso), out var f)) return f;
        f = _f.CreateTextFormat(tam >= 20 ? "Segoe UI Variable Display" : "Segoe UI Variable Text", null!, peso, FontStyle.Normal, FontStretch.Normal, tam, Lang.T("pt-br", "en-us"));
        f.WordWrapping = WordWrapping.Wrap;
        _formatos[(tam, peso)] = f;
        return f;
    }

    /// <summary>Layout quebrado na largura dada; o chamador descarta.</summary>
    public IDWriteTextLayout Layout(string texto, float tam, float largura, FontWeight peso = FontWeight.Normal) =>
        _f.CreateTextLayout(texto.Length == 0 ? " " : texto, Formato(tam, peso), MathF.Max(1, largura), 100_000);

    public float Altura(string texto, float tam, float largura, FontWeight peso = FontWeight.Normal)
    {
        using var l = Layout(texto, tam, largura, peso);
        return MathF.Ceiling(l.Metrics.Height);
    }

    public static void Desenhar(ID2D1DeviceContext dc, IDWriteTextLayout l, Vector2 pos, Color4 cor)
    {
        using var b = dc.CreateSolidColorBrush(cor);
        dc.DrawTextLayout(pos, l, b, DrawTextOptions.EnableColorFont);
    }

    /// <summary>Largura real da linha mais larga (para o balão da mensagem dele encolher no texto curto).</summary>
    public static float LarguraUsada(IDWriteTextLayout l) => MathF.Ceiling(l.Metrics.WidthIncludingTrailingWhitespace);

    /// <summary>
    /// Campo de várias linhas (a Entrada multilinha): texto quebrado, cursor, e rolagem vertical para a linha do
    /// cursor ficar visível na altura dada. `vazio` aparece apagado quando não tem nada escrito.
    /// </summary>
    public void Campo(ID2D1DeviceContext dc, string texto, int cursor, Vector2 pos, float largura, float altura, float tam, Color4 cor,
        string vazio, bool comCursor)
    {
        dc.PushAxisAlignedClip(new System.Drawing.RectangleF(pos.X, pos.Y, largura, altura), AntialiasMode.Aliased);
        try
        {
            if (texto.Length == 0)
            {
                using var lv = Layout(vazio, tam, largura);
                Desenhar(dc, lv, pos, new Color4(cor.R, cor.G, cor.B, 0.38f));
                if (comCursor) Icone.Retangulo(dc, pos.X, pos.Y + tam * 0.15f, 1.6f, tam * 1.15f, 0.8f, cor);
                return;
            }
            using var l = Layout(texto, tam, largura);
            cursor = Math.Clamp(cursor, 0, texto.Length);
            l.HitTestTextPosition((uint)cursor, false, out float cx, out float cy, out var m);
            float linha = MathF.Max(m.Height, tam * 1.3f);
            float rolagem = MathF.Max(0, cy + linha - altura);
            Desenhar(dc, l, new Vector2(pos.X, pos.Y - rolagem), cor);
            if (comCursor) Icone.Retangulo(dc, pos.X + cx, pos.Y + cy - rolagem + tam * 0.12f, 1.6f, tam * 1.15f, 0.8f, cor);
        }
        finally { dc.PopAxisAlignedClip(); }
    }
}
