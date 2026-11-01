using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ilha.Extras;
using Ilha.Privacidade;

namespace Ilha.Gravando;

/// <summary>`Ilha.exe --testar gravando`: protocolo do OBS, estado da gravação, filtro da captura e o cliente contra um OBS de mentira no 127.0.0.1.</summary>
internal static class TestesGravando
{
    public static string Rodar()
    {
        var r = new Relatorio();
        Autenticacao(r);
        Mensagens(r);
        Estado(r);
        Captura(r);
        Textos(r);
        r.Tentar("cliente contra OBS de mentira", () => Cliente(r));
        Sondas(r);
        return r.ToString();
    }

    private static void Autenticacao(Relatorio r)
    {
        // vetores calculados à parte (hashlib do Python) com os valores de exemplo do protocol.md do obs-websocket
        const string sal = "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=", desafio = "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=";
        string a = ProtocoloObs.Autenticacao("supersecretpassword", sal, desafio);
        r.Conferir("autenticação: vetor conhecido do exemplo da documentação", a == "1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=", a);
        string b = ProtocoloObs.Autenticacao("senha çã 123", "c2FsdA==", "ZGVzYWZpbw==");
        r.Conferir("autenticação: senha com acento em UTF-8", b == "0ZpN9f78ch2HhhLOl4GYqxK82auivj2OavNxJ6Edl7A=", b);
        string c = ProtocoloObs.Autenticacao("", "c2FsdA==", "ZGVzYWZpbw==");
        r.Conferir("autenticação: senha vazia", c == "re35d3RGZkHmNA1gR5i2q0IwIDJQ+vl+uBsv7Bmlmwk=", c);

        var hello = ProtocoloObs.Ler("{\"op\":0,\"d\":{\"obsStudioVersion\":\"30.2.2\",\"obsWebSocketVersion\":\"5.5.2\",\"rpcVersion\":1," +
            "\"authentication\":{\"challenge\":\"" + desafio + "\",\"salt\":\"" + sal + "\"}}}");
        r.Conferir("Hello com senha lido", hello is { Op: 0, PedeSenha: true } && hello.Sal == sal && hello.Desafio == desafio);
        var id = JsonNode.Parse(ProtocoloObs.Identify(hello!, "supersecretpassword"))!;
        r.Conferir("Identify: op 1, rpc 1, só os eventos de saída (64) e a autenticação",
            (int?)id["op"] == 1 && (int?)id["d"]?["rpcVersion"] == 1 && (int?)id["d"]?["eventSubscriptions"] == 64
            && (string?)id["d"]?["authentication"] == "1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=", id.ToJsonString());
        var semSenha = ProtocoloObs.Ler("{\"op\":0,\"d\":{\"obsWebSocketVersion\":\"5.5.2\",\"rpcVersion\":1}}");
        var id2 = JsonNode.Parse(ProtocoloObs.Identify(semSenha!, "ignorada"))!;
        r.Conferir("Hello sem senha: Identify sem autenticação", semSenha is { PedeSenha: false } && id2["d"]?["authentication"] == null, id2.ToJsonString());
    }

