using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Ilha.Keyboard;

/// <summary>
/// The Huntsman V3 HE's control interface (VID 1532, PID 02EA, interface 3, 91-byte feature reports), found through
/// SetupAPI and opened for each command. Synapse talks to the same interface: a reply that belongs to another
/// transaction is skipped and read again, and a busy keyboard is asked again a few milliseconds later. Calls block:
/// run them off the interface thread.
/// </summary>
internal static class RazerDevice
{
    public const ushort Vendor = 0x1532, Product = 0x02EA;
    private const string Match = "vid_1532&pid_02ea&mi_03";
    private static readonly object Gate = new();
    private static byte _transaction;
    private static string? _path;

    public static bool Present() => Find() != null;

    /// <summary>The device path of the interface with 91-byte feature reports, or null (not plugged in).</summary>
    public static string? Find()
    {
        lock (Gate)
        {
            if (_path != null && Probe(_path)) return _path;
            _path = null;
            HidD_GetHidGuid(out var guid);
            IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return null;
            try
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
                {
                    SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out uint need, IntPtr.Zero);
                    if (need == 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal((int)need);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, buf, need, out _, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(buf + 4) ?? "";
                        if (path.Contains(Match, StringComparison.OrdinalIgnoreCase) && Probe(path)) return _path = path;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return null;
        }
    }

    private static bool Probe(string path)
    {
        using var h = Open(path);
        if (h == null) return false;
        if (!HidD_GetPreparsedData(h, out var pd)) return false;
        try
        {
            var caps = new byte[64];
            HidP_GetCaps(pd, caps);
            return BitConverter.ToUInt16(caps, 8) == RazerProtocol.ReportLength;
        }
        finally { HidD_FreePreparsedData(pd); }
    }

    private static SafeFileHandle? Open(string path)
    {
        var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    /// <summary>Sends one command and returns its data, or null (unplugged, busy too long, refused).</summary>
    public static byte[]? Command((byte Size, byte Class, byte Id) cmd, ReadOnlySpan<byte> args)
    {
        string? path = Find();
        if (path == null) return null;
        byte[] a = args.ToArray();
        lock (Gate)
        {
            using var h = Open(path);
            if (h == null) return null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                _transaction = (byte)(_transaction % 31 + 1);
                byte t = _transaction;
                var report = RazerProtocol.Build(t, cmd, a);
                if (!HidD_SetFeature(h, report, report.Length)) { Thread.Sleep(20); continue; }
                var back = new byte[RazerProtocol.ReportLength];
                for (int i = 0; i < 30; i++)
                {
                    Thread.Sleep(i < 5 ? 3 : 8);
                    Array.Clear(back);
                    if (!HidD_GetFeature(h, back, back.Length)) break;
                    var r = RazerProtocol.Parse(back);
                    if (!RazerProtocol.IsOurs(r, t, cmd)) continue;
                    if (r.Status == RazerProtocol.StatusOk) return r.Data;
                    Log.Escrever($"teclado: comando {cmd.Class:X2} {cmd.Id:X2} voltou com status {r.Status}");
                    return null;
                }
            }
            return null;
        }
    }

    public static SnapTapState? ReadSnapTap(byte slot = RazerProtocol.LiveSlot) =>
        Command(RazerProtocol.GetSnapTap, new[] { slot }) is { } d ? RazerProtocol.ParseSnapTap(d) : null;

    /// <summary>Turns the live slot's Snap Tap on or off, keeping its pairs exactly as read. True when the keyboard accepted.</summary>
    public static bool SetSnapTap(SnapTapState current, bool enabled)
    {
        var args = RazerProtocol.SnapTapArgs(current.Slot == 0 ? RazerProtocol.LiveSlot : current.Slot, enabled, current.Used);
        return Command(RazerProtocol.SetSnapTap, args) != null;
    }

    private const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, byte[] caps);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, uint size, out uint need, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint create, uint flags, IntPtr template);
}
