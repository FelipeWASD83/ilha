using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Ilha.Recorder;

internal sealed class RecordingOptions
{
    public RecordKind Kind;
    public bool PcSound, Mic;
    public string? Ffmpeg;
    public string? Camera;
    public string Folder = "";
    public int Fps = 60, Cq = 19;
}

/// <summary>The end of a recording: the final file, or what went wrong and where the pieces were kept.</summary>
internal sealed record FinishResult(string? FinalPath, string? Error, string? KeptFolder);

/// <summary>A start that failed, with the message shown on the island.</summary>
internal sealed class RecorderException : Exception
{
    public RecorderException(string message) : base(message) { }
}

/// <summary>
/// One recording. Video goes through ffmpeg (Desktop Duplication and/or the webcam, encoded on the graphics card) into
/// a partial MKV; the PC's sound and the microphone go into WAV files written by the island. All of them are on one
/// timeline whose zero is T0, the wall clock instant taken right before anything starts, so stopping only has to put
/// them together: the video is copied as it is and the tracks are mixed into one. If that last step fails, the pieces
/// stay in the folder and nothing is deleted. Start and Stop block (seconds): call them off the interface thread.
/// </summary>
internal sealed class RecordingSession
{
    private static readonly long UnixEpochTicks = DateTime.UnixEpoch.Ticks;
    private static readonly Regex FrameLine = new(@"^frame=(\d+)", RegexOptions.Compiled);

    private readonly RecordingOptions _o;
    private readonly List<AudioCapture> _audio = new();
    private readonly List<string> _errLines = new();
    private readonly ManualResetEvent _firstFrame = new(false);
    private Process? _ffmpeg;
    private string _base = "";
    private string? _videoTemp, _mask, _shadow;
    private long _t0;
    private volatile bool _stopping;

    public RecordKind Kind => _o.Kind;
    public DateTime StartedUtc => new(_t0, DateTimeKind.Utc);
    public bool PcSound => _o.PcSound;
    public bool Mic => _o.Mic;
    /// <summary>ffmpeg ended by itself while recording (camera unplugged, out of disk...): raised on a worker thread.</summary>
    public event Action<string>? Died;

    private RecordingSession(RecordingOptions o) => _o = o;

    public static RecordingSession Start(RecordingOptions o)
    {
        var s = new RecordingSession(o);
        try { s.Begin(); }
        catch
        {
            s.Abort();
            throw;
        }
        return s;
    }

    private bool HasVideo => _o.Kind != RecordKind.Audio;

    /// <summary>"Gravação 2026-11-02 17-30-12" (in the interface language), with a number when that name is taken.</summary>
    internal static string BaseName(string folder, DateTime now, string ext)
    {
        string name = Lang.T("Gravação", "Recording") + " " + now.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        string candidate = name;
        for (int i = 2; File.Exists(Path.Combine(folder, candidate + ext)) || File.Exists(Path.Combine(folder, candidate + ".parcial.mkv")); i++)
            candidate = $"{name} ({i})";
        return Path.Combine(folder, candidate);
    }

    private string FinalPath => _base + (HasVideo ? ".mp4" : ".wav");

    private void Begin()
    {
        try { Directory.CreateDirectory(_o.Folder); }
        catch (Exception e) { throw new RecorderException(Lang.T("Não deu para criar a pasta: ", "Couldn't create the folder: ") + e.Message); }
        _base = BaseName(_o.Folder, DateTime.Now, HasVideo ? ".mp4" : ".wav");
        if (HasVideo && _o.Ffmpeg == null)
            throw new RecorderException(Lang.T("Precisa do ffmpeg: winget install Gyan.FFmpeg", "ffmpeg is needed: winget install Gyan.FFmpeg"));
        if (!HasVideo && !_o.PcSound && !_o.Mic)
            throw new RecorderException(Lang.T("Ligue o microfone ou o som do PC", "Turn on the microphone or the PC sound"));

        CameraDevice? cam = null;
        if (_o.Kind is RecordKind.Camera or RecordKind.ScreenCamera)
        {
            cam = FfmpegTools.PickCamera(FfmpegTools.ListCameras(_o.Ffmpeg!), _o.Camera);
            if (cam == null) throw new RecorderException(Lang.T("Nenhuma câmera encontrada", "No camera found"));
        }
        var (monitor, sw, sh, wr, wb) = HasVideo ? FfmpegTools.MainMonitor() : (0UL, 0, 0, 0, 0);
        var pip = FfmpegTools.Pip(sw, sh, wr, wb);
        if (_o.Kind == RecordKind.ScreenCamera)
        {
            var dir = Path.Combine(Path.GetTempPath(), "ilha-gravador");
            Directory.CreateDirectory(dir);
            _mask = Path.Combine(dir, $"mascara-{Environment.ProcessId}.png");
            _shadow = Path.Combine(dir, $"sombra-{Environment.ProcessId}.png");
            PipFrame.Write(pip, _mask, _shadow);
        }

        _t0 = DateTime.UtcNow.Ticks;
        if (_o.PcSound) _audio.Add(new AudioCapture(true, _base + Lang.T(".som-do-pc.wav", ".pc-sound.wav"), _t0));
        if (_o.Mic) _audio.Add(new AudioCapture(false, _base + Lang.T(".microfone.wav", ".microphone.wav"), _t0));
        foreach (var a in _audio)
            if (!a.Start())
                throw new RecorderException(Lang.T($"O {a.Name} não abriu: ", $"The {(a.Name == "microfone" ? "microphone" : "PC sound")} didn't open: ") + a.Error);

        if (!HasVideo) { Log.Escrever($"gravador: gravando áudio ({string.Join(", ", _audio.Select(x => x.Name))})"); return; }
        _videoTemp = _base + ".parcial.mkv";
        global::Ilha.Gravando.Capturas.GravadorAtivo = true;
        double t0s = (_t0 - UnixEpochTicks) / 1e7;
        bool cameraOptions = true;
        for (int attempt = 0; ; attempt++)
        {
            var args = FfmpegTools.RecordArgs(_o.Kind, t0s, monitor, cam, _o.Fps, _o.Cq, pip, _mask, _shadow, _videoTemp, cameraOptions);
            StartFfmpeg(args);
            int w = WaitHandle.WaitAny(new WaitHandle[] { _firstFrame, _exited }, 12_000);
            if (w == 0)
            {
                Log.Escrever($"gravador: gravando {_o.Kind} ({sw}x{sh}{(cam != null ? ", câmera " + cam.Name : "")}, som: {string.Join(", ", _audio.Select(x => x.Name))})");
                return;
            }
            string why = LastError();
            Log.Escrever($"gravador: o ffmpeg não começou (tentativa {attempt + 1}): {why}");
            KillFfmpeg();
            // a camera that does not offer 1080p30 in MJPEG gets a second try with its own default mode
            if (cam != null && cameraOptions && attempt == 0) { cameraOptions = false; continue; }
            throw new RecorderException(w == WaitHandle.WaitTimeout
                ? Lang.T("O ffmpeg não começou a gravar", "ffmpeg didn't start recording")
                : Lang.T("O ffmpeg parou: ", "ffmpeg stopped: ") + why);
        }
    }