    private static void Mensagens(Relatorio r)
    {
        var p = JsonNode.Parse(ProtocoloObs.Pedido("PauseRecord", "ilha-3"))!;
        r.Conferir("pedido: op 6 com tipo e id", (int?)p["op"] == 6 && (string?)p["d"]?["requestType"] == "PauseRecord" && (string?)p["d"]?["requestId"] == "ilha-3");

        var ev = ProtocoloObs.Ler("{\"op\":5,\"d\":{\"eventType\":\"RecordStateChanged\",\"eventIntent\":64,\"eventData\":" +
            "{\"outputActive\":true,\"outputState\":\"OBS_WEBSOCKET_OUTPUT_STARTED\",\"outputPath\":null}}}");
        r.Conferir("evento RecordStateChanged lido", ev is { Op: 5, Evento: "RecordStateChanged", SaidaAtiva: true } && ev.EstadoSaida == "OBS_WEBSOCKET_OUTPUT_STARTED");

        var st = ProtocoloObs.Ler("{\"op\":7,\"d\":{\"requestType\":\"GetRecordStatus\",\"requestId\":\"ilha-1\",\"requestStatus\":{\"result\":true,\"code\":100}," +
            "\"responseData\":{\"outputActive\":true,\"outputPaused\":false,\"outputTimecode\":\"00:01:05.000\",\"outputDuration\":65000,\"outputBytes\":123456}}}");
        r.Conferir("resposta do GetRecordStatus lida", st is { Op: 7, Ok: true, Codigo: 100, SaidaAtiva: true, Pausada: false } && st.TipoPedido == "GetRecordStatus"
            && st.IdPedido == "ilha-1" && st.DuracaoMs == 65000);
        var dur = ProtocoloObs.Ler("{\"op\":7,\"d\":{\"requestType\":\"GetRecordStatus\",\"requestStatus\":{\"result\":true,\"code\":100},\"responseData\":{\"outputActive\":true,\"outputPaused\":true,\"outputDuration\":1234.5}}}");
        r.Conferir("duração com fração", dur?.DuracaoMs == 1234.5 && dur.Pausada == true);
        var nao = ProtocoloObs.Ler("{\"op\":7,\"d\":{\"requestType\":\"StopRecord\",\"requestId\":\"x\",\"requestStatus\":{\"result\":false,\"code\":501,\"comment\":\"not active\"}}}");
        r.Conferir("pedido recusado lido", nao is { Ok: false, Codigo: 501 } && nao.Comentario == "not active" && nao.DuracaoMs == null);
        r.Conferir("JSON quebrado, sem op ou op de texto: null",
            ProtocoloObs.Ler("{nao é json") == null && ProtocoloObs.Ler("{\"d\":{}}") == null && ProtocoloObs.Ler("{\"op\":\"5\"}") == null && ProtocoloObs.Ler("[1,2]") == null && ProtocoloObs.Ler("") == null);
        var semD = ProtocoloObs.Ler("{\"op\":5}");
        r.Conferir("evento sem dados não quebra", semD is { Op: 5, Evento: null, EstadoSaida: null });
        var tipoErrado = ProtocoloObs.Ler("{\"op\":7,\"d\":{\"requestStatus\":{\"result\":\"sim\",\"code\":\"x\"},\"responseData\":{\"outputDuration\":\"10\"}}}");
        r.Conferir("campo com tipo errado vira vazio", tipoErrado is { Ok: false, Codigo: 0, DuracaoMs: null });

        r.Conferir("motivo de recusa: não está gravando", ProtocoloObs.MotivoRecusa("StopRecord", 501, "x") == "O OBS não está gravando");
        r.Conferir("motivo de recusa: código desconhecido com comentário", ProtocoloObs.MotivoRecusa("PauseRecord", 600, "falhou") == "O OBS não deixou pausar: falhou");
        r.Conferir("sufixo do estado", ProtocoloObs.Sufixo("OBS_WEBSOCKET_OUTPUT_PAUSED") == "PAUSED" && ProtocoloObs.Sufixo("resumed") == "RESUMED" && ProtocoloObs.Sufixo(null) == "");
    }

