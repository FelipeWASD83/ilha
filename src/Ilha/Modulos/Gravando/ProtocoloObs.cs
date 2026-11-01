using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ilha.Gravando;

/// <summary>Uma mensagem do obs-websocket v5 já lida: só os campos que a ilha usa.</summary>
internal sealed class MensagemObs
{
    public int Op = -1;
    // Hello (op 0)
    public bool PedeSenha;
    public string? Desafio, Sal;
    public int Rpc = 1;
    // Event (op 5)
    public string? Evento;
    public string? EstadoSaida;
    public bool? SaidaAtiva;
    // RequestResponse (op 7)
    public string? TipoPedido, IdPedido, Comentario;
    public bool Ok;
    public int Codigo;
    public bool? Pausada;
    public double? DuracaoMs;
}

/// <summary>Uma gravação do OBS como a ilha entende: ativa, pausada e quanto tempo já foi gravado.</summary>
internal readonly record struct GravacaoObs(bool Ativa, bool Pausada, TimeSpan Acumulado, DateTime? DesdeUtc)
{
    public static readonly GravacaoObs Parada = new(false, false, TimeSpan.Zero, null);

    public bool Correndo => Ativa && !Pausada;

    public TimeSpan Decorrido(DateTime agoraUtc)
    {
        var t = Acumulado + (DesdeUtc is DateTime d && agoraUtc > d ? agoraUtc - d : TimeSpan.Zero);
        return t < TimeSpan.Zero ? TimeSpan.Zero : t;
    }
}

/// <summary>
/// Lógica pura do obs-websocket v5 (o servidor que vem dentro do OBS 28 em diante): montar o Identify com a
/// autenticação, montar pedidos, ler as mensagens e andar o estado da gravação pelos eventos.
/// </summary>
internal static class ProtocoloObs
{
    public const int OP_HELLO = 0, OP_IDENTIFY = 1, OP_IDENTIFIED = 2, OP_EVENT = 5, OP_REQUEST = 6, OP_RESPONSE = 7;
    // EventSubscription.Outputs (1 << 6): RecordStateChanged e companhia
    public const int ASSINATURA_SAIDAS = 64;
    public const int FECHOU_SENHA_ERRADA = 4009;

