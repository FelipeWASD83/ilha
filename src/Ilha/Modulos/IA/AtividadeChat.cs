using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.IA;

/// <summary>
/// O chat da ilha: abre expandido pelo atalho, com o campo de texto da Entrada (multilinha, Shift+Enter quebra
/// linha, Ctrl+V cola print), resposta em streaming na cena e as últimas trocas em memória. Sem chave nenhuma, a
/// tela de chaves aparece no lugar. Recolher a ilha, fechar o campo (Esc) ou um jogo competitivo na frente param
/// a resposta na hora (o stream é cancelado: nada continua sendo pago sem ninguém ver).
/// </summary>
internal sealed class AtividadeChat : Atividade
{
    public override string Tipo => "ia";
    public override string Nome => "Chat com IA";
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    private const float L = 380, A = 300, A_CHAVES = 236;
    private const float MSG_Y = 34, MSG_A = 204, CAMPO_Y = 244, CAMPO_A = 56;

    private sealed class TrocaChat
    {
        public required string Pergunta;
        public ImagemColada? Imagem;
        public readonly StringBuilder Resposta = new();
        public string Modelo = "";
        public string? Erro;
        public string? Nota;
        public double? Custo;
        public bool Pronta;
    }

    private readonly Servicos S;
    private readonly ClienteIA _ia;
    private readonly Func<ConfigIA> _cfg;
    private readonly Action<ConfigIA> _salvar;
    private readonly List<TrocaChat> _trocas = new();
    private readonly Agendador _redesenho;
    private Paragrafo? _par;

    private bool _telaChaves;
    private string _rascunho = "";
    private ImagemColada? _imagem;
    private bool _preparandoImagem;
    private CancellationTokenSource? _cts;
    private int _geracao;
    private bool _respondendo, _escrevendo;
    private string _modeloDaVez = "";
    private float _rolagem; // 0 = colado no fim
    private float _alturaConteudo;

    private bool _entradaNossa, _trocandoEntrada;

    // tela de chaves
    private string? _digitando;
    private string _aviso = "";
    private bool _avisoRuim;
    private (string Lado, long Ate)? _confirmarApagar;
    private bool _testando;

    // logos animadas (como o GptDeMentira e o ClaudeDeMentira da demonstração)
    private readonly List<LogoAnimada> _logos = new();
    private string[] _anim = Array.Empty<string>();
    private string _familia = "";
    private int _vezes;
    private string? _bolinha;
    private static readonly string[] Bolinhas = { "cometa", "orbita", "divide", "onda", "anel" };

    private Superficie? _supTopo, _supMsgs, _supCampo;

    public AtividadeChat(Servicos s, ClienteIA ia, Func<ConfigIA> cfg, Action<ConfigIA> salvar)
    {
        S = s;
        _ia = ia;
        _cfg = cfg;
        _salvar = salvar;
        _redesenho = new Agendador(() => _supMsgs?.Redesenhar());
    }

    private string Modelo => string.IsNullOrWhiteSpace(_cfg().Modelo) ? "gpt-6-luna" : _cfg().Modelo.Trim();
    private bool TemAlgumaChave => _ia.TemChave("openai") || _ia.TemChave("claude");
    private static string Familia(string modelo) => Provedor.EhClaude(modelo) ? "claude" : "chatgpt";

    // ------------------------------------------------------------------ abrir, fechar, parar

    public void Atalho()
    {
        if (S.ContextoAtual().Competitivo) return;
        if (S.Ilha.Expandida == this) { S.Ilha.Recolher(); return; }
        _telaChaves = !TemAlgumaChave;
        _familia = Familia(_respondendo ? _modeloDaVez : Modelo);
        if (!_respondendo) Animar(_familia + "-entrada");
        _rolagem = 0;
        MudouTamanho();
        S.Ilha.Expandir(this);
        if (_telaChaves) AbrirCampoChave(_ia.TemChave("openai") ? "claude" : "openai");
        else AbrirCampoChat();
    }

    public void ConfigMudou()
    {
        if (!_respondendo && S.Ilha.Expandida == this) { _familia = Familia(Modelo); MudouTamanho(); }
    }

