using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Ilha.Volume;
using AudioCom = Ilha.Volume.Com;

namespace Ilha.Reactions;

/// <summary>
/// A hidden top-level window (not message-only: WM_DEVICECHANGE is a broadcast and message-only windows never get
/// broadcasts) that hears the clipboard changing and drives arriving or leaving.
/// </summary>
internal sealed class SystemEvents : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D, WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004, DBT_DEVTYP_VOLUME = 2;

    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr h);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr h);

    public event Action? ClipboardChanged;
    /// <summary>Drive letters that arrived (true) or left (false).</summary>
    public event Action<IReadOnlyList<char>, bool>? DrivesChanged;

    private bool _listening;

    public SystemEvents()
    {
        CreateHandle(new CreateParams
        {
            Caption = "Ilha reações",
            ExStyle = 0x80, // WS_EX_TOOLWINDOW: never in the taskbar or Alt+Tab
            Style = unchecked((int)0x80000000), // WS_POPUP, never shown
        });
        _listening = AddClipboardFormatListener(Handle);
        if (!_listening) Log.Escrever("reações: a área de transferência não deixou ouvir");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE) { ClipboardChanged?.Invoke(); return; }
        if (m.Msg == WM_DEVICECHANGE && (m.WParam == DBT_DEVICEARRIVAL || m.WParam == DBT_DEVICEREMOVECOMPLETE) && m.LParam != IntPtr.Zero
            && Marshal.ReadInt32(m.LParam, 4) == DBT_DEVTYP_VOLUME)
        {
            // DEV_BROADCAST_VOLUME: size, devicetype, reserved, unitmask (bit 0 = A:), flags (1 = media, 2 = network)
            uint mask = (uint)Marshal.ReadInt32(m.LParam, 12);
            ushort flags = (ushort)Marshal.ReadInt16(m.LParam, 16);
            if ((flags & 2) == 0) DrivesChanged?.Invoke(Letters(mask), m.WParam == DBT_DEVICEARRIVAL);
        }
        base.WndProc(ref m);
    }

    internal static List<char> Letters(uint mask)
    {
        var l = new List<char>();
        for (int i = 0; i < 26; i++) if ((mask & (1u << i)) != 0) l.Add((char)('A' + i));
        return l;
    }

    public void Dispose()
    {
        if (_listening) RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }
}

/// <summary>
/// The Downloads folder: a download counts when its file reaches its final name (browsers write to a temporary name
/// and rename it at the end) or, for a file written in place, once nothing holds it open any more.
/// </summary>
internal sealed class DownloadWatch : IDisposable
{
    private static readonly string[] Temporary = { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload", ".!ut", ".aria2", ".temp" };
    private readonly FileSystemWatcher _w;
    private readonly Action<string> _done;
    private readonly global::Ilha.Extras.Recentes _seen = new(TimeSpan.FromSeconds(30));
    private readonly CancellationTokenSource _stop = new();

    public string Folder { get; }

    public DownloadWatch(string folder, Action<string> done)
    {
        Folder = folder;
        _done = done;
        _w = new FileSystemWatcher(folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName, InternalBufferSize = 64 * 1024 };
        _w.Renamed += (_, e) => { if (IsTemporary(e.OldFullPath) && !IsTemporary(e.FullPath)) Finished(e.FullPath, waitClosed: false); };
        _w.Created += (_, e) => { if (!IsTemporary(e.FullPath)) Finished(e.FullPath, waitClosed: true); };
        _w.EnableRaisingEvents = true;
    }

    /// <summary>A browser's working name (or a hidden or system file nobody downloaded).</summary>
    internal static bool IsTemporary(string path)
    {
        string name = Path.GetFileName(path);
        if (name.StartsWith("~$") || name.StartsWith(".") || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;
        string ext = Path.GetExtension(name).ToLowerInvariant();
        if (Temporary.Contains(ext)) return true;
        // Chrome and Edge start with "Unconfirmed 123456.crdownload" and Firefox with a random name plus ".part"
        return name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Não confirmado ", StringComparison.OrdinalIgnoreCase);
    }

    private void Finished(string path, bool waitClosed)
    {
        if (!_seen.Novo(path, DateTime.UtcNow)) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                // a file written in place is done when the writer lets go of it
                if (waitClosed && !global::Ilha.Extras.Arquivo.EsperarFechar(path, TimeSpan.FromMinutes(10), _stop.Token)) return;
                Thread.Sleep(300);
                if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) != 0) return;
                _done(path);
            }
            catch (Exception e) { Log.Escrever("reações: download " + Path.GetFileName(path) + ": " + e.Message); }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _w.Dispose();
    }
}

/// <summary>
/// The default microphone's mute (Windows' own, the one the sound settings and many headsets' drivers flip): a callback on
/// its endpoint volume, moved to the new default when it changes. Callbacks come on system threads.
/// </summary>
internal sealed class MicMuteWatch : IDisposable
{
    private readonly Action<bool, string> _changed;
    private readonly Callback _cb;
    private readonly DeviceSink _sink;
    private IMMDeviceEnumerator? _en;
    private IAudioEndpointVolume? _vol;
    private bool? _muted;
    private string _name = "";
    private bool _sinkOn;
    private readonly object _lock = new();

