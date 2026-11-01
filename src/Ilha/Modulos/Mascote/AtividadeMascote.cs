using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Mascote;

/// <summary>
/// O palco do mascote dentro da ilha: mostra o quadro que o app acompanhante manda (ele compõe tudo, com o fundo e os
/// enfeites que a pessoa escolheu) e só existe enquanto o mascote mora aqui. "mascote" fica fora da lista de
/// prioridades: perde para todo tipo que está nela, mas empata (100) com os outros de fora, e no empate fica na frente
/// quem ligou por último (ele religa a cada vez que volta a ficar visível).
/// Enquanto o mascote é arrastado por cima da pílula, a pílula mostra onde soltar.
/// </summary>
internal sealed class AtividadeMascote : Atividade
{
    public override string Tipo => "mascote";
    public override string Nome => _nome ?? "Mascote";
    public override bool TemExpandida => _morando;
    public override bool AnunciaChegada => false;
    public override float? LarguraCompactaMinima => _morando ? 128 : null;
    // o núcleo pergunta antes de montar a cena: a largura do quadro na altura da pílula, com folga
    public override (float De, float Ate)? MeioLivreCompacta =>
        _morando && _a > 0 ? (MathF.Min(SPRITE_L_MAX, (float)_l * ALTURA / _a) + 8, CENA_L) : null;

    private const float ALTURA = 24, CENA_L = 132, SPRITE_L_MAX = 64, BOLHA = 24;
    private const float CARTAO_L = 320, CARTAO_A = 172, AREA_GRANDE = 156;

    private string? _nome;
    private bool _morando, _visivel = true, _previa;
    private byte[]? _px;
    private int _l, _a;
    private bool _pixelado = true;
    // as superfícies que mostram o quadro (só elas redesenham a cada quadro novo)
    private readonly List<Superficie> _sprites = new();
    private readonly ArrasteParaFora _arraste = new();

    /// <summary>Passou de 4 DIP arrastando o mascote: ele sai da ilha e segue o cursor.</summary>
    public Action? SaiuArrastando;
    public Action? Arrastou;
    public Action? Soltou;
    /// <summary>O botão do cartão: volta para a área de trabalho, onde estava antes.</summary>
    public Action? PedirSair;

    public bool Morando => _morando;

    public void DefinirNome(string? nome)
    {
        if (nome == _nome) return;
        _nome = nome;
        if (Ativa && _morando) MudouTamanho();
    }

    public void Morar(bool sim)
    {
        _morando = sim;
        if (sim) _previa = false;
        Reavaliar(refazer: true);
    }

    public void Visivel(bool sim)
    {
        _visivel = sim;
        Reavaliar();
    }

    public void Pairando(bool sim)
    {
        if (sim == _previa) return;
        _previa = sim && !_morando;
        Reavaliar(refazer: true);
    }

    /// <summary>A conexão com o app caiu: some tudo (o app volta sozinho para a área de trabalho).</summary>
    public void Desconectou()
    {
        _morando = _previa = false;
        _px = null;
        _l = _a = 0;
        _nome = null;
        Reavaliar();
    }

    public void NovoQuadro(byte[] px, int l, int a, bool pixelado)
    {
        bool layout = _px == null || l != _l || a != _a || pixelado != _pixelado;
        _px = px;
        _l = l;
        _a = a;
        _pixelado = pixelado;
        if (!Ativa) { Reavaliar(); return; }
        if (layout) MudouTamanho();
        else foreach (var s in _sprites.ToArray()) s.Redesenhar();
    }

    public void Parar() => Desligar();

    private void Reavaliar(bool refazer = false)
    {
        bool deve = _morando ? _visivel && _px != null : _previa;
        if (deve && !Ativa) Ligar();
        else if (!deve && Ativa) Desligar();
        else if (deve && refazer) MudouTamanho();
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        if (!_morando) return MontarPrevia(p, f);
        return f switch
        {
            Forma.Bolha => MontarBolha(p),
            Forma.Expandida => MontarCartao(p),
            _ => MontarCompacta(p),
        };
    }

