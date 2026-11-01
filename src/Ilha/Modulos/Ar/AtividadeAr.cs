using System;
using System.Collections.Generic;
using RectangleF = System.Drawing.RectangleF;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Ar;

/// <summary>
/// O ar-condicionado na ilha. Ferramenta: nunca fica na pílula; abre pelo menu da ilha ou pelo atalho, e só aparece
/// sozinho num alerta curto quando o estado do ar muda (ligou, desligou, modo ou alvo), seja pelo controle remoto,
/// pelo site, pela nova aba ou pela própria ilha. A sala mudando não é mudança. O alerta do que a ilha mandou sai
/// quando ela recolhe (com o cartão aberto ele cobriria os botões), um só para a leva de cliques.
///
/// O que importa é não gastar chamada da LG: cada GET /api/estado diz ao servidor "alguém está olhando" e faz ele
/// ler o ar de 3 em 3 minutos. Por isso o GET /api/estado só sai com o cartão ABERTO, de 4 em 4 s como o site e a nova
/// aba, e para quando o cartão fecha; nunca no hover. Com o cartão fechado, quem informa é o GET /api/historico, que
/// nunca faz o servidor ler a LG (ele devolve as leituras que o servidor já fez): de 45 em 45 s, barato na rede de casa.
/// Comando: POST /api/comando, um por vez. Nunca /api/atualizar, nunca turbo.
/// </summary>
internal sealed class AtividadeAr : Atividade
{
    public const string NomeCredencial = "ar-do-quarto";
    private const int LEITURA_MS = 4000;
    private const int ALVO_MS = 1200;
    private const int HISTORICO_MS = 45_000;
    private const int HISTORICO_PRIMEIRO_MS = 5_000;
    private const float L = 340;
    private const float ALERTA_SEGUNDOS = 3.5f;
    // temperatura da sala mais velha que isso não vai para o menu (o mini PC pode ter parado de ler)
    private static readonly TimeSpan Validade = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan AvisoDeComando = TimeSpan.FromSeconds(10);

    private readonly Servicos S;
    private readonly Func<string> _enderecoDaConfig;
    private readonly Action<string> _guardarEndereco;

    private readonly System.Windows.Forms.Timer _leitura = new() { Interval = LEITURA_MS };
    private readonly System.Windows.Forms.Timer _envioAlvo = new() { Interval = ALVO_MS };
    private readonly System.Windows.Forms.Timer _historico = new();
    private readonly FilaDeComandos _fila = new();
    private CancellationTokenSource _cancelar = new();

    private ResumoAr? _resumo;
    private string? _problema, _problemaComando, _avisoEntrada;
    private DateTime _problemaComandoAte;
    private bool _chaveRecusada, _lendo, _lendoHistorico, _parado = true, _digitando, _temChave;
    private int _reinicio, _versaoPost;
    private string _enderecoUsado = "";
    private EstadoAr? _conhecido;
    private DateTime _conhecidoEm;
    // o lido_em (relógio do servidor) do que está em _conhecido: ponto do histórico com o mesmo t não sobrescreve
    // o resumo do POST, que traz o pedido aplicado por cima da leitura de antes
    private long? _conhecidoT;
    private readonly DetectorDeMudanca _detector = new();
    private readonly AlertaAdiado _adiado = new();
    private bool _vigiaPausada;
    private string? _detalheMostrado;
    private string _layout = "", _assinaturaTela = "";

    // botões medidos uma vez por layout (a largura de cada pílula depende do texto)
    private readonly List<(RectangleF Area, string Modo)> _pilulasModo = new();
    private readonly List<(RectangleF Area, string Vento, bool Nivel)> _pilulasVento = new();

    public override string Tipo => "ar";
    public override string Nome => "Ar-condicionado";
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    public AtividadeAr(Servicos s, Func<string> endereco, Action<string> guardarEndereco)
    {
        S = s;
        _enderecoDaConfig = endereco;
        _guardarEndereco = guardarEndereco;
        _leitura.Tick += (_, _) => TiqueLeitura();
        _envioAlvo.Tick += (_, _) => { _envioAlvo.Stop(); Enviar(); };
        _historico.Tick += (_, _) => TiqueHistorico();
    }

    private string Endereco => ContasAr.NormalizarEndereco(_enderecoDaConfig()) ?? "";
    private bool Configurado => Endereco.Length > 0 && _temChave;
    private bool Aberta => S.Ilha.Expandida == this;

    // ------------------------------------------------------------------ ciclo

    public void Comecar()
    {
        _parado = false;
        _temChave = Credenciais.Existe(NomeCredencial);
        _enderecoUsado = Endereco;
        S.Ilha.EstadoMudou += AoMudarEstado;
        AgendarHistorico(HISTORICO_PRIMEIRO_MS);
    }

    public void Parar()
    {
        _parado = true;
        S.Ilha.EstadoMudou -= AoMudarEstado;
        _adiado.Limpar();
        _leitura.Stop();
        _envioAlvo.Stop();
        _historico.Stop();
        _cancelar.Cancel();
        _fila.Limpar();
        if (_digitando && S.Entrada.Aberta) S.Entrada.Fechar(devolverFoco: false);
        _digitando = false;
        if (Aberta) S.Ilha.Recolher();
        Desligar();
    }

    /// <summary>A config do módulo mudou: endereço novo zera o que se sabia do ar.</summary>
    public void ConfigMudou()
    {
        if (_parado || Endereco == _enderecoUsado) return;
        _enderecoUsado = Endereco;
        Reiniciar();
    }

    /// <summary>O atalho: abre o cartão; com ele aberto, recolhe.</summary>
    public void Alternar()
    {
        if (_parado) return;
        if (Aberta) S.Ilha.Recolher();
        else S.Ilha.Expandir(this);
    }

    /// <summary>A entrada do menu: abre o cartão.</summary>
    public void Abrir()
    {
        if (!_parado) S.Ilha.Expandir(this);
    }

