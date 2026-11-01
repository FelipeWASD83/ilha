using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Windows.UI.Composition;

namespace Ilha.Extras;

/// <summary>
/// Render da Adobe, só pelo que é confiável:
/// - Media Encoder: o "terminou" e o "falhou" pelos logs que ele escreve no fim de cada encode (AMEEncodingLog.txt e
///   AMEEncodingErrorLog.txt), por evento. Progresso não: o log só tem o fim.
/// - After Effects: o aerender.exe rodando (render pela linha de comando ou por script) vira a atividade
///   "Renderizando" com cronômetro, sem barra (não há API de porcentagem), e o fim vem pelo código de saída. A lista
///   de processos é conferida a cada 10 s só enquanto o After Effects, o Media Encoder ou o aerender estão abertos;
///   o gatilho é a janela da frente virar um deles. Render pela fila de dentro do After Effects não tem nada que dê
///   para observar de fora sem injetar código, então fica de fora.
/// </summary>
[Modulo("render", "Render da Adobe")]
internal sealed class ModuloRender : Modulo
{
    private static readonly string[] Adobe = { "AfterFX.exe", "Adobe Media Encoder.exe", "aerender.exe" };

    private ConfigRender _cfg = new();
    private VigiaAme? _ame;
    private AtividadeRender? _atv;
    private readonly System.Windows.Forms.Timer _conferir = new() { Interval = 10_000 };
    private readonly AcompanhamentoAerender _acomp = new();
    private readonly Dictionary<uint, (IntPtr Handle, DateTime Inicio)> _renders = new();
    private bool _parado;

