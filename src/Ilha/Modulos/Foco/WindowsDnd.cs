using System;
using System.Runtime.InteropServices;

namespace Ilha.Focus;

/// <summary>
/// Windows' Do Not Disturb, through the shell's own QuietHoursSettings COM server (undocumented; CLSID and IID from
/// Rafael Rivera's reverse engineering, used by other tools on Windows 11): its UserSelectedProfile is
/// "Microsoft.QuietHoursProfile.Unrestricted" (off), ".PriorityOnly" (Windows 11's Do Not Disturb) or ".AlarmsOnly".
/// Tried on 03/11/2026 on his PC (build 26300), the other two ways failed: writing the quiet hours WNF state changes
/// the value but not the taskbar (it is only the shell announcing its state), and FocusSessionManager.TryStartFocusSession
/// answers "access denied" outside a packaged app. Switching through this server moved the WNF state with it, both ways.
/// Only what the island turned on is turned off again: Do Not Disturb that was already on stays on.
/// </summary>
internal static unsafe class WindowsDnd
{
    private static readonly Guid Clsid = new("F53321FA-34F8-4B7F-B9A3-361877CB94CF");
    private static readonly Guid Iid = new("6BFF4732-81EC-4FFB-AE67-B6C1BC29631F");
    public const string Off = "Microsoft.QuietHoursProfile.Unrestricted";
    public const string On = "Microsoft.QuietHoursProfile.PriorityOnly";

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint ctx, in Guid iid, out IntPtr obj);
    private const uint CLSCTX_ALL = 0x17;

    // the profile that was there before the island turned Do Not Disturb on (null: the island did not change it)
    private static string? _before;
    private static readonly object _lock = new();

    private static T With<T>(Func<IntPtr, T> use)
    {
        int hr = CoCreateInstance(Clsid, IntPtr.Zero, CLSCTX_ALL, Iid, out var obj);
        if (hr != 0) throw new COMException("QuietHoursSettings", hr);
        try { return use(obj); }
        finally { Marshal.Release(obj); }
    }

    /// <summary>The profile in use ("Microsoft.QuietHoursProfile.…").</summary>
    public static string Read() => With(obj =>
    {
        IntPtr s;
        var get = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(IntPtr**)obj)[3];
        int hr = get(obj, &s);
        if (hr != 0) throw new COMException("get_UserSelectedProfile", hr);
        try { return Marshal.PtrToStringUni(s) ?? ""; }
        finally { Marshal.FreeCoTaskMem(s); }
    });

    /// <summary>
    /// Writes the profile and reads it back: a change that arrives while the shell is still applying the one before is
    /// dropped without an error (an off then an on in a row, on 03/11/2026), so it is written again after a short wait.
    /// </summary>
    internal static void Write(string profile)
    {
        for (int attempt = 0; ; attempt++)
        {
            With(obj =>
            {
                var put = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)(*(IntPtr**)obj)[4];
                int hr;
                fixed (char* p = profile) hr = put(obj, p);
                if (hr != 0) throw new COMException("put_UserSelectedProfile", hr);
                return 0;
            });
            System.Threading.Thread.Sleep(300 + attempt * 400);
            if (string.Equals(Read(), profile, StringComparison.OrdinalIgnoreCase)) return;
            if (attempt == 4) throw new InvalidOperationException("o Windows não aceitou o perfil " + Short(profile));
        }
    }

    /// <summary>Turns Do Not Disturb on, unless it already is. Run off the interface thread (it is a call to the shell).</summary>
    public static bool Start(out string detail)
    {
        lock (_lock)
        {
            try
            {
                string now = Read();
                if (_before != null) { detail = "já ligado pela ilha"; return true; }
                if (!string.Equals(now, Off, StringComparison.OrdinalIgnoreCase)) { detail = $"já estava ligado ({Short(now)}), fica como está"; return true; }
                Write(On);
                _before = now;
                detail = "ligado";
                return true;
            }
            catch (Exception e) { detail = e.Message; return false; }
        }
    }

    /// <summary>Puts back the profile the island replaced; nothing when it did not change it.</summary>
    public static void Stop()
    {
        lock (_lock)
        {
            if (_before is not { } back) return;
            _before = null;
            try { Write(back); }
            catch (Exception e) { Log.Escrever("foco: o Não Perturbe do Windows não voltou: " + e.Message); }
        }
    }

    public static bool Ours => _before != null;

    private static string Short(string profile) => profile.Replace("Microsoft.QuietHoursProfile.", "");
}
