using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Ilha.Peripherals;

internal enum NoticeKind { Profile, SnapTap, Dpi, Device, Battery, Effect }

/// <summary>What one Gear Hub event turns into on the pill (before the module makes it an <see cref="Alerta"/>).</summary>
internal sealed record GearNotice(NoticeKind Kind, string Title, string Sub, string Icon)
{
    public bool? On { get; init; }
    public int? Percent { get; init; }
}

/// <summary>Which notices the module shows (its config) and which devices the headphones module already covers.</summary>
internal sealed class NoticeRules
{
    public bool Profile = true, SnapTap = true, Dpi = true, Devices = true, Battery = true, Effects = true;
    /// <summary>(kind, key, name, battery) → the headphones module already tells this one.</summary>
    public Func<string, string, string, bool, bool> CoveredElsewhere = (_, _, _, _) => false;
}

/// <summary>How each notice shows: time, sound, Do Not Disturb and competitive games.</summary>
internal readonly record struct NoticeSpec(float? Seconds, string Sound, bool Important, bool Competitive)
{
    /// <summary>
    /// The profile is the main notice (he wants it when a game comes to the front): 2 s, no sound, allowed over a
    /// competitive game and through Do Not Disturb. Low battery is important like the headphones' warnings. The rest
    /// is silenced by competitive games; `throughDnd` (on by default, Do Not Disturb reads on all the time on his PC)
    /// lets them through Do Not Disturb.
    /// </summary>
    public static NoticeSpec For(GearNotice n, bool throughDnd) => n.Kind switch
    {
        NoticeKind.Profile => new(2f, "", true, true),
        NoticeKind.Battery => new(n.Percent <= 10 ? 7f : null, "alerta", true, false),
        NoticeKind.Device => new(3f, n.On == true ? "conectou" : "", throughDnd, false),
        NoticeKind.SnapTap => new(2.5f, "", throughDnd, false),
        _ => new(1.8f, "", throughDnd, false),
    };
}

/// <summary>
/// What the island knows from Gear Hub: the state from `hello`, kept up to date by the events. Pure logic (no pipe,
/// no window), so the self test feeds it lines.
/// </summary>
internal sealed class GearHubState
{
    public bool Connected { get; private set; }
    public string? Version { get; private set; }
    public string? Profile { get; private set; }
    public string? ProfileDisplay { get; private set; }
    public List<GearProfile> Profiles { get; private set; } = new();
    public List<GearDevice> Devices { get; private set; } = new();
    public bool? SnapTap { get; private set; }

    public IEnumerable<GearProfile> Saved => Profiles.Where(p => p.Saved);

    public void Reset()
    {
        Connected = false;
        Version = Profile = ProfileDisplay = null;
        Profiles = new();
        Devices = new();
        SnapTap = null;
    }