    public override Type? TipoConfig => typeof(ConfigRender);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigRender>(cfg);
        _atv = new AtividadeRender();
        S.Ilha.Adicionar(_atv);
        _conferir.Tick += (_, _) => Conferir();
        Aplicar();
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigRender>(cfg);
        Aplicar();
    }

    private void Aplicar()
    {
        if (_cfg.MediaEncoder && _ame == null && VigiaAme.Raiz() is string raiz)
        {
            _ame = new VigiaAme(raiz, e => S.NaInterface(() => { if (!_parado && _cfg.MediaEncoder) AlertaAme(e); }));
            _ame.Iniciar();
        }
        else if (!_cfg.MediaEncoder && _ame != null) { _ame.Dispose(); _ame = null; }
        if (_cfg.AfterEffects) Conferir();
        else { _conferir.Stop(); Esquecer(); }
    }

    public override void Contexto(Contexto ctx)
    {
        if (_cfg.AfterEffects && !_conferir.Enabled && EhAdobe(ctx.Processo)) Conferir();
    }

    public static bool EhAdobe(string exe) => Adobe.Any(a => a.Equals(exe, StringComparison.OrdinalIgnoreCase));

    public override void Parar()
    {
        _parado = true;
        _conferir.Stop();
        _conferir.Dispose();
        _ame?.Dispose();
        _ame = null;
        Esquecer();
        _atv?.Acabou();
    }

    private void Esquecer()
    {
        foreach (var r in _renders.Values) CloseHandle(r.Handle);
        _renders.Clear();
        _acomp.Limpar();
        _atv?.Acabou();
    }

    // ------------------------------------------------------------------ aerender

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, STILL_ACTIVE = 259;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint acesso, bool herdar, uint pid);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint codigo);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr h, out long criado, out long saiu, out long kernel, out long usuario);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private void Conferir()
    {
        if (_parado || !_cfg.AfterEffects) return;
        var lista = Processos.Listar();
        bool adobeAberto = lista.Any(p => EhAdobe(p.Exe));
        var aerender = lista.Where(p => p.Exe.Equals("aerender.exe", StringComparison.OrdinalIgnoreCase)).Select(p => p.Pid);
        var (novos, sumiram) = _acomp.Atualizar(aerender);
        foreach (var pid in novos)
        {
            // só o direito de consultar (código de saída e hora de início); o aerender não é jogo nenhum
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            var inicio = DateTime.Now;
            if (h != IntPtr.Zero && GetProcessTimes(h, out long criado, out _, out _, out _) && criado > 0) inicio = DateTime.FromFileTime(criado);
            _renders[pid] = (h, inicio);
        }
        foreach (var pid in sumiram)
        {
            if (!_renders.Remove(pid, out var r)) continue;
            uint? codigo = null;
            if (r.Handle != IntPtr.Zero)
            {
                if (GetExitCodeProcess(r.Handle, out uint c) && c != STILL_ACTIVE) codigo = c;
                CloseHandle(r.Handle);
            }
            AlertaAerender(codigo, DateTime.Now - r.Inicio);
        }
        if (_renders.Count > 0) _atv?.Comecou(_renders.Values.Min(x => x.Inicio), _renders.Count);
        else _atv?.Acabou();
        bool continuar = adobeAberto || _renders.Count > 0;
        if (continuar && !_conferir.Enabled) _conferir.Start();
        else if (!continuar) _conferir.Stop();
    }

    // ------------------------------------------------------------------ alertas

    private void AlertaAerender(uint? codigo, TimeSpan duracao)
    {
        bool falhou = codigo is uint c && c != 0;
        string titulo = falhou ? "Render do After Effects falhou" : "Render do After Effects terminou";
        string sub = falhou ? $"aerender saiu com o código {codigo}" : "aerender";
        Alertar(titulo, sub, "levou " + AtividadeRender.Cronometro(duracao), falhou);
    }

    private void AlertaAme(EncodeAme e)
    {
        string arquivo = LogAme.NomeDoArquivo(e.Saida ?? e.Origem);
        string titulo = e.Falhou ? "Render falhou" : "Render terminou";
        string terceira = e.Falhou ? (e.Mensagem ?? e.Estado) : "Media Encoder" + (string.IsNullOrWhiteSpace(e.Tempo) ? "" : " · " + e.Tempo);
        Alertar(titulo, arquivo.Length > 0 ? arquivo : e.Estado, terceira, e.Falhou);
    }

    private void Alertar(string titulo, string sub, string terceira, bool falhou)
    {
        S.Ilha.Alertar(new Alerta
        {
            Tipo = "render",
            Som = falhou ? "erro" : "alerta",
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(360, 66));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    var cor = falhou ? Cor.Vermelho : Cor.Verde;
                    var centro = new Vector2(24, t.Y / 2);
                    Icone.Circulo(dc, centro, 19, new Color4(cor.R, cor.G, cor.B, 0.22f));
                    if (falhou)
                    {
                        Icone.Linha(dc, centro + new Vector2(-6, -6), centro + new Vector2(6, 6), 3, cor, p.G.Fabrica);
                        Icone.Linha(dc, centro + new Vector2(6, -6), centro + new Vector2(-6, 6), 3, cor, p.G.Fabrica);
                    }
                    else
                    {
                        Icone.Linha(dc, centro + new Vector2(-7, 0), centro + new Vector2(-2, 5), 3, cor, p.G.Fabrica);
                        Icone.Linha(dc, centro + new Vector2(-2, 5), centro + new Vector2(7, -5), 3, cor, p.G.Fabrica);
                    }
                    float x0 = 58, l = t.X - x0;
                    p.Texto.Desenhar(dc, titulo, new Vector2(x0, 4), 16, Cor.Branco, largura: l);
                    p.Texto.Desenhar(dc, sub, new Vector2(x0, 27), 12.5f, Cor.Branco, FontWeight.Normal, largura: l);
                    p.Texto.Desenhar(dc, terceira, new Vector2(x0, 46), 11.5f, Cor.Secundario, FontWeight.Normal, largura: l);
                });
                return c;
            },
        });
    }

    // ------------------------------------------------------------------ autoteste

    public override string Autoteste() => TestesRender.Rodar();
}

/// <summary>Quais aerender.exe são novos e quais sumiram entre duas conferências.</summary>
internal sealed class AcompanhamentoAerender
{
    private readonly HashSet<uint> _vistos = new();

    public (List<uint> Novos, List<uint> Sumiram) Atualizar(IEnumerable<uint> agora)
    {
        var atual = new HashSet<uint>(agora);
        var novos = atual.Where(p => !_vistos.Contains(p)).OrderBy(p => p).ToList();
        var sumiram = _vistos.Where(p => !atual.Contains(p)).OrderBy(p => p).ToList();
        _vistos.Clear();
        _vistos.UnionWith(atual);
        return (novos, sumiram);
    }

    public void Limpar() => _vistos.Clear();
}