    private Cena MontarCompacta(Palco p)
    {
        var c = p.NovaCena(new Vector2(CENA_L, ALTURA));
        var t = Sprite(p, c, new Vector2(0, 0), new Vector2(SPRITE_L_MAX, ALTURA), centrar: false);
        c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(-6, -6, t.X + 12, ALTURA + 12), Arrastar = Arrastar });
        return c;
    }

    private Cena MontarBolha(Palco p)
    {
        var c = p.NovaCena(new Vector2(BOLHA, BOLHA));
        Sprite(p, c, Vector2.Zero, new Vector2(BOLHA, BOLHA), centrar: true);
        return c;
    }

    private Cena MontarCartao(Palco p)
    {
        var c = p.NovaCena(new Vector2(CARTAO_L, CARTAO_A));
        p.Desenho(c, Vector2.Zero, new Vector2(AREA_GRANDE, CARTAO_A), (dc, t) =>
            Icone.Retangulo(dc, 0, 0, t.X, t.Y, 16, new Color4(1, 1, 1, 0.05f)));
        Sprite(p, c, new Vector2(8, 8), new Vector2(AREA_GRANDE - 16, CARTAO_A - 16), centrar: true);
        c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(0, 0, AREA_GRANDE, CARTAO_A), Arrastar = Arrastar });

        float x = AREA_GRANDE + 16, largura = CARTAO_L - x;
        const float BOTAO_A = 30, BOTAO_Y = CARTAO_A - BOTAO_A;
        p.Desenho(c, new Vector2(x, 0), new Vector2(largura, CARTAO_A), (dc, t) =>
        {
            p.Texto.Desenhar(dc, Nome, new Vector2(0, 2), 17, Cor.Branco, FontWeight.SemiBold, largura: t.X);
            p.Texto.Desenhar(dc, "Morando na ilha", new Vector2(0, 28), 12, Cor.Secundario, FontWeight.Normal, largura: t.X);
            p.Texto.Desenhar(dc, "Arraste para fora para soltar na área de trabalho.", new Vector2(0, 56), 12, Cor.Secundario,
                FontWeight.Normal, largura: t.X);
            Icone.Retangulo(dc, 0, BOTAO_Y, t.X, BOTAO_A, BOTAO_A / 2, new Color4(1, 1, 1, 0.12f));
            const string ROTULO = "Soltar fora";
            var m = p.Texto.Medir(ROTULO, 12.5f, FontWeight.Medium);
            p.Texto.Desenhar(dc, ROTULO, new Vector2((t.X - m.X) / 2, BOTAO_Y + (BOTAO_A - m.Y) / 2), 12.5f, Cor.Branco, FontWeight.Medium);
        });
        c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(x, BOTAO_Y, largura, BOTAO_A), Acao = () => PedirSair?.Invoke() });
        return c;
    }

    /// <summary>A pílula enquanto o mascote é arrastado por cima: uma seta para baixo e onde soltar.</summary>
    private Cena MontarPrevia(Palco p, Forma f)
    {
        if (f == Forma.Bolha)
        {
            var b = p.NovaCena(new Vector2(BOLHA, BOLHA));
            p.Desenho(b, Vector2.Zero, b.Tamanho, (dc, t) => Seta(dc, p, new Vector2(t.X / 2, t.Y / 2), 8));
            return b;
        }
        const string TEXTO = "Solte para morar aqui";
        var m = p.Texto.Medir(TEXTO, 13, FontWeight.Medium);
        var c = p.NovaCena(new Vector2(24 + m.X, ALTURA));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            Seta(dc, p, new Vector2(8, t.Y / 2), 7);
            p.Texto.Desenhar(dc, TEXTO, new Vector2(24, (t.Y - m.Y) / 2), 13, Cor.Branco, FontWeight.Medium);
        });
        return c;
    }

    private static void Seta(ID2D1DeviceContext dc, Palco p, Vector2 c, float r)
    {
        var f = p.G.Fabrica;
        Icone.Linha(dc, new Vector2(c.X, c.Y - r), new Vector2(c.X, c.Y + r * 0.7f), 2f, Cor.Branco, f);
        Icone.Triangulo(dc, f, new Vector2(c.X - r * 0.6f, c.Y + r * 0.2f), new Vector2(c.X + r * 0.6f, c.Y + r * 0.2f), new Vector2(c.X, c.Y + r), Cor.Branco);
    }

    /// <summary>
    /// O quadro encaixado na área (DIP), em pixels exatos: pixel art cresce em múltiplos inteiros e sem borrar. Devolve
    /// o tamanho usado, em DIP.
    /// </summary>
    private Vector2 Sprite(Palco p, Cena cena, Vector2 pos, Vector2 area, bool centrar)
    {
        if (_px == null || _l == 0) return Vector2.Zero;
        float e = p.Escala;
        var enc = Encaixe.Calcular(_l, _a, (int)(area.X * e), (int)(area.Y * e), _pixelado);
        float x = pos.X * e + (centrar ? (area.X * e - enc.L) / 2 : 0), y = pos.Y * e + (area.Y * e - enc.A) / 2;
        var s = p.G.NovaSuperficie(new Vector2(enc.L, enc.A), (dc, _) => Desenhar(dc, enc), emPixels: true);
        cena.Superficies.Add(s);
        _sprites.Add(s);
        cena.AoSoltar += () => _sprites.Remove(s);
        var v = p.C.CreateSpriteVisual();
        v.Size = new Vector2(enc.L, enc.A);
        v.Offset = new Vector3(MathF.Round(x), MathF.Round(y), 0);
        v.Brush = s.Pincel;
        cena.Raiz.Children.InsertAtTop(v);
        return new Vector2((centrar ? area.X * e : enc.L) / e, enc.A / e);
    }

    private unsafe void Desenhar(ID2D1DeviceContext dc, Encaixe enc)
    {
        var px = _px;
        int l = _l, a = _a;
        if (px == null || px.Length < l * a * 4) return;
        var props = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96f, 96f, BitmapOptions.None);
        fixed (byte* d = px)
        {
            using var bmp = dc.CreateBitmap(new SizeI(l, a), (IntPtr)d, (uint)(l * 4), props);
            dc.DrawBitmap(bmp, new Rect(0, 0, enc.L, enc.A), 1f,
                enc.Vizinho ? BitmapInterpolationMode.NearestNeighbor : BitmapInterpolationMode.Linear, new Rect(0, 0, l, a));
        }
    }

    private void Arrastar(float x, float y, bool soltou)
    {
        switch (_arraste.Passo(x, y, soltou))
        {
            case ArrasteParaFora.Acao.Saiu: SaiuArrastando?.Invoke(); break;
            case ArrasteParaFora.Acao.Moveu: Arrastou?.Invoke(); break;
            case ArrasteParaFora.Acao.Soltou: Soltou?.Invoke(); break;
        }
    }
}

