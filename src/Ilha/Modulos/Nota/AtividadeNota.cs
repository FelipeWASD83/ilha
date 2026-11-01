using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Nota;

/// <summary>
/// O cartão da nota rápida. Ferramenta: nunca fica "em andamento". O campo é a S.Entrada multilinha: Enter anota,
/// Shift+Enter quebra a linha, Esc cancela (o texto some e o núcleo volta para o menu). Fechar por outro caminho
/// (clique fora, o atalho de novo, jogo competitivo) guarda o rascunho só na memória, para a próxima vez. Gravar
/// roda numa thread de fundo; deu errado, o texto volta como rascunho e o erro aparece.
/// </summary>
internal sealed class AtividadeNota : Atividade
{
    public override string Tipo => "nota";
    public override string Nome => Lang.T("Nota rápida", "Quick note");
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    private static readonly Color4 Amarelo = new(1f, 0.80f, 0.20f, 1f);
    private const float L = 360, CAMPO_Y = 22, CAMPO_A = 88, A_CARTAO = 136, A_SEM_ARQUIVO = 104;
    private const float TAM = 14;

    private readonly Servicos S;
    private readonly Func<string> _arquivo;
    private bool _aberta, _parado, _salvando, _fechandoPorSalvar, _coberto;
    private string _texto = "";
    private int _cursor;
    private string? _erro;
    private bool? _configuradoNaCena;
    private Superficie? _supCampo;
    private IDWriteTextFormat? _formatoCampo, _formatoAviso;

    public AtividadeNota(Servicos s, Func<string> arquivo)
    {
        S = s;
        _arquivo = arquivo;
    }

    private string? Caminho(out string? problema) => CaixaDeEntrada.Caminho(_arquivo(), out problema);
    private bool Configurado => Caminho(out _) != null;

    // ------------------------------------------------------------------ abrir e fechar

    /// <summary>O atalho: abre; com o cartão aberto, fecha (o rascunho fica).</summary>
    public void Alternar()
    {
        if (_parado) return;
        if (S.Ilha.Expandida == this)
        {
            if (_aberta) S.Entrada.Fechar();
            S.Ilha.Recolher();
            return;
        }
        Abrir();
    }

    /// <summary>A entrada do menu (e o atalho): abre o cartão e, com arquivo configurado, já com o campo.</summary>
    public void Abrir()
    {
        if (_parado || S.Config().Pausada) return;
        // num jogo competitivo a ilha não rouba o teclado (e a ferramenta nem aparece)
        if (S.ContextoAtual().Competitivo) return;
        S.Ilha.Expandir(this);
        if (S.Ilha.Expandida != this) return;
        if (Configurado) AbrirCampo();
        else Redesenhar();
    }

    private void AbrirCampo()
    {
        if (_parado || S.Ilha.Expandida != this || S.ContextoAtual().Competitivo) return;
        S.Entrada.Abrir(_texto, AoMudar, AoEnter, AoFechar, multilinha: true);
        // depois do Abrir: se o campo já era nosso, ele fecha o velho antes (e o AoFechar desmarcaria)
        _aberta = true;
        _cursor = S.Entrada.Cursor;
        Redesenhar();
    }

    private void AoFechar()
    {
        if (!_aberta) return;
        _aberta = false;
        if (_fechandoPorSalvar) return;
        // o Esc chega aqui de dentro do KeyDown da caixa: a tecla ainda está apertada. Esc cancela; o resto guarda.
        // Com um alerta de outro módulo por cima do cartão, o Esc foi para o alerta: o rascunho fica
        if (!_coberto && (Nativo.GetAsyncKeyState(0x1B) & 0x8000) != 0)
        {
            _texto = "";
            _cursor = 0;
            _erro = null;
            S.Menu.Redesenhar();
        }
        Redesenhar();
    }

    public override void ExpandidaMudou(bool aberta)
    {
        // fechou sem deixar de ser a expandida = um alerta cobriu o cartão. Chega por NaInterface, antes da tecla que
        // fecha o alerta, e o "aberta" de volta só chega depois do Esc tratado: no AoFechar desse Esc ainda vale true
        _coberto = !aberta && S.Ilha.Expandida == this;
        if (aberta) return;
        // o cartão saiu por outro caminho (atalho principal, outra ferramenta): o campo fecha junto, senão o teclado
        // continuaria indo para a caixa invisível
        S.NaInterface(() =>
        {
            if (_aberta && S.Ilha.Expandida != this) S.Entrada.Fechar();
        });
    }

    public void Contexto(Contexto ctx)
    {
        if (_aberta && ctx.Competitivo) S.Entrada.Fechar();
    }