    /// <summary>Applies one message; returns what deserves a notice (empty for state only, like `hello`).</summary>
    public List<GearNotice> Apply(JsonObject m, NoticeRules rules)
    {
        var r = new List<GearNotice>();
        string type = GearHubProtocol.Str(m, "type") ?? "";
        if (type == "hello")
        {
            Connected = true;
            Version = GearHubProtocol.Str(m, "version");
            Profile = GearHubProtocol.Str(m, "profile");
            ProfileDisplay = GearHubProtocol.Str(m, "profileDisplay") ?? Profile;
            Profiles = GearHubProtocol.Profiles(m["profiles"]);
            Devices = GearHubProtocol.Devices(m["devices"]);
            SnapTap = GearHubProtocol.Bool(m, "snapTap");
            return r;
        }
        // an event before the hello (should not happen) only counts after it
        if (!Connected) return r;
        switch (type)
        {
            case "profile":
            {
                string? name = GearHubProtocol.Str(m, "name");
                if (name == null) break;
                Profile = name;
                ProfileDisplay = GearHubProtocol.Str(m, "display") ?? Profiles.FirstOrDefault(p => p.Name == name)?.Display ?? name;
                string? reason = GearHubProtocol.Str(m, "reason");
                // a profile chosen on the island's own card: the card already shows it
                if (rules.Profile && reason != "ilha")
                    r.Add(new GearNotice(NoticeKind.Profile, Lang.T("Perfil ", "Profile ") + ProfileDisplay,
                        GearHubProtocol.ReasonText(reason, GearHubProtocol.Str(m, "app")), "gear"));
                break;
            }
            case "profiles":
                Profiles = GearHubProtocol.Profiles(m["profiles"]);
                if (Profile != null && Profiles.FirstOrDefault(p => p.Name == Profile) is { } cur) ProfileDisplay = cur.Display;
                break;
            case "devices":
                Devices = GearHubProtocol.Devices(m["devices"]);
                break;
            case "device":
            {
                string? key = GearHubProtocol.Str(m, "key");
                if (key == null) break;
                string name = GearHubProtocol.Str(m, "name") ?? key, kind = GearHubProtocol.Str(m, "kind") ?? "";
                bool on = GearHubProtocol.Bool(m, "connected") ?? true;
                int i = Devices.FindIndex(d => d.Key == key);
                if (on && i < 0) Devices.Add(new GearDevice(key, name, kind, null, null));
                else if (!on && i >= 0) Devices.RemoveAt(i);
                if (rules.Devices && !(on && rules.CoveredElsewhere(kind, key, name, false)))
                    r.Add(new GearNotice(NoticeKind.Device, name, on ? Lang.T("Conectado", "Connected") : Lang.T("Desconectado", "Disconnected"), kind) { On = on });
                break;
            }
            case "battery":
            {
                string? key = GearHubProtocol.Str(m, "key");
                if (key == null || GearHubProtocol.Int(m, "percent") is not int pct) break;
                pct = Math.Clamp(pct, 0, 100);
                bool charging = GearHubProtocol.Bool(m, "charging") ?? false;
                string name = GearHubProtocol.Str(m, "name") ?? key, kind = GearHubProtocol.Str(m, "kind") ?? "";
                int i = Devices.FindIndex(d => d.Key == key);
                if (i >= 0) Devices[i] = Devices[i] with { Battery = pct, Charging = charging };
                if (!charging && rules.Battery && !rules.CoveredElsewhere(kind, key, name, true))
                    r.Add(new GearNotice(NoticeKind.Battery, name, global::Ilha.Extras.ModuloFones.TextoBateriaBaixa(pct, pct, null, false), kind) { Percent = pct });
                break;
            }
            case "snaptap":
            {
                if (GearHubProtocol.Bool(m, "on") is not bool on) break;
                bool changed = SnapTap != on;
                SnapTap = on;
                if (rules.SnapTap && changed) r.Add(new GearNotice(NoticeKind.SnapTap, "Snap Tap", SnapText(on), "keyboard") { On = on });
                break;
            }
            case "dpi":
            {
                if (GearHubProtocol.Int(m, "value") is not int dpi) break;
                if (rules.Dpi)
                    r.Add(new GearNotice(NoticeKind.Dpi, "DPI " + dpi.ToString(Lang.Culture), GearHubProtocol.Str(m, "name") ?? "", "mouse"));
                break;
            }
            case "effect":
            {
                if (GearHubProtocol.Str(m, "name") is not { Length: > 0 } fx) break;
                var targets = m["targets"] is JsonArray a ? a.OfType<JsonValue>().Select(v => v.TryGetValue(out string? s) ? s : null).OfType<string>().ToList() : new List<string>();
                if (rules.Effects)
                    r.Add(new GearNotice(NoticeKind.Effect, fx, targets.Count > 0 ? string.Join(", ", targets) : Lang.T("Efeito de luz", "Lighting effect"), "light"));
                break;
            }
        }
        return r;
    }

    public static string SnapText(bool on) => on ? Lang.T("Snap Tap ligado", "Snap Tap on") : Lang.T("Snap Tap desligado", "Snap Tap off");
}

/// <summary>
/// Gear Hub sends `profile` and `snaptap` one after the other when a profile changes Snap Tap: within a short window
/// they become one notice ("Perfil cs2", "cs2 na frente · Snap Tap desligado"). A second notice of the same kind in
/// the window replaces the first.
/// </summary>
internal sealed class NoticeBatch
{
    public const int WindowMs = 300;
    private GearNotice? _profile, _snap;

    public bool Empty => _profile == null && _snap == null;

    /// <summary>Takes a profile or Snap Tap notice into the batch (true) or leaves it to be shown at once (false).</summary>
    public bool Take(GearNotice n)
    {
        if (n.Kind == NoticeKind.Profile) { _profile = n; return true; }
        if (n.Kind == NoticeKind.SnapTap) { _snap = n; return true; }
        return false;
    }

    public GearNotice? Flush()
    {
        var p = _profile;
        var s = _snap;
        _profile = _snap = null;
        if (p != null && s != null) return p with { Sub = p.Sub.Length > 0 ? p.Sub + " · " + s.Sub : s.Sub, On = s.On };
        return p ?? s;
    }
}
