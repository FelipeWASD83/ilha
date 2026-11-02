using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ilha.Recorder;

/// <summary>
/// Records the screen, the webcam, the screen with the webcam in a rounded corner, or only audio. Two menu tiles (video
/// and audio) and a hotkey for the video. Video needs ffmpeg (not shipped: winget install Gyan.FFmpeg); audio only
/// works without it when a single track is recorded. Nothing is injected into games: the screen comes from Desktop
/// Duplication and the sound from WASAPI.
/// </summary>
[Modulo("gravador", "Gravador", English = "Recorder")]
internal sealed class RecorderModule : Modulo
{
    private ConfigGravador _cfg = new();
    private RecorderActivity? _atv;

    public override Type? TipoConfig => typeof(ConfigGravador);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigGravador>(cfg);
        var atv = new RecorderActivity(S, () => _cfg, c => { _cfg = c; Salvar(c); });
        _atv = atv;
        atv.MenuChanged += S.Menu.Redesenhar;
        S.Ilha.Adicionar(atv);
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "gravar-video", Nome = Lang.T("Gravar vídeo", "Record video"), Ordem = 56,
            Abrir = () => atv.OpenPanel(RecorderActivity.Panel.Video),
            Icone = (dc, p, r) => Icons.Camera(dc, p.G.Fabrica, r, atv.Recording(RecorderActivity.Panel.Video)),
            Detalhe = () => atv.MenuDetail(RecorderActivity.Panel.Video),
        });
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "gravar-audio", Nome = Lang.T("Gravar áudio", "Record audio"), Ordem = 57,
            Abrir = () => atv.OpenPanel(RecorderActivity.Panel.Audio),
            Icone = (dc, p, r) => Icons.MicTile(dc, p.G.Fabrica, r, atv.Recording(RecorderActivity.Panel.Audio)),
            Detalhe = () => atv.MenuDetail(RecorderActivity.Panel.Audio),
        });
        // Ctrl + Alt + G was free on his PC on 02/11/2026
        var key = S.Atalhos.Registrar("gravador", new Atalho { Mods = Nativo.MOD_CONTROL | Nativo.MOD_ALT, Tecla = 0x47 }, atv.Toggle);
        atv.HotkeyLabel = key != null ? Teclas.Nome(key) : null;
    }

    public override void ConfigMudou(JsonObject cfg) => _cfg = Ler<ConfigGravador>(cfg);

    public override void Parar() => _atv?.Stop();

    public override string Autoteste()
    {
        var sb = new StringBuilder();
        void Check(bool ok, string name) => sb.AppendLine((ok ? "ok      " : "FALHOU  ") + name);

        // where each packet goes (48 kHz: the tolerance is 2880 frames)
        Check(AudioCapture.Placement(0, -2000, 480, 48000) == (0, 480), "pacote inteiro antes do começo é jogado fora");
        Check(AudioCapture.Placement(0, -100, 480, 48000) == (0, 100), "pacote que cruza o começo perde só a parte de antes");
        Check(AudioCapture.Placement(48000, 96000, 480, 48000) == (48000, 0), "um segundo sem pacote (loopback em silêncio) vira silêncio");
        Check(AudioCapture.Placement(48000, 49000, 480, 48000) == (0, 0), "diferença pequena (deriva do relógio) fica como está");
        Check(AudioCapture.Placement(48000, 40000, 480, 48000) == (0, 480), "pacote atrasado que repete o que já foi escrito sai");

        // ffmpeg's command lines
        var pip = FfmpegTools.Pip(3440, 1440);
        var acima = FfmpegTools.Pip(3440, 1440, 3440, 1380);
        int margem = 1440 - pip.Y - pip.H;
        Check(acima.Y + acima.H == 1380 - margem && acima.X == pip.X, "câmera fica acima da barra de tarefas");
        Check(pip.H == 404 && pip.W == 718 && pip.X + pip.W < 3440 && pip.Y + pip.H < 1440 && pip.Margin == pip.Blur * 3,
            $"câmera no canto: {pip.W}x{pip.H} em {pip.X},{pip.Y}, raio {pip.Radius}, sombra {pip.Blur}");
        var cam = new CameraDevice("HD Pro Webcam C920", "@device_pnp_\\\\?\\usb#vid_046d");
        var tela = FfmpegTools.RecordArgs(RecordKind.Screen, 1793650000.25, 65693, null, 60, 19, pip, null, null, "x.mkv");
        Check(tela.Any(x => x.StartsWith("gfxcapture=hmonitor=65693:max_framerate=60,setparams=range=tv")) && !tela.Contains("dshow") && tela.Contains("-copyts")
              && tela[tela.IndexOf("-itsoffset") + 1] == "-1793650000.250000" && tela[^1] == "x.mkv", "tela: gfxcapture do monitor, cores marcadas, relógio comum e saída");
        var camera = FfmpegTools.RecordArgs(RecordKind.Camera, 1, 0, cam, 60, 19, pip, null, null, "x.mkv");
        Check(camera.Contains("video=HD Pro Webcam C920") && !camera.Any(a => a.StartsWith("gfxcapture")) && camera.Any(a => a.Contains("out_range=tv")) && camera[camera.IndexOf("-r") + 1] == "30",
            "câmera sozinha: dshow a 30 fps, sem tela");
        var junto = FfmpegTools.RecordArgs(RecordKind.ScreenCamera, 1, 7, cam, 60, 19, pip, "m.png", "s.png", "x.mkv");
        string g = junto[junto.IndexOf("-filter_complex") + 1];
        Check(junto.Contains("m.png") && g.Contains("alphamerge") && g.Contains($"overlay={pip.X}:{pip.Y}") && junto.Contains("gfxcapture=hmonitor=7:max_framerate=60") && g.Contains("setparams"),
            "tela + câmera: máscara arredondada, sombra e câmera no canto");
        var semModo = FfmpegTools.RecordArgs(RecordKind.Camera, 1, 0, cam, 60, 19, pip, null, null, "x.mkv", cameraOptions: false);
        Check(!semModo.Contains("mjpeg"), "segunda tentativa da câmera sem pedir 1080p30");
        var fim = FfmpegTools.FinishArgs("v.mkv", new[] { "a.wav", "b.wav" }, "f.mp4");
        Check(fim.Contains("copy") && fim.Any(a => a.Contains("amix=inputs=2")) && fim.Contains("aac") && fim[fim.IndexOf("-ac") + 1] == "2" && fim[^1] == "f.mp4", "final: vídeo copiado e as duas faixas misturadas em estéreo");
        var soAudio = FfmpegTools.FinishArgs(null, new[] { "a.wav" }, "f.wav");
        Check(soAudio.Contains("pcm_s24le") && !soAudio.Contains("-copyts") && !soAudio.Any(a => a.Contains("amix")), "só áudio: um WAV de 24 bits");
        var semSom = FfmpegTools.FinishArgs("v.mkv", Array.Empty<string>(), "f.mp4");
        Check(!semSom.Contains("-c:a"), "vídeo sem som: só copia");

        var lista = "[in#0] \"HD Pro Webcam C920\" (video)\n[in#0]   Alternative name \"@device_pnp_\\\\?\\usb#vid_046d\"\n" +
                    "[in#0] \"OBS Virtual Camera\" (none)\n[in#0]   Alternative name \"@device_sw_{A3FC}\"\n" +
                    "[in#0] \"Meta Quest 3\" (video)\n[in#0]   Alternative name \"@device_sw_{860B}\"\n" +
                    "[in#0] \"Microfone (C920)\" (audio)\n[in#0]   Alternative name \"@device_cm_{33D9}\"\n";
        var cams = FfmpegTools.ParseCameras(lista);
        Check(cams.Count == 2 && cams[0].Physical && !cams[1].Physical, "lista do dshow: só vídeo, física marcada");
        Check(FfmpegTools.PickCamera(cams, "")?.Name == "HD Pro Webcam C920" && FfmpegTools.PickCamera(cams, "meta quest 3")?.Name == "Meta Quest 3",
            "câmera automática é a física; a da config vale quando existe");
        Check(RecorderActivity.Clock(TimeSpan.FromSeconds(75)) == "1:15" && RecorderActivity.Clock(TimeSpan.FromSeconds(3725)) == "1:02:05", "relógio da gravação");
        Check(RecorderActivity.VideoKind("telaCamera") == RecordKind.ScreenCamera && RecorderActivity.VideoKind("xyz") == RecordKind.Screen, "modo da config");

        // read-only probes of this PC
        var ff = FfmpegTools.Find(null);
        Check(true, "ffmpeg: " + (ff ?? "não encontrado (o vídeo pede winget install Gyan.FFmpeg)"));
        try
        {
            var (_, w, h, wr, wb) = FfmpegTools.MainMonitor();
            var pp = FfmpegTools.Pip(w, h, wr, wb);
            Check(w > 0 && h > 0 && pp.Y + pp.H <= wb, $"monitor principal: {w}x{h}, área útil até {wr},{wb}; câmera em {pp.X},{pp.Y}");
        }
        catch (Exception e) { Check(false, "monitor principal: " + e.Message); }
        if (ff != null)
        {
            var c = FfmpegTools.ListCameras(ff);
            Check(true, "câmeras: " + (c.Count == 0 ? "nenhuma" : string.Join(", ", c.Select(x => x.Name + (x.Physical ? " (física)" : "")))));
        }
        // the two menu icons (idle and recording) drawn with no window, for a look without opening the island
        try
        {
            var png = Path.Combine(Path.GetTempPath(), "ilha-icones-gravador.png");
            IconPreview.Render(png);
            Check(File.Exists(png), "ícones do menu desenhados em " + png);
        }
        catch (Exception e) { Check(false, "ícones do menu: " + e.Message); }
        // the PC's sound for half a second into a temporary WAV: the file is padded to the stop even in silence
        var tmp = Path.Combine(Path.GetTempPath(), $"ilha-teste-gravador-{Environment.ProcessId}.wav");
        try
        {
            long t0 = DateTime.UtcNow.Ticks;
            var a = new AudioCapture(true, tmp, t0);
            if (!a.Start()) Check(false, "loopback para WAV: " + a.Error);
            else
            {
                System.Threading.Thread.Sleep(500);
                long stop = DateTime.UtcNow.Ticks;
                a.Stop(stop);
                var bytes = File.ReadAllBytes(tmp);
                int rate = BitConverter.ToInt32(bytes, 24), block = BitConverter.ToUInt16(bytes, 32);
                long frames = (bytes.Length - 8 - 4 - 8 - BitConverter.ToInt32(bytes, 16) - 8) / Math.Max(1, block);
                double secs = frames / (double)rate, expected = (stop - t0) / 1e7;
                Check(Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Math.Abs(secs - expected) < 0.03,
                    $"loopback para WAV: {secs:F3} s gravados para {expected:F3} s de relógio ({rate} Hz)");
            }
        }
        catch (Exception e) { Check(false, "loopback para WAV: " + e.Message); }
        finally { try { File.Delete(tmp); } catch { } }
        return sb.ToString().TrimEnd();
    }
}

