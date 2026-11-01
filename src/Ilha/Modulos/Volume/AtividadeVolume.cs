using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Volume;

/// <summary>
/// O volume na ilha: aparece sozinho por uns segundos quando o volume muda por fora (tecla, roda do headset,
/// AirPods, outro app) e quando um aparelho conecta ou desconecta. Rodinha em cima muda o volume; clique abre o
/// cartão com a barra arrastável, o mudo e a lista de saídas para trocar.
/// </summary>
internal sealed class AtividadeVolume : Atividade
{
    public override string Tipo => "volume";
    public override string Nome => "Volume";
    public override bool AnunciaChegada => false;

    private readonly Audio _audio;
    private readonly Func<ConfigVolume> _cfg;
    private readonly Func<bool> _expandida;
    private readonly Exibicao _exib = new();
    private readonly System.Windows.Forms.Timer _prazo = new();
    private int _rodaAcumulada;
    private int _linhas = -1;
    private bool _arrastando;

    private const float CARTAO_L = 340, LINHA_A = 32, LISTA_Y = 100, BARRA_X = 48, MAX_LINHAS = 6;
    private static float BarraL => CARTAO_L - BARRA_X - 50;

    public AtividadeVolume(Audio audio, Func<ConfigVolume> cfg, Func<bool> expandida)
    {
        _audio = audio;
        _cfg = cfg;
        _expandida = expandida;
        _prazo.Tick += (_, _) => Conferir();
    }

    // ---------------------------------------------------------------- quando aparece

    /// <summary>O volume mudou: redesenha; se `mostrar`, aparece (ou renova o prazo).</summary>
    public void VolumeMudou(bool mostrar)
    {
        if (mostrar) Mostrar();
        else if (Ativa) Redesenhar();
    }

    private void Mostrar()
    {
        bool mudouModo = _exib.Volume(DateTime.Now, _cfg().Segundos);
        Armar();
        if (mudouModo) MudouTamanho();
        else Redesenhar();
        Ligar();
    }

    public void Aviso(bool conectou, string aparelho)
    {
        // aviso de aparelho não passa por cima do volume que está sendo mexido agora
        if (Ativa && _exib.Atual == Exibicao.Modo.Volume && _exib.Ate > DateTime.Now) return;
        bool mudou = _exib.Aviso(DateTime.Now, conectou, aparelho, Math.Max(3, _cfg().Segundos));
        Armar();
        if (mudou) MudouTamanho();
        Ligar();
    }

    public void ListaMudou()
    {
        if (!Ativa) return;
        int n = Math.Min(Math.Max(_audio.Saidas.Count, 1), (int)MAX_LINHAS);
        if (n != _linhas) { _linhas = n; MudouTamanho(); }
        else Redesenhar();
    }

    private void Armar()
    {
        _prazo.Stop();
        var ms = (_exib.Ate - DateTime.Now).TotalMilliseconds;
        _prazo.Interval = (int)Math.Clamp(ms, 50, 60_000);
        _prazo.Start();
    }

    private void Conferir()
    {
        _prazo.Stop();
        var antes = _exib.Atual;
        var prox = _exib.Conferir(DateTime.Now, _expandida() || _arrastando);
        if (prox is DateTime p)
        {
            _prazo.Interval = (int)Math.Clamp((p - DateTime.Now).TotalMilliseconds, 50, 60_000);
            _prazo.Start();
            return;
        }
        Desligar();
        // saiu do aviso de aparelho: a próxima aparição volta à cena do volume, não reaproveita a do aviso
        if (antes != Exibicao.Modo.Volume) MudouTamanho();
    }

    public void Esconder()
    {
        _prazo.Stop();
        var antes = _exib.Atual;
        _exib.Desligar();
        Desligar();
        if (antes != Exibicao.Modo.Volume) MudouTamanho();
    }

    // ---------------------------------------------------------------- mexer no volume

