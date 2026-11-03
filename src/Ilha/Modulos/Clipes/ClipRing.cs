using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Ilha.Midia;
using Ilha.Recorder;

namespace Ilha.Clips;

/// <summary>
/// A stream that only queues what is written (the audio thread never waits on ffmpeg) and is read by a pump thread.
/// More than `limit` bytes waiting means ffmpeg stopped reading: the newest chunks are dropped (and said once).
/// </summary>
internal sealed class QueueStream : Stream
{
    private readonly BlockingCollection<byte[]> _q = new();
    private readonly long _limit;
    private long _waiting;
    private bool _warned;

    /// <param name="quiet">The test: no line in the log when it drops.</param>
    public QueueStream(long limit, bool quiet = false) { _limit = limit; _warned = quiet; }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_q.IsAddingCompleted) return;
        if (Interlocked.Read(ref _waiting) > _limit)
        {
            if (!_warned) { _warned = true; Log.Escrever("clipes: o ffmpeg parou de ler o som, pedaços descartados"); }
            return;
        }
        Interlocked.Add(ref _waiting, buffer.Length);
        try { _q.Add(buffer.ToArray()); } catch (InvalidOperationException) { }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <summary>The next chunk, null when the stream was closed and everything was taken.</summary>
    public byte[]? Take()
    {
        try
        {
            var b = _q.Take();
            Interlocked.Add(ref _waiting, -b.Length);
            return b;
        }
        catch (InvalidOperationException) { return null; }
    }

    protected override void Dispose(bool disposing)
    {
        _q.CompleteAdding();
        base.Dispose(disposing);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// The replay buffer (3.7.0, his request): while a competitive game is in front, ffmpeg keeps the main monitor (Windows
/// Graphics Capture, NVENC, like the recorder) and the PC's sound (the island's own loopback, fed through stdin on the
/// same timeline) in 2-second MPEG-TS pieces that wrap around, so the last ~50 s are always on disk and nothing older.
/// A clip is the newest pieces joined into an MP4 with no re-encoding (cuts land on the 1-second keyframes).
/// </summary>
internal sealed class ClipRing : IDisposable
{
    public const int Piece = 2, Pieces = 25;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly long UnixEpochTicks = DateTime.UnixEpoch.Ticks;

    public string Folder { get; }
    private Process? _ff;
    private AudioCapture? _audio;
    private QueueStream? _queue;
    private Thread? _pump;
    private readonly List<string> _errors = new();

    public bool Running => _ff is { HasExited: false };
    public DateTime StartedUtc { get; private set; }
    public bool HasAudio { get; private set; }

    public ClipRing(string folder) => Folder = folder;

    /// <summary>ffmpeg's sample format name for the device's format.</summary>
    internal static string SampleFormat(FormatoAudio f) => f.Tipo == TipoAmostra.Float ? "f32le" : f.Bits switch { 16 => "s16le", 24 => "s24le", _ => "s32le" };

    /// <summary>The ring's command line (separate so the test can read it).</summary>
    internal static List<string> Args(double t0Seconds, ulong monitor, int fps, int cq, FormatoAudio? audio, string folder)
    {
        var a = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-y",
            "-use_wallclock_as_timestamps", "1", "-itsoffset", FfmpegTools.Seconds(-t0Seconds), "-f", "lavfi", "-i",
            $"gfxcapture=hmonitor={monitor}:max_framerate={fps}," + FfmpegTools.ColorTags };
        if (audio is { } f)
            a.AddRange(new[] { "-f", SampleFormat(f), "-ar", f.Taxa.ToString(Inv), "-ac", f.Canais.ToString(Inv), "-i", "pipe:0" });
        a.AddRange(new[] { "-map", "0:v" });
        if (audio != null) a.AddRange(new[] { "-map", "1:a" });
        a.AddRange(new[]
        {
            // a keyframe every second: a clip can start on any second
            "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", cq.ToString(Inv), "-b:v", "0",
            "-g", fps.ToString(Inv), "-copyts", "-fps_mode", "cfr", "-r", fps.ToString(Inv),
        });
        if (audio != null) a.AddRange(new[] { "-c:a", "aac", "-b:a", "160k", "-ac", "2" });
        a.AddRange(new[]
        {
            "-f", "segment", "-segment_time", Piece.ToString(Inv), "-segment_wrap", Pieces.ToString(Inv), "-segment_format", "mpegts",
            "-reset_timestamps", "0", Path.Combine(folder, "anel%03d.ts"),
        });
        return a;
    }

    public bool Start(string ffmpeg, int fps, int cq, out string error)
    {
        error = "";
        Stop();
        try
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
            Directory.CreateDirectory(Folder);
            long t0 = DateTime.UtcNow.Ticks;
            StartedUtc = new DateTime(t0, DateTimeKind.Utc);
            _queue = new QueueStream(32L << 20);
            _audio = new AudioCapture(true, _queue, t0);
            HasAudio = _audio.Start();
            if (!HasAudio) { Log.Escrever("clipes: sem o som do PC: " + _audio.Error); _audio = null; }
            var (monitor, _, _, _, _) = FfmpegTools.MainMonitor();
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            foreach (var x in Args((t0 - UnixEpochTicks) / 1e7, monitor, fps, cq, HasAudio ? _audio!.Format : null, Folder)) psi.ArgumentList.Add(x);
            var ff = Process.Start(psi) ?? throw new InvalidOperationException("o ffmpeg não abriu");
            ff.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lock (_errors) { if (_errors.Count < 20) _errors.Add(e.Data); } };
            ff.BeginErrorReadLine();
            _ff = ff;
            if (HasAudio)
            {
                var q = _queue;
                var stdin = ff.StandardInput.BaseStream;
                _pump = new Thread(() =>
                {
                    try
                    {
                        while (q.Take() is { } chunk) stdin.Write(chunk, 0, chunk.Length);
                    }
                    catch (Exception) { }
                    try { stdin.Close(); } catch (Exception) { }
                }) { IsBackground = true, Name = "ilha-clipes-som" };
                _pump.Start();
            }
            global::Ilha.Gravando.Capturas.AnelDeClipes = true;
            Log.Escrever($"clipes: anel ligado ({fps} fps, cq {cq}{(HasAudio ? ", com o som do PC " + _audio!.Format : ", sem som")})");
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            Stop();
            return false;
        }
    }

    /// <summary>What ffmpeg complained about (it ended by itself), for the log.</summary>
    public string Errors()
    {
        lock (_errors) return string.Join(" | ", _errors);
    }

    public void Stop()
    {
        var audio = _audio;
        _audio = null;
        audio?.Stop(DateTime.UtcNow.Ticks);
        _queue?.Dispose();
        _queue = null;
        var ff = _ff;
        _ff = null;
        if (ff != null)
        {
            try
            {
                // with the sound, closing its pipe ends ffmpeg; without it, "q" on stdin asks it to quit
                if (!ff.HasExited && !HasAudio) { try { ff.StandardInput.Write('q'); ff.StandardInput.Flush(); } catch (Exception) { } }
                if (!ff.WaitForExit(3000)) ff.Kill(true);
            }
            catch (Exception) { }
            ff.Dispose();
        }
        _pump?.Join(2000);
        _pump = null;
        global::Ilha.Gravando.Capturas.AnelDeClipes = false;
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); } catch (Exception) { }
    }

    /// <summary>The newest pieces that cover `seconds` (the one being written included), oldest first.</summary>
    internal static List<string> Newest(IEnumerable<(string File, DateTime Written)> pieces, int seconds)
    {
        int need = (int)Math.Ceiling(seconds / (double)Piece) + 1;
        return pieces.OrderByDescending(p => p.Written).Take(need).Reverse().Select(p => p.File).ToList();
    }

    /// <summary>
    /// The last `seconds` (about: whole pieces) into `output` (MP4, no re-encoding). Runs ffmpeg to the end: call it off
    /// the interface thread. Null when it worked, else why not.
    /// </summary>
    public string? Cut(string ffmpeg, int seconds, string output)
    {
        if (!Directory.Exists(Folder)) return "o anel não está gravando";
        var pieces = Directory.GetFiles(Folder, "anel*.ts").Select(f => (f, File.GetLastWriteTimeUtc(f)))
            .Where(p => new FileInfo(p.f).Length > 0).ToList();
        var take = Newest(pieces, seconds);
        if (take.Count == 0) return "nada gravado ainda";
        // copies first: ffmpeg keeps writing the ring (the newest piece is still growing)
        string work = Path.Combine(Path.GetTempPath(), "ilha-clipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var list = new List<string>();
            for (int i = 0; i < take.Count; i++)
            {
                string copy = Path.Combine(work, $"{i:000}.ts");
                using (var src = new FileStream(take[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var dst = File.Create(copy)) src.CopyTo(dst);
                list.Add($"file '{copy.Replace("\\", "/")}'");
            }
            string listFile = Path.Combine(work, "lista.txt");
            File.WriteAllLines(listFile, list);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var (exit, err) = FfmpegTools.Run(ffmpeg, new[]
            {
                "-hide_banner", "-nostats", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", listFile,
                "-c", "copy", "-bsf:a", "aac_adtstoasc", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", output,
            }, 60_000);
            return exit == 0 && File.Exists(output) ? null : $"ffmpeg {exit}: {err.Trim()}";
        }
        catch (Exception e) { return e.Message; }
        finally { try { Directory.Delete(work, true); } catch (Exception) { } }
    }

    /// <summary>One frame from the middle of a clip as a small JPEG next to it (for the cards). Null when it worked.</summary>
    public static string? Thumbnail(string ffmpeg, string clip, string jpg)
    {
        var (exit, err) = FfmpegTools.Run(ffmpeg, new[]
        {
            "-hide_banner", "-nostats", "-loglevel", "error", "-y", "-sseof", "-3", "-i", clip, "-frames:v", "1",
            "-vf", "scale=320:-2", "-q:v", "4", jpg,
        }, 20_000);
        return exit == 0 && File.Exists(jpg) ? null : err.Trim();
    }

    public void Dispose() => Stop();
}
