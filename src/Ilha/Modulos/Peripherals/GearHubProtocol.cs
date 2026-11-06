using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ilha.Peripherals;

internal sealed record GearProfile(string Name, string Display, IReadOnlyList<string> Apps, bool Saved);

internal sealed record GearDevice(string Key, string Name, string Kind, string? Status, int? Battery, bool Charging = false);

/// <summary>
/// Gear Hub's local channel (`gear-hub/docs/canal-local.md`, Gear Hub 1.1.0+): the named pipe `\\.\pipe\GearHub.Events`,
/// UTF-8, one JSON message per line both ways. Unknown fields are ignored, an unreadable line is skipped.
/// </summary>
internal static class GearHubProtocol
{
    public const string PipeName = "GearHub.Events";

    /// <summary>One line from Gear Hub: the object with a "type", or null for anything else.</summary>
    public static JsonObject? Parse(string line)
    {
        try { return JsonNode.Parse(line) is JsonObject o && Str(o, "type") != null ? o : null; }
        catch (JsonException) { return null; }
    }

    public static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public static bool? Bool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    public static int? Int(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out double d) && !double.IsNaN(d)) return (int)Math.Round(d);
        return null;
    }

    public static List<GearProfile> Profiles(JsonNode? node)
    {
        var r = new List<GearProfile>();
        if (node is not JsonArray a) return r;
        foreach (var x in a)
        {
            if (x is not JsonObject o || Str(o, "name") is not { Length: > 0 } name) continue;
            var apps = o["apps"] is JsonArray aa ? aa.OfType<JsonValue>().Select(v => v.TryGetValue(out string? s) ? s : null).OfType<string>().ToList() : new List<string>();
            r.Add(new GearProfile(name, Str(o, "display") is { Length: > 0 } d ? d : name, apps, Bool(o, "saved") ?? false));
        }
        return r;
    }

    public static List<GearDevice> Devices(JsonNode? node)
    {
        var r = new List<GearDevice>();
        if (node is not JsonArray a) return r;
        foreach (var x in a)
        {
            if (x is not JsonObject o || Str(o, "key") is not { Length: > 0 } key) continue;
            int? battery = Int(o, "battery");
            r.Add(new GearDevice(key, Str(o, "name") ?? key, Str(o, "kind") ?? "", Str(o, "status"),
                battery is int b ? Math.Clamp(b, 0, 100) : null, Bool(o, "charging") ?? false));
        }
        return r;
    }

    // ------------------------------------------------------------------ requests (client to Gear Hub)

    public static string State() => new JsonObject { ["cmd"] = "state" }.ToJsonString();

    public static string Profile(string name) => new JsonObject { ["cmd"] = "profile", ["name"] = name }.ToJsonString();

    public static string SnapTap(bool on) => new JsonObject { ["cmd"] = "snaptap", ["on"] = on }.ToJsonString();

    public static string Show(string? page = null)
    {
        var o = new JsonObject { ["cmd"] = "show" };
        if (!string.IsNullOrEmpty(page)) o["page"] = page;
        return o.ToJsonString();
    }

    // ------------------------------------------------------------------ texts

    /// <summary>The small line under "Perfil X": why it was applied.</summary>
    public static string ReasonText(string? reason, string? app)
    {
        switch (reason)
        {
            case "app":
                string name = app ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                return name.Length > 0 ? Lang.T(name + " na frente", name + " in front") : Lang.T("pelo app na frente", "by the app in front");
            case "manual": return Lang.T("manual", "manual");
            case "tray": return Lang.T("pela bandeja", "from the tray");
            case "hotkey": return Lang.T("atalho", "hotkey");
            case "ilha": return Lang.T("pela ilha", "from the island");
            default: return reason ?? "";
        }
    }

    public static string KindName(string kind) => kind switch
    {
        "keyboard" => Lang.T("Teclado", "Keyboard"),
        "mouse" => Lang.T("Mouse", "Mouse"),
        "headset" => Lang.T("Headset", "Headset"),
        "memory" => Lang.T("Memória", "Memory"),
        "controller" => Lang.T("Controle", "Controller"),
        "camera" => Lang.T("Câmera", "Camera"),
        "light" => Lang.T("Luz", "Light"),
        _ => Lang.T("Aparelho", "Device"),
    };
}
