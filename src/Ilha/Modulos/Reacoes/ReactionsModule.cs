using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Ilha.Extras;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Reactions;

/// <summary>
/// The collapsed island reacting to what happens on the PC: something copied, a download that finished, a drive plugged
/// in or out, the internet dropping or coming back, the microphone muted or unmuted, Do Not Disturb turned on or off.
/// All of it from Windows events (no polling, nothing injected), each a short notice that can be turned off.
/// </summary>
[Modulo("reacoes", "Reações do PC", English = "PC reactions")]
internal sealed class ReactionsModule : Modulo
{
    private const float W = 320, H = 44;
    // the Focus/Do Not Disturb moon purple
    private static readonly Color4 Purple = new(0.45f, 0.38f, 0.95f, 1f);
    private ConfigReacoes _cfg = new();
    private SystemEvents? _events;
    private DownloadWatch? _downloads;
    private MicMuteWatch? _mic;
    private InternetWatch? _net;
    private readonly HashSet<char> _drives = new();
    private Alerta? _clipAlert;
    private readonly System.Windows.Forms.Timer _clipSettle = new() { Interval = 250 };
    private bool? _dnd;
    private bool _stopped;
    private Control? _dragSource;

    public override Type? TipoConfig => typeof(ConfigReacoes);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigReacoes>(cfg);
        _clipSettle.Tick += (_, _) => { _clipSettle.Stop(); ClipboardNotice(); };
        foreach (var d in DriveInfo.GetDrives()) _drives.Add(char.ToUpperInvariant(d.Name[0]));
        _dnd = S.ContextoAtual().NaoPerturbeLido;
        Apply();
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigReacoes>(cfg);
        Apply();
    }

    /// <summary>Starts what is on and stops what was turned off.</summary>
    private void Apply()
    {
        if (_stopped) return;
        if (_cfg.Copiar || _cfg.Unidades) { _events ??= Events(); }
        else { _events?.Dispose(); _events = null; }

        string folder = string.IsNullOrWhiteSpace(_cfg.PastaDownloads) ? DownloadsFolder() ?? "" : _cfg.PastaDownloads;
        if (_cfg.Downloads && Directory.Exists(folder))
        {
            if (_downloads?.Folder != folder)
            {
                _downloads?.Dispose();
                try { _downloads = new DownloadWatch(folder, path => S.NaInterface(() => DownloadNotice(path))); }
                catch (Exception e) { Log.Escrever("reações: não deu para vigiar os downloads: " + e.Message); _downloads = null; }
            }
        }
        else { _downloads?.Dispose(); _downloads = null; }

        if (_cfg.Microfone && _mic == null)
        {
            try { _mic = new MicMuteWatch((muted, name) => S.NaInterface(() => MicNotice(muted, name))); }
            catch (Exception e) { Log.Escrever("reações: microfone: " + e.Message); }
        }
        else if (!_cfg.Microfone) { _mic?.Dispose(); _mic = null; }

        if (_cfg.Internet && _net == null) _net = new InternetWatch(online => S.NaInterface(() => NetNotice(online)));
        else if (!_cfg.Internet) { _net?.Dispose(); _net = null; }
    }

    private SystemEvents Events()
    {
        var e = new SystemEvents();
        e.ClipboardChanged += () => { if (_cfg.Copiar && !_stopped) { _clipSettle.Stop(); _clipSettle.Start(); } };
        e.DrivesChanged += (letters, arrived) => { if (_cfg.Unidades && !_stopped) DrivesNotice(letters, arrived); };
        return e;
    }

    public override void Contexto(Contexto ctx)
    {
        var now = ctx.NaoPerturbeLido;
        if (now is bool on && _dnd is bool before && on != before && _cfg.NaoPerturbe && !_stopped) DndNotice(on);
        if (now != null) _dnd = now;
    }

    public override void Parar()
    {
        _stopped = true;
        _clipSettle.Stop();
        _clipSettle.Dispose();
        _events?.Dispose();
        _downloads?.Dispose();
        _mic?.Dispose();
        _net?.Dispose();
        _dragSource?.Dispose();
    }

    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    private static string? DownloadsFolder()
    {
        if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    // ------------------------------------------------------------------ notices

    /// <summary>
    /// One notice: a circle with a drawn icon, a title and a line under it, optionally a picture at the right. The newest
    /// of the same kind replaces the one on screen (copying several times in a row is one notice that keeps changing).
    /// </summary>
    private Alerta Notice(string kind, Color4 tint, Action<ID2D1DeviceContext, ID2D1Factory1, Vector2> icon, string title, string line,
        Retrato? picture = null, Action? click = null, Action<float, float, bool>? drag = null, float seconds = 2.4f)
    {
        var a = new Alerta
        {
            Tipo = "reacao-" + kind,
            Som = "",
            // his PC reads Do Not Disturb as on all the time, so a notice that is not important never shows
            Importante = true,
            Segundos = seconds,
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(W, H));
                Vector2 pic = picture?.TamanhoDip(p.Escala) ?? Vector2.Zero;
                float textW = W - H - (picture != null ? pic.X + 10 : 0);
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    var center = new Vector2(H / 2, H / 2);
                    Icone.Circulo(dc, center, 17, tint);
                    icon(dc, p.G.Fabrica, center);
                    p.Texto.Desenhar(dc, title, new Vector2(H + 4, 3), 14, Cor.Branco, FontWeight.SemiBold, largura: textW);
                    p.Texto.Desenhar(dc, line, new Vector2(H + 4, 23), 12, Cor.Secundario, FontWeight.Normal, largura: textW);
                    if (picture != null) picture.Desenhar(dc, p.Escala, t.X - pic.X, (t.Y - pic.Y) / 2, 6);
                });
                if (click != null || drag != null)
                    c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(0, 0, W, H), Acao = click ?? (() => { }), Arrastar = drag });
                return c;
            },
        };
        S.Ilha.Alertar(a);
        return a;
    }

    private void ClipboardNotice()
    {
        if (_stopped) return;
        var info = ReadClipboard();
        if (info == null) return;
        if (_clipAlert != null) S.Ilha.FecharAlerta(_clipAlert);
        // with the island discreet (the lock closed), what was copied stays off the screen
        bool discreet = global::Ilha.Ilha.Discreta;
        _clipAlert = Notice("copiar", Cor.Verde, Icons.Copy, Lang.T("Copiado", "Copied"),
            discreet ? Lang.T("conteúdo escondido", "content hidden") : info.Value.Line, discreet ? null : info.Value.Picture, seconds: 1.8f);
    }

    /// <summary>
    /// What was copied, in one line (and a small picture for an image). Null when it must not show: copied by the island
    /// itself, or marked by its app as private (password managers set ExcludeClipboardContentFromMonitorProcessing).
    /// </summary>
    private (string Line, Retrato? Picture)? ReadClipboard()
    {
        try
        {
            if (GetClipboardOwner() is var owner && owner != IntPtr.Zero)
            {
                GetWindowThreadProcessId(owner, out uint pid);
                if (pid == (uint)Environment.ProcessId) return null;
            }
            var data = Clipboard.GetDataObject();
            if (data == null) return null;
            var formats = data.GetFormats();
            if (Private(formats, f => data.GetData(f))) return null;
            if (Clipboard.ContainsFileDropList())
            {
                StringCollection files = Clipboard.GetFileDropList();
                return (files.Count == 1 ? Path.GetFileName(files[0] ?? "") : Lang.T($"{files.Count} arquivos", $"{files.Count} files"), null);
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is System.Drawing.Bitmap bmp)
            {
                using (bmp)
                {
                    var r = Retrato.DeBitmap(bmp, (int)(56 * Escala()), (int)(34 * Escala()), recortar: false);
                    return (Lang.T($"Imagem {bmp.Width} × {bmp.Height}", $"Image {bmp.Width} × {bmp.Height}"), r);
                }
            }
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                // 3.9.1: a key or token copied from a site (not marked private by it) never goes on the screen
                return (LooksSecret(text) ? Lang.T("uma chave (escondida)", "a key (hidden)") : OneLine(text), null);
            }
            return (Lang.T("algo da área de transferência", "something to the clipboard"), null);
        }
        catch (Exception e)
        {
            // another app holding the clipboard for a moment: no notice rather than a wrong one
            Log.Escrever("reações: área de transferência: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// Text that looks like a key, a token or a password, by its shape: one word (no spaces) of 20 characters or more that
    /// is a long hex run, starts with a known key prefix (sk-, ghp_, xox, AKIA, eyJ, github_pat_...), or mixes letters
    /// and digits with few repeats (random-looking). A URL, a path or an ordinary word is not.
    /// </summary>
    internal static bool LooksSecret(string text)
    {
        string s = text.Trim();
        if (s.Length < 20 || s.Length > 512 || s.Any(char.IsWhiteSpace)) return false;
        if (s.Contains("://") || s.Contains('\\') || s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(s, "^[0-9A-Fa-f]{24,}$")) return true;
        string[] prefixes = { "sk-", "sk_", "pk_", "rk_", "ghp_", "gho_", "ghs_", "github_pat_", "xox", "AKIA", "ASIA", "eyJ", "AIza", "glpat-", "npm_", "hf_" };
        if (prefixes.Any(p => s.StartsWith(p, StringComparison.Ordinal))) return true;
        bool letters = s.Any(char.IsLetter), digits = s.Any(char.IsDigit);
        if (!letters || !digits) return false;
        // random-looking: most characters different and both cases or symbols mixed in
        double distinct = s.Distinct().Count() / (double)s.Length;
        bool mixed = s.Any(char.IsUpper) && s.Any(char.IsLower) || s.Any(c => c is '_' or '-' or '+' or '/' or '=');
        return mixed && distinct > 0.45 && s.Count(char.IsDigit) >= 3;
    }

    private float Escala() => S.Palco.Escala;

    /// <summary>The formats apps use to keep a copy out of history and monitors (Windows' documented convention).</summary>
    internal static bool Private(IEnumerable<string> formats, Func<string, object?> get)
    {
        foreach (var f in formats)
        {
            if (f is "ExcludeClipboardContentFromMonitorProcessing" or "Clipboard Viewer Ignore") return true;
            if (f is "CanIncludeInClipboardHistory" or "CanUploadToCloudClipboard")
            {
                // a DWORD 0 means "no"
                try { if (get(f) is MemoryStream ms && ms.Length >= 4 && BitConverter.ToInt32(ms.ToArray(), 0) == 0) return true; } catch { }
            }
        }
        return false;
    }

    /// <summary>The text as one short line: whitespace collapsed, quoted, cut at 60 characters.</summary>
    internal static string OneLine(string text)
    {
        var s = string.Join(" ", (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (s.Length == 0) return Lang.T("texto vazio", "empty text");
        if (s.Length > 60) s = s[..59] + "…";
        return "“" + s + "”";
    }

    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    private void DownloadNotice(string path)
    {
        if (_stopped || !File.Exists(path)) return;
        long size = 0;
        try { size = new FileInfo(path).Length; } catch { }
        Retrato? pic = null;
        if (IsImage(path)) pic = Retrato.DeArquivo(path, (int)(56 * Escala()), (int)(34 * Escala()), recortar: false);
        Log.Escrever("reações: download concluído: " + Path.GetFileName(path));
        var drag = new Arraste();
        Notice("download", Cor.Azul, Icons.Download, Lang.T("Download concluído", "Download finished"), Path.GetFileName(path) + " · " + Size(size), pic,
            click: () => { try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { } },
            drag: (x, y, released) =>
            {
                if (drag.Mover(x, y, released, Control.MouseButtons.HasFlag(MouseButtons.Left)))
                    S.NaInterface(() => { try { DragOut(path); } finally { drag.Terminou(); } });
            }, seconds: 5);
    }

    /// <summary>The file dragged out of the notice into another app (like the screenshot thumbnail).</summary>
    private void DragOut(string path)
    {
        if (!File.Exists(path)) return;
        _dragSource ??= new Control();
        _ = _dragSource.Handle;
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { path });
        S.Ilha.SoltarArraste();
        try { _dragSource.DoDragDrop(data, DragDropEffects.Copy); }
        catch (Exception e) { Log.Escrever("reações: arraste: " + e.Message); }
    }

    private static bool IsImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";

    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB".Replace('.', ','),
        < 1024L * 1024 * 1024 => (bytes / 1048576.0).ToString(bytes < 10 * 1048576 ? "0.0" : "0", Lang.Culture) + " MB",
        _ => (bytes / 1073741824.0).ToString("0.0", Lang.Culture) + " GB",
    };

    private void DrivesNotice(IReadOnlyList<char> letters, bool arrived)
    {
        foreach (char l in letters)
        {
            if (arrived ? !_drives.Add(l) : !_drives.Remove(l)) continue;
            string line = $"{l}:";
            if (arrived)
            {
                try
                {
                    var d = new DriveInfo(l + ":\\");
                    if (d.IsReady)
                    {
                        string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : d.VolumeLabel + " · ";
                        line = $"{label}{l}: · {Size(d.TotalSize)}";
                    }
                }
                catch { }
            }
            Log.Escrever($"reações: unidade {l}: {(arrived ? "conectada" : "removida")}");
            Notice("unidade", arrived ? Cor.Azul : Cor.Fraco, Icons.Drive, arrived ? Lang.T("Unidade conectada", "Drive connected") : Lang.T("Unidade removida", "Drive removed"),
                line, click: arrived ? () => { try { Process.Start("explorer.exe", $"{l}:\\"); } catch { } } : null, seconds: 3.5f);
        }
    }

    private void NetNotice(bool online)
    {
        if (_stopped) return;
        Log.Escrever("reações: internet " + (online ? "voltou" : "caiu"));
        Notice("internet", online ? Cor.Verde : Cor.Vermelho, (dc, f, c) => Icons.Wifi(dc, f, c, online),
            online ? Lang.T("Internet voltou", "Internet is back") : Lang.T("Sem internet", "No internet"),
            online ? Lang.T("conectado de novo", "connected again") : Lang.T("a conexão caiu", "the connection dropped"), seconds: online ? 2.5f : 4f);
    }

    private void MicNotice(bool muted, string name)
    {
        if (_stopped) return;
        Log.Escrever("reações: microfone " + (muted ? "mudo" : "ligado"));
        Notice("microfone", muted ? Cor.Vermelho : Cor.Verde, (dc, f, c) => Icons.Mic(dc, f, c, muted),
            muted ? Lang.T("Microfone mudo", "Microphone muted") : Lang.T("Microfone ligado", "Microphone on"),
            name.Length > 0 ? name : Lang.T("microfone padrão", "default microphone"), seconds: 1.8f);
    }

    private void DndNotice(bool on)
    {
        Log.Escrever("reações: não incomodar " + (on ? "ligou" : "desligou"));
        Notice("naoincomodar", on ? Purple : Cor.Fraco, Icons.Moon,
            on ? Lang.T("Não incomodar ligado", "Do Not Disturb on") : Lang.T("Não incomodar desligado", "Do Not Disturb off"),
            on ? Lang.T("avisos do Windows em silêncio", "Windows notifications silenced") : Lang.T("avisos do Windows de volta", "Windows notifications back"), seconds: 2f);
    }

    // ------------------------------------------------------------------ self test

    public override string Autoteste()
    {
        var sb = new StringBuilder();
        void Check(bool ok, string name) => sb.AppendLine((ok ? "ok      " : "FALHOU  ") + name);
        Check(DownloadWatch.IsTemporary(@"C:\d\Unconfirmed 123456.crdownload") && DownloadWatch.IsTemporary(@"C:\d\video.mp4.part")
              && DownloadWatch.IsTemporary(@"C:\d\x.tmp") && DownloadWatch.IsTemporary(@"C:\d\desktop.ini") && DownloadWatch.IsTemporary(@"C:\d\~$doc.docx"),
            "download: nomes de trabalho do navegador não contam");
        Check(!DownloadWatch.IsTemporary(@"C:\d\video.mp4") && !DownloadWatch.IsTemporary(@"C:\d\setup.exe") && !DownloadWatch.IsTemporary(@"C:\d\foto.png"),
            "download: o nome final conta");
        Check(OneLine("  linha um\n\n   linha dois  ") == "“linha um linha dois”" && OneLine(new string('a', 100)).Length == 62,
            "cópia: texto vira uma linha curta entre aspas");
        Check(Private(new[] { "Text", "ExcludeClipboardContentFromMonitorProcessing" }, _ => null), "cópia: marcada como privada pelo gerenciador de senha não aparece");
        Check(LooksSecret("0123456789ABCDEF0123456789ABCDEF") && LooksSecret("sk-proj-abcDEF123ghiJKL456mnoPQR789") && LooksSecret("ghp_a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6q7R8")
              && LooksSecret("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.abc123") && LooksSecret("Xk9_mQ2-vT7pLw4zR8nB3cY6"),
            "cópia: chave, token e senha aleatória ficam escondidos");
        Check(!LooksSecret("https://steamcommunity.com/dev/apikey") && !LooksSecret("C:\\Users\\alguem\\arquivo.txt") && !LooksSecret("olá, tudo bem com você?")
              && !LooksSecret("paralelepipedoparalelepipedo") && !LooksSecret("2026-11-03") && !LooksSecret("alguem.exemplo@gmail.com"),
            "cópia: link, caminho, frase e palavra comum aparecem");
        Check(Private(new[] { "Text", "CanIncludeInClipboardHistory" }, _ => new MemoryStream(BitConverter.GetBytes(0))), "cópia: fora do histórico (DWORD 0) não aparece");
        Check(!Private(new[] { "Text", "CanIncludeInClipboardHistory" }, _ => new MemoryStream(BitConverter.GetBytes(1))) && !Private(new[] { "Text", "Bitmap" }, _ => null),
            "cópia comum aparece");
        Check(SystemEvents.Letters(0b10100) is var l && l.SequenceEqual(new[] { 'C', 'E' }), "unidades: máscara de letras (C: e E:)");
        Check(Size(512) == "512 B" && Size(64L * 1073741824) .StartsWith("64") && Size(5 * 1048576).Contains("MB"), "tamanhos legíveis");

        // a real Downloads-like folder: a browser's temporary file renamed to its final name counts once
        string dir = Path.Combine(Path.GetTempPath(), $"ilha-teste-downloads-{Environment.ProcessId}");
        try
        {
            Directory.CreateDirectory(dir);
            var got = new List<string>();
            using var gotOne = new System.Threading.ManualResetEventSlim();
            using (var w = new DownloadWatch(dir, p => { lock (got) got.Add(p); gotOne.Set(); }))
            {
                string tmp = Path.Combine(dir, "Unconfirmed 4242.crdownload"), final = Path.Combine(dir, "arquivo.zip");
                File.WriteAllBytes(tmp, new byte[2048]);
                System.Threading.Thread.Sleep(200);
                File.Move(tmp, final);
                gotOne.Wait(5000);
                System.Threading.Thread.Sleep(600);
            }
            Check(got.Count == 1 && got[0].EndsWith("arquivo.zip"), $"download de verdade numa pasta: contou {got.Count} vez(es) ({string.Join(", ", got.Select(Path.GetFileName))})");
        }
        catch (Exception e) { Check(false, "pasta de teste de downloads: " + e.Message); }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Check(true, "pasta de downloads: " + (DownloadsFolder() is { } f ? PastaConhecida.Mascarar(f) : "não achada"));
        Check(true, "internet agora: " + (InternetWatch.Online() ? "conectado" : "sem internet"));
        try
        {
            using var mic = new MicMuteWatch((_, _) => { });
            Check(true, "microfone padrão: aviso de mudo registrado");
        }
        catch (Exception e) { Check(false, "microfone padrão: " + e.Message); }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>The notices' icons, drawn in white inside the colored circle (shapes only).</summary>
internal static class Icons
{
    public static void Copy(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c)
    {
        using var b = dc.CreateSolidColorBrush(Cor.Branco);
        var back = new RoundedRectangle(new System.Drawing.RectangleF(c.X - 7, c.Y - 8, 10, 12), 2, 2);
        dc.DrawRoundedRectangle(back, b, 1.6f);
        Icone.Retangulo(dc, c.X - 3, c.Y - 4, 10, 12, 2, Cor.Branco);
    }

    public static void Download(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c)
    {
        Icone.Linha(dc, c + new Vector2(0, -8), c + new Vector2(0, 3), 2, Cor.Branco, f);
        Icone.Linha(dc, c + new Vector2(-5, -2), c + new Vector2(0, 3.5f), 2, Cor.Branco, f);
        Icone.Linha(dc, c + new Vector2(5, -2), c + new Vector2(0, 3.5f), 2, Cor.Branco, f);
        Icone.Linha(dc, c + new Vector2(-7, 8), c + new Vector2(7, 8), 2, Cor.Branco, f);
    }

    public static void Drive(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c)
    {
        // a pen drive: the body and the plug
        Icone.Retangulo(dc, c.X - 4.5f, c.Y - 3, 9, 12, 2, Cor.Branco);
        using var b = dc.CreateSolidColorBrush(Cor.Branco);
        var plug = new RoundedRectangle(new System.Drawing.RectangleF(c.X - 3, c.Y - 9, 6, 6), 1, 1);
        dc.DrawRoundedRectangle(plug, b, 1.4f);
    }

    public static void Wifi(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, bool online)
    {
        var o = c + new Vector2(0, 6);
        Icone.Circulo(dc, o, 1.8f, Cor.Branco);
        foreach (float r in new[] { 5.5f, 9.5f })
            Arc(dc, f, o, r, -135, -45, 1.8f);
        if (!online) Icone.Linha(dc, c + new Vector2(-8, -7), c + new Vector2(8, 7), 2, Cor.Branco, f);
    }

    public static void Mic(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, bool muted)
    {
        Icone.Retangulo(dc, c.X - 3, c.Y - 9, 6, 11, 3, Cor.Branco);
        Arc(dc, f, c + new Vector2(0, -2), 6, 0, 180, 1.6f);
        Icone.Linha(dc, c + new Vector2(0, 4), c + new Vector2(0, 8), 1.6f, Cor.Branco, f);
        if (muted) Icone.Linha(dc, c + new Vector2(-8, -8), c + new Vector2(8, 8), 2, Cor.Branco, f);
    }

    /// <summary>An open arc from one angle to another (degrees, 0 = right, clockwise on screen), round caps.</summary>
    private static void Arc(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float r, float from, float to, float thickness)
    {
        Vector2 At(float deg) => c + new Vector2(MathF.Cos(deg * MathF.PI / 180) * r, MathF.Sin(deg * MathF.PI / 180) * r);
        using var g = f.CreatePathGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(At(from), FigureBegin.Hollow);
            s.AddArc(new ArcSegment { Point = At(to), Size = new Vortice.Mathematics.Size(r, r), SweepDirection = SweepDirection.Clockwise, ArcSize = to - from > 180 ? ArcSize.Large : ArcSize.Small });
            s.EndFigure(FigureEnd.Open);
            s.Close();
        }
        using var b = dc.CreateSolidColorBrush(Cor.Branco);
        using var stroke = f.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        dc.DrawGeometry(g, b, thickness, stroke);
    }

    public static void Moon(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c)
    {
        using var g = f.CreatePathGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(c + new Vector2(3, -8), FigureBegin.Filled);
            s.AddArc(new ArcSegment { Point = c + new Vector2(3, 8), Size = new Vortice.Mathematics.Size(8, 8), SweepDirection = SweepDirection.CounterClockwise, ArcSize = ArcSize.Large });
            s.AddArc(new ArcSegment { Point = c + new Vector2(3, -8), Size = new Vortice.Mathematics.Size(6.5f, 6.5f), SweepDirection = SweepDirection.Clockwise, ArcSize = ArcSize.Small });
            s.EndFigure(FigureEnd.Closed);
            s.Close();
        }
        using var b = dc.CreateSolidColorBrush(Cor.Branco);
        dc.FillGeometry(g, b);
    }
}

public sealed class ConfigReacoes
{
    [Label("Avisar ao copiar", "Notice when copying")]
    [Help("Uma prévia do que foi copiado. Senha copiada de gerenciador de senha não aparece.", "A preview of what was copied. Passwords copied from a password manager don't show.")]
    [JsonPropertyName("copiar")] public bool Copiar { get; set; } = true;

    [Label("Avisar download concluído", "Notice finished downloads")]
    [JsonPropertyName("downloads")] public bool Downloads { get; set; } = true;

    [Label("Pasta dos downloads", "Downloads folder")]
    [Help("Vazio = a pasta Downloads do Windows.", "Empty = the Windows Downloads folder.")]
    [JsonPropertyName("pastaDownloads")] public string PastaDownloads { get; set; } = "";

    [Label("Avisar pendrive e HD externo", "Notice drives plugged in or out")]
    [JsonPropertyName("unidades")] public bool Unidades { get; set; } = true;

    [Label("Avisar quando a internet cai e volta", "Notice internet dropping and coming back")]
    [JsonPropertyName("internet")] public bool Internet { get; set; } = true;

    [Label("Avisar microfone mudo", "Notice microphone mute")]
    [JsonPropertyName("microfone")] public bool Microfone { get; set; } = true;

    [Label("Avisar Não Incomodar", "Notice Do Not Disturb")]
    [JsonPropertyName("naoPerturbe")] public bool NaoPerturbe { get; set; } = true;
}