    /// <summary>
    /// Jogo competitivo: a consulta ao histórico para (o alerta nem apareceria). Ao voltar, a primeira leitura é só
    /// referência de novo: o que mudou durante o jogo não vira alerta atrasado.
    /// </summary>
    public void PausarVigia(bool pausar)
    {
        if (_parado || pausar == _vigiaPausada) return;
        _vigiaPausada = pausar;
        if (pausar) { _historico.Stop(); _adiado.Limpar(); return; }
        _detector.Zerar();
        AgendarHistorico(HISTORICO_PRIMEIRO_MS);
    }

    /// <summary>A linha pequena da entrada no menu: a temperatura da sala que se sabe (ou o que falta configurar).</summary>
    public string? DetalheDoMenu()
    {
        if (_parado) return null;
        if (!Configurado) return Endereco.Length == 0 ? "configurar" : "falta a chave";
        if (_chaveRecusada) return "chave recusada";
        if (_conhecido?.Sala is double s && DateTime.UtcNow - _conhecidoEm < Validade) return "sala " + ContasAr.FormatarNumero(s) + " °C";
        return null;
    }

    /// <summary>Estado do ar para o ícone do menu (cor do modo quando ligado).</summary>
    public EstadoAr? Conhecido => _conhecido != null && DateTime.UtcNow - _conhecidoEm < Validade ? _conhecido : null;

    private void Reiniciar()
    {
        _reinicio++;
        _cancelar.Cancel();
        _cancelar = new CancellationTokenSource();
        _envioAlvo.Stop();
        _fila.Limpar();
        _resumo = null;
        _conhecido = null;
        _conhecidoT = null;
        _detector.Zerar();
        _adiado.Limpar();
        _problema = _problemaComando = null;
        _chaveRecusada = false;
        _lendo = _lendoHistorico = false;
        _temChave = Credenciais.Existe(NomeCredencial);
        AtualizarMenu();
        AtualizarTela();
        AgendarHistorico(HISTORICO_PRIMEIRO_MS);
        if (Aberta && Configurado) LerEstado();
    }

    private void AbriuCartao()
    {
        if (_parado || !Aberta || _leitura.Enabled) return;
        // reabrir é o "tenta de novo" depois de uma chave recusada
        _chaveRecusada = false;
        _problema = null;
        _temChave = Credenciais.Existe(NomeCredencial);
        _leitura.Start();
        if (Configurado) LerEstado();
        AtualizarTela();
    }

    /// <summary>O núcleo avisa na hora: abriu lê já, fechou para os GETs sem esperar o próximo tique.</summary>
    public override void ExpandidaMudou(bool aberta)
    {
        if (aberta) AbriuCartao();
        else if (_leitura.Enabled) TiqueLeitura();
    }

    private void TiqueLeitura()
    {
        if (_parado || !Aberta)
        {
            // fechou: nada de GET até abrir de novo
            _leitura.Stop();
            if (_digitando && S.Entrada.Aberta) S.Entrada.Fechar(devolverFoco: false);
            AtualizarMenu();
            return;
        }
        if (Configurado && !_chaveRecusada && !_lendo) LerEstado();
    }

    private void AgendarHistorico(int ms)
    {
        _historico.Stop();
        if (_parado || !Configurado || _vigiaPausada) return;
        _historico.Interval = ms;
        _historico.Start();
    }

    private void TiqueHistorico()
    {
        _historico.Interval = HISTORICO_MS;
        AtualizarMenu();
        if (_parado || !Configurado || _vigiaPausada) { _historico.Stop(); return; }
        // com o cartão aberto quem informa é o /api/estado
        if (Aberta || _lendoHistorico || _chaveRecusada) return;
        LerHistorico();
    }

    // ------------------------------------------------------------------ rede

    private async void LerEstado()
    {
        if (_lendo) return;
        _lendo = true;
        int reinicio = _reinicio, versao = _versaoPost;
        var r = await ClienteAr.Pedir(Endereco, Credenciais.Ler(NomeCredencial), HttpMethod.Get, "/api/estado", null, _cancelar.Token);
        S.NaInterface(() =>
        {
            if (reinicio != _reinicio) return;
            _lendo = false;
            // um comando foi ou voltou no meio: esta leitura pode ser de antes dele
            if (_parado || versao != _versaoPost) return;
            Aplicar(r);
        });
    }

    private async void LerHistorico()
    {
        _lendoHistorico = true;
        int reinicio = _reinicio;
        var r = await ClienteAr.Pedir(Endereco, Credenciais.Ler(NomeCredencial), HttpMethod.Get, "/api/historico", null, _cancelar.Token);
        S.NaInterface(() =>
        {
            if (reinicio != _reinicio) return;
            _lendoHistorico = false;
            if (_parado) return;
            if (r.Tipo == TipoResposta.Chave) { _chaveRecusada = true; AtualizarMenu(); return; }
            if (r.Tipo != TipoResposta.Ok) return;
            var h = ContasAr.LerHistorico(r.Corpo);
            if (h?.Ultimo is not PontoHistorico p || p.Ligado == null) return;
            long agora = Environment.TickCount64;
            // o cartão abriu enquanto o pedido ia: o /api/estado já está informando
            if (!Aberta) Avisar(_detector.Leitura(p.T, EstadoBasico.De(p), agora));
            // a confirmação atrasada da LG ainda pode trazer o valor de antes do comando: o menu fica com o pedido
            var lido = _detector.ComPedidos(EstadoBasico.De(p), agora);
            var e = _conhecido?.Copia() ?? new EstadoAr();
            e.Ligado = lido.Ligado;
            e.Modo = lido.Modo ?? e.Modo;
            e.Alvo = lido.Alvo ?? e.Alvo;
            e.Sala = p.Sala ?? e.Sala;
            Conhecer(e, p.T, h.Value.Agora, doHistorico: true);
            AtualizarMenu();
            AtualizarTela();
        });
    }

