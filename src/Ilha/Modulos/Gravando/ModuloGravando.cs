using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Ilha.Gravando;

/// <summary>
/// Indicador de gravação de tela, por dois caminhos:
/// - OBS pelo obs-websocket v5 (vem dentro do OBS 28+): só tenta conectar com um obs64.exe na lista de processos
///   (Toolhelp, sem handle), conferida a cada 20 s enquanto não há conexão e na hora em que o OBS vem para a frente;
///   conectado, só escuta eventos. A senha fica no Gerenciador de Credenciais ("Ilha/obs"), digitada no cartão.
/// - Captura de tela do Windows pelo ConsentStore (graphicsCapture*), avisada pelo registro. É heurística: acende
///   também com compartilhamento de tela e print; usos curtos são ignorados e o cartão diz "capturando a tela".
/// A Xbox Game Bar fica de fora: a única API de estado de gravação dela só funciona de dentro de um widget dela.
/// Escondido no modo competitivo (a atividade não é permitida lá e a procura pelo OBS para).
/// </summary>
[Modulo("gravando", "Gravação", English = "Recording")]
internal sealed class ModuloGravando : Modulo
{
    public const string CREDENCIAL = "obs";
    private const int PROCURA_MS = 20_000;

    private ConfigGravando _cfg = new();
    private AtividadeGravando? _atv;
    private ClienteObs? _obs;
    private DetectorDeCaptura? _cap;
    private readonly System.Windows.Forms.Timer _procurar = new() { Interval = PROCURA_MS };
    private EstadoObs _estado = EstadoObs.Fechado;
    private GravacaoObs _gravacao = GravacaoObs.Parada;
    private List<CapturaEmUso> _capturas = new();
    private uint? _pidObs;
    private int _portaUsada;
    private bool _competitivo, _parado;
    private EstadoObs? _registrado;

