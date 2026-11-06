using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Ilha.Dsx;

/// <summary>
/// DualSense controls through DSX (4.2.0, his request on 06/11/2026): while DSX runs, its mod server is asked for its
/// state every 5 s. With a controller connected the menu gets "Controle" (with the battery), and its card changes the
/// light bar, the mic LED and the adaptive triggers, or hands everything back to the DSX profile. What the island sends
/// replaces the DSX profile until "Voltar ao perfil do DSX".
/// </summary>
[Modulo("dsx", "Controle (DSX)", English = "Controller (DSX)")]
internal sealed class DsxModule : Modulo
{
    private DsxActivity? _activity;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 5000 };
    private bool _stopped, _busy, _menu;

    internal DsxClient.Status? State { get; private set; }
    internal string? Problem { get; private set; }
    internal int MicMode { get; private set; } = -1;
    internal int[]? Light { get; private set; }
    internal string? Trigger { get; private set; }

    internal DsxDevice? Device => State?.Devices.FirstOrDefault();

    protected override void Iniciar(JsonObject cfg)
    {
        _activity = new DsxActivity(S, this);
        S.Ilha.Adicionar(_activity);
        _tick.Tick += (_, _) => Poll();
        _tick.Start();
        Poll();
    }

    public override void Parar()
    {
        _stopped = true;
        _tick.Stop();
        _tick.Dispose();
    }

    private static bool DsxRunning() => Games.GamesModule.Running("DSX");

    private void Poll()
    {
        if (_stopped || _busy) return;
        if (!DsxRunning())
        {
            if (State != null || _menu) { State = null; ShowMenu(false); _activity?.Changed(); }
            return;
        }
        Send(null);
    }

    /// <summary>Sends instructions (or only asks for the state) and keeps DSX's answer.</summary>
    private void Send(string? packet, Action? after = null)
    {
        if (_busy) return;
        _busy = true;
        _ = Task.Run(() =>
        {
            DsxClient.Status? st = null;
            string? problem = null;
            int? port = DsxClient.Port();
            if (port == null) problem = Lang.T("Ligue o UDP de entrada no DSX (Configurações > Rede).", "Turn on incoming UDP in DSX (Settings > Networking).");
            else
            {
                try
                {
                    st = packet == null ? DsxClient.Ask(port.Value) : DsxClient.Send(port.Value, packet);
                    if (st == null) problem = Lang.T("O DSX não respondeu.", "DSX did not answer.");
                }
                catch (Exception e) { problem = e.Message; }
            }
            S.NaInterface(() =>
            {
                _busy = false;
                if (_stopped) return;
                if (st != null) State = st;
                Problem = problem;
                ShowMenu(State?.Devices.Count > 0 && problem == null);
                after?.Invoke();
                _activity?.Changed();
                S.Menu.Redesenhar();
            });
        });
    }

    private void ShowMenu(bool on)
    {
        if (on == _menu) return;
        _menu = on;
        if (!on) { S.Menu.Remover("dsx"); return; }
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "dsx", Nome = Lang.T("Controle", "Controller"), Ordem = 67,
            Abrir = () => _activity?.Open(),
            Icone = (dc, p, r) => global::Ilha.Extras.Desenhos.Controle(dc, p.G.Fabrica, new System.Numerics.Vector2(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width, Cor.Branco),
            Detalhe = () => Device is { Battery: >= 0 } d ? d.Battery + "%" : null,
        });
    }

    internal void Refresh() => Send(null);

    internal void SetLight(int r, int g, int b)
    {
        if (Device is not { } d) return;
        Light = new[] { r, g, b };
        Send(DsxClient.Packet((DsxClient.RgbUpdate, new object[] { d.Index, r, g, b })));
    }

    internal void CycleMic()
    {
        if (Device is not { } d) return;
        MicMode = MicMode switch { DsxClient.MicOff => DsxClient.MicOn, DsxClient.MicOn => DsxClient.MicPulse, _ => DsxClient.MicOff };
        Send(DsxClient.Packet((DsxClient.MicLed, new object[] { d.Index, MicMode })));
    }

    internal void SetTriggers(int preset)
    {
        if (Device is not { } d || preset < 0 || preset >= DsxClient.TriggerPresets.Length) return;
        var p = DsxClient.TriggerPresets[preset];
        Trigger = Lang.T(p.Pt, p.En);
        object[] Args(int trigger) => new object[] { d.Index, trigger }.Concat(p.Mode.Cast<object>()).ToArray();
        Send(DsxClient.Packet((DsxClient.TriggerUpdate, Args(DsxClient.Left)), (DsxClient.TriggerUpdate, Args(DsxClient.Right))));
    }

    internal void ResetToDsx()
    {
        if (Device is not { } d) return;
        Light = null;
        MicMode = -1;
        Trigger = null;
        Send(DsxClient.Packet((DsxClient.ResetToUser, new object[] { d.Index })));
    }

    // ------------------------------------------------------------------ self test

    public override string Autoteste()
    {
        var r = new StringBuilder();
        void Ok(string n, bool ok, string d = "") => r.AppendLine((ok ? "ok      " : "FALHOU  ") + n + (d.Length > 0 ? $" ({d})" : ""));
        string pkt = DsxClient.Packet((DsxClient.TriggerUpdate, new object[] { 0, DsxClient.Left, 13, 0, 6 }));
        Ok("dsx: pacote no formato do DSX (instructions, type, parameters)", pkt == "{\"instructions\":[{\"type\":1,\"parameters\":[0,1,13,0,6]}]}", pkt);
        const string reply = """{"Status":"DSX Received UDP Instructions","TimeReceived":"x","isControllerConnected":true,"BatteryLevel":80,"Devices":[{"Index":0,"MacAddress":"AA:BB","DeviceType":1,"ConnectionType":1,"BatteryLevel":80,"IsSupportAT":true,"IsSupportLightBar":true,"IsSupportPlayerLED":true,"IsSupportLegacyPlayerLED":true,"IsSupportMicLED":true}]}""";
        var st = DsxClient.ParseStatus(reply);
        Ok("dsx: resposta com o controle (Edge por Bluetooth, 80%)", st is { Connected: true } && st.Devices.Count == 1 && st.Devices[0].Name == "DualSense Edge"
            && st.Devices[0].Link == "Bluetooth" && st.Devices[0].Battery == 80 && st.Devices[0].Triggers, st?.Devices.FirstOrDefault()?.Name ?? "nada");
        Ok("dsx: resposta sem controle", DsxClient.ParseStatus("""{"Status":"ok","isControllerConnected":false,"BatteryLevel":0,"Devices":[]}""") is { Connected: false, Devices.Count: 0 });
        Ok("dsx: resposta estragada não derruba", DsxClient.ParseStatus("nada") == null);

        // a fake DSX on a free port: gets the packet and answers like the real one
        using (var fake = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            int port = ((IPEndPoint)fake.Client.LocalEndPoint!).Port;
            string? got = null;
            var t = new Thread(() =>
            {
                try
                {
                    fake.Client.ReceiveTimeout = 2000;
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    got = Encoding.UTF8.GetString(fake.Receive(ref from));
                    var b = Encoding.UTF8.GetBytes(reply);
                    fake.Send(b, b.Length, from);
                }
                catch (Exception) { }
            }) { IsBackground = true };
            t.Start();
            var answer = DsxClient.Send(port, DsxClient.Packet((DsxClient.RgbUpdate, new object[] { 0, 255, 0, 0 })), 1500);
            t.Join(2500);
            Ok("dsx: manda pelo UDP e lê a resposta (DSX de mentira)", answer?.Devices.Count == 1 && got == "{\"instructions\":[{\"type\":2,\"parameters\":[0,255,0,0]}]}", got ?? "nada chegou");
        }
        if (DsxRunning() && DsxClient.Port() is int real)
        {
            var live = DsxClient.Ask(real);
            Ok("dsx: DSX de verdade (só pergunta o estado)", live != null, live == null ? "sem resposta" : $"{live.Devices.Count} controle(s): {string.Join(", ", live.Devices.Select(x => $"{x.Name} {x.Link} {x.Battery}%"))}");
        }
        else Ok("dsx: DSX de verdade pulado (não está aberto)", true);
        return r.ToString().TrimEnd();
    }
}
