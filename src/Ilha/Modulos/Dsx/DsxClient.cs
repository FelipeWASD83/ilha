using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ilha.Dsx;

/// <summary>A controller DSX reports.</summary>
internal sealed record DsxDevice(int Index, string Mac, int Type, int Connection, int Battery, bool Triggers, bool LightBar, bool MicLed)
{
    public string Name => Type switch
    {
        0 => "DualSense",
        1 => "DualSense Edge",
        2 or 3 or 4 => "DualShock 4",
        7 => Lang.T("Controle Access", "Access controller"),
        _ => Lang.T("Controle", "Controller"),
    };

    public string Link => Connection switch
    {
        0 => "USB",
        1 => "Bluetooth",
        2 => "Dongle",
        _ => "",
    };
}

/// <summary>
/// DSX's mod server (4.2.0): JSON over UDP to 127.0.0.1 (port 6969 unless DSX's settings say otherwise), read from
/// DSX 3.1.7's own types with REA on 06/11/2026 (DSX.ModificationSystem: Packet, Instruction, InstructionType,
/// UDPResponse, Device; trigger modes are the public TriggerType_v2_Legacy). DSX answers every packet with its state.
/// Nothing of DSX is changed; its own license checks are not touched.
/// </summary>
internal static class DsxClient
{
    // DSX.ModificationSystem.InstructionType
    public const int GetStatus = 0, TriggerUpdate = 1, RgbUpdate = 2, PlayerLed = 3, TriggerThreshold = 4, MicLed = 5, ResetToUser = 7;
    // DSX.ModificationSystem.Trigger
    public const int Left = 1, Right = 2;
    // DSX.ModificationSystem.MicLEDMode
    public const int MicOn = 0, MicPulse = 1, MicOff = 2;

    /// <summary>The trigger presets the card offers: name and the TriggerType_v2_Legacy mode with its parameters.</summary>
    public static readonly (string Pt, string En, int[] Mode)[] TriggerPresets =
    {
        ("Normal", "Normal", new[] { 0 }),
        ("Resistência", "Resistance", new[] { 13, 0, 6 }),
        ("Arma", "Gun", new[] { 16, 2, 7, 8 }),
        ("Vibração", "Vibration", new[] { 8, 30 }),
        ("Rígido", "Rigid", new[] { 7 }),
    };

    /// <summary>One packet with the given instructions (type and parameters), the JSON DSX deserializes.</summary>
    public static string Packet(params (int Type, object[] Parameters)[] instructions)
    {
        var list = new JsonArray();
        foreach (var (type, ps) in instructions)
            list.Add(new JsonObject { ["type"] = type, ["parameters"] = new JsonArray(ps.Select(Value).ToArray()) });
        return new JsonObject { ["instructions"] = list }.ToJsonString();
    }

    private static JsonNode? Value(object p) => p switch
    {
        int i => JsonValue.Create(i),
        bool b => JsonValue.Create(b),
        byte y => JsonValue.Create((int)y),
        _ => JsonValue.Create(p.ToString()),
    };

    public sealed record Status(bool Connected, int Battery, IReadOnlyList<DsxDevice> Devices, string Raw);

    public static Status? ParseStatus(string json)
    {
        try
        {
            var n = JsonNode.Parse(json) as JsonObject;
            if (n == null) return null;
            JsonNode? Get(JsonObject o, string k) => o.FirstOrDefault(p => string.Equals(p.Key, k, StringComparison.OrdinalIgnoreCase)).Value;
            int I(JsonObject o, string k) => Get(o, k) is JsonValue v && v.TryGetValue<int>(out int x) ? x : Get(o, k) is JsonValue s && int.TryParse(s.ToString(), out int y) ? y : -1;
            bool B(JsonObject o, string k) => Get(o, k) is JsonValue v && v.TryGetValue<bool>(out bool b) && b;
            var devices = new List<DsxDevice>();
            if (Get(n, "Devices") is JsonArray arr)
                foreach (var d in arr.OfType<JsonObject>())
                    devices.Add(new DsxDevice(I(d, "Index"), Get(d, "MacAddress")?.ToString() ?? "", I(d, "DeviceType"), I(d, "ConnectionType"), I(d, "BatteryLevel"),
                        B(d, "IsSupportAT"), B(d, "IsSupportLightBar"), B(d, "IsSupportMicLED")));
            return new Status(B(n, "isControllerConnected") || devices.Count > 0, I(n, "BatteryLevel"), devices, json);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Where DSX listens: its settings file (use_incoming_udp, incoming_udp_port), else 6969. null = UDP off in DSX.</summary>
    public static int? Port()
    {
        try
        {
            if (Games.GameSetup.SteamAppFolder(1812620) is { } dir)
            {
                string f = Path.Combine(dir, "Main_v3_Beta", "DSX_Savefile", "app_settings.json");
                if (File.Exists(f))
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var doc = JsonDocument.Parse(fs);
                    var r = doc.RootElement;
                    if (r.TryGetProperty("use_incoming_udp", out var on) && on.ValueKind == JsonValueKind.False) return null;
                    if (r.TryGetProperty("incoming_udp_port", out var p) && p.TryGetInt32(out int port) && port > 0) return port;
                }
            }
        }
        catch (Exception) { }
        return 6969;
    }

    /// <summary>Sends a packet and waits for DSX's answer (blocks up to `timeoutMs`; never on the interface thread).</summary>
    public static Status? Send(int port, string packet, int timeoutMs = 600)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        udp.Client.ReceiveTimeout = timeoutMs;
        var bytes = Encoding.UTF8.GetBytes(packet);
        udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
        try
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            var reply = udp.Receive(ref from);
            return ParseStatus(Encoding.UTF8.GetString(reply));
        }
        catch (SocketException) { return null; }
    }

    public static Status? Ask(int port) => Send(port, Packet((GetStatus, Array.Empty<object>())));
}