    private void Comandar(string campo, object valor)
    {
        if (!Configurado || _chaveRecusada || _parado) return;
        _fila.Adicionar(campo, valor);
        _problemaComando = null;
        if (campo == "alvo") { _envioAlvo.Stop(); _envioAlvo.Start(); }
        else Enviar();
        AtualizarMenu();
        AtualizarTela();
    }

    private async void Enviar()
    {
        var lote = _fila.Retirar();
        if (lote == null) return;
        _envioAlvo.Stop();
        string corpo;
        try { corpo = ContasAr.MontarComando(lote); }
        catch (ArgumentException e)
        {
            Log.Escrever("ar: comando recusado aqui: " + e.Message);
            _fila.Terminou();
            SoltarAdiado();
            return;
        }
        _versaoPost++;
        int reinicio = _reinicio;
        var r = await ClienteAr.Pedir(Endereco, Credenciais.Ler(NomeCredencial), HttpMethod.Post, "/api/comando", corpo, _cancelar.Token);
        S.NaInterface(() =>
        {
            if (reinicio != _reinicio) return;
            _fila.Terminou();
            _versaoPost++;
            if (_parado) return;
            if (r.Tipo == TipoResposta.Ok) Aplicar(r, lote);
            else
            {
                if (r.Tipo == TipoResposta.Chave) { _chaveRecusada = true; _problema = r.Problema; }
                else { _problemaComando = r.Problema; _problemaComandoAte = DateTime.UtcNow + AvisoDeComando; }
                AtualizarMenu();
                AtualizarTela();
            }
            if (_fila.TemEsperando && !_envioAlvo.Enabled) Enviar();
            // a ilha recolheu com este POST no caminho: o alerta guardado sai agora, já com ele
            SoltarAdiado();
        });
    }

    /// <summary>Resposta do GET /api/estado, ou do POST /api/comando (com o `pedido` que foi nele).</summary>
    private void Aplicar(RespostaAr r, IReadOnlyDictionary<string, object>? pedido = null)
    {
        if (r.Tipo == TipoResposta.Ok)
        {
            var resumo = ContasAr.LerResumo(r.Corpo);
            if (resumo == null) _problema = ContasAr.ProblemaResposta;
            else
            {
                _resumo = resumo;
                _problema = null;
                long agora = Environment.TickCount64;
                if (pedido != null)
                    Avisar(_detector.Comandou(pedido, resumo.LidoEm, resumo.Estado is { } er ? EstadoBasico.De(er) : null, agora));
                else if (resumo.Estado != null && resumo.LidoEm is long t)
                    Avisar(_detector.Leitura(t, EstadoBasico.De(resumo.Estado), agora));
                if (resumo.Estado != null)
                {
                    var e = resumo.Estado.Copia();
                    if (pedido == null)
                    {
                        var lido = _detector.ComPedidos(EstadoBasico.De(e), agora);
                        e.Ligado = lido.Ligado;
                        e.Modo = lido.Modo;
                        e.Alvo = lido.Alvo;
                    }
                    Conhecer(e, resumo.LidoEm, null, doHistorico: false);
                }
            }
        }
        else
        {
            // o último resumo continua na tela, com o aviso por cima
            _problema = r.Problema;
            if (r.Tipo == TipoResposta.Chave) _chaveRecusada = true;
        }
        AtualizarMenu();
        AtualizarTela();
    }

    /// <summary>
    /// Guarda o que se sabe do ar e de quando é (a idade pelo relógio do servidor, quando ele manda o dele). Ponto do
    /// histórico só entra se for de uma leitura MAIS NOVA que a do que está guardado: o resumo do POST vem com o
    /// lido_em antigo e o pedido aplicado, e o ponto dessa mesma leitura traria o valor de antes do comando.
    /// </summary>
    private void Conhecer(EstadoAr e, long? lidoEm, long? agoraServidor, bool doHistorico)
    {
        long agora = agoraServidor ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var quando = lidoEm is long l ? DateTime.UtcNow - TimeSpan.FromSeconds(Math.Max(0, agora - l)) : DateTime.UtcNow;
        if (_conhecido != null)
        {
            if (lidoEm is long t && _conhecidoT is long ct) { if (doHistorico ? t <= ct : t < ct) return; }
            else if (quando < _conhecidoEm) return;
        }
        if (_conhecido?.Vento != null && e.Vento == null) e.Vento = _conhecido.Vento;
        _conhecido = e.Copia();
        _conhecidoEm = quando;
        _conhecidoT = lidoEm ?? _conhecidoT;
    }

    // ------------------------------------------------------------------ alerta de mudança

    private void Avisar(MudancaAr? m)
    {
        if (m == null || _parado) return;
        if (m.PelaIlha)
        {
            // o comando saiu do cartão: o alerta espera a ilha recolher (aberto, ele seria desenhado por cima)
            _adiado.Guardar(m, Environment.TickCount64);
            SoltarAdiado();
            return;
        }
        // mudança de fora com o cartão aberto já está na frente dele
        if (!Aberta) Mostrar(m);
    }

    private void AoMudarEstado(Estado e)
    {
        // de dentro do Aplicar do núcleo: o alerta entra depois que ele termina
        if (!_adiado.Vazio && S.Ilha.Expandida == null) S.NaInterface(SoltarAdiado);
    }

    /// <summary>Solta o alerta dos comandos da ilha quando nada está aberto e nenhum comando está no caminho.</summary>
    private void SoltarAdiado()
    {
        if (_adiado.Vazio || _parado || S.Ilha.Expandida != null) return;
        if (_fila.Enviando || _fila.TemEsperando || _envioAlvo.Enabled) return;
        if (_vigiaPausada) { _adiado.Limpar(); return; }
        if (_adiado.Soltar(_detector.Referencia, Environment.TickCount64) is { } m) Mostrar(m);
    }

