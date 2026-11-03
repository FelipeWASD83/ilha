using System;

namespace Ilha.Midia;

/// <summary>
/// Releases a WinRT object right now, on the calling thread, instead of leaving it to the .NET finalizer (3.9.0).
/// The three crashes up to 03/11/2026 were all on the finalizer thread releasing an object of the system media
/// controls (once inside Windows.Media.MediaControl.dll) while the interface thread was using the session manager;
/// readings released here never reach the finalizer.
/// </summary>
internal static class WinRtRelease
{
    public static void Now(object? o)
    {
        if (o is not WinRT.IWinRTObject w) return;
        try { w.NativeObject.Dispose(); } catch (Exception) { }
    }
}
