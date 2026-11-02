using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;

namespace Ilha.Extras;

/// <summary>A wired keyboard the island knows: the name on the card and its photo in imagens/fones (they have no battery).</summary>
internal sealed record KnownKeyboard(string Name, string? Photo);

/// <summary>
/// Wired keyboards, seen through the USB device interface (one per physical device, composite or not): the card shows
/// when one is plugged in. Only the device id and the name the device reports are read; nothing is opened on it.
/// </summary>
internal static class Keyboards
{
    public const ushort Logitech = 0x046D, Razer = 0x1532;
    public const ushort G213 = 0xC336;

    /// <summary>GUID_DEVINTERFACE_USB_DEVICE, enabled interfaces only (a device being removed drops out).</summary>
    public const string UsbSelector =
        "System.Devices.InterfaceClassGuid:=\"{A5DCBF10-6530-11D2-901F-00C04FB951ED}\" AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True";

    private static readonly Regex Ids = new(@"VID_(?<vid>[0-9A-F]{4})&PID_(?<pid>[0-9A-F]{4})", RegexOptions.IgnoreCase);

    /// <summary>Vendor and product from an interface path (\\?\USB#VID_046D&amp;PID_C336#serial#{guid}) or an instance id.</summary>
    public static (ushort Vid, ushort Pid)? ParseIds(string id)
    {
        var m = Ids.Match(id);
        if (!m.Success) return null;
        return (Convert.ToUInt16(m.Groups["vid"].Value, 16), Convert.ToUInt16(m.Groups["pid"].Value, 16));
    }

    /// <summary>
    /// The G213 by its product id. The Razer Huntsman V3 by its name: its product id was not known when this was written
    /// (he had not bought it yet), so the log keeps the id of every Razer device that shows up.
    /// </summary>
    public static KnownKeyboard? Match(ushort vid, ushort pid, string name)
    {
        if (vid == Logitech && pid == G213) return new("G213 Prodigy", "logitech-g213.png");
        if (vid == Razer && name.Contains("Huntsman", StringComparison.OrdinalIgnoreCase))
        {
            bool he = Regex.IsMatch(name, @"\b(HE|Magnetic)\b", RegexOptions.IgnoreCase);
            bool tkl = Regex.IsMatch(name, @"\b(TKL|Tenkeyless)\b", RegexOptions.IgnoreCase);
            if (he && tkl) return new("Huntsman V3 HE TKL", "razer-huntsman-v3-he-tkl.png");
            string clean = Regex.Replace(name, @"^\s*Razer\s+", "", RegexOptions.IgnoreCase).Trim();
            return new(clean.Length > 0 ? clean : "Huntsman", null);
        }
        return null;
    }

    /// <summary>The known keyboards plugged in right now (for the self test; blocks, so never on the interface thread).</summary>
    public static List<(string Id, KnownKeyboard Keyboard)> PluggedNow()
    {
        var list = new List<(string, KnownKeyboard)>();
        // no await: the self test runs on a thread with a synchronization context, and resuming on it would deadlock
        foreach (var d in DeviceInformation.FindAllAsync(UsbSelector).AsTask().GetAwaiter().GetResult())
            if (ParseIds(d.Id) is var (vid, pid) && Match(vid, pid, d.Name ?? "") is { } k) list.Add((d.Id, k));
        return list;
    }
}