    private static void Estado(Relatorio r)
    {
        var t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var g = ProtocoloObs.Aplicar(GravacaoObs.Parada, "OBS_WEBSOCKET_OUTPUT_STARTING", true, t0, out bool rs0);
        r.Conferir("STARTING ainda não grava", !g.Ativa && !rs0);
        g = ProtocoloObs.Aplicar(g, "OBS_WEBSOCKET_OUTPUT_STARTED", true, t0, out _);
        r.Conferir("STARTED: gravando do zero", g.Correndo && g.Decorrido(t0.AddSeconds(10)) == TimeSpan.FromSeconds(10));
        g = ProtocoloObs.Aplicar(g, "OBS_WEBSOCKET_OUTPUT_PAUSED", true, t0.AddSeconds(10), out bool rs1);
        r.Conferir("PAUSED: o tempo para", g is { Ativa: true, Pausada: true } && !rs1 && g.Decorrido(t0.AddSeconds(50)) == TimeSpan.FromSeconds(10));
        var g2 = ProtocoloObs.Aplicar(g, "OBS_WEBSOCKET_OUTPUT_PAUSED", true, t0.AddSeconds(30), out _);
        r.Conferir("PAUSED repetido não muda", g2 == g);
        g = ProtocoloObs.Aplicar(g, "OBS_WEBSOCKET_OUTPUT_RESUMED", true, t0.AddSeconds(20), out _);
        r.Conferir("RESUMED: continua de onde parou", g.Correndo && g.Decorrido(t0.AddSeconds(25)) == TimeSpan.FromSeconds(15));
        g = ProtocoloObs.Aplicar(g, "OBS_WEBSOCKET_OUTPUT_STOPPING", true, t0.AddSeconds(30), out bool rs2);
        r.Conferir("STOPPING ainda aparece", g.Correndo && !rs2);
        g = ProtocoloObs.Aplicar(g, "OBS_WEBSOCKET_OUTPUT_STOPPED", false, t0.AddSeconds(31), out _);
        r.Conferir("STOPPED: parou", !g.Ativa && g.Decorrido(t0.AddSeconds(99)) == TimeSpan.Zero);
        var perdida = ProtocoloObs.Aplicar(GravacaoObs.Parada, "OBS_WEBSOCKET_OUTPUT_PAUSED", true, t0, out bool rs3);
        r.Conferir("pausa de gravação não vista: aparece pausada e pede o status", perdida is { Ativa: true, Pausada: true } && rs3);
        var retomada = ProtocoloObs.Aplicar(GravacaoObs.Parada, "RESUMED", true, t0, out bool rs4);
        r.Conferir("retomada de gravação não vista: aparece e pede o status", retomada.Correndo && rs4);
        ProtocoloObs.Aplicar(new GravacaoObs(true, false, TimeSpan.Zero, t0), "OBS_WEBSOCKET_OUTPUT_UNKNOWN", false, t0, out bool rs5);
        r.Conferir("estado estranho com a saída inativa pede o status", rs5);
        var st = ProtocoloObs.DoStatus(true, false, 65000, t0);
        r.Conferir("status: 65 s gravados e correndo", st.Correndo && st.Decorrido(t0.AddSeconds(5)) == TimeSpan.FromSeconds(70));
        var sp = ProtocoloObs.DoStatus(true, true, 65000, t0);
        r.Conferir("status pausado: tempo parado", sp is { Pausada: true, DesdeUtc: null } && sp.Decorrido(t0.AddHours(1)) == TimeSpan.FromSeconds(65));
        r.Conferir("status inativo, duração negativa ou NaN", !ProtocoloObs.DoStatus(false, false, 5000, t0).Ativa
            && ProtocoloObs.DoStatus(true, true, -10, t0).Acumulado == TimeSpan.Zero && ProtocoloObs.DoStatus(true, true, double.NaN, t0).Acumulado == TimeSpan.Zero);
        r.Conferir("relógio voltando não dá tempo negativo", new GravacaoObs(true, false, TimeSpan.Zero, t0).Decorrido(t0.AddSeconds(-5)) == TimeSpan.Zero);
    }