/// <summary>Tamanho do quadro na área: pixel art em múltiplo inteiro com vizinho mais próximo; menor que 1x, suavizado.</summary>
internal readonly record struct Encaixe(int L, int A, bool Vizinho)
{
    public static Encaixe Calcular(int l, int a, int maxL, int maxA, bool pixelado)
    {
        if (l < 1 || a < 1 || maxL < 1 || maxA < 1) return new Encaixe(Math.Max(1, l), Math.Max(1, a), pixelado);
        float k = MathF.Min((float)maxL / l, (float)maxA / a);
        bool vizinho = pixelado && k >= 1;
        if (vizinho) k = MathF.Floor(k);
        return new Encaixe(Math.Max(1, (int)MathF.Round(l * k)), Math.Max(1, (int)MathF.Round(a * k)), vizinho);
    }
}

/// <summary>
/// O arrastar do botão do mascote: aperta (início), passou de 4 DIP é "saiu" (o mascote deixa a ilha e segue o cursor),
/// cada movimento depois disso é "moveu" e o soltar, "soltou". Apertar e soltar sem mexer não faz nada (vale o clique).
/// </summary>
internal sealed class ArrasteParaFora
{
    public enum Acao { Nada, Saiu, Moveu, Soltou }
    public const float LIMITE = 4;

    private Vector2? _inicio;
    private bool _fora;

    public Acao Passo(float x, float y, bool soltou)
    {
        if (_inicio == null)
        {
            if (soltou) return Acao.Nada;
            _inicio = new Vector2(x, y);
            _fora = false;
            return Acao.Nada;
        }
        if (soltou)
        {
            bool fora = _fora;
            _inicio = null;
            _fora = false;
            return fora ? Acao.Soltou : Acao.Nada;
        }
        if (_fora) return Acao.Moveu;
        if (Vector2.Distance(_inicio.Value, new Vector2(x, y)) > LIMITE) { _fora = true; return Acao.Saiu; }
        return Acao.Nada;
    }
}