/// <summary>"Renderizando" com cronômetro desde o início do aerender. Sem barra: não existe porcentagem para ler.</summary>
internal sealed class AtividadeRender : Atividade
{
    public override string Tipo => "render";
    public override string Nome => "Render do After Effects";

    private static readonly Color4 Violeta = new(0.6f, 0.58f, 1f, 1f);
    private readonly System.Windows.Forms.Timer _segundo = new() { Interval = 1000 };
    private DateTime _inicio;
    private int _quantos;
    private long _desenhado = -1;

    public AtividadeRender()
    {
        // só anda enquanto alguma cena está na tela, e só redesenha quando o segundo vira
        _segundo.Tick += (_, _) =>
        {
            if (!TemCenaViva || !Ativa) { _segundo.Stop(); return; }
            long s = (long)(DateTime.Now - _inicio).TotalSeconds;
            if (s != _desenhado) Redesenhar();
        };
    }

    public void Comecou(DateTime inicio, int quantos)
    {
        bool mudou = inicio != _inicio || quantos != _quantos;
        _inicio = inicio;
        _quantos = quantos;
        Ligar();
        if (mudou) Redesenhar();
    }

    public void Acabou()
    {
        _segundo.Stop();
        Desligar();
    }

    public static string Cronometro(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        long s = (long)t.TotalSeconds;
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    private string Agora()
    {
        var t = DateTime.Now - _inicio;
        _desenhado = (long)t.TotalSeconds;
        return Cronometro(t);
    }

    private static void Girador(Palco p, Cena c, Vector2 pos, float lado)
    {
        var s = p.G.NovaSuperficie(new Vector2(lado, lado), (dc, t) => Desenhos.Arco(dc, p.G.Fabrica, t / 2, lado / 2 - 2, 2.6f, Violeta));
        var v = p.C.CreateSpriteVisual();
        v.Size = s.TamanhoPx;
        v.Offset = new Vector3(p.Px(pos.X), p.Px(pos.Y), 0);
        v.Brush = s.Pincel;
        v.CenterPoint = new Vector3(v.Size.X / 2, v.Size.Y / 2, 0);
        c.Raiz.Children.InsertAtTop(v);
        c.Superficies.Add(s);
        // gira no compositor (DWM), não por timer do app
        var a = p.C.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0f, 0f);
        a.InsertKeyFrame(1f, 360f, p.C.CreateLinearEasingFunction());
        a.Duration = TimeSpan.FromSeconds(1.1);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        v.StartAnimation("RotationAngleInDegrees", a);
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        _segundo.Start();
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(24, 24));
                Girador(p, c, Vector2.Zero, 24);
                return c;
            }
            case Forma.Compacta:
            {
                var c = p.NovaCena(new Vector2(170, 24));
                Girador(p, c, Vector2.Zero, 24);
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    string txt = Agora();
                    var tam = p.Texto.Medir(txt, 15, FontWeight.SemiBold, tabular: true);
                    p.Texto.Desenhar(dc, txt, new Vector2(t.X - tam.X, (t.Y - tam.Y) / 2), 15, Violeta, FontWeight.SemiBold, tabular: true);
                });
                return c;
            }
            default:
            {
                var c = p.NovaCena(new Vector2(320, 86));
                Girador(p, c, new Vector2(0, 0), 20);
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    p.Texto.Desenhar(dc, _quantos > 1 ? $"After Effects · {_quantos} renders (aerender)" : "After Effects · renderizando (aerender)", new Vector2(28, 1), 13, Cor.Secundario, largura: t.X - 28);
                    p.Texto.Desenhar(dc, Agora(), new Vector2(-2, 22), 40, Violeta, FontWeight.Medium, tabular: true);
                    p.Texto.Desenhar(dc, "Sem porcentagem: o After Effects não informa o progresso", new Vector2(0, 70), 11.5f, Cor.Secundario, FontWeight.Normal, largura: t.X);
                });
                return c;
            }
        }
    }

    public override void Dispose() => _segundo.Dispose();
}

public sealed class ConfigRender
{
    [JsonPropertyName("mediaEncoder")] public bool MediaEncoder { get; set; } = true;
    [JsonPropertyName("afterEffects")] public bool AfterEffects { get; set; } = true;
}