    public override Type? TipoConfig => typeof(ConfigGravando);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigGravando>(cfg);
        var atv = new AtividadeGravando(S, Pedir, GuardarSenha);
        _atv = atv;
        atv.MenuMudou += S.Menu.Redesenhar;
        S.Ilha.Adicionar(atv);
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "gravando",
            Nome = Lang.T("Gravação", "Recording"),
            Abrir = atv.Abrir,
            Detalhe = atv.DetalheDoMenu,
            Icone = (dc, palco, r) => IconeDoMenu(dc, r, atv.AcesoNoMenu),
            Ordem = 55,
        });
        _competitivo = S.ContextoAtual().Competitivo;
        _procurar.Tick += (_, _) => Procurar();
        Aplicar();
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigGravando>(cfg);
        Aplicar();
    }

    private static string[] Lista(List<string>? l) => (l ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
    private TimeSpan Minimo => TimeSpan.FromSeconds(Math.Clamp(_cfg.MinimoSegundos, 0, 60));
    private int Porta => _cfg.ObsPorta is > 0 and < 65536 ? _cfg.ObsPorta : 4455;

    private void Aplicar()
    {
        if (_parado || _atv == null) return;
        _atv.Porta = Porta;
        if (_cfg.Obs)
        {
            if (_obs == null)
            {
                _obs = new ClienteObs();
                _obs.Mudou += e => S.NaInterface(() => EstadoMudou(e));
                _obs.Mensagem += m => S.NaInterface(() => Mensagem(m));
                _estado = EstadoObs.Fechado;
            }
            else if (_portaUsada != Porta && _obs.Estado != EstadoObs.Fechado)
            {
                _obs.Desconectar();
                _pidObs = null;
            }
            _portaUsada = Porta;
            Procurar();
        }
        else
        {
            _procurar.Stop();
            _obs?.Dispose();
            _obs = null;
            _estado = EstadoObs.Desligado;
            _gravacao = GravacaoObs.Parada;
        }

        if (_cfg.CapturaDeTela && _cap == null)
        {
            _cap = new DetectorDeCaptura(Lista(_cfg.Ignorar), Minimo, l => S.NaInterface(() =>
            {
                if (_parado || _cap == null) return;
                _capturas = l;
                Publicar();
            }));
            _cap.Iniciar();
        }
        else if (!_cfg.CapturaDeTela && _cap != null)
        {
            _cap.Dispose();
            _cap = null;
            _capturas = new();
        }
        else _cap?.Ajustar(Lista(_cfg.Ignorar), Minimo);
        Publicar();
    }

    private void Publicar() =>
        _atv?.Atualizar(_cfg.Obs ? _estado : EstadoObs.Desligado, _cfg.Obs ? _gravacao : GravacaoObs.Parada,
            _cfg.CapturaDeTela ? _capturas : new(), _cfg.Obs, _cfg.CapturaDeTela);

    // ------------------------------------------------------------------ OBS

    /// <summary>O pid do OBS aberto, pela lista Toolhelp (nenhum handle aberto); null = fechado.</summary>
    public static uint? PidDoObs(IEnumerable<(uint Pid, string Exe)> processos)
    {
        foreach (var (pid, exe) in processos)
            if (Capturas.EhObs(exe)) return pid;
        return null;
    }

    /// <summary>
    /// Sem conexão: confere se o OBS está aberto e, se estiver, conecta. Senha pedida ou recusada não tenta de novo
    /// com o mesmo OBS (espera a senha nova ou o OBS reabrir). O relógio lento só roda sem conexão e fora do competitivo.
    /// </summary>
    private void Procurar()
    {
        if (_parado || !_cfg.Obs || _obs == null) { _procurar.Stop(); return; }
        if (_obs.Ocupado) { _procurar.Stop(); return; }
        if (_competitivo) { _procurar.Stop(); return; }
        if (!_procurar.Enabled) _procurar.Start();
        uint? pid;
        try { pid = PidDoObs(global::Ilha.Extras.Processos.Listar()); }
        catch (Exception e) { Log.Escrever("gravando: lista de processos: " + e.Message); return; }
        if (pid == null)
        {
            _pidObs = null;
            if (_estado != EstadoObs.Fechado) { _estado = EstadoObs.Fechado; _gravacao = GravacaoObs.Parada; Publicar(); }
            return;
        }
        if (_estado is EstadoObs.PedeSenha or EstadoObs.SenhaRecusada && pid == _pidObs) return;
        _pidObs = pid;
        _obs.Conectar(Porta, Credenciais.Ler(CREDENCIAL));
    }

    private void EstadoMudou(EstadoObs e)
    {
        if (_parado || _obs == null) return;
        _estado = e;
        // sem conexão, não se sabe mais nada da gravação
        if (e != EstadoObs.Conectado) _gravacao = GravacaoObs.Parada;
        if (!_obs.Ocupado && !_competitivo && !_procurar.Enabled) _procurar.Start();
        // sem servidor, a tentativa se repete a cada 20 s: só registra quando o estado muda de verdade
        if (e != EstadoObs.Conectando && e != _registrado) { _registrado = e; Log.Escrever("gravando: OBS " + e); }
        Publicar();
    }

    private void Mensagem(MensagemObs m)
    {
        if (_parado || _obs == null) return;
        var agora = DateTime.UtcNow;
        if (m.Op == ProtocoloObs.OP_EVENT)
        {
            if (m.Evento != "RecordStateChanged") return;
            _gravacao = ProtocoloObs.Aplicar(_gravacao, m.EstadoSaida, m.SaidaAtiva, agora, out bool resincronizar);
            if (resincronizar) _obs.Pedir("GetRecordStatus");
            Publicar();
            return;
        }
        if (m.Op != ProtocoloObs.OP_RESPONSE) return;
        if (m.TipoPedido == "GetRecordStatus")
        {
            if (m.Ok) _gravacao = ProtocoloObs.DoStatus(m.SaidaAtiva ?? false, m.Pausada ?? false, m.DuracaoMs ?? 0, agora);
            Publicar();
        }
        else if (!m.Ok && m.TipoPedido is "PauseRecord" or "ResumeRecord" or "StopRecord")
            _atv?.Avisar(ProtocoloObs.MotivoRecusa(m.TipoPedido, m.Codigo, m.Comentario));
    }

    private bool Pedir(string tipo) => _obs != null && _obs.Pedir(tipo);

    /// <summary>A senha digitada no cartão: vazio apaga a guardada. Devolve o erro para mostrar, ou null.</summary>
    private string? GuardarSenha(string texto)
    {
        if (texto.Length == 0) Credenciais.Apagar(CREDENCIAL);
        else if (!Credenciais.Gravar(CREDENCIAL, texto)) return Lang.T("Não consegui guardar a senha no Gerenciador de Credenciais do Windows.", "Couldn't save the password in Windows Credential Manager.");
        if (_obs != null)
        {
            if (_obs.Estado != EstadoObs.Fechado) _obs.Desconectar();
            _estado = EstadoObs.Fechado;
            _pidObs = null;
            Procurar();
        }
        Publicar();
        return null;
    }

    public override void Contexto(Contexto ctx)
    {
        bool estava = _competitivo;
        _competitivo = ctx.Competitivo;
        if (_competitivo) { _procurar.Stop(); return; }
        // o OBS veio para a frente (acabou de abrir) ou o jogo competitivo saiu: confere já
        if (_obs != null && !_obs.Ocupado && (estava || Capturas.EhObs(ctx.Processo))) Procurar();
    }

    public override void Parar()
    {
        _parado = true;
        _procurar.Stop();
        _procurar.Dispose();
        _obs?.Dispose();
        _obs = null;
        _cap?.Dispose();
        _cap = null;
        _atv?.Parar();
    }

    /// <summary>Anel com a bolinha de gravar no meio (vermelha acesa gravando).</summary>
    public static void IconeDoMenu(ID2D1DeviceContext dc, System.Drawing.RectangleF r, bool aceso)
    {
        var c = new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2);
        float raio = MathF.Min(r.Width, r.Height) / 2 - 1.5f;
        using (var b = dc.CreateSolidColorBrush(Cor.Branco)) dc.DrawEllipse(new Ellipse(c, raio, raio), b, 2f);
        var v = Cor.Vermelho;
        Icone.Circulo(dc, c, raio * 0.52f, aceso ? v : new Color4(v.R, v.G, v.B, 0.7f));
    }

    public override string Autoteste() => TestesGravando.Rodar();
}

