using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Ilha.Recorder;

/// <summary>A DirectShow video device as ffmpeg lists it; Physical = a USB/PnP camera (not a virtual one).</summary>
internal sealed record CameraDevice(string Name, string AltName)
{
    public bool Physical => AltName.StartsWith("@device_pnp_", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What the recorder needs from ffmpeg and the system: where ffmpeg is, the cameras, the main monitor, and the argument
/// lists. ffmpeg is not shipped with the island (winget install Gyan.FFmpeg).
/// </summary>
internal static class FfmpegTools
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The configured ffmpeg.exe, else the first one on PATH, else the winget links folder; null when none.</summary>
    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? configured : null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        var links = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        return File.Exists(links) ? links : null;
    }

    /// <summary>Runs ffmpeg to the end and returns its exit code (-1 when it had to be killed) and what it wrote to stderr.</summary>
    public static (int Exit, string Err) Run(string ffmpeg, IEnumerable<string> args, int timeoutMs = 8000)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); p.WaitForExit(3000); } catch { } return (-1, err.Wait(1000) ? err.Result : ""); }
        return (p.ExitCode, err.Wait(1000) ? err.Result : "");
    }

    /// <summary>What ffmpeg wrote to stderr (for listings, which end with an error code on purpose).</summary>
    public static string RunForText(string ffmpeg, IEnumerable<string> args, int timeoutMs = 8000) => Run(ffmpeg, args, timeoutMs).Err;

    private static readonly Regex DeviceLine = new("\"(?<name>[^\"]+)\" \\((?<kind>video|audio|none)\\)");
    private static readonly Regex AltLine = new("Alternative name \"(?<alt>[^\"]+)\"");

    /// <summary>The video devices in a `-list_devices true -f dshow` listing, in its order.</summary>
    public static List<CameraDevice> ParseCameras(string listing)
    {
        var list = new List<CameraDevice>();
        string? pending = null;
        foreach (var line in listing.Split('\n'))
        {
            var d = DeviceLine.Match(line);
            if (d.Success) { pending = d.Groups["kind"].Value == "video" ? d.Groups["name"].Value : null; continue; }
            var a = AltLine.Match(line);
            if (a.Success && pending != null) { list.Add(new CameraDevice(pending, a.Groups["alt"].Value)); pending = null; }
        }
        return list;
    }

    public static List<CameraDevice> ListCameras(string ffmpeg) =>
        ParseCameras(RunForText(ffmpeg, new[] { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" }));

    /// <summary>The configured camera when it is there; else the first physical one; else none.</summary>
    public static CameraDevice? PickCamera(IReadOnlyList<CameraDevice> cams, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            foreach (var c in cams)
                if (string.Equals(c.Name, configured.Trim(), StringComparison.OrdinalIgnoreCase)) return c;
        foreach (var c in cams) if (c.Physical) return c;
        return null;
    }

    /// <summary>
    /// The main monitor's handle (gfxcapture takes it as `hmonitor`, valid in any process of the session), its size and
    /// the bottom-right corner of its work area (the taskbar left out), relative to the monitor.
    /// </summary>
    public static (ulong Handle, int Width, int Height, int WorkRight, int WorkBottom) MainMonitor()
    {
        var mon = Nativo.MonitorFromPoint(new Nativo.POINT(), Nativo.MONITOR_DEFAULTTOPRIMARY);
        var mi = new Nativo.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Nativo.MONITORINFO>() };
        Nativo.GetMonitorInfoW(mon, ref mi);
        return ((ulong)(long)mon, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top,
            mi.rcWork.Right - mi.rcMonitor.Left, mi.rcWork.Bottom - mi.rcMonitor.Top);
    }

    /// <summary>
    /// The color tags of every video: NVENC turns the RGB frames into BT.601 limited YUV (measured on 02/11/2026), the
    /// screen is sRGB. Without them the file said "GBR, full range" and an editor could read the colors wrong.
    /// </summary>
    private const string Tags = "setparams=range=tv:colorspace=bt470bg:color_primaries=bt709:color_trc=iec61966-2-1";

    public static string Seconds(double s) => s.ToString("0.000000", Inv);

    /// <summary>
    /// Where the camera sits on the screen in the picture-in-picture mode, in pixels (even numbers): bottom right, above
    /// the taskbar (workRight/workBottom = the work area's corner; 0 = the screen's).
    /// </summary>
    public static PipLayout Pip(int screenW, int screenH, int workRight = 0, int workBottom = 0)
    {
        if (workRight <= 0) workRight = screenW;
        if (workBottom <= 0) workBottom = screenH;
        int h = (int)Math.Round(screenH * 0.28 / 2) * 2;
        int w = (int)Math.Round(h * 16.0 / 9.0 / 2) * 2;
        int margin = (int)Math.Round(screenH * 0.03);
        int radius = Math.Max(8, (int)Math.Round(h * 0.065));
        int blur = Math.Max(6, (int)Math.Round(h * 0.045));
        return new PipLayout(workRight - w - margin, workBottom - h - margin, w, h, radius, blur, blur * 3, (int)Math.Round(blur * 0.5));
    }

    /// <summary>
    /// The recording command. Every live input is stamped with the wall clock minus T0 (the session start), so video and
    /// the WAV tracks written by the island share one timeline; the output keeps those times (-copyts) at a constant rate.
    /// The screen comes from Windows.Graphics.Capture (gfxcapture): Desktop Duplication (ddagrab) gave washed-out grays on
    /// this PC (128 came back as 217) while gfxcapture matched the screen within 3 levels, at 2% of one core.
    /// </summary>
    public static List<string> RecordArgs(RecordKind kind, double t0Seconds, ulong monitor, CameraDevice? cam, int fps, int cq,
        PipLayout pip, string? maskPng, string? shadowPng, string output, bool cameraOptions = true)
    {
        var a = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-progress", "pipe:1", "-y" };
        string offset = Seconds(-t0Seconds);
        if (kind is RecordKind.Screen or RecordKind.ScreenCamera)
            a.AddRange(new[] { "-use_wallclock_as_timestamps", "1", "-itsoffset", offset, "-f", "lavfi", "-i",
                $"gfxcapture=hmonitor={monitor}:max_framerate={fps}" + (kind == RecordKind.Screen ? "," + Tags : "") });
        if (kind is RecordKind.Camera or RecordKind.ScreenCamera)
        {
            a.AddRange(new[] { "-use_wallclock_as_timestamps", "1", "-itsoffset", offset, "-f", "dshow", "-rtbufsize", "256M" });
            if (cameraOptions) a.AddRange(new[] { "-vcodec", "mjpeg", "-video_size", "1920x1080", "-framerate", "30" });
            a.AddRange(new[] { "-i", "video=" + cam!.Name });
        }
        if (kind == RecordKind.ScreenCamera)
        {
            a.AddRange(new[] { "-i", maskPng!, "-i", shadowPng! });
            string g = string.Join(";", new[]
            {
                "[0:v]hwdownload,format=bgra[s]",
                $"[1:v]scale={pip.W}:{pip.H}:force_original_aspect_ratio=increase,crop={pip.W}:{pip.H},format=rgba[c]",
                "[2:v]format=gray[m]",
                "[c][m]alphamerge[cr]",
                "[3:v]format=rgba[sh]",
                $"[s][sh]overlay={pip.X - pip.Margin}:{pip.Y - pip.Margin + pip.ShadowDy}:format=rgb[s2]",
                $"[s2][cr]overlay={pip.X}:{pip.Y}:format=rgb,{Tags}[v]",
            });
            a.AddRange(new[] { "-filter_complex", g, "-map", "[v]" });
        }
        // the webcam's MJPEG is BT.601 full range: squeezed into the limited range the tags say
        else if (kind == RecordKind.Camera)
            a.AddRange(new[] { "-map", "0:v", "-vf", "scale=out_range=tv:out_color_matrix=bt601,format=yuv420p," + Tags });
        else a.AddRange(new[] { "-map", "0:v" });
        int rate = kind == RecordKind.Camera ? 30 : fps;
        a.AddRange(new[]
        {
            "-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", cq.ToString(Inv), "-b:v", "0",
            "-g", (rate * 2).ToString(Inv), "-copyts", "-fps_mode", "cfr", "-r", rate.ToString(Inv), output,
        });
        return a;
    }

    /// <summary>
    /// The final file: the video copied as it is plus the WAV tracks mixed into one AAC track (video), or the tracks mixed
    /// into one 24-bit WAV (audio only). Same timeline as the recording (-copyts), so nothing is shifted.
    /// </summary>
    public static List<string> FinishArgs(string? video, IReadOnlyList<string> wavs, string output)
    {
        var a = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-y" };
        if (video != null) a.AddRange(new[] { "-copyts", "-i", video });
        foreach (var w in wavs) a.AddRange(new[] { "-i", w });
        int first = video != null ? 1 : 0;
        if (video != null) a.AddRange(new[] { "-map", "0:v", "-c:v", "copy" });
        if (wavs.Count > 1)
        {
            string ins = "";
            for (int i = 0; i < wavs.Count; i++) ins += $"[{first + i}:a]";
            a.AddRange(new[] { "-filter_complex", $"{ins}amix=inputs={wavs.Count}:duration=longest:normalize=0[a]", "-map", "[a]" });
        }
        else if (wavs.Count == 1) a.AddRange(new[] { "-map", $"{first}:a" });
        // stereo: a 7.1 headset's loopback comes in 8 channels, which nobody wants in a recording
        if (wavs.Count > 0)
            a.AddRange(video != null ? new[] { "-c:a", "aac", "-b:a", "192k", "-ac", "2" } : new[] { "-c:a", "pcm_s24le", "-ar", "48000", "-ac", "2" });
        if (video != null) a.AddRange(new[] { "-movflags", "+faststart" });
        a.Add(output);
        return a;
    }
}

internal readonly record struct PipLayout(int X, int Y, int W, int H, int Radius, int Blur, int Margin, int ShadowDy);

internal enum RecordKind { Screen, Camera, ScreenCamera, Audio }
