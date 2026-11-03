using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Ilha.Midia;
using Ilha.Recorder;

namespace Ilha.Clips;

internal static class ClipTests
{
    public static string Run()
    {
        var r = new StringBuilder();
        void Ok(string name, bool ok, string detail = "") => r.AppendLine((ok ? "ok      " : "FALHOU  ") + name + (detail.Length > 0 ? $" ({detail})" : ""));

        var t0 = new DateTime(2026, 11, 3, 20, 0, 0, DateTimeKind.Utc);
        var pieces = Enumerable.Range(0, 25).Select(i => ($"anel{i:000}.ts", t0.AddSeconds((i + 7) % 25 * 2))).ToList();
        var newest = ClipRing.Newest(pieces, 30);
        Ok("clipes: os pedaços mais novos que cobrem 30 s, do mais velho ao mais novo (o anel dá a volta)",
            newest.Count == 16 && newest[^1] == "anel017.ts" && newest[0] == "anel002.ts", string.Join(",", newest.Take(3)) + "..." + newest[^1]);

        var args = ClipRing.Args(1800000000.5, 12345, 60, 23, new FormatoAudio(TipoAmostra.Float, 32, 8, 48000, 32), @"C:\anel");
        string line = string.Join(" ", args);
        Ok("clipes: comando do anel (tela na NVENC, som pelo stdin em 7.1 virando estéreo, pedaços de 2 s que dão a volta)",
            line.Contains("gfxcapture=hmonitor=12345:max_framerate=60") && line.Contains("-f f32le -ar 48000 -ac 8 -i pipe:0")
            && line.Contains("-ac 2") && line.Contains("-segment_time 2 -segment_wrap 25") && line.Contains("-g 60") && line.EndsWith(@"C:\anel\anel%03d.ts"), line);
        Ok("clipes: sem som, só a tela", !string.Join(" ", ClipRing.Args(1, 1, 30, 23, null, "x")).Contains("pipe:0"));
        Ok("clipes: formato do som para o ffmpeg", ClipRing.SampleFormat(new FormatoAudio(TipoAmostra.Inteiro, 16, 2, 44100, 4)) == "s16le"
            && ClipRing.SampleFormat(new FormatoAudio(TipoAmostra.Inteiro, 24, 2, 48000, 6)) == "s24le");
        Ok("clipes: nome do arquivo sem caractere proibido", ClipStore.Name("CS2", new DateTime(2026, 11, 3, 16, 21, 5), "ace, vitória/x") == "CS2 2026-11-03 16-21-05 ace, vitória-x");

        var q = new QueueStream(10, quiet: true);
        q.Write(new byte[8]);
        q.Write(new byte[8]);
        q.Write(new byte[8]);
        q.Dispose();
        int got = 0;
        while (q.Take() is { } b) got += b.Length;
        Ok("clipes: a fila do som não segura o áudio e larga o excesso se o ffmpeg parar de ler", got == 16, got + " bytes");

        // the real ring for ~9 s (the main monitor and the PC's sound) and a cut, only on purpose
        if (Environment.GetEnvironmentVariable("ILHA_TESTE_CLIPE") == "1")
        {
            string? ff = FfmpegTools.Find(null);
            if (ff == null) { Ok("clipes: ffmpeg não encontrado", false); return r.ToString().TrimEnd(); }
            string dir = Path.Combine(Path.GetTempPath(), "ilha-teste-clipe-" + Guid.NewGuid().ToString("N"));
            using var ring = new ClipRing(Path.Combine(dir, "anel"));
            try
            {
                bool started = ring.Start(ff, 60, 23, out string err);
                Thread.Sleep(9000);
                string clip = Path.Combine(dir, "clipe.mp4"), jpg = Path.Combine(dir, "clipe.jpg");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                string? cut = ring.Cut(ff, 6, clip);
                long cutMs = watch.ElapsedMilliseconds;
                string? thumb = cut == null ? ClipRing.Thumbnail(ff, clip, jpg) : "sem clipe";
                string info = FfmpegTools.RunForText(ff, new[] { "-hide_banner", "-i", clip });
                var dur = Regex.Match(info, @"Duration: (\d+):(\d+):(\d+\.\d+)");
                double seconds = dur.Success ? double.Parse(dur.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) + int.Parse(dur.Groups[2].Value) * 60 : 0;
                bool video = info.Contains("Video: h264"), audio = info.Contains("Audio: aac");
                Ok("clipes: anel de verdade grava e o corte vira MP4 com vídeo e som", started && cut == null && video && (audio || !ring.HasAudio) && seconds is >= 5 and <= 12,
                    $"{(started ? "" : err + "; ")}{cut ?? "corte ok"} em {cutMs} ms, {seconds:0.0} s, vídeo {video}, som {audio} (anel com som: {ring.HasAudio})");
                Ok("clipes: miniatura do clipe", thumb == null && File.Exists(jpg) && ClipStore.Load(jpg) is { W: 320 }, thumb ?? "");
                if (!ring.Running) Ok("clipes: o ffmpeg do anel parou: " + ring.Errors(), false);
                // for a look at the result (development): ILHA_TESTE_CLIPE_PASTA=<folder> keeps a copy
                if (Environment.GetEnvironmentVariable("ILHA_TESTE_CLIPE_PASTA") is { Length: > 0 } keep && File.Exists(clip))
                {
                    Directory.CreateDirectory(keep);
                    File.Copy(clip, Path.Combine(keep, "clipe.mp4"), true);
                    if (File.Exists(jpg)) File.Copy(jpg, Path.Combine(keep, "clipe.jpg"), true);
                }
            }
            finally
            {
                ring.Stop();
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }
        else Ok("clipes: teste de verdade pulado (ILHA_TESTE_CLIPE=1 grava a tela por 9 s)", true);
        return r.ToString().TrimEnd();
    }
}
