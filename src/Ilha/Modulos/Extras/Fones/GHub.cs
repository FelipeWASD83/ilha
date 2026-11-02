using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ilha.Extras;

/// <summary>One Logitech device G HUB knows about, with the battery it reports.</summary>
internal readonly record struct GHubDevice(string Id, string Model, string Name, int Percent, bool Charging, double? HoursLeft)
{
    public bool IsMouse => Model.Contains("g502", StringComparison.OrdinalIgnoreCase);
    public bool IsG733 => Model.StartsWith("g733", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Battery of the Logitech devices through the local WebSocket of G HUB (ws://localhost:9010, subprotocol "json"). It is
/// not documented by Logitech (the format is the one G HUB answered on the PC on 01/11/2026, same API as LGSTrayBattery)
/// so it can change in a G HUB update; every failure just means "no reading". G HUB has to be running. Nothing is
/// sent but reads (GET /devices/list and GET /battery/{id}/state).
/// </summary>
internal static class GHubClient
{
    public const string Address = "ws://localhost:9010";

    /// <summary>All devices with a battery. Null when G HUB is not reachable (not running, port closed, timeout).</summary>
    public static async Task<List<GHubDevice>?> ReadAll(int timeoutMs = 6000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            using var ws = new ClientWebSocket();
            ws.Options.AddSubProtocol("json");
            ws.Options.SetRequestHeader("Origin", "file://");
            ws.Options.SetRequestHeader("Pragma", "no-cache");
            ws.Options.SetRequestHeader("Cache-Control", "no-cache");
            await ws.ConnectAsync(new Uri(Address), cts.Token).ConfigureAwait(false);

            var list = await Ask(ws, "/devices/list", cts.Token).ConfigureAwait(false);
            var result = new List<GHubDevice>();
            foreach (var d in ParseDeviceList(list))
            {
                var msg = await Ask(ws, $"/battery/{d.Id}/state", cts.Token).ConfigureAwait(false);
                if (ParseBattery(msg, d) is { } dev) result.Add(dev);
            }
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            return result;
        }
        catch (Exception e)
        {
            Log.Escrever("ghub: sem leitura (" + e.GetType().Name + ")");
            return null;
        }
    }

    private static async Task<JsonElement> Ask(ClientWebSocket ws, string path, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { msgId = "", verb = "GET", path });
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        var buffer = new byte[1 << 16];
        // G HUB also sends things nobody asked for (the first message is an OPTIONS): skip until the path matches
        while (true)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) throw new IOException("G HUB closed the connection");
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > 4_000_000) throw new IOException("message too big");
            } while (!r.EndOfMessage);
            using var doc = JsonDocument.Parse(ms.ToArray());
            if (doc.RootElement.TryGetProperty("path", out var p) && p.GetString() == path) return doc.RootElement.Clone();
        }
    }

    /// <summary>Id, model and display name of each device in the answer of GET /devices/list (the battery comes later).</summary>
    internal static List<(string Id, string Model, string Name)> ParseDeviceList(JsonElement list)
    {
        var r = new List<(string, string, string)>();
        if (!list.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("deviceInfos", out var infos) || infos.ValueKind != JsonValueKind.Array) return r;
        foreach (var d in infos.EnumerateArray())
        {
            string? id = Str(d, "id");
            if (string.IsNullOrEmpty(id)) continue;
            r.Add((id, Str(d, "deviceModel") ?? "", Str(d, "displayName") ?? ""));
        }
        return r;
    }

    /// <summary>The device with its battery, or null when G HUB says the device has none (the receiver, for instance).</summary>
    internal static GHubDevice? ParseBattery(JsonElement msg, (string Id, string Model, string Name) d)
    {
        if (!msg.TryGetProperty("result", out var res) || Str(res, "code") != "SUCCESS") return null;
        if (!msg.TryGetProperty("payload", out var p) || !p.TryGetProperty("percentage", out var pc) || !pc.TryGetInt32(out int pct)) return null;
        if (pct is < 0 or > 100) return null;
        bool charging = p.TryGetProperty("charging", out var ch) && ch.ValueKind == JsonValueKind.True;
        double? hours = p.TryGetProperty("mileage", out var m) && m.TryGetDouble(out double h) && h > 0 ? h : null;
        return new GHubDevice(d.Id, d.Model, d.Name, pct, charging, hours);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
