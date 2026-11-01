using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Vortice.DirectWrite;

namespace Ilha.Claude;

/// <summary>
/// Claude Code na ilha: o que cada sessão está fazendo em tempo real (pela fila de eventos dos hooks e pelos arquivos
/// de sessão), alerta ao terminar, aprovar ou negar pedido de permissão direto da ilha (pelo hook PermissionRequest,
/// ferramentas/hook_permissao.py) e trocar modelo, esforço e mandar mensagem pela ponte de acessibilidade com o app.
/// Sem "arquivoEventos" na config o módulo fica calado (o pipe de permissão continua, se "aprovarPermissoes").
/// </summary>
[Modulo("claude", "Claude Code")]
internal sealed class ModuloClaude : Modulo
{
    private ConfigClaude _cfg = new();
    private readonly Sessoes _m = new();
    private PonteClaudeApp? _ponte;
    private AtividadeClaude? _atv;
    private VigiaDaFila? _fila;
    private VigiaDasSessoes? _vigiaSessoes;
    private ServidorPermissao? _servidor;
    private readonly System.Windows.Forms.Timer _relogio = new();
    private readonly List<PedidoPermissao> _pedidos = new();
    // o alerta de permissão que está na tela agora (para fechar quando o pedido for respondido por outro caminho)
    private PedidoPermissao? _alertaNaTela;
    private readonly Dictionary<PedidoPermissao, Alerta> _alertas = new();
    private bool _comecou;

    public override Type? TipoConfig => typeof(ConfigClaude);

