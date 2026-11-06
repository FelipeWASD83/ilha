using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Ilha.Peripherals;

/// <summary>
/// The client end of Gear Hub's pipe, on one background thread. While the pipe does not exist (Gear Hub closed) it
/// only asks Windows whether it exists every few seconds (`WaitNamedPipe`, which answers at once when there is no such
/// pipe): no connection attempt, no exception, no log. Connected, it reads one line at a time and hands each one over;
/// when the pipe drops it goes back to waiting. The handle is overlapped so a write never waits behind the pending read.
/// Callbacks run on the background thread.
/// </summary>
internal sealed class GearHubLink : IDisposable
{
    private readonly string _pipe;
    private readonly Action<string> _line;
    private readonly Action<bool> _connection;
    private readonly Func<int, bool>? _serverOk;
    private readonly ManualResetEvent _stop = new(false);
    private readonly object _write = new();
    private Thread? _thread;
    private volatile NamedPipeClientStream? _stream;
    private volatile bool _stopped;
    private int _refused = -1;

    /// <summary>How long to wait before looking for the pipe again (the contract says 5 s).</summary>
    public int RetryMs { get; init; } = 5000;

    public bool Connected => _stream is { IsConnected: true };

    /// <param name="serverOk">Checks the process id of the pipe's server (null = any).</param>
    public GearHubLink(string pipe, Action<string> line, Action<bool> connection, Func<int, bool>? serverOk = null)
    {
        _pipe = pipe;
        _line = line;
        _connection = connection;
        _serverOk = serverOk;
    }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ilha-gear-hub" };
        _thread.Start();
    }

    /// <summary>Sends one request line; false when not connected or the write failed.</summary>
    public bool Send(string json)
    {
        var s = _stream;
        if (s == null || !s.IsConnected) return false;
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        try
        {
            lock (_write)
            {
                s.Write(bytes, 0, bytes.Length);
                s.Flush();
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private void Loop()
    {
        string full = @"\\.\pipe\" + _pipe;
        while (!_stopped)
        {
            // no pipe = false right away (ERROR_FILE_NOT_FOUND); every instance busy = false after 1 ms
            if (!WaitNamedPipeW(full, 1))
            {
                if (_stop.WaitOne(RetryMs)) return;
                continue;
            }
            var s = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { s.Connect(1000); }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
            {
                s.Dispose();
                if (_stop.WaitOne(RetryMs)) return;
                continue;
            }
            if (_serverOk != null && GetNamedPipeServerProcessId(s.SafePipeHandle.DangerousGetHandle(), out uint pid) && !_serverOk((int)pid))
            {
                if (_refused != (int)pid) Log.Escrever($"periféricos: o pipe do Gear Hub é de outro programa (pid {pid}), ignorado");
                _refused = (int)pid;
                s.Dispose();
                if (_stop.WaitOne(RetryMs)) return;
                continue;
            }
            _stream = s;
            if (_stopped) { s.Dispose(); return; }
            _connection(true);
            try
            {
                using var reader = new StreamReader(s, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                string? line;
                while (!_stopped && (line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    try { _line(line); }
                    catch (Exception e) { Log.Escrever("periféricos: " + e.Message); }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
            _stream = null;
            lock (_write) s.Dispose();
            _connection(false);
            // Gear Hub restarting: look again soon, then at the regular pace
            if (_stop.WaitOne(Math.Min(1000, RetryMs))) return;
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _stop.Set();
        var s = _stream;
        if (s != null)
        {
            // a pending overlapped read ends when its handle is closed
            try { CancelIoEx(s.SafePipeHandle.DangerousGetHandle(), IntPtr.Zero); } catch (ObjectDisposedException) { }
            try { s.Dispose(); } catch (IOException) { }
        }
        _thread?.Join(1500);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WaitNamedPipeW(string name, uint timeoutMs);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);
}