    /// <summary>`changed` gets the new state and the device's name, on a system thread.</summary>
    public MicMuteWatch(Action<bool, string> changed)
    {
        _changed = changed;
        _cb = new Callback(this);
        _sink = new DeviceSink(this);
        _en = (IMMDeviceEnumerator)ComDeAudio.NovoEnumerador();
        _sinkOn = _en.RegisterEndpointNotificationCallback(_sink) == 0;
        Attach();
    }

    private void Attach()
    {
        lock (_lock)
        {
            Detach();
            if (_en == null || _en.GetDefaultAudioEndpoint(FluxoAudio.Captura, PapelAudio.Console, out var dev) != 0 || dev == null) return;
            try
            {
                _name = Name(dev);
                var iid = AudioCom.IID_IAudioEndpointVolume;
                if (dev.Activate(ref iid, AudioCom.CLSCTX_ALL, IntPtr.Zero, out var o) != 0) return;
                _vol = (IAudioEndpointVolume)o;
                _vol.GetMute(out bool m);
                _muted = m;
                _vol.RegisterControlChangeNotify(_cb);
            }
            finally { AudioCom.Soltar(dev); }
        }
    }

    private void Detach()
    {
        if (_vol == null) return;
        try { _vol.UnregisterControlChangeNotify(_cb); } catch { }
        AudioCom.Soltar(_vol);
        _vol = null;
    }

    private static string Name(IMMDevice dev)
    {
        try
        {
            if (dev.OpenPropertyStore(AudioCom.STGM_READ, out var store) != 0) return "";
            try
            {
                var key = Propriedades.NomeDoAparelho;
                if (store.GetValue(ref key, out var v) == 0 && v.Tipo == ValorDePropriedade.VT_LPWSTR)
                {
                    string s = Marshal.PtrToStringUni(v.Ponteiro) ?? "";
                    AudioCom.PropVariantClear(ref v);
                    return s;
                }
            }
            finally { AudioCom.Soltar(store); }
        }
        catch { }
        return "";
    }

    private void Notified(bool muted)
    {
        bool changed;
        string name;
        lock (_lock)
        {
            changed = _muted != null && _muted != muted;
            _muted = muted;
            name = _name;
        }
        // the first reading and volume moves (which also land here) are not news
        if (changed) _changed(muted, name);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Detach();
            if (_en != null && _sinkOn) try { _en.UnregisterEndpointNotificationCallback(_sink); } catch { }
            AudioCom.Soltar(_en);
            _en = null;
        }
    }

    private sealed class Callback : IAudioEndpointVolumeCallback
    {
        private readonly MicMuteWatch _w;
        public Callback(MicMuteWatch w) => _w = w;

        public int OnNotify(IntPtr data)
        {
            if (data != IntPtr.Zero) _w.Notified(Marshal.ReadInt32(data, 16) != 0);
            return 0;
        }
    }

    private sealed class DeviceSink : IMMNotificationClient
    {
        private readonly MicMuteWatch _w;
        public DeviceSink(MicMuteWatch w) => _w = w;

        public int OnDeviceStateChanged(string id, uint state) => 0;
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) => 0;
        public int OnPropertyValueChanged(string id, ChaveDePropriedade key) => 0;

        public int OnDefaultDeviceChanged(FluxoAudio flow, PapelAudio role, string? id)
        {
            // never re-register inside the callback: a pool thread does it
            if (flow == FluxoAudio.Captura && role == PapelAudio.Console)
                ThreadPool.QueueUserWorkItem(_ => { try { _w.Attach(); } catch (Exception e) { Log.Escrever("reações: microfone padrão: " + e.Message); } });
            return 0;
        }
    }
}

/// <summary>
/// Internet up or down, from Windows' own network status (no probing of any server). A change only counts once it held
/// for a few seconds (Wi-Fi roaming and waking from sleep flap), and the first reading is only the reference.
/// </summary>
internal sealed class InternetWatch : IDisposable
{
    private readonly Action<bool> _changed;
    private readonly System.Threading.Timer _settle;
    private bool? _reported;
    private bool _pending;

    public InternetWatch(Action<bool> changed)
    {
        _changed = changed;
        _reported = Online();
        _settle = new System.Threading.Timer(_ => Settle(), null, Timeout.Infinite, Timeout.Infinite);
        Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += OnStatus;
    }

    internal static bool Online()
    {
        try
        {
            var p = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            return p != null && p.GetNetworkConnectivityLevel() == Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess;
        }
        catch { return true; }
    }

    private void OnStatus(object? sender)
    {
        _pending = true;
        _settle.Change(4000, Timeout.Infinite);
    }

    private void Settle()
    {
        if (!_pending) return;
        _pending = false;
        bool now = Online();
        if (_reported == now) return;
        _reported = now;
        _changed(now);
    }

    public void Dispose()
    {
        Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged -= OnStatus;
        _settle.Dispose();
    }
}
