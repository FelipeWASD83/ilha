using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Ilha.Midia;

namespace Ilha.Recorder;

/// <summary>
/// WASAPI capture (the PC's sound by loopback, or the default microphone) into a WAV file, on its own MTA thread. The
/// file's first sample is the session's start, a wall clock instant shared with ffmpeg's video, so every track lines up
/// with no offset: each packet is placed by its QPC time, gaps become silence (loopback sends nothing while nothing
/// plays), what was captured before the start is dropped, and the end is padded up to the stop instant.
/// </summary>
internal sealed class AudioCapture
{
    /// <summary>Gaps and overlaps smaller than this are left alone: correcting the device clock's tiny drift would click.</summary>
    private const double TOLERANCE_SECONDS = 0.06;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    private readonly bool _loopback;
    private readonly long _startWall;
    private readonly ManualResetEvent _stop = new(false);
    private readonly ManualResetEvent _ready = new(false);
    private long _stopWall;
    private Thread? _thread;
    private FileStream? _file;
    private byte[]? _formatBytes;
    private FormatoAudio _format;
    private long _written;
    private long _dataStart;

    public string Path { get; }
    public string? Error { get; private set; }
    /// <summary>Frames written so far (silence included).</summary>
    public long Frames => Interlocked.Read(ref _written);
    public string Name => _loopback ? "som do PC" : "microfone";

    public AudioCapture(bool loopback, string path, long startWallUtcTicks)
    {
        _loopback = loopback;
        Path = path;
        _startWall = startWallUtcTicks;
    }

