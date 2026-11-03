using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ilha.Network;

/// <summary>
/// Ping during competitive games (3.9.0): while a competitive game is in front (and 30 s after it leaves), one echo a
/// second to the router and to an internet host (`alvo`, 1.1.1.1 by default). When the connection turns bad the score
/// pill shows a small mark ("ping 180 · perda 3%", "rede local · ..." when the router itself is the slow one). It
/// measures his connection, not the game server (see NetQuality).
/// </summary>
[Modulo("ping", "Ping", English = "Ping")]
internal sealed class PingModule : Modulo
{
    private ConfigPing _cfg = new();
    private readonly List<int?> _host = new(), _router = new();
    private readonly object _lock = new();
    private Timer? _timer;
    private readonly System.Windows.Forms.Timer _stopLater = new() { Interval = 30_000 };
    private IPAddress? _gateway;
    private bool _stopped, _busy;

    public override Type? TipoConfig => typeof(ConfigPing);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigPing>(cfg);
        NetQuality.LossLimit = _cfg.PerdaPct;
        _stopLater.Tick += (_, _) => { _stopLater.Stop(); StopPinging(); };
        Contexto(S.ContextoAtual());
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigPing>(cfg);
        NetQuality.LossLimit = _cfg.PerdaPct;
    }

    public override void Contexto(Contexto ctx)
    {
        if (_stopped) return;
        if (ctx.Jogo.Length > 0) { _stopLater.Stop(); StartPinging(); }
        else if (_timer != null && !_stopLater.Enabled) _stopLater.Start();
    }

    private void StartPinging()
    {
        if (_timer != null) return;
        lock (_lock) { _host.Clear(); _router.Clear(); }
        _gateway = Gateway();
        Log.Escrever($"ping: medindo ({_cfg.Alvo}, roteador {(_gateway?.ToString() ?? "não achado")})");
        _timer = new Timer(_ => _ = Tick(), null, 0, 1000);
    }

    private void StopPinging()
    {
        _timer?.Dispose();
        _timer = null;
        S.NaInterface(() => NetQuality.Publish(NetReading.None));
    }

    public override void Parar()
    {
        _stopped = true;
        _stopLater.Stop();
        _stopLater.Dispose();
        _timer?.Dispose();
        _timer = null;
        NetQuality.Publish(NetReading.None);
    }

    private async Task Tick()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var host = Echo(_cfg.Alvo);
            var router = _gateway != null && _cfg.Roteador ? Echo(_gateway.ToString()) : Task.FromResult<int?>(null);
            int? h = await host.ConfigureAwait(false), r = await router.ConfigureAwait(false);
            NetReading reading;
            lock (_lock)
            {
                Push(_host, h);
                if (_gateway != null && _cfg.Roteador) Push(_router, r);
                reading = NetQuality.Evaluate(_host, _router, Math.Max(20, _cfg.LimiteMs), _cfg.PerdaPct, NetQuality.Current.Bad);
            }
            if (reading.Bad != NetQuality.Current.Bad) Log.Escrever($"ping: {(reading.Bad ? "ruim" : "normal")} ({reading.Mark(_cfg.PerdaPct)})");
            S.NaInterface(() => { if (!_stopped && _timer != null) NetQuality.Publish(reading); });
        }
        catch (Exception e) { Log.Escrever("ping: " + e.Message); }
        finally { _busy = false; }
    }

    private static void Push(List<int?> l, int? v)
    {
        l.Add(v);
        if (l.Count > NetQuality.Window) l.RemoveAt(0);
    }

    private static async Task<int?> Echo(string host)
    {
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync(host, 1000, new byte[32]).ConfigureAwait(false);
            return r.Status == IPStatus.Success ? (int)r.RoundtripTime : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The IPv4 default gateway of the interface that is up and has one (the router).</summary>
    internal static IPAddress? Gateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch (Exception) { return null; }
    }

    public override string Autoteste()
    {
        var r = new StringBuilder();
        void Ok(string n, bool ok, string d = "") => r.AppendLine((ok ? "ok      " : "FALHOU  ") + n + (d.Length > 0 ? $" ({d})" : ""));
        var none = new List<int?>();
        var good = Enumerable.Repeat<int?>(20, 30).ToList();
        Ok("ping: conexão boa não marca nada", !NetQuality.Evaluate(good, good, 100, 2, false).Bad);
        var slow = Enumerable.Repeat<int?>(20, 25).Concat(Enumerable.Repeat<int?>(180, 5)).ToList();
        var e1 = NetQuality.Evaluate(slow, good, 100, 2, false);
        Ok("ping: cinco respostas lentas seguidas marcam o ping", e1.Bad && e1.Ms == 180 && !e1.LocalBad, e1.Mark(2));
        var spike = Enumerable.Repeat<int?>(20, 28).Concat(new int?[] { 400, 20 }).ToList();
        Ok("ping: um pacote lento sozinho não marca", !NetQuality.Evaluate(spike, good, 100, 2, false).Bad);
        var lossy = Enumerable.Range(0, 30).Select(i => i % 10 == 0 ? (int?)null : 25).ToList();
        var e2 = NetQuality.Evaluate(lossy, good, 100, 2, false);
        Ok("ping: 10% de perda marca a perda", e2.Bad && e2.Mark(2).Contains("10%"), e2.Mark(2));
        var edge = Enumerable.Repeat<int?>(90, 30).ToList();
        Ok("ping: abaixo do limite, mas acima de 80%, segue ruim se já estava (sem piscar)",
            NetQuality.Evaluate(edge, good, 100, 2, true).Bad && !NetQuality.Evaluate(edge, good, 100, 2, false).Bad);
        var down = Enumerable.Repeat<int?>(20, 27).Concat(new int?[] { null, null, null }).ToList();
        Ok("ping: três sem resposta seguidas marcam na hora", NetQuality.Evaluate(down, good, 100, 2, false).Bad);
        var badRouter = Enumerable.Repeat<int?>(60, 30).ToList();
        Ok("ping: roteador lento diz que é a rede local", NetQuality.Evaluate(slow, badRouter, 100, 2, false).LocalBad);
        Ok("ping: sem amostra, sem leitura", NetQuality.Evaluate(none, none, 100, 2, false) == NetReading.None);
        var gw = Gateway();
        Ok("ping: roteador achado", gw != null, gw?.ToString() ?? "nenhum");
        var real = Echo("1.1.1.1").GetAwaiter().GetResult();
        Ok("ping: um eco de verdade (1.1.1.1)", real != null, real is int ms ? ms + " ms" : "sem resposta");
        return r.ToString().TrimEnd();
    }
}

public sealed class ConfigPing
{
    [Label("Host do ping", "Ping host")]
    [Help("Para onde mandar o ping durante a partida (o servidor do jogo o Windows não mostra).",
        "Where to ping during a match (Windows does not show the game server).")]
    [JsonPropertyName("alvo")] public string Alvo { get; set; } = "1.1.1.1";

    [Label("Medir também o roteador", "Also measure the router")]
    [JsonPropertyName("roteador")] public bool Roteador { get; set; } = true;

    [Label("Ping ruim acima de (ms)", "Bad ping above (ms)")]
    [JsonPropertyName("limiteMs")] public int LimiteMs { get; set; } = 100;

    [Label("Perda ruim a partir de (%)", "Bad loss from (%)")]
    [JsonPropertyName("perdaPct")] public double PerdaPct { get; set; } = 2;
}