    internal static double Agora() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigClaude>(cfg);
        _m.JanelaDepoisDeTerminar = Math.Clamp(_cfg.SegundosDepoisDeTerminar, 5, 3600);
        _ponte = new PonteClaudeApp();
        _atv = new AtividadeClaude(S, _m, _ponte, Agora, () => _cfg) { Decidir = Decidir };
        S.Ilha.Adicionar(_atv);
        _relogio.Tick += (_, _) => { _relogio.Stop(); Processar(_m.Tique(Agora())); };
        Ligar();
        // a seção nasce só com "ligado": grava os campos com os padrões para quem usa achar onde pôr o caminho
        if (!cfg.ContainsKey("arquivoEventos")) Salvar(_cfg);
    }

    private void Ligar()
    {
        string arq = Environment.ExpandEnvironmentVariables(_cfg.ArquivoEventos ?? "").Trim();
        if (arq.Length > 0)
        {
            try { _fila = new VigiaDaFila(arq, (evs, primeira) => S.NaInterface(() => Chegaram(evs, primeira))); }
            catch (Exception e) { Log.Escrever("claude: fila de eventos não abriu: " + e.Message); }
            string pasta = Environment.ExpandEnvironmentVariables(_cfg.PastaSessoes ?? "").Trim();
            if (pasta.Length == 0) pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");
            try { _vigiaSessoes = new VigiaDasSessoes(pasta, (lidos, sumiram) => S.NaInterface(() => Arquivos(lidos, sumiram))); }
            catch (Exception e) { Log.Escrever("claude: pasta de sessões não abriu: " + e.Message); }
        }
        if (_cfg.AprovarPermissoes)
            _servidor = new ServidorPermissao(ProtocoloPermissao.PIPE, () => _cfg.EsperaSegundos, ChegouPedido);
    }

    private void Desligar()
    {
        _fila?.Dispose();
        _fila = null;
        _vigiaSessoes?.Dispose();
        _vigiaSessoes = null;
        _servidor?.Dispose();
        _servidor = null;
        foreach (var p in _pedidos.ToList()) Encerrar(p, null, atualizar: false);
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        var novo = Ler<ConfigClaude>(cfg);
        bool refazer = novo.ArquivoEventos != _cfg.ArquivoEventos || novo.PastaSessoes != _cfg.PastaSessoes || novo.AprovarPermissoes != _cfg.AprovarPermissoes;
        _cfg = novo;
        _m.JanelaDepoisDeTerminar = Math.Clamp(_cfg.SegundosDepoisDeTerminar, 5, 3600);
        if (refazer) { Desligar(); Ligar(); }
        Processar(_m.Tique(Agora()));
    }

    public override void Parar()
    {
        Desligar();
        _relogio.Stop();
        _relogio.Dispose();
        _ponte?.Dispose();
        _atv?.Dispose();
    }

    // ------------------------------------------------------------------ chegadas (já na thread da interface)

    private void Chegaram(List<Evento> evs, bool primeira)
    {
        var ef = new Efeitos();
        // a leitura inicial é o histórico: monta o estado sem alerta nenhum
        foreach (var ev in evs) ef.Juntar(_m.Aplicar(ev, silencioso: primeira || !_comecou));
        if (primeira) _comecou = true;
        ef.Juntar(_m.Tique(Agora()));
        Processar(ef);
    }

    private void Arquivos(List<ArquivoDeSessao> lidos, List<string> sumiram)
    {
        var ef = new Efeitos();
        double agora = Agora();
        foreach (var a in lidos) ef.Juntar(_m.AplicarArquivo(a, agora));
        foreach (var id in sumiram) ef.Juntar(_m.ArquivoSumiu(id));
        ef.Juntar(_m.Tique(agora));
        Processar(ef);
    }

    /// <summary>Numa thread do pool, vindo do pipe: registra o pedido e devolve como tirá-lo (a conexão caiu ou acabou).</summary>
    private Action ChegouPedido(ProtocoloPermissao.Pedido pedido, Action<string?> responder)
    {
        int respondido = 0;
        var p = new PedidoPermissao
        {
            Sessao = pedido.Sessao.Length > 0 ? pedido.Sessao : "sem-sessao",
            Ferramenta = pedido.Ferramenta,
            Resumo = pedido.Resumo,
            Projeto = Path.GetFileName(pedido.Cwd.TrimEnd('\\', '/')),
            Chegou = Agora(),
        };
        p.Responder = d => { if (System.Threading.Interlocked.Exchange(ref respondido, 1) == 0) responder(d); };
        S.NaInterface(() =>
        {
            // a ilha não vai aparecer (pausada, jogo competitivo, Não Incomodar, tela cheia): o hook sai na hora e o
            // diálogo normal do Claude Code segue, em vez de esperar um clique que ninguém vai ver
            if (_atv == null || !PodeAparecer(NovoAlerta(p)))
            {
                p.Responder(null);
                return;
            }
            _pedidos.Add(p);
            _m.PedidoChegou(p);
            AlertarPermissao(p);
            Processar(new Efeitos { Mudou = true });
        });
        return () => S.NaInterface(() => Encerrar(p, null));
    }

    private bool PodeAparecer(Alerta a)
    {
        var ctx = S.ContextoAtual();
        return S.Ilha.PodeMostrarAlerta(a) && !ctx.TelaCheia && !ctx.D3DExclusivo;
    }

    /// <summary>Entrou num jogo, pausou ou ligou o Não Incomodar com pedido esperando: devolve ao diálogo normal.</summary>
    public override void Contexto(Contexto ctx)
    {
        if (_pedidos.Count == 0) return;
        foreach (var p in _pedidos.ToList())
            if (_alertas.TryGetValue(p, out var a) && !PodeAparecer(a)) Encerrar(p, null);
    }

    private void Decidir(PedidoPermissao p, string decisao)
    {
        p.Responder(decisao);
        Encerrar(p, decisao);
    }

    /// <summary>Tira o pedido da ilha (respondido aqui, no app, ou o hook desistiu). Sem decisão, o hook sai calado.</summary>
    private void Encerrar(PedidoPermissao p, string? decisao, bool atualizar = true)
    {
        p.Responder(decisao);
        bool tinha = _pedidos.Remove(p);
        tinha |= _m.PedidoSaiu(p);
        if (_alertas.Remove(p, out var a)) S.Ilha.FecharAlerta(a);
        if (_alertaNaTela == p) _alertaNaTela = null;
        if (tinha && atualizar) Processar(new Efeitos { Mudou = true });
    }

    private void Processar(Efeitos ef)
    {
        foreach (var p in ef.Encerrados) Encerrar(p, null, atualizar: false);
        foreach (var (s, dur) in ef.Terminaram) AlertarFim(s, dur);
        foreach (var s in ef.PediuPermissaoSemPonte) AlertarPermissaoSemPonte(s);
        _atv?.Atualizar();
        Agendar();
    }

    private void Agendar()
    {
        _relogio.Stop();
        var prazo = _m.ProximoPrazo();
        if (prazo == null) return;
        double ms = (prazo.Value - Agora()) * 1000 + 50;
        _relogio.Interval = (int)Math.Clamp(ms, 50, int.MaxValue / 2);
        _relogio.Start();
    }

    // ------------------------------------------------------------------ alertas

    private void AlertarFim(Sessao s, double duracao)
    {
        if (!_cfg.AlertarAoTerminar || _atv == null) return;
        string titulo = s.Pergunta ? "Claude Code fez uma pergunta" : "Claude Code terminou";
        string sub = (s.Projeto.Length > 0 ? s.Projeto : "Claude Code") + (duracao > 0 ? " · " + Neutro.Duracao(duracao) : "");
        S.Ilha.Alertar(new Alerta
        {
            Tipo = "claude",
            Som = "claude",
            Dono = _atv,
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(300, 52));
                p.Logo(c, new Vector2(8, 10), 32).Tocar("claude-volta");
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    p.Texto.Desenhar(dc, titulo, new Vector2(56, 4), 17, Cor.Branco, largura: t.X - 56);
                    p.Texto.Desenhar(dc, sub, new Vector2(56, 28), 13, Cor.Secundario, FontWeight.Normal, largura: t.X - 56);
                });
                return c;
            },
        });
    }

    private void AlertarPermissaoSemPonte(Sessao s)
    {
        if (_atv == null) return;
        string sub = s.Projeto.Length > 0 ? s.Projeto + " · responda no app do Claude" : "Responda no app do Claude";
        S.Ilha.Alertar(new Alerta
        {
            Tipo = "claude",
            Som = "alerta",
            Dono = _atv,
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(300, 52));
                p.Logo(c, new Vector2(8, 10), 32).Tocar("claude-pulso");
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    p.Texto.Desenhar(dc, "Claude Code pede permissão", new Vector2(56, 4), 17, Cor.Branco, largura: t.X - 56);
                    p.Texto.Desenhar(dc, sub, new Vector2(56, 28), 13, Cor.Secundario, FontWeight.Normal, largura: t.X - 56);
                });
                return c;
            },
        });
    }

    private void AlertarPermissao(PedidoPermissao pedido)
    {
        if (_atv == null) return;
        var a = NovoAlerta(pedido);
        _alertas[pedido] = a;
        S.Ilha.Alertar(a);
    }

    private Alerta NovoAlerta(PedidoPermissao pedido) =>
        new Alerta
        {
            Tipo = "claude",
            Som = "alerta",
            Dono = _atv,
            Segundos = Math.Clamp(_cfg.AlertaPermissaoSegundos, 3, 120),
            Criar = p =>
            {
                const float L = 340;
                var c = p.NovaCena(new Vector2(L, 98));
                long nasceu = Environment.TickCount64;
                _alertaNaTela = pedido;
                c.AoSoltar += () => { if (_alertaNaTela == pedido) _alertaNaTela = null; };
                p.Logo(c, new Vector2(-4, -5), 30).Tocar("claude-pulso");
                string cab = (pedido.Projeto.Length > 0 ? pedido.Projeto : "Claude Code") + " pede permissão";
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    p.Texto.Desenhar(dc, cab, new Vector2(32, 1), 13, Cor.Secundario, largura: L - 32);
                    p.Texto.Desenhar(dc, pedido.Ferramenta, new Vector2(0, 22), 17, Cor.Branco, largura: L);
                    if (pedido.Resumo.Length > 0) p.Texto.Desenhar(dc, pedido.Resumo, new Vector2(0, 44), 13, Cor.Secundario, FontWeight.Normal, largura: L);
                    foreach (var (x, texto, cor) in new[] { (0f, "Permitir", Cor.Verde), (176f, "Negar", Cor.Vermelho) })
                    {
                        Icone.Retangulo(dc, x, 66, 164, 30, 10, new Vortice.Mathematics.Color4(cor.R, cor.G, cor.B, 0.28f));
                        var tam = p.Texto.Medir(texto, 14);
                        p.Texto.Desenhar(dc, texto, new Vector2(x + (164 - tam.X) / 2, 66 + (30 - tam.Y) / 2), 14, Cor.Branco);
                    }
                });
                c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(0, 66, 164, 30), Acao = () => { if (PedidoPermissao.PassouCarencia(nasceu, Environment.TickCount64)) Decidir(pedido, "allow"); } });
                c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(176, 66, 164, 30), Acao = () => Decidir(pedido, "deny") });
                return c;
            },
        };

    // ------------------------------------------------------------------ autoteste

    public override string Autoteste() => AutotesteClaude.Rodar();
}