    /// <summary>Opens the device and the file; false (with Error) when the device does not open in 3 s.</summary>
    public bool Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "ilha-gravador-" + (_loopback ? "pc" : "mic") };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        if (!_ready.WaitOne(3000)) Error ??= "o dispositivo não respondeu";
        return Error == null;
    }

    /// <summary>Pads up to the stop instant, closes the file and waits for the thread (a few seconds at most).</summary>
    public void Stop(long stopWallUtcTicks)
    {
        Interlocked.Exchange(ref _stopWall, stopWallUtcTicks);
        _stop.Set();
        if (_thread != null && !_thread.Join(5000)) Log.Escrever($"gravador: a captura do {Name} não parou em 5 s");
    }

    private static long Qpc100ns() => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));

    private void Run()
    {
        IMMDeviceEnumerator? en = null;
        try
        {
            en = (IMMDeviceEnumerator)ComDeAudio.NovoEnumerador();
            bool first = true;
            while (!_stop.WaitOne(0))
            {
                var end = Session(en, first);
                first = false;
                if (end == SessionEnd.Stop || _file == null) break;
                // the device went away (headset turned off, default changed): silence until it comes back
                if (_stop.WaitOne(1000)) break;
            }
        }
        catch (Exception e)
        {
            Error ??= e.Message;
            Log.Escrever($"gravador: captura do {Name}: {e.Message}");
        }
        finally
        {
            Finish();
            CoreAudio.Soltar(en);
            _ready.Set();
        }
    }

    private enum SessionEnd { Stop, Lost }

    private SessionEnd Session(IMMDeviceEnumerator en, bool first)
    {
        IMMDevice? dev = null;
        IAudioClient? client = null;
        IAudioCaptureClient? cap = null;
        IntPtr pFormat = IntPtr.Zero;
        using var evt = new AutoResetEvent(false);
        bool started = false;
        try
        {
            int hr = en.GetDefaultAudioEndpoint(_loopback ? FluxoAudio.Render : FluxoAudio.Captura, PapelAudio.Console, out dev);
            if (hr != 0 || dev == null)
            {
                if (first) Error = _loopback ? "nenhuma saída de som" : "nenhum microfone";
                return SessionEnd.Lost;
            }
            var iid = CoreAudio.IID_IAudioClient;
            if ((hr = dev.Activate(ref iid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out object o)) != 0) { if (first) Error = $"Activate 0x{hr:X8}"; return SessionEnd.Lost; }
            client = (IAudioClient)o;
            if ((hr = client.GetMixFormat(out pFormat)) != 0) { if (first) Error = $"GetMixFormat 0x{hr:X8}"; return SessionEnd.Lost; }
            int cb = Marshal.ReadInt16(pFormat) == unchecked((short)0xFFFE) ? 40 : 18 + Marshal.ReadInt16(pFormat, 16);
            var wf = new byte[Math.Clamp(cb, 18, 64)];
            Marshal.Copy(pFormat, wf, 0, wf.Length);
            if (FormatoAudio.Ler(wf) is not { Suportado: true } f) { if (first) Error = "formato de som não suportado"; return SessionEnd.Lost; }
            if (first) OpenFile(wf, f);
            else if (!wf.AsSpan().SequenceEqual(_formatBytes))
            {
                // the WAV has one format for good: a device that came back with another one stays as silence
                Log.Escrever($"gravador: o {Name} voltou em outro formato ({f}); o resto fica em silêncio");
                _stop.WaitOne();
                return SessionEnd.Stop;
            }
            uint flags = CoreAudio.AUDCLNT_STREAMFLAGS_EVENTCALLBACK | (_loopback ? CoreAudio.AUDCLNT_STREAMFLAGS_LOOPBACK : 0);
            if ((hr = client.Initialize(CoreAudio.AUDCLNT_SHAREMODE_SHARED, flags, 2_000_000, 0, pFormat, IntPtr.Zero)) != 0)
            { if (first) Error = $"Initialize 0x{hr:X8}"; return SessionEnd.Lost; }
            if ((hr = client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle())) != 0) { if (first) Error = $"SetEventHandle 0x{hr:X8}"; return SessionEnd.Lost; }
            var iidCap = CoreAudio.IID_IAudioCaptureClient;
            if ((hr = client.GetService(ref iidCap, out object oc)) != 0) { if (first) Error = $"GetService 0x{hr:X8}"; return SessionEnd.Lost; }
            cap = (IAudioCaptureClient)oc;
            if ((hr = client.Start()) != 0) { if (first) Error = $"Start 0x{hr:X8}"; return SessionEnd.Lost; }
            started = true;
            if (first) Log.Escrever($"gravador: {Name} abriu ({f})");
            _ready.Set();
            return Listen(cap, evt);
        }
        finally
        {
            if (started) { try { client!.Stop(); } catch { } }
            CoreAudio.Soltar(cap);
            CoreAudio.Soltar(client);
            CoreAudio.Soltar(dev);
            if (pFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(pFormat);
            if (first && Error != null) _ready.Set();
        }
    }

    private SessionEnd Listen(IAudioCaptureClient cap, AutoResetEvent evt)
    {
        long refQpc = Qpc100ns(), refWall = DateTime.UtcNow.Ticks;
        var waits = new WaitHandle[] { _stop, evt };
        while (true)
        {
            if (WaitHandle.WaitAny(waits, 100) == 0) return SessionEnd.Stop;
            while (true)
            {
                int hr = cap.GetNextPacketSize(out uint size);
                if (hr == CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED) return SessionEnd.Lost;
                if (hr < 0) return SessionEnd.Lost;
                if (size == 0) break;
                hr = cap.GetBuffer(out IntPtr data, out uint frames, out uint bflags, out _, out ulong qpc);
                if (hr == CoreAudio.AUDCLNT_S_BUFFER_EMPTY) break;
                if (hr == CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED) return SessionEnd.Lost;
                if (hr < 0) return SessionEnd.Lost;
                try
                {
                    long wall = refWall + ((long)qpc - refQpc);
                    Place(data, (int)frames, wall, (bflags & AUDCLNT_BUFFERFLAGS_SILENT) != 0);
                }
                finally { cap.ReleaseBuffer(frames); }
            }
        }
    }

    /// <summary>
    /// A packet that belongs at frame `packetIndex` (by the clock) with the file at `written`: how much silence goes before
    /// it and how many of its first frames are dropped (before the start, or overlapping what is already there).
    /// </summary>
    internal static (long Pad, int Skip) Placement(long written, long packetIndex, int frames, int rate)
    {
        long tol = (long)(rate * TOLERANCE_SECONDS);
        if (packetIndex + frames <= 0) return (0, frames);
        if (packetIndex < 0) return (0, (int)-packetIndex);
        if (packetIndex > written + tol) return (packetIndex - written, 0);
        if (packetIndex < written - tol) return (0, (int)Math.Min(frames, written - packetIndex));
        return (0, 0);
    }

    private int _peakBits;

    /// <summary>The loudest sample since the last call (0 to 1), and starts over: the pill's waveform reads it ~30 times a second.</summary>
    public float TakePeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));

    private unsafe void Measure(byte* p, int frames)
    {
        int count = frames * _format.Canais;
        float peak = 0;
        if (_format.Tipo == TipoAmostra.Float)
        {
            var s = (float*)p;
            for (int i = 0; i < count; i++) { float a = MathF.Abs(s[i]); if (a > peak) peak = a; }
        }
        else if (_format.Bits == 16)
        {
            var s = (short*)p;
            for (int i = 0; i < count; i++) { float a = MathF.Abs(s[i] / 32768f); if (a > peak) peak = a; }
        }
        else
        {
            // 24 and 32 bit integers: the top 16 bits of each sample are enough for a level
            int bytes = _format.Bits / 8;
            for (int i = 0; i < count; i++) { float a = MathF.Abs(*(short*)(p + i * bytes + bytes - 2) / 32768f); if (a > peak) peak = a; }
        }
        int seen;
        do
        {
            seen = Volatile.Read(ref _peakBits);
            if (BitConverter.Int32BitsToSingle(seen) >= peak) return;
        } while (Interlocked.CompareExchange(ref _peakBits, BitConverter.SingleToInt32Bits(peak), seen) != seen);
    }

    private unsafe void Place(IntPtr data, int frames, long wall, bool silent)
    {
        if (_file == null) return;
        if (!silent) Measure((byte*)data, frames);
        long index = (long)Math.Round((wall - _startWall) * (_format.Taxa / 10_000_000.0));
        var (pad, skip) = Placement(_written, index, frames, _format.Taxa);
        if (pad > 0) WriteSilence(pad);
        int n = frames - skip;
        if (n <= 0) return;
        if (silent) { WriteSilence(n); return; }
        _file.Write(new ReadOnlySpan<byte>((byte*)data + (long)skip * _format.Bloco, n * _format.Bloco));
        Interlocked.Add(ref _written, n);
    }

    private readonly byte[] _zeros = new byte[65536];

    private void WriteSilence(long frames)
    {
        long bytes = frames * _format.Bloco;
        while (bytes > 0)
        {
            int k = (int)Math.Min(bytes, _zeros.Length);
            _file!.Write(_zeros, 0, k);
            bytes -= k;
        }
        Interlocked.Add(ref _written, frames);
    }

    private void OpenFile(byte[] wf, FormatoAudio f)
    {
        _formatBytes = wf;
        _format = f;
        _file = new FileStream(Path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 18);
        using var w = new BinaryWriter(_file, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8);
        w.Write(0u);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write((uint)wf.Length);
        w.Write(wf);
        w.Write("data"u8);
        w.Write(0u);
        w.Flush();
        _dataStart = _file.Position;
    }

    /// <summary>
    /// The RIFF and data sizes of a track whose writer never closed it (the island quit mid-recording), set from the file
    /// length. Only the two size fields change. False when the file is not one of these WAVs.
    /// </summary>
    public static bool RepairHeader(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var head = new byte[Math.Min(4096, (int)Math.Min(int.MaxValue, f.Length))];
        int got = f.Read(head, 0, head.Length);
        if (got < 44 || head[0] != 'R' || head[1] != 'I' || head[2] != 'F' || head[3] != 'F') return false;
        int data = -1;
        for (int i = 12; i + 8 <= got; i++)
            if (head[i] == 'd' && head[i + 1] == 'a' && head[i + 2] == 't' && head[i + 3] == 'a') { data = i; break; }
        if (data < 0) return false;
        long len = f.Length;
        f.Position = 4;
        f.Write(BitConverter.GetBytes((uint)Math.Min(len - 8, uint.MaxValue)));
        f.Position = data + 4;
        f.Write(BitConverter.GetBytes((uint)Math.Min(len - data - 8, uint.MaxValue)));
        return true;
    }

    /// <summary>Pads to the stop instant and writes the real sizes in the header (a file past 4 GB keeps the maximum).</summary>
    private void Finish()
    {
        if (_file == null) return;
        try
        {
            long stop = Interlocked.Read(ref _stopWall);
            if (stop > 0)
            {
                long end = (long)Math.Round((stop - _startWall) * (_format.Taxa / 10_000_000.0));
                if (end > _written) WriteSilence(end - _written);
            }
            long dataBytes = _file.Position - _dataStart;
            uint data = (uint)Math.Min(dataBytes, uint.MaxValue - 64);
            _file.Position = 4;
            _file.Write(BitConverter.GetBytes((uint)Math.Min(dataBytes + _dataStart - 8, uint.MaxValue)));
            _file.Position = _dataStart - 4;
            _file.Write(BitConverter.GetBytes(data));
            _file.Flush();
        }
        catch (Exception e) { Log.Escrever($"gravador: fechando o WAV do {Name}: {e.Message}"); }
        finally
        {
            _file.Dispose();
            _file = null;
        }
    }
}