    public void ConfigMudou()
    {
        if (_parado) return;
        if (_configuradoNaCena != null && _configuradoNaCena != Configurado) MudouTamanho();
        else Redesenhar();
    }

    public void Parar()
    {
        _parado = true;
        if (_aberta) S.Entrada.Fechar();
        // o núcleo não chama o Dispose ao desligar o módulo: sem isto, cada desligar e ligar deixaria dois formatos
        LiberarFormatos();
    }

    private void LiberarFormatos()
    {
        _formatoCampo?.Dispose();
        _formatoCampo = null;
        _formatoAviso?.Dispose();
        _formatoAviso = null;
    }

    /// <summary>A linha pequena da entrada no menu.</summary>
    public string? DetalheDoMenu()
    {
        if (_parado) return null;
        if (!Configurado) return Lang.T("configurar", "set up");
        return _texto.Trim().Length > 0 ? Lang.T("rascunho", "draft") : null;
    }

    // ------------------------------------------------------------------ texto e gravação

    private void AoMudar(string texto)
    {
        int cursor = S.Entrada.Cursor;
        if (texto == _texto && cursor == _cursor) return;
        bool tinhaRascunho = _texto.Trim().Length > 0;
        _cursor = cursor;
        if (texto != _texto)
        {
            _texto = texto;
            if (_erro != null) { _erro = null; Redesenhar(); }
            if (tinhaRascunho != (_texto.Trim().Length > 0)) S.Menu.Redesenhar();
        }
        _supCampo?.Redesenhar();
    }

    private void AoEnter(string texto)
    {
        if (_salvando || _parado) return;
        var item = CaixaDeEntrada.Formatar(texto, DateTime.Now);
        if (item == null) return;
        var caminho = Caminho(out var problema);
        if (caminho == null)
        {
            _erro = problema == "vazio" ? Lang.T("Falta escolher o arquivo da nota na configuração.", "Choose the note file in the settings first.") : problema;
            Redesenhar();
            return;
        }
        _salvando = true;
        _texto = "";
        _cursor = 0;
        _erro = null;
        _fechandoPorSalvar = true;
        try { S.Entrada.Fechar(); }
        finally { _fechandoPorSalvar = false; }
        if (S.Ilha.Expandida == this) S.Ilha.Recolher();
        S.Menu.Redesenhar();
        _ = Task.Run(() =>
        {
            string? erro;
            try { erro = CaixaDeEntrada.Anexar(caminho, item); }
            catch (Exception e) { erro = e.Message; }
            S.NaInterface(() => Terminou(texto, erro));
        });
    }

    private void Terminou(string texto, string? erro)
    {
        _salvando = false;
        if (erro == null)
        {
            Log.Escrever("nota: anotada");
            if (!_parado) AlertarAnotado(CaixaDeEntrada.Resumo(texto));
            return;
        }
        // o texto da nota nunca vai para o log
        Log.Escrever("nota: não anotou: " + erro);
        if (_parado) return;
        // o que ele começou a escrever enquanto gravava fica depois do que voltou
        _texto = _texto.Trim().Length == 0 ? texto : texto.TrimEnd() + "\r\n" + _texto;
        // campo aberto de novo: a caixa de verdade recebe o texto junto, senão a próxima tecla jogaria fora o que voltou
        if (_aberta)
        {
            S.Entrada.Definir(_texto);
            _cursor = S.Entrada.Cursor;
        }
        _erro = Lang.T("Não anotou: ", "Not saved: ") + erro;
        S.Menu.Redesenhar();
        Redesenhar();
        AlertarErro(_erro);
    }

    // ------------------------------------------------------------------ alertas