    public override bool Rolar(int delta)
    {
        if (!_audio.TemAparelho) return false;
        _rodaAcumulada += delta;
        int dentes = _rodaAcumulada / 120;
        if (dentes == 0) return true;
        _rodaAcumulada -= dentes * 120;
        var nova = Logica.Rolar(_audio.Escala, dentes * 120, _cfg().PassoRolar);
        // subir com o som mudo tira o mudo, como a tecla de volume do Windows
        if (dentes > 0 && _audio.Mudo) _audio.DefinirMudo(false);
        _audio.DefinirEscala(nova);
        Mostrar();
        return true;
    }

    private void Arrastar(float x)
    {
        _audio.DefinirEscala(Logica.DaBarra(x, BARRA_X, BarraL));
        if (_audio.Mudo && _audio.Escala > 0) _audio.DefinirMudo(false);
        Redesenhar();
    }

    private void AlternarMudo()
    {
        _audio.DefinirMudo(!_audio.Mudo);
        Mostrar();
    }

    private void Trocar(Saida s)
    {
        if (_audio.TrocarSaida(s.Id)) Mostrar();
        else Redesenhar();
    }

    // ---------------------------------------------------------------- desenho

    private List<Saida> SaidasVisiveis()
    {
        var todas = _audio.Saidas;
        if (todas.Count <= MAX_LINHAS) return todas;
        var atual = todas.FirstOrDefault(x => x.Contem(_audio.IdPadrao));
        var r = todas.Where(x => x != atual).Take((int)MAX_LINHAS - (atual != null ? 1 : 0)).ToList();
        if (atual != null) r.Insert(Math.Min(todas.IndexOf(atual), r.Count), atual);
        return r;
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(24, 24));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                    Icone.Anel(dc, p.G.Fabrica, new Vector2(12, 12), 9, 3, _audio.Mudo ? 0 : _audio.Escala, _audio.Mudo ? Cor.Secundario : Cor.Branco));
                return c;
            }
            case Forma.Compacta when _exib.Atual != Exibicao.Modo.Volume:
            {
                var c = p.NovaCena(new Vector2(240, 24));
                bool conectou = _exib.Atual == Exibicao.Modo.Conectou;
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    Desenhos.Fone(dc, p.G.Fabrica, new Vector2(0, 0), 1f, conectou ? Cor.Verde : Cor.Secundario);
                    string estado = conectou ? "conectado" : "desconectado";
                    var te = p.Texto.Medir(estado, 12, FontWeight.Normal);
                    p.Texto.Desenhar(dc, estado, new Vector2(t.X - te.X, (t.Y - te.Y) / 2), 12, conectou ? Cor.Verde : Cor.Secundario, FontWeight.Normal);
                    var tn = p.Texto.Medir("Ág", 13);
                    p.Texto.Desenhar(dc, _exib.Aparelho, new Vector2(30, (t.Y - tn.Y) / 2), 13, Cor.Branco, largura: MathF.Max(20, t.X - 30 - te.X - 8));
                });
                return c;
            }
            case Forma.Compacta:
            {
                var c = p.NovaCena(new Vector2(196, 24));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    bool mudo = _audio.Mudo;
                    Desenhos.AltoFalante(dc, p.G.Fabrica, Vector2.Zero, 1f, Cor.Branco, Logica.Ondas(_audio.Escala, mudo), mudo);
                    string num = _audio.TemAparelho ? Logica.Porcentagem(_audio.Escala).ToString() : "-";
                    var tn = p.Texto.Medir(num, 13, FontWeight.SemiBold, tabular: true);
                    p.Texto.Desenhar(dc, num, new Vector2(t.X - tn.X, (t.Y - tn.Y) / 2), 13, mudo ? Cor.Secundario : Cor.Branco, FontWeight.SemiBold, tabular: true);
                    float x0 = 32, l = t.X - x0 - 34;
                    Icone.Retangulo(dc, x0, 10, l, 4, 2, Cor.Fraco);
                    if (_audio.Escala > 0.005f) Icone.Retangulo(dc, x0, 10, MathF.Max(4, l * _audio.Escala), 4, 2, mudo ? Cor.Secundario : Cor.Branco);
                });
                return c;
            }
            default:
                return Cartao(p);
        }
    }

    private Cena Cartao(Palco p)
    {
        var saidas = SaidasVisiveis();
        _linhas = Math.Max(saidas.Count, 1);
        var c = p.NovaCena(new Vector2(CARTAO_L, LISTA_Y + _linhas * LINHA_A));
        var F = p.G.Fabrica;
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            bool mudo = _audio.Mudo;
            float e = _audio.Escala;
            p.Texto.Desenhar(dc, "Volume", new Vector2(0, 0), 13, Cor.Secundario);
            string db = !_audio.TemAparelho ? "sem saída" : mudo ? "mudo" : Logica.TextoDb(_audio.Db);
            var td = p.Texto.Medir(db, 12, FontWeight.Normal, tabular: true);
            p.Texto.Desenhar(dc, db, new Vector2(t.X - td.X, 1), 12, Cor.Secundario, FontWeight.Normal, tabular: true);

            // mudo
            Icone.Circulo(dc, new Vector2(18, 42), 17, mudo ? new Color4(1f, 1f, 1f, 0.9f) : Cor.Fraco);
            Desenhos.AltoFalante(dc, F, new Vector2(6, 30), 1f, mudo ? new Color4(0f, 0f, 0f, 1f) : Cor.Branco, Logica.Ondas(e, mudo), mudo);

            // barra
            float l = BarraL;
            Icone.Retangulo(dc, BARRA_X, 39, l, 6, 3, Cor.Fraco);
            float cheio = l * Math.Clamp(e, 0f, 1f);
            if (cheio > 0.5f) Icone.Retangulo(dc, BARRA_X, 39, MathF.Max(6, cheio), 6, 3, mudo ? Cor.Secundario : Cor.Branco);
            Icone.Circulo(dc, new Vector2(BARRA_X + cheio, 42), 8, mudo ? new Color4(0.75f, 0.75f, 0.75f, 1f) : Cor.Branco);
            string num = _audio.TemAparelho ? Logica.Porcentagem(e).ToString() : "-";
            var tn = p.Texto.Medir(num, 18, FontWeight.SemiBold, tabular: true);
            p.Texto.Desenhar(dc, num, new Vector2(t.X - tn.X, 42 - tn.Y / 2), 18, mudo ? Cor.Secundario : Cor.Branco, FontWeight.SemiBold, tabular: true);

            Icone.Retangulo(dc, 0, 70, t.X, 1, 0.5f, Cor.Fraco);
            p.Texto.Desenhar(dc, _audio.PodeTrocar ? "Saídas" : "Saídas · trocar por aqui não está disponível neste Windows", new Vector2(0, 78), 12, Cor.Secundario, largura: t.X);

            if (saidas.Count == 0)
            {
                p.Texto.Desenhar(dc, "Nenhuma saída de áudio ativa", new Vector2(26, LISTA_Y + 7), 14, Cor.Secundario, FontWeight.Normal, largura: t.X - 26);
                return;
            }
            for (int i = 0; i < saidas.Count; i++)
            {
                float y = LISTA_Y + i * LINHA_A;
                bool atual = saidas[i].Contem(_audio.IdPadrao);
                var centro = new Vector2(8, y + 16);
                if (atual)
                {
                    Icone.Circulo(dc, centro, 8, Cor.Verde);
                    Icone.Linha(dc, new Vector2(centro.X - 3.5f, centro.Y + 0.2f), new Vector2(centro.X - 1, centro.Y + 2.8f), 1.8f, Cor.Branco, F);
                    Icone.Linha(dc, new Vector2(centro.X - 1, centro.Y + 2.8f), new Vector2(centro.X + 3.8f, centro.Y - 2.6f), 1.8f, Cor.Branco, F);
                }
                else Icone.Anel(dc, F, centro, 7, 1.6f, 0, Cor.Branco);
                p.Texto.Desenhar(dc, saidas[i].Nome, new Vector2(26, y + 6), 14, atual ? Cor.Branco : new Color4(1f, 1f, 1f, 0.82f),
                    atual ? FontWeight.SemiBold : FontWeight.Normal, largura: t.X - 26);
            }
        });

        c.Botoes.Add(new Botao { Area = new(0, 24, 36, 36), Acao = AlternarMudo });
        c.Botoes.Add(new Botao
        {
            Area = new(BARRA_X - 10, 26, BarraL + 20, 32),
            Arrastar = (x, y, soltou) =>
            {
                _arrastando = !soltou;
                Arrastar(x);
                if (soltou) Mostrar();
            },
        });
        if (_audio.PodeTrocar)
            for (int i = 0; i < saidas.Count; i++)
            {
                var s = saidas[i];
                if (s.Contem(_audio.IdPadrao)) continue;
                c.Botoes.Add(new Botao { Area = new(0, LISTA_Y + i * LINHA_A, CARTAO_L, LINHA_A), Acao = () => Trocar(s) });
            }
        return c;
    }

    public override void Dispose()
    {
        _prazo.Stop();
        _prazo.Dispose();
    }
}