public sealed class ConfigGravando
{
    /// <summary>Acompanhar o OBS pelo obs-websocket (servidor ligado no OBS em Ferramentas).</summary>
    [Label("Acompanhar o OBS", "Follow OBS")]
    [Help("Pelo obs-websocket (servidor ligado no OBS em Ferramentas)", "Through obs-websocket (server turned on in OBS under Tools)")]
    [JsonPropertyName("obs")] public bool Obs { get; set; } = true;
    [Label("Porta do WebSocket do OBS", "OBS WebSocket port")]
    [JsonPropertyName("obsPorta")] public int ObsPorta { get; set; } = 4455;
    /// <summary>Mostrar quando um app captura a tela pelo Windows (Ferramenta de Captura, compartilhamento...).</summary>
    [Label("Mostrar captura de tela do Windows", "Show Windows screen capture")]
    [Help("Quando um app captura a tela pelo Windows (Ferramenta de Captura, compartilhamento...)", "When an app captures the screen through Windows (Snipping Tool, screen sharing...)")]
    [JsonPropertyName("capturaDeTela")] public bool CapturaDeTela { get; set; } = true;
    /// <summary>Captura mais curta que isso não aparece (print).</summary>
    [Label("Segundos mínimos de captura", "Minimum capture seconds")]
    [Help("Captura mais curta que isso não aparece (print)", "Shorter captures don't show up (screenshots)")]
    [JsonPropertyName("minimoSegundos")] public double MinimoSegundos { get; set; } = 2;
    /// <summary>Apps que nunca acendem a captura: nome do .exe, nome que aparece ou família do pacote.</summary>
    [Label("Apps ignorados", "Ignored apps")]
    [Help("Nunca acendem a captura: nome do .exe, nome que aparece ou família do pacote", "Never light up the capture: .exe name, display name or package family")]
    [JsonPropertyName("ignorar")] public List<string> Ignorar { get; set; } = new();
}