    private void Mostrar(MudancaAr m)
    {
        var (titulo, sub) = TextosDoAlerta(m, _conhecido?.Sala);
        var depois = m.Depois;
        bool desligou = m.Tipo == TipoMudanca.Desligou;
        Alertar(new Alerta
        {
            Tipo = "ar",
            // o que a própria ilha mandou não precisa de som: quem mandou está olhando
            Som = m.PelaIlha ? "" : "chegada",
            Segundos = ALERTA_SEGUNDOS,
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(300, 52));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    var centro = new Vector2(26, 26);
                    var cor = ContasAr.CorModo(depois.Modo);
                    Icone.Circulo(dc, centro, 20, desligou ? Cor.Fraco : Alfa(cor, 0.22f));
                    if (m.Tipo == TipoMudanca.Ajuste) IconesAr.DoModo(dc, p.G.Fabrica, depois.Modo, centro, 10, cor);
                    else IconesAr.Energia(dc, p.G.Fabrica, centro, 9, desligou ? Cor.Branco : cor, 2.4f);
                    p.Texto.Desenhar(dc, titulo, new Vector2(60, 4), 17, Cor.Branco, largura: t.X - 60);
                    p.Texto.Desenhar(dc, sub, new Vector2(60, 28), 13, Cor.Secundario, FontWeight.Normal, largura: t.X - 60);
                });
                return c;
            },
        });
    }

    /// <summary>Título e linha de baixo do alerta ("Ar ligado" / "Gelando · 22°", "Ar em 23°" / "Era 22° · Gelando"...).</summary>
    internal static (string Titulo, string Sub) TextosDoAlerta(MudancaAr m, double? sala)
    {
        var d = m.Depois;
        bool temAlvo = d.Alvo != null && d.Modo != "FAN";
        string acao = d.Modo != null ? ContasAr.AcaoModo(d.Modo) : "";
        string graus = temAlvo ? ContasAr.FormatarGraus(d.Alvo) : "";
        string salaTxt = sala != null ? "Sala com " + ContasAr.FormatarGraus(sala) : "";
        static string Juntar(params string[] partes) => string.Join(" · ", Array.FindAll(partes, x => x.Length > 0));
        switch (m.Tipo)
        {
            case TipoMudanca.Ligou:
                return ("Ar ligado", Juntar(acao, graus));
            case TipoMudanca.Desligou:
                return ("Ar desligado", salaTxt.Length > 0 ? salaTxt : "Ar do quarto");
            default:
                if (m.MudouModo)
                    return ("Ar no modo " + ContasAr.NomeModo(d.Modo), Juntar(graus, m.MudouAlvo ? "era " + ContasAr.FormatarGraus(m.Antes.Alvo) : ""));
                return ("Ar em " + graus, Juntar("Era " + ContasAr.FormatarGraus(m.Antes.Alvo), acao));
        }
    }

    // ------------------------------------------------------------------ o que mostrar

    private EstadoAr? NoCartao => ContasAr.Mesclar(_resumo?.Estado, _fila.Locais);

    /// <summary>O detalhe ou o ícone da entrada do menu mudou: o menu aberto redesenha.</summary>
    private void AtualizarMenu()
    {
        var k = Conhecido;
        string? detalhe = DetalheDoMenu() + "|" + k?.Ligado + "|" + k?.Modo;
        if (detalhe == _detalheMostrado) return;
        _detalheMostrado = detalhe;
        S.Menu.Redesenhar();
    }

    private string ChaveLayout()
    {
        if (_digitando) return "digitando";
        if (!Configurado) return "config:" + (Endereco.Length > 0);
        if (_resumo?.Capacidades == null || _resumo.Estado == null) return "lendo";
        return "ar:" + _resumo.Capacidades.Assinatura;
    }

    /// <summary>Layout mudou: cena nova; só o conteúdo: redesenha, e só se mudou alguma coisa que aparece.</summary>
    private void AtualizarTela()
    {
        string k = ChaveLayout();
        if (k != _layout)
        {
            _layout = k;
            _assinaturaTela = "";
            MudouTamanho();
            return;
        }
        var e = NoCartao;
        string assinatura = string.Join("|", e?.Ligado, e?.Modo, e?.Alvo, e?.Sala, e?.Vento, e?.Erro, Aviso()?.Texto, Meta(), _avisoEntrada);
        if (assinatura == _assinaturaTela) return;
        _assinaturaTela = assinatura;
        Redesenhar();
    }

    private (string Texto, bool Erro)? Aviso()
    {
        if (_problema != null) return (_problema, true);
        if (_problemaComando != null && DateTime.UtcNow < _problemaComandoAte) return (_problemaComando, true);
        if (_resumo != null)
        {
            string s = ContasAr.TextoSituacao(_resumo.Situacao, _resumo.Mensagem, _resumo.PausadoAte);
            if (s.Length > 0) return (s, _resumo.Situacao != "lendo");
            if (_resumo.Falhas.Count > 0) return (_resumo.Falhas[0].Mensagem, true);
        }
        if (_resumo?.Estado != null && NoCartao?.Ligado != true)
            return ("Ligue o ar pra mexer na temperatura, no modo e no vento.", false);
        return null;
    }

    private string Meta()
    {
        if (_fila.Locais.Count > 0 || _resumo?.Pendente.Count > 0) return "mandando…";
        if (_resumo == null) return "";
        string t = ContasAr.TempoDesde(_resumo.LidoEm, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return _resumo.Simulado ? t + " · simulado" : t;
    }

    private void Passo(int delta)
    {
        var e = NoCartao;
        if (e?.Ligado != true) return;
        var f = ContasAr.FaixaDoModo(_resumo?.Capacidades, e.Modo);
        if (ContasAr.ProximoAlvo(e.Alvo, delta, f, e.Sala) is int n && (e.Alvo is not double a || Math.Abs(a - n) > 1e-9))
            Comandar("alvo", n);
    }

    // ------------------------------------------------------------------ a chave

    private void AbrirEntrada()
    {
        // aberto pelo clique, o cartão recolheria quando o mouse saísse: fica preso como pelo atalho enquanto digita
        S.Ilha.Expandir(this);
        _digitando = true;
        _avisoEntrada = null;
        S.Entrada.Abrir("", _ => Redesenhar(), Confirmar, fechou: () => { _digitando = false; AtualizarTela(); }, senha: true);
        AtualizarTela();
    }

    private void Confirmar(string texto)
    {
        var r = ContasAr.InterpretarEntrada(texto);
        if (r.Erro != null) { _avisoEntrada = r.Erro; Redesenhar(); return; }
        if (r.Endereco != null && r.Endereco != Endereco) _guardarEndereco(r.Endereco);
        if (r.Chave == null)
        {
            _avisoEntrada = "Endereço salvo. Agora cole a chave (ou o link inteiro).";
            S.Entrada.Definir("");
            Redesenhar();
            return;
        }
        if (Endereco.Length == 0 && r.Endereco == null)
        {
            _avisoEntrada = "Falta o endereço do mini PC: cole o link inteiro que o mostrar-link.ps1 mostra.";
            Redesenhar();
            return;
        }
        if (!Credenciais.Gravar(NomeCredencial, r.Chave))
        {
            _avisoEntrada = "Não consegui guardar a chave no Gerenciador de Credenciais do Windows.";
            Redesenhar();
            return;
        }
        S.Entrada.Fechar();
        // o endereço novo também já está valendo aqui (a config salva chega depois pelo ConfigMudou)
        _enderecoUsado = r.Endereco ?? Endereco;
        Reiniciar();
        if (Aberta && !_leitura.Enabled) _leitura.Start();
    }

    // ------------------------------------------------------------------ cenas

    protected override Cena Montar(Palco p, Forma f)
    {
        // nunca fica na pílula (a atividade não liga): só o cartão é pedido
        if (Aberta && !_leitura.Enabled) S.NaInterface(AbriuCartao);
        _layout = ChaveLayout();
        _assinaturaTela = "";
        return _layout switch
        {
            "digitando" => CenaDigitando(p),
            "lendo" => CenaLendo(p),
            _ when _layout.StartsWith("config") => CenaConfig(p),
            _ => CenaCompleta(p),
        };
    }

    private static Color4 Alfa(Color4 c, float a) => new(c.R, c.G, c.B, c.A * a);

    private string Titulo => _resumo?.NomeAparelho ?? "Ar do quarto";

    /// <summary>Quebra o texto em até `max` linhas pela largura (a última corta com reticências).</summary>
    private static List<string> Quebrar(Palco p, string texto, float tam, float largura, int max)
    {
        var linhas = new List<string>();
        var atual = "";
        var palavras = texto.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < palavras.Length; i++)
        {
            string tentativa = atual.Length == 0 ? palavras[i] : atual + " " + palavras[i];
            if (atual.Length > 0 && p.Texto.Medir(tentativa, tam, FontWeight.Normal).X > largura)
            {
                if (linhas.Count == max - 1) { linhas.Add(string.Join(" ", atual, string.Join(" ", palavras[i..]))); return linhas; }
                linhas.Add(atual);
                atual = palavras[i];
            }
            else atual = tentativa;
        }
        if (atual.Length > 0) linhas.Add(atual);
        return linhas;
    }

    private static void Paragrafo(ID2D1DeviceContext dc, Palco p, string texto, Vector2 pos, float tam, float largura, int max, Color4 cor)
    {
        var linhas = Quebrar(p, texto, tam, largura, max);
        for (int i = 0; i < linhas.Count; i++)
            p.Texto.Desenhar(dc, linhas[i], new Vector2(pos.X, pos.Y + i * (tam + 4.5f)), tam, cor, FontWeight.Normal, largura: largura);
    }

    private void Pilula(ID2D1DeviceContext dc, Palco p, RectangleF r, string texto, float tam, Color4 fundo, Color4 cor)
    {
        Icone.Retangulo(dc, r.X, r.Y, r.Width, r.Height, r.Height / 2, fundo);
        var m = p.Texto.Medir(texto, tam);
        p.Texto.Desenhar(dc, texto, new Vector2(r.X + (r.Width - Math.Min(m.X, r.Width - 8)) / 2, r.Y + (r.Height - m.Y) / 2), tam, cor, largura: r.Width - 8);
    }

    /// <summary>O título, o "lido há" e o botão da chave (trocar a chave a qualquer hora).</summary>
    private void Cabecalho(Cena c, ID2D1DeviceContext dc, Palco p, bool comChave)
    {
        p.Texto.Desenhar(dc, Titulo, new Vector2(0, 0), 13, Cor.Secundario, largura: 180);
        float direita = comChave ? L - 28 : L;
        string meta = Meta();
        if (meta.Length > 0)
        {
            var m = p.Texto.Medir(meta, 11, FontWeight.Normal);
            p.Texto.Desenhar(dc, meta, new Vector2(direita - Math.Min(m.X, 150), 2), 11, Cor.Secundario, FontWeight.Normal, largura: 150);
        }
        if (comChave) IconesAr.Chave(dc, p.G.Fabrica, new Vector2(L - 11, 9), Cor.Secundario);
    }

    private void BotaoChave(Cena c) => c.Botoes.Add(new Botao { Area = new RectangleF(L - 26, -4, 30, 26), Acao = AbrirEntrada });

    private Cena CenaConfig(Palco p)
    {
        var c = p.NovaCena(new Vector2(L, 114));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            Cabecalho(c, dc, p, false);
            string texto = Endereco.Length > 0
                ? "Falta a chave do ar. Cole o link que o implantar\\mostrar-link.ps1 do ar-do-quarto mostra, ou só a chave."
                : "Para controlar o ar por aqui, cole o link que o implantar\\mostrar-link.ps1 do ar-do-quarto mostra (http://…:8088/#chave=…).";
            Paragrafo(dc, p, texto, new Vector2(0, 24), 13, L, 3, Cor.Branco);
            Pilula(dc, p, new RectangleF(0, 84, 130, 30), "Colar o link", 13, Cor.Fraco, Cor.Branco);
        });
        c.Botoes.Add(new Botao { Area = new RectangleF(0, 84, 130, 30), Acao = AbrirEntrada });
        return c;
    }

    private Cena CenaDigitando(Palco p)
    {
        var c = p.NovaCena(new Vector2(L, 98));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            p.Texto.Desenhar(dc, "Link ou chave do ar", new Vector2(0, 0), 13, Cor.Secundario);
            Icone.Retangulo(dc, 0, 22, L, 36, 11, Cor.Fraco);
            p.Texto.DesenharCampo(dc, S.Entrada.Texto, S.Entrada.Cursor, true, new Vector2(12, 30), L - 24, 15, Cor.Branco,
                vazio: "http://…:8088/#chave=…", comCursor: S.Entrada.Aberta);
            if (_avisoEntrada != null) Paragrafo(dc, p, _avisoEntrada, new Vector2(0, 66), 12, L, 2, Cor.Vermelho);
            else p.Texto.Desenhar(dc, "Enter salva · Esc cancela · a chave vai para o Gerenciador de Credenciais", new Vector2(0, 66), 12, Cor.Secundario, FontWeight.Normal, largura: L);
        });
        return c;
    }

    private Cena CenaLendo(Palco p)
    {
        var c = p.NovaCena(new Vector2(L, 80));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            Cabecalho(c, dc, p, true);
            var a = Aviso();
            Paragrafo(dc, p, a?.Texto ?? "Lendo o ar…", new Vector2(0, 26), 13, L, 3, a?.Erro == true ? Cor.Vermelho : Cor.Branco);
        });
        BotaoChave(c);
        return c;
    }

    private const float Y_TEMP = 82, Y_MODO = 136, Y_VENTO = 196, Y_AVISO = 254, ALTURA_PILULA = 30;

    private void MedirPilulas(Palco p, Capacidades caps)
    {
        _pilulasModo.Clear();
        _pilulasVento.Clear();
        // modos: largura de cada pílula pelo texto, e a sobra dividida entre todas
        const float VAO = 6, FOLGA = 16, TAM = 12.5f;
        var larguras = new List<float>();
        foreach (var m in caps.Modos) larguras.Add(p.Texto.Medir(ContasAr.NomeModo(m), TAM).X + FOLGA);
        float soma = 0;
        foreach (var w in larguras) soma += w;
        float total = soma + VAO * Math.Max(0, larguras.Count - 1);
        float extra = larguras.Count > 0 ? (L - total) / larguras.Count : 0;
        float x = 0;
        for (int i = 0; i < caps.Modos.Count; i++)
        {
            // não coube: encolhe por igual (o texto corta com reticências)
            float w = extra >= 0 ? larguras[i] + extra : larguras[i] * (L - VAO * (larguras.Count - 1)) / soma;
            _pilulasModo.Add((new RectangleF(x, Y_MODO + 18, w, ALTURA_PILULA), caps.Modos[i]));
            x += w + VAO;
        }

        var (niveis, especiais) = ContasAr.AgruparVentos(caps.Vento);
        x = 0;
        foreach (var v in especiais)
        {
            float w = p.Texto.Medir(ContasAr.NomeVento(v), TAM).X + FOLGA + 4;
            if (x + w > L) break;
            _pilulasVento.Add((new RectangleF(x, Y_VENTO + 18, w, ALTURA_PILULA), v, false));
            x += w + VAO;
        }
        if (especiais.Count > 0) x += 6;
        foreach (var v in niveis)
        {
            if (x + 20 > L) break;
            _pilulasVento.Add((new RectangleF(x, Y_VENTO + 18, 20, ALTURA_PILULA), v, true));
            x += 20;
        }
    }

    private Cena CenaCompleta(Palco p)
    {
        var caps = _resumo!.Capacidades!;
        MedirPilulas(p, caps);
        var c = p.NovaCena(new Vector2(L, Y_AVISO + 36));
        var F = p.G.Fabrica;
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var e = NoCartao ?? new EstadoAr();
            bool ligado = e.Ligado == true;
            var corModo = ContasAr.CorModo(e.Modo);
            Cabecalho(c, dc, p, true);

            // liga e desliga
            var centro = new Vector2(22, 46);
            Icone.Circulo(dc, centro, 22, ligado ? Alfa(corModo, 0.22f) : Cor.Fraco);
            IconesAr.Energia(dc, F, centro, 9, ligado ? corModo : Cor.Branco, 2.4f);
            var (forte, fraco) = ContasAr.FraseStatus(e, caps);
            p.Texto.Desenhar(dc, forte, new Vector2(58, 21), 20, Cor.Branco, largura: L - 58);
            p.Texto.Desenhar(dc, fraco, new Vector2(58, 50), 13, Cor.Secundario, FontWeight.Normal, largura: L - 58);

            // temperatura de 1 em 1 grau
            var faixa = ContasAr.FaixaDoModo(caps, e.Modo);
            bool temTemp = ligado && faixa != null;
            var (desce, sobe) = temTemp ? ContasAr.LimitesDoAlvo(e.Alvo, faixa) : (false, false);
            float cy = Y_TEMP + 20;
            Icone.Circulo(dc, new Vector2(20, cy), 18, desce ? Cor.Fraco : Alfa(Cor.Fraco, 0.4f));
            Icone.Linha(dc, new Vector2(14, cy), new Vector2(26, cy), 2.2f, desce ? Cor.Branco : Alfa(Cor.Branco, 0.3f), F);
            Icone.Circulo(dc, new Vector2(156, cy), 18, sobe ? Cor.Fraco : Alfa(Cor.Fraco, 0.4f));
            Icone.Linha(dc, new Vector2(150, cy), new Vector2(162, cy), 2.2f, sobe ? Cor.Branco : Alfa(Cor.Branco, 0.3f), F);
            Icone.Linha(dc, new Vector2(156, cy - 6), new Vector2(156, cy + 6), 2.2f, sobe ? Cor.Branco : Alfa(Cor.Branco, 0.3f), F);
            if (faixa != null && e.Alvo != null)
            {
                string graus = ContasAr.FormatarGraus(e.Alvo);
                bool pendente = _fila.Pendente("alvo") || _resumo?.Pendente.Contains("alvo") == true;
                var cor = ligado ? corModo : Cor.Secundario;
                if (pendente) cor = Alfa(cor, 0.6f);
                var m = p.Texto.Medir(graus, 32, FontWeight.Medium, tabular: true);
                p.Texto.Desenhar(dc, graus, new Vector2(88 - m.X / 2, cy - m.Y / 2), 32, cor, FontWeight.Medium, tabular: true);
            }
            p.Texto.Desenhar(dc, "Temperatura", new Vector2(190, Y_TEMP + 2), 12, Cor.Secundario);
            string sob = faixa == null
                ? (e.Modo == "FAN" ? "No modo ventilar não tem" : "Este modo não tem")
                : ContasAr.FaixaInteira(faixa) is (int mn, int mx) ? $"de {mn}° a {mx}°" : "";
            p.Texto.Desenhar(dc, sob, new Vector2(190, Y_TEMP + 20), 12, Cor.Secundario, FontWeight.Normal, largura: L - 190);

            // modo
            p.Texto.Desenhar(dc, "Modo", new Vector2(0, Y_MODO), 12, Cor.Secundario);
            foreach (var (r, modo) in _pilulasModo)
            {
                bool escolhido = e.Modo == modo;
                var cm = ContasAr.CorModo(modo);
                var fundo = escolhido ? Alfa(cm, ligado ? 0.24f : 0.12f) : Alfa(Cor.Fraco, 0.7f);
                var cor = escolhido ? (ligado ? cm : Alfa(cm, 0.55f)) : Alfa(Cor.Branco, ligado ? 0.85f : 0.35f);
                Pilula(dc, p, r, ContasAr.NomeModo(modo), 12.5f, fundo, cor);
            }

            // vento: pílulas para o que não é nível e barrinhas crescentes para os níveis
            p.Texto.Desenhar(dc, "Vento", new Vector2(0, Y_VENTO), 12, Cor.Secundario);
            var corEscolha = ligado ? corModo : Alfa(corModo, 0.5f);
            int nivelEscolhido = -1, k = 0, quantos = 0;
            foreach (var (_, v, nivel) in _pilulasVento) if (nivel) { if (v == e.Vento) nivelEscolhido = quantos; quantos++; }
            float fimBarras = 0;
            foreach (var (r, v, nivel) in _pilulasVento)
            {
                if (!nivel)
                {
                    bool escolhido = e.Vento == v;
                    Pilula(dc, p, r, ContasAr.NomeVento(v), 12.5f, escolhido ? Alfa(corModo, ligado ? 0.24f : 0.12f) : Alfa(Cor.Fraco, 0.7f),
                        escolhido ? corEscolha : Alfa(Cor.Branco, ligado ? 0.85f : 0.35f));
                    continue;
                }
                float h = 9 + 17f * (quantos <= 1 ? 1 : k / (float)(quantos - 1));
                bool cheio = nivelEscolhido >= 0 && k <= nivelEscolhido;
                Icone.Retangulo(dc, r.X + 4, r.Bottom - 3 - h, 12, h, 2.5f, cheio ? corEscolha : Alfa(Cor.Fraco, ligado ? 1f : 0.6f));
                fimBarras = r.Right;
                k++;
            }
            if (e.Vento != null && fimBarras > 0 && fimBarras + 70 < L)
                p.Texto.Desenhar(dc, ContasAr.NomeVento(e.Vento), new Vector2(fimBarras + 10, Y_VENTO + 25), 12, Cor.Secundario, FontWeight.Normal, largura: L - fimBarras - 10);

            var aviso = Aviso();
            if (aviso != null) Paragrafo(dc, p, aviso.Value.Texto, new Vector2(0, Y_AVISO), 12, L, 2, aviso.Value.Erro ? Cor.Vermelho : Cor.Secundario);
        });

        BotaoChave(c);
        c.Botoes.Add(new Botao { Area = new RectangleF(0, 24, 44, 44), Acao = () => Comandar("ligado", NoCartao?.Ligado != true) });
        c.Botoes.Add(new Botao { Area = new RectangleF(0, Y_TEMP + 2, 40, 36), Acao = () => Passo(-1) });
        c.Botoes.Add(new Botao { Area = new RectangleF(136, Y_TEMP + 2, 40, 36), Acao = () => Passo(+1) });
        foreach (var (r, modo) in _pilulasModo)
            c.Botoes.Add(new Botao
            {
                Area = r,
                Acao = () => { if (NoCartao is { Ligado: true } e && e.Modo != modo) Comandar("modo", modo); },
            });
        foreach (var (r, v, _) in _pilulasVento)
            c.Botoes.Add(new Botao
            {
                Area = r,
                Acao = () => { if (NoCartao is { Ligado: true } e && e.Vento != v) Comandar("vento", v); },
            });
        return c;
    }

    public override void Dispose()
    {
        _leitura.Dispose();
        _envioAlvo.Dispose();
        _historico.Dispose();
        _cancelar.Dispose();
    }
}