public sealed class ConfigGravador
{
    [Label("Modo do vídeo", "Video mode")]
    [Help("tela, camera ou telaCamera (a câmera no canto da tela). O cartão troca isso.", "tela (screen), camera, or telaCamera (the camera in a corner of the screen). The card changes it.")]
    [JsonPropertyName("modo")] public string Modo { get; set; } = "tela";

    [Label("Vídeo: som do PC", "Video: PC sound")]
    [JsonPropertyName("somDoPc")] public bool SomDoPc { get; set; } = true;

    [Label("Vídeo: microfone", "Video: microphone")]
    [JsonPropertyName("microfone")] public bool Microfone { get; set; } = true;

    [Label("Áudio: microfone", "Audio: microphone")]
    [JsonPropertyName("audioMicrofone")] public bool AudioMicrofone { get; set; } = true;

    [Label("Áudio: som do PC", "Audio: PC sound")]
    [JsonPropertyName("audioSomDoPc")] public bool AudioSomDoPc { get; set; }

    [Label("Pasta dos vídeos", "Videos folder")]
    [Help("Vazio = Vídeos\\Ilha.", "Empty = Videos\\Ilha.")]
    [JsonPropertyName("pastaVideos")] public string PastaVideos { get; set; } = "";

    [Label("Pasta dos áudios", "Audio folder")]
    [Help("Vazio = Músicas\\Ilha.", "Empty = Music\\Ilha.")]
    [JsonPropertyName("pastaAudios")] public string PastaAudios { get; set; } = "";

    [Label("Câmera", "Camera")]
    [Help("O nome como o Windows mostra. Vazio = a primeira câmera USB.", "The name as Windows shows it. Empty = the first USB camera.")]
    [JsonPropertyName("camera")] public string Camera { get; set; } = "";

    [Label("Quadros por segundo da tela", "Screen frames per second")]
    [JsonPropertyName("fps")] public int Fps { get; set; } = 60;

    [Label("Qualidade (CQ do NVENC)", "Quality (NVENC CQ)")]
    [Help("Menor é melhor e maior: 19 é quase sem perda visível.", "Lower is better and bigger: 19 looks nearly lossless.")]
    [JsonPropertyName("qualidade")] public int Qualidade { get; set; } = 19;

    [Label("Caminho do ffmpeg", "ffmpeg path")]
    [Help("Vazio = o do PATH.", "Empty = the one on PATH.")]
    [JsonPropertyName("ffmpeg")] public string Ffmpeg { get; set; } = "";
}