/// <summary>Ícones do volume desenhados em Direct2D, numa caixa de 24x24 DIP (vezes `s`).</summary>
internal static class Desenhos
{
    public static void AltoFalante(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 o, float s, Color4 cor, int ondas, bool mudo)
    {
        Vector2 P(float x, float y) => new(o.X + x * s, o.Y + y * s);
        Icone.Retangulo(dc, o.X + 2.5f * s, o.Y + 8.5f * s, 4f * s, 7f * s, 1f * s, cor);
        Poligono(dc, f, cor, P(6f, 8.5f), P(11.5f, 4f), P(11.5f, 20f), P(6f, 15.5f));
        if (mudo)
        {
            Icone.Linha(dc, P(14.5f, 9f), P(20.5f, 15f), 1.9f * s, cor, f);
            Icone.Linha(dc, P(20.5f, 9f), P(14.5f, 15f), 1.9f * s, cor, f);
            return;
        }
        for (int i = 0; i < ondas; i++)
            Arco(dc, f, P(11.5f, 12f), (4.2f + i * 3.6f) * s, -48, 48, 1.8f * s, cor);
    }

    /// <summary>Fone de ouvido: arco da tiara e as duas conchas.</summary>
    public static void Fone(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 o, float s, Color4 cor)
    {
        Arco(dc, f, new Vector2(o.X + 12 * s, o.Y + 13 * s), 8.5f * s, -180, 0, 2f * s, cor);
        Icone.Retangulo(dc, o.X + 2.5f * s, o.Y + 12.5f * s, 4.5f * s, 8f * s, 2f * s, cor);
        Icone.Retangulo(dc, o.X + 17f * s, o.Y + 12.5f * s, 4.5f * s, 8f * s, 2f * s, cor);
    }

