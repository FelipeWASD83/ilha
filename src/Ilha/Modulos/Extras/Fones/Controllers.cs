using System;
using System.Collections.Generic;

namespace Ilha.Extras;

/// <summary>A game controller the island knows: the name on the card and the photo it looks for in imagens/fones.</summary>
internal sealed record KnownController(string Name, string PhotoName, bool Xbox);

/// <summary>
/// Xbox Series and PlayStation 5 controllers (4.2.0, his request on 06/11/2026), by USB vendor and product id when
/// plugged in with a cable, and by the name they announce over Bluetooth. The Xbox comes in white and black with the
/// same ids, so its photo follows the setting "corXbox", or a color word in the device's name (a paired device can be
/// renamed in Control Panel > Devices and Printers).
/// </summary>
internal static class Controllers
{
    public const ushort Microsoft = 0x045E, Sony = 0x054C;
    private static readonly HashSet<ushort> XboxSeries = new() { 0x0B12, 0x0B13, 0x0B20, 0x0B21, 0x0B22 };
    public const ushort DualSense = 0x0CE6, DualSenseEdge = 0x0DF2;

    public static KnownController? ByIds(ushort vid, ushort pid) => (vid, pid) switch
    {
        (Microsoft, var p) when XboxSeries.Contains(p) => Xbox,
        (Sony, DualSense) => Ds,
        (Sony, DualSenseEdge) => Edge,
        _ => null,
    };

    public static KnownController? ByName(string name)
    {
        if (name.Contains("DualSense Edge", StringComparison.OrdinalIgnoreCase)) return Edge;
        if (name.Contains("DualSense", StringComparison.OrdinalIgnoreCase)) return Ds;
        if (name.Contains("Xbox Wireless Controller", StringComparison.OrdinalIgnoreCase) || name.Contains("Xbox Controller", StringComparison.OrdinalIgnoreCase)) return Xbox;
        return null;
    }

    private static readonly KnownController Xbox = new("Xbox Wireless Controller", "xbox wireless controller", true);
    private static readonly KnownController Ds = new("DualSense", "dualsense wireless controller", false);
    private static readonly KnownController Edge = new("DualSense Edge", "dualsense edge wireless controller", false);

    /// <summary>The color word for the Xbox photo: one in the device's name wins over the setting.</summary>
    public static string XboxColor(string deviceName, string setting)
    {
        foreach (var (word, color) in new[] { ("preto", "preto"), ("black", "preto"), ("carbon", "preto"), ("branco", "branco"), ("white", "branco"), ("robot", "branco") })
            if (deviceName.Contains(word, StringComparison.OrdinalIgnoreCase)) return color;
        return string.IsNullOrWhiteSpace(setting) ? "preto" : setting.Trim().ToLowerInvariant();
    }
}