/// <summary>Os ícones do ar, desenhados em Direct2D: floco (gelar), sol (aquecer), gota (desumidificar), hélice (ventilar).</summary>
internal static class IconesAr
{
    public static void DoModo(ID2D1DeviceContext dc, ID2D1Factory1 f, string? modo, Vector2 c, float r, Color4 cor)
    {
        switch (modo)
        {
            case "HEAT": Sol(dc, f, c, r, cor); break;
            case "AIR_DRY": Gota(dc, f, c, r, cor); break;
            case "FAN": Helice(dc, c, r, cor); break;
            default: Floco(dc, f, c, r, cor); break;
        }
    }

    public static void Floco(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float r, Color4 cor)
    {
        float esp = MathF.Max(1.4f, r * 0.17f);
        for (int i = 0; i < 3; i++)
        {
            float a = MathF.PI / 2 + i * MathF.PI / 3;
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            Icone.Linha(dc, c - dir * r, c + dir * r, esp, cor, f);
            foreach (float s in new[] { 1f, -1f })
            {
                var d = dir * s;
                var perp = new Vector2(-d.Y, d.X);
                var base_ = c + d * r * 0.55f;
                Icone.Linha(dc, base_, base_ + (d * 0.3f + perp * 0.28f) * r, esp, cor, f);
                Icone.Linha(dc, base_, base_ + (d * 0.3f - perp * 0.28f) * r, esp, cor, f);
            }
        }
    }