internal static class TestesRender
{
    public static string Rodar()
    {
        var r = new Relatorio();
        const string ok =
            "11/01/2026 03:12:01 PM : Queue Started\n" +
            " - Source File: C:\\Projetos\\Abertura.aep\n" +
            " - Output File: C:\\Projetos\\Saida\\Abertura.mp4\n" +
            " - Preset Used: Match Source - High bitrate\n" +
            " - Video: 1920x1080 (1.0), 30 fps, Progressive, Hardware Encoding, 00;00;10;00\n" +
            " - Audio: AAC, 320 kbps, 48 kHz, Stereo\n" +
            " - Bitrate: VBR, 1 pass, Target 10.00 Mbps, Max 12.00 Mbps\n" +
            " - Encoding Time: 00:00:12\n" +
            "11/01/2026 03:12:14 PM : File Successfully Encoded\n" +
            " - Source File: C:\\Projetos\\Outro.mov\n" +
            " - Output File: C:\\Projetos\\Saida\\Outro.mp4\n" +
            " - Encoding Time: 00:01:03\n" +
            "11/01/2026 03:13:20 PM : File Successfully Encoded\n" +
            "11/01/2026 03:13:20 PM : Queue Stopped\n";
        var lidos = LogAme.Ler(ok.Split('\n'), logDeErro: false);
        r.Conferir("log de sucesso: dois encodes, sem o início e o fim da fila", lidos.Count == 2, lidos.Count.ToString());
        r.Conferir("arquivo de saída, origem e tempo", lidos.Count > 0 && LogAme.NomeDoArquivo(lidos[0].Saida) == "Abertura.mp4" && lidos[0].Origem!.EndsWith("Abertura.aep") && lidos[0].Tempo == "00:00:12" && !lidos[0].Falhou);
        r.Conferir("estado e segundo encode", lidos.Count > 1 && lidos[1].Estado == "File Successfully Encoded" && lidos[1].Tempo == "00:01:03");
        const string erro =
            "11/01/2026 15:20:00 : Queue Started\r\n" +
            " - Source File: D:\\Render\\Comp 1.aep\r\n" +
            " - Output File: D:\\Render\\Comp 1.mov\r\n" +
            " - Encoding Time: 00:00:03\r\n" +
            "11/01/2026 15:20:04 : Encoding Failed\r\n" +
            "------------------------------------------------------------\r\n" +
            "Export Error\r\n" +
            "Error compiling movie.\r\n" +
            "Accelerated Renderer Error\r\n" +
            "------------------------------------------------------------\r\n";
        var e = LogAme.Ler(erro.Split('\n'), logDeErro: true);
        r.Conferir("log de erro: falhou, com as duas primeiras linhas da mensagem",
            e.Count == 1 && e[0].Falhou && e[0].Estado == "Encoding Failed" && e[0].Mensagem == "Export Error: Error compiling movie.", e.Count == 1 ? e[0].Mensagem ?? "" : e.Count.ToString());
        const string traduzido =
            " - Arquivo de origem: C:\\a\\b.aep\n - Arquivo de saída: C:\\a\\b.mp4\n - Tempo de codificação: 00:00:30\n" +
            "01/11/2026 15:30:00 : Arquivo codificado com êxito\n";
        var t = LogAme.Ler(traduzido.Split('\n'), false);
        r.Conferir("log traduzido para português", t.Count == 1 && LogAme.NomeDoArquivo(t[0].Saida) == "b.mp4" && t[0].Tempo == "00:00:30" && t[0].Estado == "Arquivo codificado com êxito");
        var semChave = LogAme.Ler(" - Quelldatei: C:\\x\\y.aep\n - Zieldatei: C:\\x\\y.mp4\n2026-11-01 15:31:00 : Fertig\n".Split('\n'), false);
        r.Conferir("chave desconhecida: vale a posição", semChave.Count == 1 && LogAme.NomeDoArquivo(semChave[0].Saida) == "y.mp4");
        r.Conferir("nome do arquivo", LogAme.NomeDoArquivo(@"C:\a\b c.mp4") == "b c.mp4" && LogAme.NomeDoArquivo("/x/y.mov") == "y.mov" && LogAme.NomeDoArquivo(null) == "");

        // leitura incremental de verdade num arquivo temporário (UTF-8 e UTF-16)
        r.Tentar("leitor incremental", () =>
        {
            string tmp = Path.Combine(Path.GetTempPath(), "ilha-teste-ame-" + Environment.ProcessId + ".txt");
            try
            {
                File.WriteAllText(tmp, "linha velha\n", new UTF8Encoding(false));
                var li = new LeitorIncremental(tmp, doFim: true);
                bool vazio = li.Novas().Count == 0;
                File.AppendAllText(tmp, "nova 1\nnova 2\nmeia");
                var n1 = li.Novas();
                File.AppendAllText(tmp, " linha\n");
                var n2 = li.Novas();
                File.WriteAllText(tmp, "recomeçou\n", new UTF8Encoding(false));
                var n3 = li.Novas();
                r.Conferir("incremental: só o novo, linha incompleta espera, arquivo recriado recomeça",
                    vazio && n1.Where(x => x.Length > 0).SequenceEqual(new[] { "nova 1", "nova 2" }) && n2.Where(x => x.Length > 0).SequenceEqual(new[] { "meia linha" })
                    && n3.Where(x => x.Length > 0).SequenceEqual(new[] { "recomeçou" }), string.Join("|", n1.Concat(n2).Concat(n3)));
                File.WriteAllText(tmp, "ação\n", Encoding.Unicode);
                var u = new LeitorIncremental(tmp, doFim: false).Novas();
                r.Conferir("incremental: UTF-16 com BOM", u.Where(x => x.Length > 0).SequenceEqual(new[] { "ação" }), string.Join("|", u));
                var sumiu = new LeitorIncremental(tmp + ".nao-existe", doFim: true).Novas();
                r.Conferir("incremental: arquivo que não existe não quebra", sumiu.Count == 0);
            }
            finally { try { File.Delete(tmp); } catch { } }
        });

        var ac = new AcompanhamentoAerender();
        var p1 = ac.Atualizar(new uint[] { 10 });
        var p2 = ac.Atualizar(new uint[] { 10, 20 });
        var p3 = ac.Atualizar(new uint[] { 20 });
        var p4 = ac.Atualizar(Array.Empty<uint>());
        r.Conferir("aerender: começa, outro junto, um termina, o outro termina",
            p1.Novos.SequenceEqual(new uint[] { 10 }) && p2.Novos.SequenceEqual(new uint[] { 20 }) && p2.Sumiram.Count == 0
            && p3.Sumiram.SequenceEqual(new uint[] { 10 }) && p4.Sumiram.SequenceEqual(new uint[] { 20 }) && p4.Novos.Count == 0);
        r.Conferir("cronômetro", AtividadeRender.Cronometro(TimeSpan.FromSeconds(75)) == "1:15" && AtividadeRender.Cronometro(TimeSpan.FromSeconds(3725)) == "1:02:05" && AtividadeRender.Cronometro(TimeSpan.FromSeconds(-3)) == "0:00");
        r.Conferir("processos da Adobe", ModuloRender.EhAdobe("afterfx.exe") && ModuloRender.EhAdobe("Adobe Media Encoder.exe") && ModuloRender.EhAdobe("aerender.exe") && !ModuloRender.EhAdobe("Photoshop.exe"));

        r.Tentar("sonda", () =>
        {
            var raiz = VigiaAme.Raiz();
            if (raiz == null || !Directory.Exists(raiz)) { r.Sonda($"Media Encoder: pasta {PastaConhecida.Mascarar(raiz)} não existe (o vigia espera ela aparecer)"); return; }
            var versoes = Directory.GetDirectories(raiz).Select(Path.GetFileName);
            var logs = Directory.EnumerateFiles(raiz, "AMEEncoding*Log.txt", SearchOption.AllDirectories).Select(x => PastaConhecida.Mascarar(x)).ToList();
            r.Sonda($"Media Encoder: versões {string.Join(", ", versoes)}; logs: {(logs.Count == 0 ? "nenhum ainda" : string.Join(", ", logs))}");
        });
        r.Tentar("processos", () =>
        {
            var abertos = Processos.Listar().Where(p => ModuloRender.EhAdobe(p.Exe)).Select(p => p.Exe).Distinct().ToList();
            r.Sonda("Adobe aberto agora: " + (abertos.Count == 0 ? "nada" : string.Join(", ", abertos)));
        });
        return r.ToString();
    }
}