    private static void Captura(Relatorio r)
    {
        var agora = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        long Ft(double segundosAtras) => agora.AddSeconds(-segundosAtras).ToFileTimeUtc();
        r.Conferir("FILETIME do exemplo", Ft(0) == 134380080000000000L, Ft(0).ToString());
        const string P = Capturas.Programatica, B = Capturas.SemBorda;
        const string proprio = @"C:\Programas\Ilha\Ilha.exe";
        var entradas = new List<EntradaConsent>
        {
            new(P, "C:#Programas#Discord#app-1.0#Discord.exe", true, Ft(90), 0),
            new(B, "C:#Programas#Discord#app-1.0#Discord.exe", true, Ft(95), 0),
            new(B, "Microsoft.ScreenSketch_8wekyb3d8bbwe", false, Ft(0.6), 0),
            new(P, "Microsoft.ScreenSketch_8wekyb3d8bbwe", false, Ft(400), Ft(399.5)),
            new(P, "C:#Programas#obs-studio#bin#64bit#obs64.exe", true, Ft(600), 0),
            new(P, "C:#Programas#Ilha#Ilha.exe", true, Ft(30), 0),
            new(P, "C:#Programas#Morto#morto.exe", true, Ft(300), 0),
            new(P, "C:#Programas#Gravador#gravador.exe", true, Ft(10), 0),
            new(P, "C:#Programas#Chat#chat.exe", true, Ft(20), 0),
            new(B, "Fabricante.Nunca_abc", false, 0, 0),
        };
        var rodando = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Discord.exe", "obs64.exe", "Ilha.exe", "gravador.exe", "chat.exe" };
        string Pacote(string f) => f.StartsWith("Microsoft.ScreenSketch") ? "Ferramenta de Captura" : Consentimento.NomeAmigavelPacote(f);
        var l = Capturas.Decidir(entradas, agora, TimeSpan.FromSeconds(2), proprio, rodando.Contains, new[] { "chat" }, Pacote, out var rever);
        string nomes = string.Join(", ", l.Select(c => c.Nome));
        r.Conferir("captura: só os longos, sem o OBS, sem a ilha, sem o morto, sem o ignorado, mais antigo primeiro", nomes == "Discord, gravador", nomes);
        r.Conferir("captura: o mesmo app nas duas chaves conta uma vez, com o início mais antigo",
            l.Count > 0 && l[0].InicioUtc == agora.AddSeconds(-95));
        r.Conferir("captura: o print de 0,6 s espera e pede releitura em ~1,4 s",
            rever is TimeSpan rv && Math.Abs(rv.TotalSeconds - 1.4) < 0.01, rever?.TotalSeconds.ToString() ?? "null");
        var depois = Capturas.Decidir(entradas, agora.AddSeconds(1.5), TimeSpan.FromSeconds(2), proprio, rodando.Contains, Array.Empty<string>(), Pacote, out var rever2);
        r.Conferir("captura: passados os 2 s, a Ferramenta de Captura aparece",
            depois.Any(c => c.Nome == "Ferramenta de Captura") && depois.Any(c => c.Nome == "chat") && rever2 == null, string.Join(", ", depois.Select(c => c.Nome)));
        var curto = Capturas.Decidir(new[] { new EntradaConsent(P, "Microsoft.ScreenSketch_8wekyb3d8bbwe", false, Ft(5), Ft(4.6)) },
            agora, TimeSpan.FromSeconds(2), proprio, null, Array.Empty<string>(), Pacote, out var rever3);
        r.Conferir("captura: print que já terminou nunca aparece nem espera", curto.Count == 0 && rever3 == null);
        var semMinimo = Capturas.Decidir(entradas, agora, TimeSpan.Zero, proprio, null, Array.Empty<string>(), Pacote, out _);
        r.Conferir("captura: sem lista de processos o morto conta; mínimo 0 mostra tudo",
            semMinimo.Any(c => c.Nome == "morto") && semMinimo.Any(c => c.Nome == "Ferramenta de Captura") && !semMinimo.Any(c => c.Nome == "obs64"));
        var futuro = Capturas.Decidir(new[] { new EntradaConsent(P, "C:#x#y.exe", true, agora.AddSeconds(30).ToFileTimeUtc(), 0) },
            agora, TimeSpan.FromSeconds(2), proprio, null, Array.Empty<string>(), Pacote, out var rever4);
        r.Conferir("captura: início no futuro (relógio) conta como agora", futuro.Count == 0 && rever4 == TimeSpan.FromSeconds(2));
        r.Conferir("FILETIME inválido ou zero", Capturas.Instante(0) == null && Capturas.Instante(-5) == null && Capturas.Instante(long.MaxValue) == null && Capturas.Instante(Ft(0)) == agora);
        r.Conferir("exes do OBS", Capturas.EhObs("OBS64.exe") && Capturas.EhObs("obs32.exe") && !Capturas.EhObs("obsidian.exe"));
        var a1 = new List<CapturaEmUso> { new("A", agora) };
        r.Conferir("listas iguais e diferentes", Capturas.Iguais(a1, new List<CapturaEmUso> { new("A", agora) }) && !Capturas.Iguais(a1, new List<CapturaEmUso>()));
        r.Conferir("pid do OBS na lista de processos", ModuloGravando.PidDoObs(new (uint, string)[] { (4, "System"), (900, "obs64.exe") }) == 900
            && ModuloGravando.PidDoObs(new (uint, string)[] { (4, "System") }) == null);
    }