    public static void Sol(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float r, Color4 cor)
    {
        Icone.Circulo(dc, c, r * 0.42f, cor);
        float esp = MathF.Max(1.4f, r * 0.17f);
        for (int i = 0; i < 8; i++)
        {
            float a = i * MathF.PI / 4;
            var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
            Icone.Linha(dc, c + d * r * 0.66f, c + d * r, esp, cor, f);
        }
    }

    public static void Gota(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float r, Color4 cor)
    {
        var centro = new Vector2(c.X, c.Y + r * 0.28f);
        float rc = r * 0.62f;
        Icone.Circulo(dc, centro, rc, cor);
        // tangentes do topo ao círculo, para a ponta emendar sem degrau
        var topo = new Vector2(c.X, c.Y - r);
        float dist = centro.Y - topo.Y;
        float ang = MathF.Asin(rc / dist);
        float lado = rc * MathF.Cos(ang);
        float altura = centro.Y - rc * MathF.Sin(ang);
        Icone.Triangulo(dc, f, topo, new Vector2(c.X - lado, altura), new Vector2(c.X + lado, altura), cor);
    }

    public static void Helice(ID2D1DeviceContext dc, Vector2 c, float r, Color4 cor)
    {
        var antes = dc.Transform;
        using var b = dc.CreateSolidColorBrush(cor);
        for (int i = 0; i < 3; i++)
        {
            dc.Transform = Matrix3x2.CreateRotation(i * MathF.PI * 2 / 3 - MathF.PI / 2, c) * antes;
            dc.FillEllipse(new Ellipse(new Vector2(c.X + r * 0.5f, c.Y + r * 0.12f), r * 0.5f, r * 0.26f), b);
        }
        dc.Transform = antes;
        Icone.Circulo(dc, c, r * 0.2f, cor);
    }

