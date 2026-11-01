using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace Ilha.Privacidade;

/// <summary>
/// Câmera ou microfone em uso. Com outra atividade na frente, quem desenha as bolinhas é o núcleo (S.Indicadores);
/// esta atividade é a que aparece quando não tem mais nada: a pílula mínima só com as bolinhas, e o cartão (ao
/// passar o mouse) dizendo quem está usando. Não anuncia chegada e some no modo competitivo.
/// </summary>
internal sealed class AtividadePrivacidade : Atividade
{
    public const float PONTO = 7, VAO = 5;
    private const float LARGURA_CARTAO = 300, LINHA = 26, TOPO = 22;

    public override string Tipo => "privacidade";
    public override string Nome => "Câmera e microfone";
    public override bool AnunciaChegada => false;
    public override float? LarguraCompactaMinima => 40;

    private EstadoPrivacidade _estado = EstadoPrivacidade.Vazio;

    public void Atualizar(EstadoPrivacidade e)
    {
        if (e.Equals(_estado)) return;
        _estado = e;
        if (!e.Algum) { Desligar(); return; }
        if (!Ativa) Ligar();
        else MudouTamanho();
    }

    public void Parar()
    {
        _estado = EstadoPrivacidade.Vazio;
        Desligar();
    }

    /// <summary>Largura das bolinhas lado a lado (microfone à esquerda e câmera à direita, como o núcleo desenha).</summary>
    public static float LarguraDasBolinhas(bool camera, bool microfone)
    {
        int n = (camera ? 1 : 0) + (microfone ? 1 : 0);
        return n == 0 ? PONTO : n * PONTO + (n - 1) * VAO;
    }

    private void Bolinhas(ID2D1DeviceContext dc, Vector2 tamanho)
    {
        bool cam = _estado.Camera.Count > 0, mic = _estado.Microfone.Count > 0;
        float l = LarguraDasBolinhas(cam, mic);
        float x = (tamanho.X - l) / 2 + PONTO / 2, y = tamanho.Y / 2;
        if (mic) { Icone.Circulo(dc, new Vector2(x, y), PONTO / 2, Cor.Laranja); x += PONTO + VAO; }
        if (cam) Icone.Circulo(dc, new Vector2(x, y), PONTO / 2, Cor.Verde);
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(24, 24));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) => Bolinhas(dc, t));
                return c;
            }
            case Forma.Compacta:
            {
                float l = LarguraDasBolinhas(_estado.Camera.Count > 0, _estado.Microfone.Count > 0);
                var c = p.NovaCena(new Vector2(l, 24));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) => Bolinhas(dc, t));
                return c;
            }
            default:
            {
                var linhas = _estado.Linhas();
                var c = p.NovaCena(new Vector2(LARGURA_CARTAO, TOPO + Math.Max(1, linhas.Count) * LINHA - 4));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    var ls = _estado.Linhas();
                    p.Texto.Desenhar(dc, "Em uso agora", new Vector2(0, 0), 12, Cor.Secundario, FontWeight.Normal);
                    float y = TOPO;
                    foreach (var (camera, rotulo, apps) in ls)
                    {
                        Icone.Circulo(dc, new Vector2(5, y + 10), 4.5f, camera ? Cor.Verde : Cor.Laranja);
                        p.Texto.Desenhar(dc, rotulo, new Vector2(18, y), 14, Cor.Branco);
                        float x = 18 + p.Texto.Medir(rotulo, 14).X + 5;
                        p.Texto.Desenhar(dc, apps, new Vector2(x, y), 14, Cor.Secundario, FontWeight.Normal, largura: MathF.Max(20, t.X - x));
                        y += LINHA;
                    }
                });
                return c;
            }
        }
    }
}