    private static void Textos(Relatorio r)
    {
        r.Conferir("cronômetro", AtividadeGravando.Cronometro(TimeSpan.FromSeconds(75)) == "1:15" && AtividadeGravando.Cronometro(TimeSpan.FromSeconds(3725)) == "1:02:05"
            && AtividadeGravando.Cronometro(TimeSpan.FromSeconds(-3)) == "0:00");
        var t = DateTime.UtcNow;
        r.Conferir("nome dos apps no título", AtividadeGravando.Apps(new[] { new CapturaEmUso("Discord", t) }) == "Discord"
            && AtividadeGravando.Apps(new[] { new CapturaEmUso("Discord", t), new CapturaEmUso("X", t), new CapturaEmUso("Y", t) }) == "Discord e mais 2"
            && AtividadeGravando.Apps(Array.Empty<CapturaEmUso>()) == "");
        bool todos = Enum.GetValues<EstadoObs>().All(e => AtividadeGravando.TextoObs(e, 4455).Length > 0);
        r.Conferir("todo estado do OBS tem texto", todos);
        r.Conferir("dica do servidor desligado traz a porta", AtividadeGravando.DicaObs(EstadoObs.SemServidor, 4460).Contains("4460"));
        var cfg = new ConfigGravando();
        r.Conferir("config padrão", cfg.Obs && cfg.ObsPorta == 4455 && cfg.CapturaDeTela && cfg.MinimoSegundos == 2 && cfg.Ignorar.Count == 0);
    }

    // ------------------------------------------------------------------ cliente de verdade, servidor de mentira