    /// <summary>O símbolo de liga e desliga: arco aberto em cima e o traço no meio.</summary>
    public static void Energia(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float r, Color4 cor, float esp)
    {
        float abre = 38 * MathF.PI / 180;
        float a0 = -MathF.PI / 2 + abre, a1 = -MathF.PI / 2 - abre;
        using var g = f.CreatePathGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(c + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * r, FigureBegin.Hollow);
            s.AddArc(new ArcSegment
            {
                Point = c + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * r,
                Size = new Vortice.Mathematics.Size(r, r),
                SweepDirection = SweepDirection.Clockwise,
                ArcSize = ArcSize.Large,
            });
            s.EndFigure(FigureEnd.Open);
            s.Close();
        }
        using var b = dc.CreateSolidColorBrush(cor);
        using var traco = f.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        dc.DrawGeometry(g, b, esp, traco);
        Icone.Linha(dc, new Vector2(c.X, c.Y - r * 1.12f), new Vector2(c.X, c.Y - r * 0.2f), esp, cor, f);
    }

    /// <summary>
    /// O ícone do menu: o aparelho na parede e três traços de vento saindo, na cor do modo quando se sabe que está
    /// ligado (cinza quando desligado ou sem leitura recente).
    /// </summary>
    public static void Aparelho(ID2D1DeviceContext dc, ID2D1Factory1 f, RectangleF r, EstadoAr? e)
    {
        float x = r.X, y = r.Y, s = r.Width / 24f;
        Icone.Retangulo(dc, x + 1 * s, y + 3 * s, 22 * s, 10 * s, 3 * s, new Color4(1, 1, 1, 0.92f));
        Icone.Linha(dc, new Vector2(x + 5 * s, y + 10 * s), new Vector2(x + 19 * s, y + 10 * s), 1.4f * s, new Color4(0, 0, 0, 0.55f), f);
        var cor = e?.Ligado == true ? ContasAr.CorModo(e.Modo) : Cor.Secundario;
        for (int i = -1; i <= 1; i++)
        {
            float cx = x + (12 + i * 5.5f) * s;
            Icone.Linha(dc, new Vector2(cx, y + 15.5f * s), new Vector2(cx + i * 1.6f * s, y + 21.5f * s), 1.8f * s, cor, f);
        }
    }

    /// <summary>Uma chave pequena (o botão de trocar a chave do servidor).</summary>
    public static void Chave(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, Color4 cor)
    {
        using var b = dc.CreateSolidColorBrush(cor);
        dc.DrawEllipse(new Ellipse(new Vector2(c.X - 4.5f, c.Y), 3.6f, 3.6f), b, 1.6f);
        Icone.Linha(dc, new Vector2(c.X - 0.9f, c.Y), new Vector2(c.X + 7, c.Y), 1.6f, cor, f);
        Icone.Linha(dc, new Vector2(c.X + 4, c.Y), new Vector2(c.X + 4, c.Y + 3.2f), 1.6f, cor, f);
        Icone.Linha(dc, new Vector2(c.X + 6.6f, c.Y), new Vector2(c.X + 6.6f, c.Y + 2.6f), 1.6f, cor, f);
    }
}
