using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.LimiteClaude;

/// <summary>
/// O cartão do limite do plano do Claude: as barras da janela de 5 horas e da semana, quando cada uma volta e quão
/// velho é o dado. Ferramenta: abre pelo menu e nunca fica na pílula sozinho.
/// </summary>
internal sealed class AtividadeLimite : Atividade
{
    public override string Tipo => "limite";
    public override string Nome => Lang.T("Limite do Claude", "Claude limit");
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    private const float L = 320, LINHA = 52, TOPO = 30;
    private static readonly Color4 Trilho = new(1f, 1f, 1f, 0.09f);

    private readonly Func<LeituraDoUso?> _leitura;
    private readonly Func<bool> _arquivoExiste;
    private readonly Func<int[]> _limiares;
    // só com o cartão na tela: o "atualizado há" anda sozinho
    private readonly System.Windows.Forms.Timer _idade = new() { Interval = 30_000 };
    private bool? _vazioMontado;

    public AtividadeLimite(Func<LeituraDoUso?> leitura, Func<bool> arquivoExiste, Func<int[]> limiares)
    {
        _leitura = leitura;
        _arquivoExiste = arquivoExiste;
        _limiares = limiares;
        _idade.Tick += (_, _) =>
        {
            if (!TemCenaViva) { _idade.Stop(); return; }
            Redesenhar();
        };
    }

    internal static double Agora() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private bool Vazio => _leitura() == null;

    /// <summary>Chegou leitura nova, o reset passou ou a config mudou.</summary>
    public void Atualizar()
    {
        if (_vazioMontado != null && _vazioMontado != Vazio) MudouTamanho();
        else Redesenhar();
    }

    public override void ExpandidaMudou(bool aberta)
    {
        if (aberta) { _idade.Start(); Redesenhar(); }
        else _idade.Stop();
    }

    public override void Dispose() => _idade.Dispose();