    /// <summary>Para a resposta em andamento (e fecha o campo, se for nosso).</summary>
    public void Interromper(bool fecharCampo)
    {
        _cts?.Cancel();
        if (fecharCampo && _entradaNossa && S.Entrada.Aberta)
        {
            _trocandoEntrada = true;
            try { S.Entrada.Fechar(); }
            finally { _trocandoEntrada = false; }
            _entradaNossa = false;
            _digitando = null;
        }
    }

    private void FecharCampo()
    {
        if (!_entradaNossa || !S.Entrada.Aberta) { _entradaNossa = false; return; }
        _trocandoEntrada = true;
        try { S.Entrada.Fechar(); }
        finally { _trocandoEntrada = false; }
        _entradaNossa = false;
    }

    private void AbrirCampoChat()
    {
        FecharCampo();
        _digitando = null;
        _entradaNossa = true;
        S.Entrada.Abrir(_rascunho,
            mudou: t => { _rascunho = t; _supCampo?.Redesenhar(); },
            enter: Enviar,
            fechou: AoFecharCampo,
            imagem: Colar,
            multilinha: true);
        _supCampo?.Redesenhar();
    }

    private void AbrirCampoChave(string lado)
    {
        FecharCampo();
        _digitando = lado;
        _entradaNossa = true;
        S.Entrada.Abrir("",
            mudou: _ => _supCampo?.Redesenhar(),
            enter: t => GravarChave(lado, t),
            fechou: AoFecharCampo,
            senha: true);
        Redesenhar();
    }

    private void AoFecharCampo()
    {
        if (_trocandoEntrada) return;
        _entradaNossa = false;
        if (_telaChaves && _digitando != null)
        {
            // Esc no campo da chave só cancela a digitação
            _digitando = null;
            Redesenhar();
            return;
        }
        _cts?.Cancel();
        if (S.Ilha.Expandida == this) S.Ilha.Recolher();
    }

    // ------------------------------------------------------------------ chat

