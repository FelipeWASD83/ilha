using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Ilha.Volume;
using AudioCom = Ilha.Volume.Com;

namespace Ilha.Privacidade;

// Media Foundation's sensor activity monitor (mfidl.h, checked against the 10.0.26100 SDK on 02/11/2026). Vtable
// order, nothing reordered, everything [PreserveSig].

[ComImport, Guid("D0CEF145-B3F4-4340-A2E5-7A5080CA05CB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSensorActivityMonitor
{
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
}

[ComImport, Guid("DE5072EE-DBE3-46DC-8A87-B6F631194751"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSensorActivitiesReportCallback
{
    [PreserveSig] int OnActivitiesReport(IMFSensorActivitiesReport report);
}

[ComImport, Guid("683F7A5E-4A19-43CD-B1A9-DBF4AB3F7777"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSensorActivitiesReport
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetActivityReport(uint index, out IMFSensorActivityReport report);
    [PreserveSig] int GetActivityReportByDeviceName([MarshalAs(UnmanagedType.LPWStr)] string name, out IMFSensorActivityReport report);
}

[ComImport, Guid("3E8C4BE1-A8C2-4528-90DE-2851BDE5FEAD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSensorActivityReport
{
    [PreserveSig] int GetFriendlyName(IntPtr name, uint size, out uint written);
    [PreserveSig] int GetSymbolicLink(IntPtr link, uint size, out uint written);
    [PreserveSig] int GetProcessCount(out uint count);
    [PreserveSig] int GetProcessActivity(uint index, out IMFSensorProcessActivity activity);
}

[ComImport, Guid("39DC7F4A-B141-4719-813C-A7F46162A2B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSensorProcessActivity
{
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int GetStreamingState([MarshalAs(UnmanagedType.Bool)] out bool streaming);
    [PreserveSig] int GetStreamingMode(out int mode);
    [PreserveSig] int GetReportTime(out long filetime);
}

/// <summary>
/// Who is recording from a microphone or streaming from a camera right now, by process id, from documented APIs:
/// the capture sessions of every active microphone (WASAPI: a session in the Active state is an app recording) and
/// the Media Foundation sensor activity monitor (the processes streaming from each camera). The ConsentStore in the
/// registry stopped getting these times on this Windows build (on 26300 its newest entries are from 15/10 for the
/// microphone and 18/10/2026 for the camera), so the island no longer reads it.
/// Everything COM lives on one MTA thread fed by a queue (session notices only reach an MTA thread); the system
/// callbacks only queue a reread. Nothing polls: idle, the thread is blocked on the queue.
/// </summary>
internal sealed class UsoAoVivo : IDisposable
{
    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfsensorgroup.dll")]
    private static extern int MFCreateSensorActivityMonitor(IMFSensorActivitiesReportCallback callback, out IMFSensorActivityMonitor monitor);

    private const uint MF_VERSION = 0x00020070, MFSTARTUP_LITE = 1;
    private const int SESSION_ACTIVE = 1, SESSION_EXPIRED = 2;

    private sealed class Session
    {
        public required IAudioSessionControl2 Control;
        public required SessionSink Sink;
        public bool Registered;
        public uint Pid;
        public bool System;
    }

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Action<IReadOnlyCollection<uint>, IReadOnlyCollection<uint>> _changed;
    private readonly uint _own = (uint)Environment.ProcessId;
    private int _pendingRead, _pendingSessions, _pendingDevices;

    // only the worker thread touches these
    private IMMDeviceEnumerator? _enum;
    private readonly DeviceSink _deviceSink;
    private bool _deviceSinkRegistered;
    private readonly CreatedSink _created;
    private readonly List<IAudioSessionManager2> _managers = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private IMFSensorActivityMonitor? _monitor;
    private readonly CameraSink _cameraSink;
    private bool _mfStarted;
    private HashSet<uint> _camera = new();
    private HashSet<uint> _lastMic = new(), _lastCamera = new();
    private bool _published;

    /// <summary>How many microphones are watched and whether the camera monitor started (for the self test's probe).</summary>
    public int Microphones { get; private set; }
    public bool CameraOn { get; private set; }

    /// <summary>Called on the worker thread with the microphone and camera process ids, only when they change.</summary>
    public UsoAoVivo(Action<IReadOnlyCollection<uint>, IReadOnlyCollection<uint>> changed)
    {
        _changed = changed;
        _deviceSink = new DeviceSink(this);
        _created = new CreatedSink(this);
        _cameraSink = new CameraSink(this);
    }

    public void Start()
    {
        var t = new Thread(Loop) { IsBackground = true, Name = "ilha-privacidade" };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        Queue(Open);
    }

    /// <summary>Reads again (the 30 s check while something is in use: a process that died keeps no session).</summary>
    public void Refresh() => RequestRead();

    public void Dispose()
    {
        try { _queue.CompleteAdding(); } catch { }
    }

    private void Queue(Action a)
    {
        try { _queue.TryAdd(a); } catch { /* closed */ }
    }

    private void RequestRead()
    {
        if (Interlocked.Exchange(ref _pendingRead, 1) == 0) Queue(Read);
    }

    private void RequestSessions()
    {
        if (Interlocked.Exchange(ref _pendingSessions, 1) == 0) Queue(RereadSessions);
    }

    private void RequestDevices()
    {
        if (Interlocked.Exchange(ref _pendingDevices, 1) == 0) Queue(RebuildDevices);
    }

    private void Loop()
    {
        try
        {
            foreach (var a in _queue.GetConsumingEnumerable())
            {
                try { a(); }
                catch (Exception e) { Log.Escrever("privacidade: " + e.Message); }
            }
        }
        catch (Exception e) { Log.Escrever("privacidade: laço: " + e.Message); }
        finally
        {
            try { Close(); } catch { }
        }
    }

    // ---------------------------------------------------------------- on the worker thread

    // the camera monitor starts first: the first read (published even when empty) then already says how it went
    private void Open()
    {
        try
        {
            _mfStarted = MFStartup(MF_VERSION, MFSTARTUP_LITE) == 0;
            int hr = MFCreateSensorActivityMonitor(_cameraSink, out var monitor);
            if (hr != 0) Log.Escrever($"privacidade: monitor de câmera não criou (0x{hr:X8})");
            else
            {
                _monitor = monitor;
                hr = _monitor.Start();
                CameraOn = hr == 0;
                if (hr != 0) Log.Escrever($"privacidade: monitor de câmera não ligou (0x{hr:X8})");
            }
        }
        catch (Exception e) { Log.Escrever("privacidade: câmera: " + e.Message); }
        try
        {
            _enum = (IMMDeviceEnumerator)ComDeAudio.NovoEnumerador();
            _deviceSinkRegistered = _enum.RegisterEndpointNotificationCallback(_deviceSink) == 0;
            RebuildDevices();
        }
        catch (Exception e) { Log.Escrever("privacidade: microfones não abriram: " + e.Message); Read(); }
    }

    private void Close()
    {
        if (_monitor != null)
        {
            try { _monitor.Stop(); } catch { }
            AudioCom.Soltar(_monitor);
            _monitor = null;
        }
        if (_mfStarted) { try { MFShutdown(); } catch { } _mfStarted = false; }
        CloseDevices();
        if (_enum != null && _deviceSinkRegistered) try { _enum.UnregisterEndpointNotificationCallback(_deviceSink); } catch { }
        AudioCom.Soltar(_enum);
        _enum = null;
    }

    private void CloseDevices()
    {
        foreach (var s in _sessions.Values) Release(s);
        _sessions.Clear();
        foreach (var m in _managers)
        {
            try { m.UnregisterSessionNotification(_created); } catch { }
            AudioCom.Soltar(m);
        }
        _managers.Clear();
    }

    private static void Release(Session s)
    {
        if (s.Registered) try { s.Control.UnregisterAudioSessionNotification(s.Sink); } catch { }
        s.Registered = false;
        AudioCom.Soltar(s.Control);
    }

    /// <summary>A microphone came, went or changed state: one session manager per active capture device.</summary>
    private void RebuildDevices()
    {
        Interlocked.Exchange(ref _pendingDevices, 0);
        CloseDevices();
        if (_enum == null || _enum.EnumAudioEndpoints(FluxoAudio.Captura, EstadoAparelho.Ativo, out var list) != 0 || list == null) { Read(); return; }
        try
        {
            list.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                if (list.Item(i, out var d) != 0 || d == null) continue;
                try
                {
                    var iid = typeof(IAudioSessionManager2).GUID;
                    if (d.Activate(ref iid, AudioCom.CLSCTX_ALL, IntPtr.Zero, out var obj) != 0) continue;
                    var m = (IAudioSessionManager2)obj;
                    // registered before reading, so a session created in between still gets noticed
                    m.RegisterSessionNotification(_created);
                    _managers.Add(m);
                }
                catch (Exception e) { Log.Escrever("privacidade: microfone: " + e.Message); }
                finally { AudioCom.Soltar(d); }
            }
        }
        finally { AudioCom.Soltar(list); }
        Microphones = _managers.Count;
        RereadSessions();
    }

    /// <summary>The sessions of every microphone (the GetCount also turns the new-session notices on).</summary>
    private void RereadSessions()
    {
        Interlocked.Exchange(ref _pendingSessions, 0);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in _managers)
        {
            if (m.GetSessionEnumerator(out var list) != 0 || list == null) continue;
            try
            {
                list.GetCount(out int n);
                for (int i = 0; i < n; i++)
                {
                    if (list.GetSession(i, out var obj) != 0 || obj == null) continue;
                    IAudioSessionControl2? c2 = null;
                    bool kept = false;
                    try
                    {
                        c2 = (IAudioSessionControl2)obj;
                        if (c2.GetSessionInstanceIdentifier(out var inst) != 0 || string.IsNullOrEmpty(inst)) continue;
                        seen.Add(inst);
                        if (_sessions.ContainsKey(inst)) continue;
                        if (c2.GetState(out int state) == 0 && state == SESSION_EXPIRED) continue;
                        var s = new Session { Control = c2, Sink = new SessionSink(this) };
                        c2.GetProcessId(out s.Pid);
                        s.System = c2.IsSystemSoundsSession() == 0;
                        s.Registered = c2.RegisterAudioSessionNotification(s.Sink) == 0;
                        _sessions[inst] = s;
                        kept = true;
                    }
                    catch (Exception e) { Log.Escrever("privacidade: sessão: " + e.Message); }
                    finally
                    {
                        if (!kept) { AudioCom.Soltar(c2); if (!ReferenceEquals(c2, obj)) AudioCom.Soltar(obj); }
                    }
                }
            }
            finally { AudioCom.Soltar(list); }
        }
        foreach (var inst in _sessions.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Release(_sessions[inst]);
            _sessions.Remove(inst);
        }
        Read();
    }

    private void Read()
    {
        Interlocked.Exchange(ref _pendingRead, 0);
        var mic = new HashSet<uint>();
        foreach (var (inst, s) in _sessions.ToList())
        {
            if (s.Control.GetState(out int state) != 0) continue;
            if (state == SESSION_EXPIRED) { Release(s); _sessions.Remove(inst); continue; }
            if (state == SESSION_ACTIVE && !s.System && s.Pid != 0 && s.Pid != _own) mic.Add(s.Pid);
        }
        var cam = new HashSet<uint>(_camera.Where(p => p != 0 && p != _own));
        if (_published && mic.SetEquals(_lastMic) && cam.SetEquals(_lastCamera)) return;
        _published = true;
        _lastMic = mic;
        _lastCamera = cam;
        _changed(mic, cam);
    }

    /// <summary>The processes streaming from any camera in a report (read during the callback: the report is not kept).</summary>
    internal static HashSet<uint> StreamingPids(IMFSensorActivitiesReport report)
    {
        var pids = new HashSet<uint>();
        if (report.GetCount(out uint n) != 0) return pids;
        for (uint i = 0; i < n; i++)
        {
            if (report.GetActivityReport(i, out var device) != 0 || device == null) continue;
            try
            {
                if (device.GetProcessCount(out uint pc) != 0) continue;
                for (uint j = 0; j < pc; j++)
                {
                    if (device.GetProcessActivity(j, out var act) != 0 || act == null) continue;
                    try
                    {
                        if (act.GetStreamingState(out bool on) == 0 && on && act.GetProcessId(out uint pid) == 0) pids.Add(pid);
                    }
                    finally { AudioCom.Soltar(act); }
                }
            }
            finally { AudioCom.Soltar(device); }
        }
        return pids;
    }

    // ---------------------------------------------------------------- callbacks (system threads: only queue work)

    private sealed class CameraSink : IMFSensorActivitiesReportCallback
    {
        private readonly UsoAoVivo _o;
        public CameraSink(UsoAoVivo o) => _o = o;

        public int OnActivitiesReport(IMFSensorActivitiesReport report)
        {
            try
            {
                var pids = StreamingPids(report);
                _o.Queue(() => { _o._camera = pids; _o.Read(); });
            }
            catch (Exception e) { Log.Escrever("privacidade: relatório da câmera: " + e.Message); }
            return 0;
        }
    }

    private sealed class CreatedSink : IAudioSessionNotification
    {
        private readonly UsoAoVivo _o;
        public CreatedSink(UsoAoVivo o) => _o = o;

        public int OnSessionCreated(IntPtr session)
        {
            try { _o.RequestSessions(); } catch { }
            return 0;
        }
    }

    private sealed class SessionSink : IAudioSessionEvents
    {
        private readonly UsoAoVivo _o;
        public SessionSink(UsoAoVivo o) => _o = o;

        public int OnDisplayNameChanged(string? name, IntPtr context) => 0;
        public int OnIconPathChanged(string? path, IntPtr context) => 0;
        public int OnSimpleVolumeChanged(float level, bool mute, IntPtr context) => 0;
        public int OnChannelVolumeChanged(uint channels, IntPtr volumes, uint changed, IntPtr context) => 0;
        public int OnGroupingParamChanged(IntPtr group, IntPtr context) => 0;

        public int OnStateChanged(int state)
        {
            try { _o.RequestRead(); } catch { }
            return 0;
        }

        public int OnSessionDisconnected(int reason)
        {
            try { _o.RequestSessions(); } catch { }
            return 0;
        }
    }

    private sealed class DeviceSink : IMMNotificationClient
    {
        private readonly UsoAoVivo _o;
        public DeviceSink(UsoAoVivo o) => _o = o;

        public int OnDeviceStateChanged(string id, uint state) { try { _o.RequestDevices(); } catch { } return 0; }
        public int OnDeviceAdded(string id) { try { _o.RequestDevices(); } catch { } return 0; }
        public int OnDeviceRemoved(string id) { try { _o.RequestDevices(); } catch { } return 0; }
        public int OnDefaultDeviceChanged(FluxoAudio flow, PapelAudio role, string? id) => 0;
        public int OnPropertyValueChanged(string id, ChaveDePropriedade key) => 0;
    }
}
