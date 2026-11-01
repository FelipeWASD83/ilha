using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.ContaGotas;

/// <summary>
/// O cartão do conta-gotas: a cor que acabou de ser pega (o hex já foi copiado), RGB e HSL para copiar com um clique
/// e as últimas cores. Ferramenta: nunca fica em andamento, só aparece expandida depois de pegar uma cor.
/// </summary>
internal sealed class AtividadeContaGotas : Atividade
{
    public override string Tipo => "contagotas";
    public override string Nome => "Conta-gotas";
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    private const float L = 340, Y_COR = 24, LADO_COR = 64, X_FMT = 80, LINHA_FMT = 22, Y_RECENTES = 106, Y_GRADE = 126,
        LADO_MINI = 22, PASSO_MINI = 28, LARGURA_BOTAO = 104;

    private readonly Func<IReadOnlyList<string>> _recentes;
    private readonly Func<bool> _minusculas;
    private readonly Action _pegarOutra;
    private readonly Action<string> _copiar;
    private CorDaTela? _atual;
    private string? _copiado;

    public AtividadeContaGotas(Func<IReadOnlyList<string>> recentes, Func<bool> minusculas, Action pegarOutra, Action<string> copiar)
    {
        _recentes = recentes;
        _minusculas = minusculas;
        _pegarOutra = pegarOutra;
        _copiar = copiar;
    }

    public CorDaTela? Atual => _atual;

    /// <summary>Uma cor nova foi pega (o hex já está na área de transferência se `copiado` for "hex").</summary>
    public void Mostrar(CorDaTela cor, string? copiado)
    {
        _atual = cor;
        _copiado = copiado;
        // a grade de recentes pode ter ganhado uma linha: cena nova
        MudouTamanho();
    }

    public void Recarregar() => MudouTamanho();

    internal string Formato(string qual, CorDaTela c) => qual switch
    {
        "rgb" => c.Rgb(),
        "hsl" => c.Hsl(),
        _ => c.Hex(_minusculas()),
    };

    private void Copiar(string qual)
    {
        if (_atual is not { } c) return;
        _copiar(Formato(qual, c));
        _copiado = qual;
        Redesenhar();
    }

    private void Escolher(string hex)
    {
        if (!CorDaTela.TentarLer(hex, out var c)) return;
        _atual = c;
        _copiar(Formato("hex", c));
        _copiado = "hex";
        Redesenhar();
    }

    private static int Colunas => (int)((L + PASSO_MINI - LADO_MINI) / PASSO_MINI);