    /// <summary>base64(sha256(base64(sha256(senha + sal)) + desafio)), como manda o protocolo.</summary>
    public static string Autenticacao(string senha, string sal, string desafio)
    {
        string segredo = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(senha + sal)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(segredo + desafio)));
    }

    public static string Identify(MensagemObs hello, string? senha)
    {
        var d = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = ASSINATURA_SAIDAS };
        if (hello.PedeSenha && senha != null && hello.Sal != null && hello.Desafio != null)
            d["authentication"] = Autenticacao(senha, hello.Sal, hello.Desafio);
        return new JsonObject { ["op"] = OP_IDENTIFY, ["d"] = d }.ToJsonString();
    }

    public static string Pedido(string tipo, string id) =>
        new JsonObject { ["op"] = OP_REQUEST, ["d"] = new JsonObject { ["requestType"] = tipo, ["requestId"] = id } }.ToJsonString();

    /// <summary>Lê uma mensagem do servidor. JSON quebrado ou sem "op" devolve null.</summary>
    public static MensagemObs? Ler(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject raiz) return null;
            if (raiz["op"] is not JsonValue opv || !opv.TryGetValue<int>(out int op)) return null;
            var m = new MensagemObs { Op = op };
            var d = raiz["d"] as JsonObject;
            switch (op)
            {
                case OP_HELLO:
                    if (d?["authentication"] is JsonObject a)
                    {
                        m.PedeSenha = true;
                        m.Desafio = Texto(a["challenge"]);
                        m.Sal = Texto(a["salt"]);
                    }
                    if (d?["rpcVersion"] is JsonValue rv && rv.TryGetValue<int>(out int rpc)) m.Rpc = rpc;
                    break;
                case OP_EVENT:
                    m.Evento = Texto(d?["eventType"]);
                    var ed = d?["eventData"] as JsonObject;
                    m.EstadoSaida = Texto(ed?["outputState"]);
                    m.SaidaAtiva = Logico(ed?["outputActive"]);
                    break;
                case OP_RESPONSE:
                    m.TipoPedido = Texto(d?["requestType"]);
                    m.IdPedido = Texto(d?["requestId"]);
                    var st = d?["requestStatus"] as JsonObject;
                    m.Ok = Logico(st?["result"]) == true;
                    if (st?["code"] is JsonValue cv && cv.TryGetValue<int>(out int c)) m.Codigo = c;
                    m.Comentario = Texto(st?["comment"]);
                    var rd = d?["responseData"] as JsonObject;
                    m.SaidaAtiva = Logico(rd?["outputActive"]);
                    m.Pausada = Logico(rd?["outputPaused"]);
                    m.DuracaoMs = Numero(rd?["outputDuration"]);
                    break;
            }
            return m;
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }

    private static string? Texto(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool? Logico(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static double? Numero(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        return null;
    }

    /// <summary>"OBS_WEBSOCKET_OUTPUT_STARTED" ou "STARTED" viram "STARTED" (aceita os dois jeitos).</summary>
    public static string Sufixo(string? estado)
    {
        if (string.IsNullOrEmpty(estado)) return "";
        const string prefixo = "OBS_WEBSOCKET_OUTPUT_";
        return (estado.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase) ? estado[prefixo.Length..] : estado).ToUpperInvariant();
    }

    /// <summary>
    /// Anda o estado pelo RecordStateChanged. `resincronizar` = o evento não bate com o que se sabia (pausou uma
    /// gravação que a ilha não viu começar): quem chama pede o GetRecordStatus.
    /// </summary>
    public static GravacaoObs Aplicar(GravacaoObs g, string? estadoSaida, bool? saidaAtiva, DateTime agoraUtc, out bool resincronizar)
    {
        resincronizar = false;
        switch (Sufixo(estadoSaida))
        {
            case "STARTED":
                return new GravacaoObs(true, false, TimeSpan.Zero, agoraUtc);
            case "STOPPED":
                return GravacaoObs.Parada;
            case "PAUSED":
                if (!g.Ativa) { resincronizar = true; return new GravacaoObs(true, true, TimeSpan.Zero, null); }
                return g.Pausada ? g : new GravacaoObs(true, true, g.Decorrido(agoraUtc), null);
            case "RESUMED":
                if (!g.Ativa) { resincronizar = true; return new GravacaoObs(true, false, TimeSpan.Zero, agoraUtc); }
                return g.Pausada ? new GravacaoObs(true, false, g.Acumulado, agoraUtc) : g;
            default:
                // STARTING, STOPPING, RECONNECTING...: nada muda; "inativa" fora do STOPPED é estranho, confere
                if (saidaAtiva == false && g.Ativa && Sufixo(estadoSaida) != "STOPPING") resincronizar = true;
                return g;
        }
    }

    /// <summary>A resposta do GetRecordStatus vira o estado inteiro (outputDuration em ms, parado durante a pausa).</summary>
    public static GravacaoObs DoStatus(bool ativa, bool pausada, double duracaoMs, DateTime agoraUtc)
    {
        if (!ativa) return GravacaoObs.Parada;
        var acumulado = TimeSpan.FromMilliseconds(Math.Max(0, double.IsFinite(duracaoMs) ? duracaoMs : 0));
        return new GravacaoObs(true, pausada, acumulado, pausada ? null : agoraUtc);
    }

    /// <summary>Texto curto para um pedido recusado ("não está gravando" em vez do código).</summary>
    public static string MotivoRecusa(string? tipo, int codigo, string? comentario)
    {
        // 500/501: OutputRunning/OutputNotRunning; 502/503: OutputPaused/OutputNotPaused
        string? m = codigo switch
        {
            501 => "O OBS não está gravando",
            500 => "O OBS já está gravando",
            502 => "A gravação já está pausada",
            503 => "A gravação não está pausada",
            _ => null,
        };
        if (m != null) return m;
        string nome = tipo switch { "PauseRecord" => "pausar", "ResumeRecord" => "retomar", "StopRecord" => "parar", _ => tipo ?? "pedido" };
        return string.IsNullOrWhiteSpace(comentario) ? $"O OBS não deixou {nome} (código {codigo})" : $"O OBS não deixou {nome}: {comentario}";
    }
}