    private static void Cliente(Relatorio r)
    {
        const string senha = "s3nh@ Ç";
        // senha certa: conecta, pede o status sozinho, recebe evento, manda pedido e lê a recusa
        using (var obs = new ObsDeMentira(senha))
        using (var cli = new ClienteObs())
        {
            var estados = new BlockingCollection<EstadoObs>();
            var msgs = new BlockingCollection<MensagemObs>();
            cli.Mudou += estados.Add;
            cli.Mensagem += msgs.Add;
            cli.Conectar(obs.Porta, senha);
            bool conectou = Esperar(estados, e => e == EstadoObs.Conectado, out var visto);
            r.Conferir("cliente: conecta com a senha certa", conectou, string.Join(">", visto));
            var status = Pegar(msgs, m => m.TipoPedido == "GetRecordStatus");
            r.Conferir("cliente: pede o GetRecordStatus ao conectar", status is { Ok: true, SaidaAtiva: true, DuracaoMs: 65000 });
            var ev = Pegar(msgs, m => m.Op == ProtocoloObs.OP_EVENT);
            r.Conferir("cliente: recebe o RecordStateChanged", ev?.EstadoSaida == "OBS_WEBSOCKET_OUTPUT_PAUSED");
            bool mandou = cli.Pedir("StopRecord");
            var rec = Pegar(msgs, m => m.TipoPedido == "StopRecord");
            r.Conferir("cliente: manda o StopRecord e lê a recusa", mandou && rec is { Ok: false, Codigo: 501 });
            var ident = obs.Recebidas.FirstOrDefault(x => x.Contains("\"op\":1"));
            var no = ident == null ? null : JsonNode.Parse(ident);
            r.Conferir("servidor viu Identify com 64 e o subprotocolo json", (int?)no?["d"]?["eventSubscriptions"] == 64 && obs.Protocolo == "obswebsocket.json",
                $"{obs.Protocolo}");
            cli.Desconectar();
            r.Conferir("cliente: desconectar volta a fechado sem estado atrasado", cli.Estado == EstadoObs.Fechado && !cli.Pedir("StopRecord"));
        }

        using (var obs = new ObsDeMentira("certa"))
        using (var cli = new ClienteObs())
        {
            var estados = new BlockingCollection<EstadoObs>();
            cli.Mudou += estados.Add;
            cli.Conectar(obs.Porta, "errada");
            bool ok = Esperar(estados, e => e == EstadoObs.SenhaRecusada, out var visto);
            r.Conferir("cliente: senha errada vira 'senha recusada' (fechou com 4009)", ok && !obs.Identificou, string.Join(">", visto));
        }

        using (var obs = new ObsDeMentira("certa"))
        using (var cli = new ClienteObs())
        {
            var estados = new BlockingCollection<EstadoObs>();
            cli.Mudou += estados.Add;
            cli.Conectar(obs.Porta, null);
            bool ok = Esperar(estados, e => e == EstadoObs.PedeSenha, out var visto);
            r.Conferir("cliente: sem senha guardada e OBS pedindo vira 'pede a senha'", ok, string.Join(">", visto));
        }

        using (var obs = new ObsDeMentira(null))
        using (var cli = new ClienteObs())
        {
            var estados = new BlockingCollection<EstadoObs>();
            cli.Mudou += estados.Add;
            cli.Conectar(obs.Porta, null);
            bool ok = Esperar(estados, e => e == EstadoObs.Conectado, out var visto);
            r.Conferir("cliente: OBS sem senha conecta sem senha", ok && obs.Identificou, string.Join(">", visto));
        }

        int livre;
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        livre = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using (var cli = new ClienteObs())
        {
            var estados = new BlockingCollection<EstadoObs>();
            cli.Mudou += estados.Add;
            cli.Conectar(livre, null);
            bool ok = Esperar(estados, e => e == EstadoObs.SemServidor, out var visto);
            r.Conferir("cliente: porta fechada vira 'sem servidor'", ok, string.Join(">", visto));
        }
    }

    private static bool Esperar(BlockingCollection<EstadoObs> fila, Func<EstadoObs, bool> alvo, out List<EstadoObs> visto)
    {
        visto = new();
        var fim = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < fim)
        {
            if (!fila.TryTake(out var e, 250)) continue;
            visto.Add(e);
            if (alvo(e)) return true;
        }
        return false;
    }

    private static MensagemObs? Pegar(BlockingCollection<MensagemObs> fila, Func<MensagemObs, bool> alvo)
    {
        var fim = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < fim)
            if (fila.TryTake(out var m, 250) && alvo(m)) return m;
        return null;
    }

    private static void Sondas(Relatorio r)
    {
        r.Tentar("sonda do ConsentStore", () =>
        {
            var todas = DetectorDeCaptura.LerEntradas();
            r.Sonda($"captura: {todas.Count} entradas ({todas.Count(e => e.Capacidade == Capturas.Programatica)} programáticas, {todas.Count(e => e.Capacidade == Capturas.SemBorda)} sem borda)");
            var agora = DetectorDeCaptura.Estado(Array.Empty<string>(), TimeSpan.FromSeconds(2), Environment.ProcessPath, out var rever);
            r.Sonda($"capturando agora: [{string.Join(", ", agora.Select(c => c.Nome))}]" + (rever != null ? " (um uso curto esperando)" : ""));
            foreach (var cap in Capturas.Capacidades)
            {
                var v = VigiaDeChave.Abrir(Consentimento.Raiz + "\\" + cap, () => { });
                r.Sonda($"notificação em {cap}: " + (v == null ? "chave ausente ou erro" : v.Valida ? "armada" : $"erro {v.UltimoErro}"));
                v?.Dispose();
            }
            r.Sonda("OBS aberto agora: " + (ModuloGravando.PidDoObs(Processos.Listar()) is uint pid ? "sim (pid " + pid + ")" : "não"));
        });
    }
}