public sealed class ConfigClaude
{
    /// <summary>A fila JSONL que os hooks escrevem (vazio = módulo calado).</summary>
    [JsonPropertyName("arquivoEventos")] public string ArquivoEventos { get; set; } = "";
    /// <summary>Pasta dos arquivos de sessão do Claude Code (vazio = ~/.claude/sessions).</summary>
    [JsonPropertyName("pastaSessoes")] public string PastaSessoes { get; set; } = "";
    /// <summary>Serve o pipe do hook PermissionRequest (ferramentas/hook_permissao.py).</summary>
    [JsonPropertyName("aprovarPermissoes")] public bool AprovarPermissoes { get; set; } = true;
    /// <summary>Quanto o hook espera o clique antes de cair no diálogo normal (o timeout do hook tem que ser maior).</summary>
    [JsonPropertyName("esperaSegundos")] public int EsperaSegundos { get; set; } = 45;
    /// <summary>Quanto o alerta de permissão fica na tela (depois, os botões continuam no cartão).</summary>
    [JsonPropertyName("alertaPermissaoSegundos")] public float AlertaPermissaoSegundos { get; set; } = 20;
    /// <summary>Quanto a sessão que terminou continua na pílula.</summary>
    [JsonPropertyName("segundosDepoisDeTerminar")] public int SegundosDepoisDeTerminar { get; set; } = 90;
    [JsonPropertyName("alertarAoTerminar")] public bool AlertarAoTerminar { get; set; } = true;
    /// <summary>Os modelos da lista (o menu do app acrescenta os que tiver a mais).</summary>
    [JsonPropertyName("modelos")] public List<string> Modelos { get; set; } = new() { "Opus 5.5", "Fable 5.1", "Sonnet 5.5", "Haiku 5.5" };
}