    private static void Poligono(ID2D1DeviceContext dc, ID2D1Factory1 f, Color4 cor, params Vector2[] pts)
    {
        using var g = f.CreatePathGeometry();
        using (var sk = g.Open())
        {
            sk.BeginFigure(pts[0], FigureBegin.Filled);
            for (int i = 1; i < pts.Length; i++) sk.AddLine(pts[i]);
            sk.EndFigure(FigureEnd.Closed);
            sk.Close();
        }
        using var b = dc.CreateSolidColorBrush(cor);
        dc.FillGeometry(g, b);
    }

    /// <summary>Arco no sentido horário de `de` até `ate` graus (0 = direita, 90 = embaixo).</summary>
    private static void Arco(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float r, float de, float ate, float espessura, Color4 cor)
    {
        float a0 = de * MathF.PI / 180f, a1 = ate * MathF.PI / 180f;
        using var g = f.CreatePathGeometry();
        using (var sk = g.Open())
        {
            sk.BeginFigure(new Vector2(c.X + r * MathF.Cos(a0), c.Y + r * MathF.Sin(a0)), FigureBegin.Hollow);
            sk.AddArc(new ArcSegment
            {
                Point = new Vector2(c.X + r * MathF.Cos(a1), c.Y + r * MathF.Sin(a1)),
                Size = new Size(r, r),
                SweepDirection = SweepDirection.Clockwise,
                ArcSize = ate - de > 180 ? ArcSize.Large : ArcSize.Small,
            });
            sk.EndFigure(FigureEnd.Open);
            sk.Close();
        }
        using var b = dc.CreateSolidColorBrush(cor);
        using var traco = f.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        dc.DrawGeometry(g, b, espessura, traco);
    }
}