/// <summary>
/// Um servidor WebSocket mínimo no 127.0.0.1 (porta sorteada) que imita o obs-websocket v5 para o autoteste: Hello
/// (com senha ou sem), confere o Identify e fecha com 4009 se a autenticação não bater, responde ao GetRecordStatus
/// com 65 s gravados, manda um evento de pausa e recusa o StopRecord com 501. Uma conexão só, tudo com tempo-limite.
/// </summary>
internal sealed class ObsDeMentira : IDisposable
{
    public const string Sal = "c2FsdA==", Desafio = "ZGVzYWZpbw==";
    private readonly TcpListener _l = new(IPAddress.Loopback, 0);
    private readonly string? _senha;
    private readonly CancellationTokenSource _parar = new();
    private readonly Task _tarefa;
    private TcpClient? _tcp;

    public int Porta { get; }
    public ConcurrentQueue<string> Recebidas { get; } = new();
    public string? Protocolo { get; private set; }
    public bool Identificou { get; private set; }

    public ObsDeMentira(string? senha)
    {
        _senha = senha;
        _l.Start();
        Porta = ((IPEndPoint)_l.LocalEndpoint).Port;
        _tarefa = Task.Run(Servir);
    }

    private async Task Servir()
    {
        try
        {
            _tcp = await _l.AcceptTcpClientAsync(_parar.Token).ConfigureAwait(false);
            var s = _tcp.GetStream();
            s.ReadTimeout = 6000;
            s.WriteTimeout = 6000;
            string pedido = LerCabecalho(s);
            string chave = Cabecalho(pedido, "Sec-WebSocket-Key");
            Protocolo = Cabecalho(pedido, "Sec-WebSocket-Protocol");
            string aceite = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(chave + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var resposta = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + aceite + "\r\nSec-WebSocket-Protocol: obswebsocket.json\r\n\r\n");
            s.Write(resposta);
            string auth = _senha == null ? "" : ",\"authentication\":{\"challenge\":\"" + Desafio + "\",\"salt\":\"" + Sal + "\"}";
            Quadro(s, "{\"op\":0,\"d\":{\"obsWebSocketVersion\":\"5.5.2\",\"rpcVersion\":1" + auth + "}}");
            string? identify = LerQuadro(s);
            if (identify == null) return;
            Recebidas.Enqueue(identify);
            var d = JsonNode.Parse(identify)?["d"];
            if (_senha != null && (string?)d?["authentication"] != ProtocoloObs.Autenticacao(_senha, Sal, Desafio))
            {
                Fechar(s, ProtocoloObs.FECHOU_SENHA_ERRADA);
                return;
            }
            Identificou = true;
            Quadro(s, "{\"op\":2,\"d\":{\"negotiatedRpcVersion\":1}}");
            while (!_parar.IsCancellationRequested)
            {
                string? m = LerQuadro(s);
                if (m == null) return;
                Recebidas.Enqueue(m);
                var dd = JsonNode.Parse(m)?["d"];
                string tipo = (string?)dd?["requestType"] ?? "", id = (string?)dd?["requestId"] ?? "";
                if (tipo == "GetRecordStatus")
                {
                    Quadro(s, "{\"op\":7,\"d\":{\"requestType\":\"GetRecordStatus\",\"requestId\":\"" + id + "\",\"requestStatus\":{\"result\":true,\"code\":100}," +
                        "\"responseData\":{\"outputActive\":true,\"outputPaused\":false,\"outputTimecode\":\"00:01:05.000\",\"outputDuration\":65000,\"outputBytes\":1}}}");
                    Quadro(s, "{\"op\":5,\"d\":{\"eventType\":\"RecordStateChanged\",\"eventIntent\":64,\"eventData\":{\"outputActive\":true,\"outputState\":\"OBS_WEBSOCKET_OUTPUT_PAUSED\"}}}");
                }
                else
                    Quadro(s, "{\"op\":7,\"d\":{\"requestType\":\"" + tipo + "\",\"requestId\":\"" + id + "\",\"requestStatus\":{\"result\":false,\"code\":501,\"comment\":\"not active\"}}}");
            }
        }
        catch (Exception) { }
    }