    protected override Cena Montar(Palco p, Forma f)
    {
        if (f is Forma.Compacta or Forma.Bolha)
        {
            // não costuma aparecer (a ferramenta não fica em andamento), mas toda forma tem cena
            var pequena = p.NovaCena(f == Forma.Bolha ? new Vector2(24, 24) : new Vector2(130, 24));
            p.Desenho(pequena, Vector2.Zero, pequena.Tamanho, (dc, t) =>
            {
                IconeContaGotas.Desenhar(dc, p, new System.Drawing.RectangleF(0, 0, 24, 24), _atual);
                if (f == Forma.Compacta && _atual is { } c)
                    p.Texto.Desenhar(dc, c.Hex(_minusculas()), new Vector2(32, 3), 13, Cor.Branco, tabular: true, largura: t.X - 32);
            });
            return pequena;
        }

        var recentes = _recentes();
        int linhas = Math.Max(1, (int)Math.Ceiling(recentes.Count / (double)Colunas));
        float altura = _atual == null ? 92 : Y_GRADE + linhas * PASSO_MINI - (PASSO_MINI - LADO_MINI);
        var cena = p.NovaCena(new Vector2(L, altura));
        p.Desenho(cena, Vector2.Zero, cena.Tamanho, (dc, t) =>
        {
            p.Texto.Desenhar(dc, "Conta-gotas", new Vector2(0, 0), 12, Cor.Secundario);
            // botão "Pegar outra" no canto
            Icone.Retangulo(dc, t.X - LARGURA_BOTAO, -2, LARGURA_BOTAO, 20, 10, new Color4(1, 1, 1, 0.1f));
            IconeContaGotas.Desenhar(dc, p, new System.Drawing.RectangleF(t.X - LARGURA_BOTAO + 6, 0, 16, 16), null);
            p.Texto.Desenhar(dc, _atual == null ? "Pegar cor" : "Pegar outra", new Vector2(t.X - LARGURA_BOTAO + 26, 0), 11.5f, Cor.Branco, FontWeight.Medium);

            if (_atual is not { } c)
            {
                p.Texto.Desenhar(dc, "Nenhuma cor ainda.", new Vector2(0, 30), 15, Cor.Branco, FontWeight.Medium);
                p.Texto.Desenhar(dc, "\"Pegar cor\" (ou o atalho) abre a lupa; clique copia, Esc cancela.", new Vector2(0, 56), 12, Cor.Secundario, FontWeight.Normal, largura: t.X);
                return;
            }

            Amostra(dc, 0, Y_COR, LADO_COR, 14, c, false);
            string[] quais = { "hex", "rgb", "hsl" };
            string[] rotulos = { "HEX", "RGB", "HSL" };
            for (int i = 0; i < 3; i++)
            {
                float y = Y_COR + i * LINHA_FMT;
                p.Texto.Desenhar(dc, rotulos[i], new Vector2(X_FMT, y + 3), 10.5f, Cor.Secundario, FontWeight.SemiBold);
                bool copiado = _copiado == quais[i];
                float larguraValor = t.X - X_FMT - 36 - (copiado ? 62 : 0);
                p.Texto.Desenhar(dc, Formato(quais[i], c), new Vector2(X_FMT + 36, y), 14, Cor.Branco, i == 0 ? FontWeight.SemiBold : FontWeight.Normal, tabular: true, largura: larguraValor);
                if (copiado)
                {
                    var tm = p.Texto.Medir("Copiado", 11, FontWeight.Medium);
                    p.Texto.Desenhar(dc, "Copiado", new Vector2(t.X - tm.X, y + 3), 11, Cor.Verde, FontWeight.Medium);
                }
            }
            Icone.Retangulo(dc, 0, Y_RECENTES - 8, t.X, 1, 0.5f, Cor.Fraco);
            p.Texto.Desenhar(dc, "Recentes", new Vector2(0, Y_RECENTES), 11, Cor.Secundario);
            var dica = "clique numa para copiar";
            var td = p.Texto.Medir(dica, 11, FontWeight.Normal);
            p.Texto.Desenhar(dc, dica, new Vector2(t.X - td.X, Y_RECENTES), 11, Cor.Secundario, FontWeight.Normal);

            var lista = _recentes();
            for (int i = 0; i < lista.Count; i++)
            {
                if (!CorDaTela.TentarLer(lista[i], out var k)) continue;
                var (x, y) = PosicaoMini(i);
                Amostra(dc, x, y, LADO_MINI, 6, k, k == c);
            }
        });

        cena.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(L - LARGURA_BOTAO, -2, LARGURA_BOTAO, 20), Acao = _pegarOutra });
        if (_atual != null)
        {
            string[] quais = { "hex", "rgb", "hsl" };
            for (int i = 0; i < 3; i++)
            {
                string q = quais[i];
                cena.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(X_FMT - 4, Y_COR + i * LINHA_FMT, L - X_FMT + 4, LINHA_FMT), Acao = () => Copiar(q) });
            }
            // o quadrado grande copia o hex de novo
            cena.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(0, Y_COR, LADO_COR, LADO_COR), Acao = () => Copiar("hex") });
            for (int i = 0; i < recentes.Count; i++)
            {
                var (x, y) = PosicaoMini(i);
                string hex = recentes[i];
                cena.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(x - 2, y - 2, LADO_MINI + 4, LADO_MINI + 4), Acao = () => Escolher(hex) });
            }
        }
        return cena;
    }

    private static (float x, float y) PosicaoMini(int i) => (i % Colunas * PASSO_MINI, Y_GRADE + i / Colunas * PASSO_MINI);

    /// <summary>Quadrado arredondado da cor, com contorno fino (cor escura sumiria no preto da ilha) e anel na escolhida.</summary>
    private static void Amostra(ID2D1DeviceContext dc, float x, float y, float lado, float raio, CorDaTela c, bool marcada)
    {
        if (marcada) Icone.Retangulo(dc, x - 3, y - 3, lado + 6, lado + 6, raio + 3, Cor.Branco);
        if (marcada) Icone.Retangulo(dc, x - 1.5f, y - 1.5f, lado + 3, lado + 3, raio + 1.5f, new Color4(0, 0, 0, 1));
        Icone.Retangulo(dc, x, y, lado, lado, raio, c.Color4());
        using var b = dc.CreateSolidColorBrush(new Color4(1, 1, 1, c.Clara ? 0.12f : 0.22f));
        var rr = new RoundedRectangle(new System.Drawing.RectangleF(x + 0.5f, y + 0.5f, lado - 1, lado - 1), raio, raio);
        dc.DrawRoundedRectangle(rr, b, 1f);
    }
}

/// <summary>O ícone do conta-gotas (menu, botão e pílula): bulbo, anel, tubo na diagonal e uma gota da última cor.</summary>
internal static class IconeContaGotas
{
    public static void Desenhar(ID2D1DeviceContext dc, Palco p, System.Drawing.RectangleF r, CorDaTela? gota)
    {
        float u = r.Width / 24f;
        Vector2 P(float x, float y) => new(r.X + x * u, r.Y + y * u);
        var F = p.G.Fabrica;
        // tubo de vidro (do anel até a ponta)
        Icone.Linha(dc, P(14.5f, 9.5f), P(6.5f, 17.5f), 3.4f * u, new Color4(1, 1, 1, 0.62f), F);
        Icone.Linha(dc, P(6.5f, 17.5f), P(4.6f, 19.4f), 1.6f * u, new Color4(1, 1, 1, 0.62f), F);
        // anel (perpendicular ao tubo) e bulbo de borracha
        Icone.Linha(dc, P(11.6f, 7.6f), P(16.4f, 12.4f), 2.6f * u, Cor.Branco, F);
        Icone.Circulo(dc, P(17.4f, 6.6f), 4.6f * u, Cor.Branco);
        // a gota pingando
        var cor = gota?.Color4() ?? Cor.Azul;
        Icone.Circulo(dc, P(4.2f, 22f), 1.9f * u, cor);
    }
}