    private void Colar(System.Drawing.Image img)
    {
        if (_preparandoImagem) return;
        _preparandoImagem = true;
        _supCampo?.Redesenhar();
        int lado = Math.Clamp(_cfg().LadoMaiorDaImagem, 256, 8000);
        var copia = new System.Drawing.Bitmap(img);
        Task.Run(() => { using (copia) return ImagemColada.Preparar(copia, lado); }).ContinueWith(t =>
        {
            _preparandoImagem = false;
            if (t.IsFaulted) Log.Escrever("ia: imagem colada não preparou: " + t.Exception?.GetBaseException().Message);
            else _imagem = t.Result;
            _supCampo?.Redesenhar();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Enviar(string texto)
    {
        texto = texto.Trim();
        if (_respondendo || _preparandoImagem || (texto.Length == 0 && _imagem == null)) return;
        var troca = new TrocaChat { Pergunta = texto, Imagem = _imagem };
        _imagem = null;
        _rascunho = "";
        S.Entrada.Definir("");
        int max = Math.Clamp(_cfg().TrocasNoHistorico, 1, 30);
        while (_trocas.Count >= max) _trocas.RemoveAt(0);
        _trocas.Add(troca);
        _rolagem = 0;
        _ = Responder(troca);
    }

    private JsonArray Itens(TrocaChat atual)
    {
        var itens = new JsonArray();
        foreach (var t in _trocas)
        {
            if (t == atual) break;
            if (t.Erro != null || !t.Pronta || t.Resposta.Length == 0) continue;
            itens.Add(ItemDoUsuario(t));
            itens.Add(new JsonObject { ["role"] = "assistant", ["content"] = t.Resposta.ToString() });
        }
        itens.Add(ItemDoUsuario(atual));
        var agora = DateTime.Now;
        itens.Add(new JsonObject
        {
            ["role"] = "developer",
            ["content"] = "Agora é " + agora.ToString("dddd, d 'de' MMMM 'de' yyyy, HH:mm", CultureInfo.GetCultureInfo("pt-BR")) + ".",
        });
        return itens;
    }

    private static JsonObject ItemDoUsuario(TrocaChat t)
    {
        if (t.Imagem == null) return new JsonObject { ["role"] = "user", ["content"] = t.Pergunta };
        var partes = new JsonArray();
        if (t.Pergunta.Length > 0) partes.Add(new JsonObject { ["type"] = "input_text", ["text"] = t.Pergunta });
        partes.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = t.Imagem.DataUrl });
        return new JsonObject { ["role"] = "user", ["content"] = partes };
    }

    public const string Instrucoes =
        "Você é o chat rápido da Ilha, uma barra no topo da tela do Windows. Responda em português do Brasil, direto ao " +
        "ponto e curto (a janela tem uns 380 pixels de largura), a não ser que peçam detalhe. Escreva texto simples, sem " +
        "Markdown: nada de negrito com asteriscos, títulos com # nem tabelas; para listas, hífen no começo da linha. " +
        "Código vai em linhas próprias, sem cercas de crase.";

    private async Task Responder(TrocaChat troca)
    {
        var cfg = _cfg();
        string modelo = Modelo;
        int g = ++_geracao;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _respondendo = true;
        _escrevendo = false;
        _modeloDaVez = modelo;
        troca.Modelo = modelo;
        TrocarFamilia(Familia(modelo));
        Pensar();
        Redesenhar();
        var pedido = new PedidoIA
        {
            Modelo = modelo, Esforco = Modelos.Esforco(modelo, cfg.Esforco), Reserva = cfg.Reserva, Instrucoes = Instrucoes,
            Itens = Itens(troca), MaxSaida = Math.Clamp(cfg.MaxSaida, 256, 64000),
        };
        try
        {
            var r = await _ia.Responder(pedido,
                aoTexto: t => S.NaInterface(() => Chegou(g, troca, t)),
                aoTrocar: m => S.NaInterface(() => { if (g != _geracao) return; troca.Modelo = m; _modeloDaVez = m; TrocarFamilia(Familia(m)); Pensar(); }),
                ct);
            troca.Resposta.Clear();
            troca.Resposta.Append(r.Texto);
            troca.Modelo = r.Modelo;
            troca.Custo = r.Custo;
            troca.Pronta = true;
            var notas = new List<string>();
            if (r.Trocou != null)
                notas.Add(r.Trocou.Motivo == "sem chave"
                    ? $"Sem chave para {Modelos.Rotulo(r.Trocou.De)}: respondeu {Modelos.Rotulo(r.Modelo)}."
                    : $"{Modelos.Rotulo(r.Trocou.De)} {r.Trocou.Motivo}: respondeu {Modelos.Rotulo(r.Modelo)}.");
            if (r.Incompleta != null) notas.Add(r.Incompleta == "max_output_tokens" ? "A resposta bateu no teto de tamanho e foi cortada." : $"Resposta incompleta ({r.Incompleta}).");
            if (r.Texto.Length == 0 && r.Incompleta == null) notas.Add("Veio vazia.");
            troca.Nota = string.Join(" ", notas);
        }
        catch (OperationCanceledException)
        {
            troca.Pronta = troca.Resposta.Length > 0;
            troca.Nota = "Interrompida.";
            if (troca.Resposta.Length == 0) troca.Erro = "Interrompida antes de responder.";
        }
        catch (ErroIA e) { troca.Erro = e.Message; }
        catch (Exception e)
        {
            troca.Erro = "Deu um erro inesperado (" + e.GetType().Name + ").";
            Log.Escrever("ia: " + Segredo.Limpar(e.ToString()));
        }
        finally
        {
            if (g == _geracao)
            {
                _respondendo = false;
                _escrevendo = false;
                Terminar();
                Redesenhar();
            }
        }
    }

    private void Chegou(int g, TrocaChat troca, string pedaco)
    {
        if (g != _geracao || !_respondendo) return;
        // a ilha passou a mostrar outra coisa sem mudar de estado (outra ferramenta): ninguém está vendo
        if (S.Ilha.Expandida != this) { _cts?.Cancel(); return; }
        if (!_escrevendo) { _escrevendo = true; Escrever(); _supTopo?.Redesenhar(); }
        troca.Resposta.Append(pedaco);
        _redesenho.Pedir();
    }

    private void NovaConversa()
    {
        if (_respondendo) { _cts?.Cancel(); return; }
        _trocas.Clear();
        _imagem = null;
        _rolagem = 0;
        Redesenhar();
    }

    private void TrocarModelo()
    {
        if (_respondendo) return;
        var cfg = _cfg();
        var lista = cfg.Modelos.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct().ToList();
        if (!lista.Contains(Modelo)) lista.Insert(0, Modelo);
        if (lista.Count < 2) return;
        var copia = new ConfigIA
        {
            Modelo = lista[(lista.IndexOf(Modelo) + 1) % lista.Count], Modelos = cfg.Modelos, Esforco = cfg.Esforco, Reserva = cfg.Reserva,
            TrocasNoHistorico = cfg.TrocasNoHistorico, MaxSaida = cfg.MaxSaida, LadoMaiorDaImagem = cfg.LadoMaiorDaImagem,
            TradutorModelo = cfg.TradutorModelo, TradutorReserva = cfg.TradutorReserva, LimiteGoogle = cfg.LimiteGoogle,
            LimiteTradutorIA = cfg.LimiteTradutorIA,
        };
        _salvar(copia);
        string fam = Familia(copia.Modelo);
        if (fam != _familia) { _familia = fam; _anim = new[] { fam + "-entrada" }; MudouTamanho(); }
        else Redesenhar();
    }

    public override bool Rolar(int delta)
    {
        if (_telaChaves || S.Ilha.Expandida != this) return false;
        float max = MathF.Max(0, _alturaConteudo - MSG_A);
        _rolagem = Math.Clamp(_rolagem + delta / 120f * 36f, 0, max);
        _supMsgs?.Redesenhar();
        return true;
    }

    // ------------------------------------------------------------------ chaves

    private static string NomeDoLado(string lado) => lado == "claude" ? "Anthropic" : "OpenAI";

    private void GravarChave(string lado, string texto)
    {
        texto = texto.Trim();
        S.Entrada.Definir("");
        if (texto.Length < 20 || texto.Any(char.IsWhiteSpace))
        {
            Avisar("Isso não parece uma chave de API. Cole a chave inteira.", ruim: true);
            return;
        }
        bool ok = Credenciais.Gravar(ClienteIA.NomeDaCredencial(lado), texto);
        texto = "";
        _digitando = null;
        FecharCampo();
        if (!ok) { Avisar($"Não consegui gravar a chave da {NomeDoLado(lado)} no Gerenciador de Credenciais.", ruim: true); return; }
        Avisar($"Chave da {NomeDoLado(lado)} salva no Gerenciador de Credenciais.", ruim: false);
        _ = Testar(lado);
    }

    private async Task Testar(string lado)
    {
        if (_testando) return;
        _testando = true;
        Avisar($"Testando a chave da {NomeDoLado(lado)}…", ruim: false);
        try
        {
            var (ok, msg) = await _ia.TestarChave(lado, CancellationToken.None);
            Avisar($"{NomeDoLado(lado)}: {msg}", ruim: !ok);
        }
        catch (Exception e) { Avisar($"{NomeDoLado(lado)}: o teste falhou ({e.GetType().Name}).", ruim: true); }
        finally { _testando = false; }
    }

    private void Apagar(string lado)
    {
        long agora = Environment.TickCount64;
        if (_confirmarApagar is { } c && c.Lado == lado && agora < c.Ate)
        {
            _confirmarApagar = null;
            Credenciais.Apagar(ClienteIA.NomeDaCredencial(lado));
            Avisar($"Chave da {NomeDoLado(lado)} apagada.", ruim: false);
            return;
        }
        _confirmarApagar = (lado, agora + 4000);
        Avisar($"Clique de novo em apagar para tirar a chave da {NomeDoLado(lado)}.", ruim: true);
    }

    private void Avisar(string texto, bool ruim)
    {
        _aviso = texto;
        _avisoRuim = ruim;
        Redesenhar();
    }

    private void IrParaChaves()
    {
        if (_respondendo) return;
        _telaChaves = true;
        _aviso = "";
        FecharCampo();
        MudouTamanho();
    }

    private void VoltarAoChat()
    {
        if (!TemAlgumaChave) { Avisar("Salve pelo menos uma chave antes.", ruim: true); return; }
        _telaChaves = false;
        _digitando = null;
        _familia = Familia(Modelo);
        _anim = new[] { _familia + "-entrada" };
        MudouTamanho();
        AbrirCampoChat();
    }

    // ------------------------------------------------------------------ logos

    private LogoAnimada Logo(Palco p, Cena c, Vector2 pos, float lado)
    {
        var l = p.Logo(c, pos, lado);
        _logos.Add(l);
        c.AoSoltar += () => _logos.Remove(l);
        if (_anim.Length > 0) l.Tocar(_anim[^1]);
        return l;
    }

    private void Animar(params string[] seq)
    {
        _anim = seq;
        foreach (var l in _logos.ToArray()) l.Tocar(seq);
    }

    private void TrocarFamilia(string fam)
    {
        if (fam == _familia) return;
        _familia = fam;
        MudouTamanho(); // a logo do outro lado tem outra posição e outro tamanho
    }

    private void Pensar()
    {
        _bolinha = $"{_familia}-bola-{Bolinhas[_vezes++ % Bolinhas.Length]}";
        Animar(_familia + "-bola", _familia + "-bola-cresce", _bolinha + "-entrada", _bolinha);
    }

    private void Escrever()
    {
        if (_bolinha != null) Animar(_bolinha + "-saida", _familia + "-bola-encolhe", _familia + "-surge", _familia + "-loop");
        else Animar(_familia + "-loop");
        _bolinha = null;
    }

    private void Terminar()
    {
        if (_bolinha != null) Animar(_bolinha + "-saida", _familia + "-bola-encolhe", _familia + "-surge", _familia + "-saida");
        else Animar(_familia + "-saida");
        _bolinha = null;
    }

    // ------------------------------------------------------------------ cenas

    private static void Chip(ID2D1DeviceContext dc, Palco p, string texto, float x, float y, float l, bool ativo = true, Color4? cor = null)
    {
        Icone.Retangulo(dc, x, y, l, 22, 11, ativo ? Cor.Fraco : new Color4(1, 1, 1, 0.06f));
        var tam = p.Texto.Medir(texto, 12);
        p.Texto.Desenhar(dc, texto, new Vector2(x + (l - tam.X) / 2, y + (22 - tam.Y) / 2), 12, cor ?? (ativo ? Cor.Branco : Cor.Secundario));
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        _par ??= new Paragrafo(p.G.Escrita);
        if (f == Forma.Bolha || f == Forma.Compacta)
        {
            // o chat não fica "em andamento" sozinho, mas a forma pequena existe se o núcleo pedir
            var c = p.NovaCena(f == Forma.Bolha ? new Vector2(22, 22) : new Vector2(150, 24));
            Logo(p, c, Vector2.Zero, 22);
            if (f == Forma.Compacta)
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    string s = _respondendo ? "Respondendo" : "Chat";
                    var tam = p.Texto.Medir(s, 13);
                    p.Texto.Desenhar(dc, s, new Vector2(t.X - tam.X, (t.Y - tam.Y) / 2), 13, Cor.Branco);
                });
            return c;
        }
        return _telaChaves ? CenaChaves(p) : CenaChat(p);
    }

    private Cena CenaChat(Palco p)
    {
        var c = p.NovaCena(new Vector2(L, A));
        if (_familia == "") _familia = Familia(Modelo);
        if (_anim.Length == 0) _anim = new[] { _familia + "-entrada" };
        if (_familia == "claude") Logo(p, c, new Vector2(-4, -5), 30);
        else Logo(p, c, new Vector2(0, 0), 24);

        var topo = p.Desenho(c, Vector2.Zero, new Vector2(L, 28), (dc, t) =>
        {
            string m = Modelos.Rotulo(_respondendo ? _modeloDaVez : Modelo);
            p.Texto.Desenhar(dc, m, new Vector2(32, 2), 13, Cor.Secundario, largura: 170);
            if (!_respondendo)
            {
                float x = 32 + MathF.Min(170, p.Texto.Medir(m, 13).X) + 4;
                Icone.Triangulo(dc, p.G.Fabrica, new Vector2(x, 9), new Vector2(x + 7, 9), new Vector2(x + 3.5f, 13), Cor.Secundario);
            }
            Chip(dc, p, _respondendo ? "Parar" : "Nova", L - 118, 0, 56, cor: _respondendo ? Cor.Laranja : null);
            Chip(dc, p, "Chaves", L - 58, 0, 58, ativo: !_respondendo);
        });
        c.Botoes.Add(new Botao { Area = new(28, 0, 200, 24), Acao = TrocarModelo });
        c.Botoes.Add(new Botao { Area = new(L - 118, 0, 56, 22), Acao = NovaConversa });
        c.Botoes.Add(new Botao { Area = new(L - 58, 0, 58, 22), Acao = IrParaChaves });

        var msgs = p.Desenho(c, new Vector2(0, MSG_Y), new Vector2(L, MSG_A), (dc, t) => DesenharMensagens(dc, p, t));
        var campo = p.Desenho(c, new Vector2(0, CAMPO_Y), new Vector2(L, CAMPO_A), (dc, t) => DesenharCampo(dc, p, t));
        // o × da miniatura vem antes na lista (o núcleo usa o primeiro botão que contém o clique)
        c.Botoes.Add(new Botao
        {
            Area = new(6 + 46 - 16, CAMPO_Y + 3, 20, 20),
            Acao = () => { if (_imagem != null) { _imagem = null; _supCampo?.Redesenhar(); } else if (!S.Entrada.Aberta) AbrirCampoChat(); },
        });
        // o resto do campo devolve o teclado se ele tiver saído
        c.Botoes.Add(new Botao { Area = new(0, CAMPO_Y, L, CAMPO_A), Acao = () => { if (!S.Entrada.Aberta) AbrirCampoChat(); } });
        Prender(c, topo, msgs, campo);
        return c;
    }

    private void Prender(Cena c, Superficie topo, Superficie? msgs, Superficie campo)
    {
        _supTopo = topo;
        _supMsgs = msgs;
        _supCampo = campo;
        c.AoSoltar += () =>
        {
            if (_supTopo == topo) _supTopo = null;
            if (_supMsgs == msgs) _supMsgs = null;
            if (_supCampo == campo) _supCampo = null;
        };
    }

    private void DesenharMensagens(ID2D1DeviceContext dc, Palco p, Vector2 t)
    {
        var par = _par!;
        if (_trocas.Count == 0)
        {
            using var l = par.Layout(_ia.TemChave(Provedor.ProvedorDe(Modelo)) || _ia.TemChave(Provedor.ProvedorDe(Provedor.ReservaDe(Modelo, _cfg().Reserva) ?? Modelo))
                ? "Pergunte qualquer coisa. Ctrl+V cola um print. Clique no nome do modelo para trocar."
                : "Sem chave para este modelo nem para a reserva. Abra Chaves.", 13, t.X);
            Paragrafo.Desenhar(dc, l, new Vector2(0, 6), Cor.Secundario);
            _alturaConteudo = 0;
            return;
        }
        // monta tudo, mede, e desenha a partir do fim (rolagem 0 = a última mensagem embaixo)
        var blocos = new List<(IDWriteTextLayout? Texto, float X, float Y, float Alt, Color4 Cor, bool Balao, ImagemColada? Img, float BalaoL)>();
        float y = 0;
        const float BALAO_MAX = 290, PAD = 8;
        foreach (var tr in _trocas)
        {
            // pergunta: balão à direita
            float imgA = tr.Imagem != null ? 54 : 0;
            IDWriteTextLayout? lp = tr.Pergunta.Length > 0 ? par.Layout(tr.Pergunta, 13, BALAO_MAX - PAD * 2) : null;
            float tl = lp != null ? Paragrafo.LarguraUsada(lp) : 0, ta = lp != null ? MathF.Ceiling(lp.Metrics.Height) : 0;
            float bl = MathF.Max(tl, tr.Imagem != null ? 72 : 0) + PAD * 2;
            float ba = ta + imgA + PAD * 2 - (lp == null ? 6 : 0);
            blocos.Add((lp, t.X - bl, y, ba, Cor.Branco, true, tr.Imagem, bl));
            y += ba + 8;
            // resposta
            string resp = Limpeza.ParaTela(tr.Resposta.ToString());
            bool vazia = resp.Length == 0;
            string mostra = vazia ? (tr.Erro ?? (_respondendo && tr == _trocas[^1] ? (_escrevendo ? "" : "Pensando…") : "")) : resp;
            if (mostra.Length > 0)
            {
                var lr = par.Layout(mostra, 13.5f, t.X);
                float a = MathF.Ceiling(lr.Metrics.Height);
                blocos.Add((lr, 0, y, a, vazia && tr.Erro != null ? Cor.Vermelho : vazia ? Cor.Secundario : Cor.Branco, false, null, 0));
                y += a + 2;
            }
            if (!vazia && tr.Erro != null)
            {
                var le = par.Layout(tr.Erro, 12, t.X);
                float a = MathF.Ceiling(le.Metrics.Height);
                blocos.Add((le, 0, y, a, Cor.Vermelho, false, null, 0));
                y += a + 2;
            }
            var rodape = new List<string>();
            if (tr.Pronta || tr.Erro != null)
            {
                if (tr.Modelo.Length > 0 && (tr.Pronta || tr.Erro == null)) rodape.Add(Modelos.Rotulo(tr.Modelo));
                if (tr.Custo != null) rodape.Add(Modelos.Dinheiro(tr.Custo));
                if (!string.IsNullOrEmpty(tr.Nota)) rodape.Add(tr.Nota);
            }
            if (rodape.Count > 0)
            {
                var ln = par.Layout(string.Join(" · ", rodape), 11, t.X);
                float a = MathF.Ceiling(ln.Metrics.Height);
                blocos.Add((ln, 0, y, a, Cor.Secundario, false, null, 0));
                y += a;
            }
            y += 12;
        }
        _alturaConteudo = y;
        float max = MathF.Max(0, y - t.Y);
        _rolagem = Math.Clamp(_rolagem, 0, max);
        float desloca = max - _rolagem;
        dc.PushAxisAlignedClip(new System.Drawing.RectangleF(0, 0, t.X, t.Y), AntialiasMode.Aliased);
        try
        {
            foreach (var b in blocos)
            {
                float by = b.Y - desloca;
                if (by + b.Alt < 0 || by > t.Y) continue;
                if (b.Balao)
                {
                    Icone.Retangulo(dc, b.X, by, b.BalaoL, b.Alt, 12, Cor.Fraco);
                    float yy = by + PAD;
                    if (b.Img != null) { b.Img.Desenhar(dc, b.X + PAD, yy, 72, 48); yy += 54; }
                    if (b.Texto != null) Paragrafo.Desenhar(dc, b.Texto, new Vector2(b.X + PAD, yy), b.Cor);
                }
                else if (b.Texto != null) Paragrafo.Desenhar(dc, b.Texto, new Vector2(b.X, by), b.Cor);
            }
        }
        finally
        {
            dc.PopAxisAlignedClip();
            foreach (var b in blocos) b.Texto?.Dispose();
        }
        // indicador de que tem mais em cima
        if (desloca > 1) Icone.Retangulo(dc, t.X - 3, 2, 3, MathF.Max(12, t.Y * t.Y / y), 1.5f, Cor.Fraco);
    }

    private void DesenharCampo(ID2D1DeviceContext dc, Palco p, Vector2 t)
    {
        Icone.Retangulo(dc, 0, 2, t.X, t.Y - 2, 14, new Color4(1, 1, 1, 0.10f));
        float x = 12;
        if (_imagem != null)
        {
            _imagem.Desenhar(dc, 6, 6, 46, t.Y - 10);
            Icone.Circulo(dc, new Vector2(6 + 46 - 6, 13), 7, new Color4(0, 0, 0, 0.7f));
            Icone.Linha(dc, new Vector2(6 + 46 - 9, 10), new Vector2(6 + 46 - 3, 16), 1.6f, Cor.Branco, p.G.Fabrica);
            Icone.Linha(dc, new Vector2(6 + 46 - 3, 10), new Vector2(6 + 46 - 9, 16), 1.6f, Cor.Branco, p.G.Fabrica);
            x = 60;
        }
        bool nosso = _entradaNossa && S.Entrada.Aberta && _digitando == null;
        string texto = nosso ? S.Entrada.Texto : _rascunho;
        string vazio = _preparandoImagem ? "Preparando a imagem…"
            : _respondendo ? "Respondendo… (Parar no alto)"
            : _imagem != null ? $"Imagem {_imagem.Largura}×{_imagem.Altura}. Escreva algo ou só Enter."
            : "Pergunte algo. Shift+Enter quebra a linha.";
        _par!.Campo(dc, texto, nosso ? S.Entrada.Cursor : texto.Length, new Vector2(x, 10), t.X - x - 12, t.Y - 16, 13, Cor.Branco, vazio, nosso);
    }

    private Cena CenaChaves(Palco p)
    {
        var c = p.NovaCena(new Vector2(L, A_CHAVES));
        var topo = p.Desenho(c, Vector2.Zero, new Vector2(L, A_CHAVES), (dc, t) =>
        {
            p.Texto.Desenhar(dc, "Chaves de API", new Vector2(0, 1), 15, Cor.Branco);
            if (TemAlgumaChave) Chip(dc, p, "Voltar ao chat", L - 108, 0, 108);
            float y = 34;
            foreach (var lado in new[] { "openai", "claude" })
            {
                bool tem = _ia.TemChave(lado);
                p.Texto.Desenhar(dc, NomeDoLado(lado), new Vector2(0, y), 14, Cor.Branco);
                p.Texto.Desenhar(dc, tem ? "salva" : "não salva", new Vector2(0, y + 20), 12, tem ? Cor.Verde : Cor.Secundario, FontWeight.Normal);
                Chip(dc, p, _digitando == lado ? "Digitando" : "Colar", L - 196, y + 6, 68, ativo: _digitando != lado);
                Chip(dc, p, "Testar", L - 122, y + 6, 58, ativo: tem && !_testando);
                Chip(dc, p, "Apagar", L - 58, y + 6, 58, ativo: tem, cor: _confirmarApagar is { } ca && ca.Lado == lado && Environment.TickCount64 < ca.Ate ? Cor.Vermelho : null);
                y += 50;
            }
        });
        var campo = p.Desenho(c, new Vector2(0, 136), new Vector2(L, A_CHAVES - 136), (dc, t) =>
        {
            if (_digitando != null)
            {
                Icone.Retangulo(dc, 0, 0, t.X, 32, 12, new Color4(1, 1, 1, 0.10f));
                bool aberto = _entradaNossa && S.Entrada.Aberta;
                p.Texto.DesenharCampo(dc, aberto ? S.Entrada.Texto : "", aberto ? S.Entrada.Cursor : 0, true, new Vector2(12, 8), t.X - 24, 13, Cor.Branco,
                    $"Cole a chave da {NomeDoLado(_digitando)} e aperte Enter (Esc cancela)", aberto);
            }
            else
            {
                using var l = _par!.Layout(TemAlgumaChave ? "Clique em Colar para trocar uma chave." : "Cole uma chave da OpenAI ou da Anthropic para começar. Basta uma: a outra serve de reserva quando o crédito acabar.", 12, t.X);
                Paragrafo.Desenhar(dc, l, new Vector2(0, 2), Cor.Secundario);
            }
            if (_aviso.Length > 0)
            {
                using var la = _par!.Layout(_aviso, 12, t.X);
                Paragrafo.Desenhar(dc, la, new Vector2(0, 40), _avisoRuim ? Cor.Laranja : Cor.Verde);
            }
            using var ln = _par!.Layout("Ficam no Gerenciador de Credenciais do Windows (Ilha/openai e Ilha/anthropic). A ilha nunca mostra a chave nem grava em arquivo.", 11, t.X);
            Paragrafo.Desenhar(dc, ln, new Vector2(0, t.Y - MathF.Ceiling(ln.Metrics.Height)), Cor.Secundario);
        });
        if (TemAlgumaChave) c.Botoes.Add(new Botao { Area = new(L - 108, 0, 108, 22), Acao = VoltarAoChat });
        float yb = 34;
        foreach (var lado in new[] { "openai", "claude" })
        {
            string ld = lado;
            c.Botoes.Add(new Botao { Area = new(L - 196, yb + 6, 68, 22), Acao = () => AbrirCampoChave(ld) });
            c.Botoes.Add(new Botao { Area = new(L - 122, yb + 6, 58, 22), Acao = () => { if (_ia.TemChave(ld)) _ = Testar(ld); } });
            c.Botoes.Add(new Botao { Area = new(L - 58, yb + 6, 58, 22), Acao = () => { if (_ia.TemChave(ld)) Apagar(ld); } });
            yb += 50;
        }
        Prender(c, topo, null, campo);
        return c;
    }

    public override void Dispose()
    {
        _cts?.Cancel();
        _redesenho.Dispose();
    }
}