    private static string LerCabecalho(Stream s)
    {
        var b = new List<byte>();
        while (b.Count < 16384)
        {
            int x = s.ReadByte();
            if (x < 0) break;
            b.Add((byte)x);
            int n = b.Count;
            if (n >= 4 && b[n - 4] == '\r' && b[n - 3] == '\n' && b[n - 2] == '\r' && b[n - 1] == '\n') break;
        }
        return Encoding.ASCII.GetString(b.ToArray());
    }

    private static string Cabecalho(string pedido, string nome)
    {
        foreach (var linha in pedido.Split("\r\n"))
        {
            int i = linha.IndexOf(':');
            if (i > 0 && linha[..i].Trim().Equals(nome, StringComparison.OrdinalIgnoreCase)) return linha[(i + 1)..].Trim();
        }
        return "";
    }

    private static byte[] LerN(Stream s, long n)
    {
        var b = new byte[n];
        int lido = 0;
        while (lido < n)
        {
            int k = s.Read(b, lido, (int)(n - lido));
            if (k <= 0) throw new EndOfStreamException();
            lido += k;
        }
        return b;
    }

    /// <summary>Um quadro de texto do cliente (mascarado). Fechamento: null; ping: responde e continua.</summary>
    private static string? LerQuadro(Stream s)
    {
        while (true)
        {
            int b0 = s.ReadByte(), b1 = s.ReadByte();
            if (b0 < 0 || b1 < 0) return null;
            int op = b0 & 0x0F;
            long n = b1 & 0x7F;
            if (n == 126) { var e = LerN(s, 2); n = e[0] << 8 | e[1]; }
            else if (n == 127) { var e = LerN(s, 8); n = 0; foreach (var x in e) n = n << 8 | x; }
            var mascara = (b1 & 0x80) != 0 ? LerN(s, 4) : null;
            var dados = LerN(s, n);
            if (mascara != null) for (int i = 0; i < dados.Length; i++) dados[i] ^= mascara[i % 4];
            if (op == 8) return null;
            if (op == 9) { Enviar(s, 0x8A, dados); continue; }
            if (op == 1) return Encoding.UTF8.GetString(dados);
        }
    }

    private static void Enviar(Stream s, byte cabeca, byte[] dados)
    {
        var q = new List<byte> { cabeca };
        if (dados.Length < 126) q.Add((byte)dados.Length);
        else { q.Add(126); q.Add((byte)(dados.Length >> 8)); q.Add((byte)dados.Length); }
        q.AddRange(dados);
        s.Write(q.ToArray());
        s.Flush();
    }

    private static void Quadro(Stream s, string texto) => Enviar(s, 0x81, Encoding.UTF8.GetBytes(texto));

    private static void Fechar(Stream s, int codigo) => Enviar(s, 0x88, new[] { (byte)(codigo >> 8), (byte)codigo });

    public void Dispose()
    {
        _parar.Cancel();
        try { _tcp?.Close(); } catch { }
        try { _l.Stop(); } catch { }
        try { _tarefa.Wait(2000); } catch { }
    }
}
