using System;
using System.Collections.Generic;
using System.Linq;

namespace Ilha.Keyboard;

/// <summary>One Snap Tap pair: two analog key ids and how the keyboard settles both pressed.</summary>
internal readonly record struct SnapPair(byte Key1, byte Key2, byte Mode)
{
    public bool Empty => Key1 == 0 && Key2 == 0 && Mode == 0;
}

/// <summary>The Snap Tap of one slot as the keyboard reports it.</summary>
internal sealed record SnapTapState(byte Slot, bool Enabled, IReadOnlyList<SnapPair> Pairs)
{
    /// <summary>The pairs in use (the keyboard pads its reply with empty ones).</summary>
    public IReadOnlyList<SnapPair> Used => Pairs.Where(p => !p.Empty).ToList();
}

/// <summary>
/// The Razer HID protocol as Synapse 4.0.896 speaks it to the Huntsman V3 HE (product 746, rzDevice25, read from its
/// cached code on 06/11/2026): a 91-byte feature report (report id 0) holding status, transaction id (1 to 31),
/// remaining packets, protocol type, data size, command class, command id, 80 bytes of arguments and an XOR checksum of
/// bytes 2 to 87. A "get" command is the "set" one with the high bit of the id set. Pure: the self test runs it.
/// </summary>
internal static class RazerProtocol
{
    public const int ReportLength = 91;
    public const byte StatusBusy = 1, StatusOk = 2, StatusFailed = 3, StatusTimeout = 4, StatusUnsupported = 5;

    // [data size, class, id], copied from Synapse's command table
    public static readonly (byte Size, byte Class, byte Id) GetActiveProfile = (1, 0x05, 0x84);
    public static readonly (byte Size, byte Class, byte Id) GetProfileList = (80, 0x05, 0x81);
    public static readonly (byte Size, byte Class, byte Id) GetSnapTap = (15, 0x02, 0xA7);
    public static readonly (byte Size, byte Class, byte Id) SetSnapTap = (15, 0x02, 0x27);

    /// <summary>Synapse writes the active profile's settings to slot 1 (the live one): reading it gives what the keys do now.</summary>
    public const byte LiveSlot = 1;

    public static byte[] Build(byte transaction, (byte Size, byte Class, byte Id) cmd, ReadOnlySpan<byte> args)
    {
        var r = new byte[ReportLength];
        // r[0] is the report id (0); the 90-byte packet follows
        r[2] = transaction;
        r[6] = cmd.Size;
        r[7] = cmd.Class;
        r[8] = cmd.Id;
        for (int i = 0; i < args.Length && i < 80; i++) r[9 + i] = args[i];
        r[89] = Checksum(r.AsSpan(1));
        return r;
    }

    /// <summary>XOR of bytes 2 to 87 of the 90-byte packet.</summary>
    public static byte Checksum(ReadOnlySpan<byte> packet)
    {
        byte c = 0;
        for (int i = 2; i < 88; i++) c ^= packet[i];
        return c;
    }

    public readonly record struct Reply(byte Status, byte Transaction, byte Size, byte Class, byte Id, byte[] Data);

    /// <summary>A 91-byte feature report read back (report id first).</summary>
    public static Reply Parse(ReadOnlySpan<byte> report)
    {
        var p = report[1..];
        int size = Math.Min((int)p[5], 80);
        return new Reply(p[0], p[1], p[5], p[6], p[7], p.Slice(8, size).ToArray());
    }

    public static bool IsOurs(Reply r, byte transaction, (byte Size, byte Class, byte Id) cmd) =>
        r.Status != StatusBusy && r.Transaction == transaction && r.Class == cmd.Class && r.Id == cmd.Id;

    public static SnapTapState ParseSnapTap(byte[] d)
    {
        var pairs = new List<SnapPair>();
        for (int i = 3; i + 2 < d.Length; i += 3) pairs.Add(new SnapPair(d[i], d[i + 1], d[i + 2]));
        return new SnapTapState(d.Length > 0 ? d[0] : (byte)0, d.Length > 2 && d[2] != 0, pairs);
    }

    /// <summary>The "set" arguments the way Synapse sends them: slot, number of pairs, on/off, then 3 bytes per pair in use.</summary>
    public static byte[] SnapTapArgs(byte slot, bool enabled, IReadOnlyList<SnapPair> used)
    {
        var a = new byte[3 + 3 * used.Count];
        a[0] = slot;
        a[1] = (byte)used.Count;
        a[2] = (byte)(enabled ? 1 : 0);
        for (int i = 0; i < used.Count; i++)
        {
            a[3 + 3 * i] = used[i].Key1;
            a[4 + 3 * i] = used[i].Key2;
            a[5 + 3 * i] = used[i].Mode;
        }
        return a;
    }

    /// <summary>The profile ids stored in the keyboard (its slots).</summary>
    public static List<byte> ParseProfileList(byte[] d) => d.Length == 0 ? new() : d.Skip(1).Take(d[0]).ToList();

    // Synapse's analog key ids (its layout table), the ones a Snap Tap pair is likely to use
    private static readonly Dictionary<byte, string> Keys = new()
    {
        [31] = "A", [50] = "B", [48] = "C", [33] = "D", [19] = "E", [34] = "F", [35] = "G", [36] = "H", [24] = "I", [37] = "J",
        [38] = "K", [39] = "L", [52] = "M", [51] = "N", [25] = "O", [26] = "P", [17] = "Q", [20] = "R", [32] = "S", [21] = "T",
        [23] = "U", [49] = "V", [18] = "W", [47] = "X", [22] = "Y", [46] = "Z", [2] = "1", [3] = "2", [4] = "3", [5] = "4",
        [6] = "5", [7] = "6", [8] = "7", [9] = "8", [10] = "9", [11] = "0", [83] = "↑", [84] = "↓", [79] = "←", [89] = "→",
        [61] = "Espaço", [44] = "Shift", [58] = "Ctrl", [60] = "Alt", [16] = "Tab", [30] = "Caps",
    };

    public static string KeyName(byte id) => Keys.TryGetValue(id, out var n) ? n == "Espaço" ? Lang.T("Espaço", "Space") : n : "#" + id;

    // LAST_INPUT, PRIO_LEFT, PRIO_RIGHT, NEUTRAL, COMPARE_LEVEL in Synapse
    public static string ModeName(byte mode) => mode switch
    {
        0 => Lang.T("última tecla vale", "last input wins"),
        1 => Lang.T("a primeira tem prioridade", "first key wins"),
        2 => Lang.T("a segunda tem prioridade", "second key wins"),
        3 => Lang.T("as duas anulam", "both cancel"),
        4 => Lang.T("a mais apertada vale", "deeper press wins"),
        _ => Lang.T("modo ", "mode ") + mode,
    };

    public static string PairText(SnapPair p) => $"{KeyName(p.Key1)} ⇄ {KeyName(p.Key2)}";
}