    private readonly ManualResetEvent _exited = new(false);

    private void StartFfmpeg(List<string> args)
    {
        _firstFrame.Reset();
        _exited.Reset();
        lock (_errLines) _errLines.Clear();
        var psi = new ProcessStartInfo(_o.Ffmpeg!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null && FrameLine.Match(e.Data) is { Success: true } m && long.Parse(m.Groups[1].Value) > 0) _firstFrame.Set();
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (_errLines)
            {
                _errLines.Add(e.Data.Trim());
                if (_errLines.Count > 8) _errLines.RemoveAt(0);
            }
        };
        p.Exited += (_, _) =>
        {
            _exited.Set();
            if (!_stopping && _firstFrame.WaitOne(0) && ReferenceEquals(p, _ffmpeg))
                Died?.Invoke(LastError() is { Length: > 0 } e ? e : Lang.T("o ffmpeg fechou", "ffmpeg closed"));
        };
        _ffmpeg = p;
        p.Start();
        try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
    }

    private string LastError()
    {
        lock (_errLines) return _errLines.Count == 0 ? "" : _errLines[^1];
    }

    private void KillFfmpeg()
    {
        var p = _ffmpeg;
        if (p == null) return;
        try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch { }
    }

    /// <summary>A start that failed: everything stops and the pieces written so far are deleted (nothing was recorded).</summary>
    private void Abort()
    {
        _stopping = true;
        KillFfmpeg();
        global::Ilha.Gravando.Capturas.GravadorAtivo = false;
        foreach (var a in _audio) a.Stop(DateTime.UtcNow.Ticks);
        foreach (var f in _audio.Select(a => a.Path).Append(_videoTemp).Append(_mask).Append(_shadow))
            if (f != null) { try { File.Delete(f); } catch { } }
    }

    public FinishResult Stop()
    {
        _stopping = true;
        long stopWall = DateTime.UtcNow.Ticks;
        var p = _ffmpeg;
        if (p != null)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.StandardInput.Write('q');
                    p.StandardInput.Flush();
                    if (!p.WaitForExit(15_000)) { Log.Escrever("gravador: o ffmpeg não fechou em 15 s; encerrado"); p.Kill(); p.WaitForExit(3000); }
                }
            }
            catch (Exception e) { Log.Escrever("gravador: parando o ffmpeg: " + e.Message); KillFfmpeg(); }
        }
        global::Ilha.Gravando.Capturas.GravadorAtivo = false;
        foreach (var a in _audio) a.Stop(stopWall);
        foreach (var f in new[] { _mask, _shadow }) if (f != null) { try { File.Delete(f); } catch { } }

        var wavs = _audio.Where(a => File.Exists(a.Path) && a.Frames > 0).Select(a => a.Path).ToList();
        string final = FinalPath;
        if (_o.Ffmpeg == null)
        {
            // audio only without ffmpeg: one track is already the file; two stay as two
            if (wavs.Count == 1) { File.Move(wavs[0], final); return new FinishResult(final, null, null); }
            return new FinishResult(null, Lang.T("Sem ffmpeg para juntar as faixas", "No ffmpeg to join the tracks"), _o.Folder);
        }
        if (HasVideo && (_videoTemp == null || !File.Exists(_videoTemp) || new FileInfo(_videoTemp).Length == 0))
            return new FinishResult(null, Lang.T("O vídeo saiu vazio", "The video came out empty"), _o.Folder);

        var args = FfmpegTools.FinishArgs(HasVideo ? _videoTemp : null, wavs, final);
        var sw = Stopwatch.StartNew();
        var (exit, err) = FfmpegTools.Run(_o.Ffmpeg, args, 30 * 60_000);
        bool ok = exit == 0 && File.Exists(final) && new FileInfo(final).Length > 0;
        Log.Escrever($"gravador: juntando em {sw.ElapsedMilliseconds} ms: {(ok ? "ok" : $"falhou ({exit}): " + err.Trim())}");
        if (!ok) return new FinishResult(null, Lang.T("Não deu para juntar vídeo e som; as partes ficaram na pasta", "Couldn't join video and sound; the parts stayed in the folder"), _o.Folder);
        foreach (var f in wavs.Append(_videoTemp)) if (f != null) { try { File.Delete(f); } catch { } }
        return new FinishResult(final, null, null);
    }
}