    protected override Cena Montar(Palco p, Forma f)
    {
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(24, 24));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) => Anel(dc, p, new Vector2(12, 12), 9, 2.6f));
                return c;
            }
            case Forma.Compacta:
            {
                var c = p.NovaCena(new Vector2(110, 24));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    Anel(dc, p, new Vector2(12, 12), 9, 2.6f);
                    string tx = TextoDoUso.Detalhe(_leitura(), Agora()) ?? Lang.T("Limite", "Limit");
                    var tam = p.Texto.Medir(tx, 13, tabular: true);
                    p.Texto.Desenhar(dc, tx, new Vector2(t.X - MathF.Min(tam.X, t.X - 30), (t.Y - tam.Y) / 2), 13, Cor.Branco, tabular: true, largura: t.X - 30);
                });
                return c;
            }
            default:
                return Cartao(p);
        }
    }

    /// <summary>O ícone: um anel com o uso da janela de 5 horas (vazio sem dado). Usado também no menu e no alerta.</summary>
    /// <summary>The menu's ring: the window closer to its limit (session or week), with its percentage inside.</summary>
    internal void Anel(ID2D1DeviceContext dc, Palco p, Vector2 centro, float raio, float espessura)
    {
        var j = TextoDoUso.MaisApertada(_leitura(), Agora());
        float progresso = j == null ? 0 : (float)(j.Porcentagem / 100);
        Icone.Anel(dc, p.G.Fabrica, centro, raio, espessura, progresso, j == null ? Cor.Secundario : CorDe(j.Porcentagem, _limiares()));
        string texto = j == null ? "–" : TextoDoUso.Porcento(j.Porcentagem);
        float tam = texto.Length > 3 ? 8.5f : 9.5f;
        var m = p.Texto.Medir(texto, tam, FontWeight.SemiBold, tabular: true);
        p.Texto.Desenhar(dc, texto, new Vector2(centro.X - m.X / 2, centro.Y - m.Y / 2), tam, Cor.Branco, FontWeight.SemiBold, tabular: true);
    }

    internal static Color4 CorDe(double pct, int[] limiares)
    {
        if (limiares.Length > 1 && pct >= limiares[^1]) return Cor.Vermelho;
        if (limiares.Length > 0 && pct >= limiares[0]) return Cor.Laranja;
        return Cor.Coral;
    }

    private Cena Cartao(Palco p)
    {
        bool vazio = Vazio;
        _vazioMontado = vazio;
        float altura = vazio ? 100 : TOPO + 2 * LINHA - 4;
        var c = p.NovaCena(new Vector2(L, altura));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            double agora = Agora();
            var l = _leitura();
            Anel(dc, p, new Vector2(9, 10), 7, 2.4f);
            p.Texto.Desenhar(dc, Lang.T("Limite do Claude", "Claude limit"), new Vector2(24, 1), 13, Cor.Secundario, largura: 160);
            if (l == null)
            {
                p.Texto.Desenhar(dc, _arquivoExiste() ? Lang.T("Não deu para ler o uso", "Couldn't read the usage") : Lang.T("Sem dados ainda", "No data yet"), new Vector2(0, 26), 17, Cor.Branco, largura: L);
                string[] linhas = Lang.English
                    ? new[]
                    {
                        "Relies on the Claude Code status line:",
                        "ferramentas/statusline_uso.py writes the usage to",
                        ".ilha/uso-claude.json after every reply.",
                    }
                    : new[]
                    {
                        "Depende da status line do Claude Code: o",
                        "ferramentas/statusline_uso.py grava o uso em",
                        ".ilha/uso-claude.json a cada resposta.",
                    };
                for (int i = 0; i < linhas.Length; i++)
                    p.Texto.Desenhar(dc, linhas[i], new Vector2(0, 52 + i * 16), 12, Cor.Secundario, FontWeight.Normal, largura: L);
                return;
            }
            string idade = TextoDoUso.Idade(agora - l.ColetadoEm);
            var ti = p.Texto.Medir(idade, 12, FontWeight.Normal);
            p.Texto.Desenhar(dc, idade, new Vector2(L - MathF.Min(ti.X, 140), 2), 12, Cor.Secundario, FontWeight.Normal, largura: 140);
            var limiares = _limiares();
            var agoraLocal = DateTime.Now;
            Linha(dc, p, TOPO, Lang.T("Sessão de 5 horas", "5-hour session"), l.Sessao, agora, agoraLocal, limiares);
            Linha(dc, p, TOPO + LINHA, Lang.T("Semana", "Week"), l.Semana, agora, agoraLocal, limiares);
        });
        return c;
    }

    private static void Linha(ID2D1DeviceContext dc, Palco p, float y, string nome, Janela? j, double agora, DateTime agoraLocal, int[] limiares)
    {
        bool venceu = j != null && j.Venceu(agora);
        p.Texto.Desenhar(dc, nome, new Vector2(0, y), 14, Cor.Branco, largura: 200);
        Icone.Retangulo(dc, 0, y + 22, L, 6, 3, Trilho);
        string sub;
        if (j == null) sub = Lang.T("sem leitura (só para assinantes, após uma resposta)", "no reading (subscribers only, after a reply)");
        else if (venceu) sub = Lang.T("renovou ", "reset ") + TextoDoUso.Quando(TextoDoUso.Local(j.VoltaEm!.Value), agoraLocal, j.Semanal) + Lang.T(" · sem leitura nova", " · no new reading");
        else
        {
            string pct = TextoDoUso.Porcento(j.Porcentagem);
            var tp = p.Texto.Medir(pct, 14, tabular: true);
            p.Texto.Desenhar(dc, pct, new Vector2(L - tp.X, y), 14, Cor.Branco, tabular: true);
            float w = (float)(L * Math.Clamp(j.Porcentagem, 0, 100) / 100);
            // 1% ainda aparece como um tracinho
            if (j.Porcentagem > 0) Icone.Retangulo(dc, 0, y + 22, MathF.Max(w, 6), 6, 3, CorDe(j.Porcentagem, limiares));
            sub = TextoDoUso.Volta(j, agoraLocal);
        }
        p.Texto.Desenhar(dc, sub, new Vector2(0, y + 31), 12, Cor.Secundario, FontWeight.Normal, largura: L);
    }
}