    private void AlertarAnotado(string resumo)
    {
        Alertar(new Alerta
        {
            Tipo = "nota",
            // quem anotou está olhando
            Som = "",
            Segundos = 1.8f,
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(300, 40));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    var F = p.G.Fabrica;
                    Icone.Circulo(dc, new Vector2(18, 20), 14, Cor.Verde);
                    var preto = new Color4(0f, 0f, 0f, 0.85f);
                    Icone.Linha(dc, new Vector2(11.5f, 20.5f), new Vector2(16, 25), 2.4f, preto, F);
                    Icone.Linha(dc, new Vector2(16, 25), new Vector2(24.5f, 15), 2.4f, preto, F);
                    float y = resumo.Length > 0 ? 1 : 10;
                    p.Texto.Desenhar(dc, Lang.T("Anotado", "Noted"), new Vector2(42, y), 15, Cor.Branco);
                    if (resumo.Length > 0) p.Texto.Desenhar(dc, resumo, new Vector2(42, 21), 12, Cor.Secundario, FontWeight.Normal, largura: t.X - 42);
                });
                return c;
            },
        });
    }

    private void AlertarErro(string erro)
    {
        Alertar(new Alerta
        {
            Tipo = "nota",
            Som = "erro",
            Segundos = 4f,
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(320, 40));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    var F = p.G.Fabrica;
                    Icone.Circulo(dc, new Vector2(18, 20), 14, Cor.Vermelho);
                    Icone.Linha(dc, new Vector2(13, 15), new Vector2(23, 25), 2.4f, Cor.Branco, F);
                    Icone.Linha(dc, new Vector2(23, 15), new Vector2(13, 25), 2.4f, Cor.Branco, F);
                    p.Texto.Desenhar(dc, Lang.T("Nota não anotada", "Note not saved"), new Vector2(42, 1), 15, Cor.Branco);
                    p.Texto.Desenhar(dc, erro, new Vector2(42, 21), 12, Cor.Secundario, FontWeight.Normal, largura: t.X - 42);
                });
                return c;
            },
        });
    }

    // ------------------------------------------------------------------ desenho

    protected override Cena Montar(Palco p, Forma f)
    {
        if (f is Forma.Compacta or Forma.Bolha)
        {
            // não costuma aparecer (a nota não fica em andamento), mas toda forma tem cena
            var c = p.NovaCena(f == Forma.Bolha ? new Vector2(24, 24) : new Vector2(110, 24));
            p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
            {
                Simbolo(dc, p, 2, 2, 20);
                if (f == Forma.Compacta) p.Texto.Desenhar(dc, Lang.T("Nota", "Note"), new Vector2(30, 3), 13, Cor.Branco);
            });
            return c;
        }

        bool configurado = Configurado;
        _configuradoNaCena = configurado;
        return configurado ? CenaCampo(p) : CenaSemArquivo(p);
    }

    private Cena CenaCampo(Palco p)
    {
        var cena = p.NovaCena(new Vector2(L, A_CARTAO));
        p.Desenho(cena, Vector2.Zero, cena.Tamanho, (dc, t) =>
        {
            Simbolo(dc, p, 0, 0, 16);
            p.Texto.Desenhar(dc, Lang.T("Nota rápida", "Quick note"), new Vector2(22, 0), 12, Cor.Secundario);
            string dica = Lang.T("Enter anota · Shift+Enter quebra · Esc cancela", "Enter saves · Shift+Enter new line · Esc cancels");
            var td = p.Texto.Medir(dica, 11, FontWeight.Normal);
            p.Texto.Desenhar(dc, dica, new Vector2(t.X - td.X, 1), 11, Cor.Secundario, FontWeight.Normal);

            Color4 cor = Cor.Secundario;
            string baixo;
            if (_erro != null) { baixo = _erro; cor = Cor.Laranja; }
            else if (_salvando) baixo = Lang.T("Anotando…", "Saving…");
            else
            {
                var caminho = Caminho(out _);
                baixo = caminho == null ? "" : Lang.T("Vai para ", "Goes to ") + Path.GetFileNameWithoutExtension(caminho);
            }
            p.Texto.Desenhar(dc, baixo, new Vector2(0, CAMPO_Y + CAMPO_A + 10), 12, cor, FontWeight.Normal, largura: t.X);
        });
        var campo = p.Desenho(cena, new Vector2(0, CAMPO_Y), new Vector2(L, CAMPO_A), (dc, t) =>
        {
            Icone.Retangulo(dc, 0, 0, t.X, t.Y, 12, new Color4(1, 1, 1, 0.10f));
            bool nosso = _aberta && S.Entrada.Aberta;
            string vazio = nosso ? Lang.T("Escreva a nota. Shift+Enter quebra a linha.", "Write the note. Shift+Enter starts a new line.") : Lang.T("Clique aqui para escrever.", "Click here to write.");
            Campo(dc, p, _texto, nosso ? _cursor : _texto.Length, new Vector2(12, 9), t.X - 24, t.Y - 16, vazio, nosso);
        });
        _supCampo = campo;
        cena.AoSoltar += () => { if (_supCampo == campo) _supCampo = null; };
        // o campo fechou com o cartão ainda na tela (clicou no menu e voltou, por exemplo): clicar nele reabre
        cena.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(0, CAMPO_Y, L, CAMPO_A), Acao = () => { if (!_aberta) AbrirCampo(); } });
        return cena;
    }

    private Cena CenaSemArquivo(Palco p)
    {
        var cena = p.NovaCena(new Vector2(L, A_SEM_ARQUIVO));
        p.Desenho(cena, Vector2.Zero, cena.Tamanho, (dc, t) =>
        {
            Simbolo(dc, p, 0, 0, 16);
            p.Texto.Desenhar(dc, Lang.T("Nota rápida", "Quick note"), new Vector2(22, 0), 12, Cor.Secundario);
            Caminho(out var problema);
            string texto = problema is null or "vazio"
                ? Lang.T("Falta escolher o arquivo. Nas configurações da ilha, módulo Nota rápida, ponha em \"Arquivo da nota\" o caminho completo de um .md. Cada nota entra no fim dele como um item \"- data hora texto\".",
                    "No file chosen yet. In the island settings, Quick note module, put the full path of a .md file in \"Note file\". Each note goes in at its end as a \"- date time text\" item.")
                : problema;
            _formatoAviso ??= Formato(p, 12.5f);
            using var l = p.G.Escrita.CreateTextLayout(texto, _formatoAviso, t.X, 1000);
            using var b = dc.CreateSolidColorBrush(Cor.Branco);
            dc.DrawTextLayout(new Vector2(0, 24), l, b, DrawTextOptions.EnableColorFont);
        });
        return cena;
    }

    private static IDWriteTextFormat Formato(Palco p, float tam)
    {
        var f = p.G.Escrita.CreateTextFormat("Segoe UI Variable Text", null!, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, tam, Lang.English ? "en-us" : "pt-br");
        f.WordWrapping = WordWrapping.Wrap;
        return f;
    }

    /// <summary>
    /// Campo de várias linhas: texto quebrado na largura, cursor, e rolagem vertical para a linha do cursor ficar à
    /// vista (o DesenharCampo do núcleo é de uma linha só).
    /// </summary>
    private void Campo(ID2D1DeviceContext dc, Palco p, string texto, int cursor, Vector2 pos, float largura, float altura, string vazio, bool comCursor)
    {
        _formatoCampo ??= Formato(p, TAM);
        dc.PushAxisAlignedClip(new System.Drawing.RectangleF(pos.X - 2, pos.Y, largura + 4, altura), AntialiasMode.Aliased);
        try
        {
            if (texto.Length == 0)
            {
                using var lv = p.G.Escrita.CreateTextLayout(vazio, _formatoCampo, MathF.Max(1, largura), 1000);
                using var bv = dc.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0.38f));
                dc.DrawTextLayout(pos, lv, bv, DrawTextOptions.EnableColorFont);
                if (comCursor) Icone.Retangulo(dc, pos.X, pos.Y + TAM * 0.15f, 1.6f, TAM * 1.15f, 0.8f, Cor.Branco);
                return;
            }
            using var l = p.G.Escrita.CreateTextLayout(texto, _formatoCampo, MathF.Max(1, largura), 100_000);
            cursor = Math.Clamp(cursor, 0, texto.Length);
            l.HitTestTextPosition((uint)cursor, false, out float cx, out float cy, out var m);
            float linha = MathF.Max(m.Height, TAM * 1.3f);
            float rolagem = MathF.Max(0, cy + linha - altura);
            using var b = dc.CreateSolidColorBrush(Cor.Branco);
            dc.DrawTextLayout(new Vector2(pos.X, pos.Y - rolagem), l, b, DrawTextOptions.EnableColorFont);
            if (comCursor) Icone.Retangulo(dc, pos.X + cx, pos.Y + cy - rolagem + TAM * 0.12f, 1.6f, TAM * 1.15f, 0.8f, Cor.Branco);
        }
        finally { dc.PopAxisAlignedClip(); }
    }

    /// <summary>O ícone da nota: um bloquinho amarelo com três linhas de texto (a última mais curta).</summary>
    internal static void Simbolo(ID2D1DeviceContext dc, Palco p, float x, float y, float l)
    {
        Icone.Retangulo(dc, x + l * 0.06f, y, l * 0.88f, l, l * 0.2f, Amarelo);
        var F = p.G.Fabrica;
        var tinta = new Color4(0f, 0f, 0f, 0.72f);
        float e = MathF.Max(1.3f, l * 0.085f), a = x + l * 0.27f, b = x + l * 0.73f;
        Icone.Linha(dc, new Vector2(a, y + l * 0.32f), new Vector2(b, y + l * 0.32f), e, tinta, F);
        Icone.Linha(dc, new Vector2(a, y + l * 0.5f), new Vector2(b, y + l * 0.5f), e, tinta, F);
        Icone.Linha(dc, new Vector2(a, y + l * 0.68f), new Vector2(x + l * 0.55f, y + l * 0.68f), e, tinta, F);
    }

    public override void Dispose() => LiberarFormatos();
}
