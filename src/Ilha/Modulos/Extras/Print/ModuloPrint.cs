using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Forms;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Extras;

/// <summary>
/// Print tirado (Win+Shift+S com o salvamento automático do Recorte, ou PrtScn): a ilha abre com a miniatura, e a
/// miniatura pode ser arrastada para soltar em outro app (Discord, navegador, Explorer, editor). Gatilho é a pasta
/// de capturas do Windows (FOLDERID_Screenshots, o nome em português é só o que o Explorador mostra), vigiada por
/// evento; o arquivo só é lido depois que o Recorte termina de gravar.
/// </summary>
[Modulo("print", "Prints")]
internal sealed class ModuloPrint : Modulo
{
    private const float L = 380, A = 96, MINI_L = 150, MINI_A = 84;

    private ConfigPrint _cfg = new();
    private FileSystemWatcher? _vigia;
    private string? _pasta;
    private readonly Recentes _recentes = new(TimeSpan.FromSeconds(4));
    private Control? _origemDoArraste;
    private bool _parado;

    public override Type? TipoConfig => typeof(ConfigPrint);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigPrint>(cfg);
        Vigiar();
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        var antes = _cfg.Pasta;
        _cfg = Ler<ConfigPrint>(cfg);
        if (antes != _cfg.Pasta) Vigiar();
    }

    public override void Parar()
    {
        _parado = true;
        _vigia?.Dispose();
        _vigia = null;
        _origemDoArraste?.Dispose();
        _origemDoArraste = null;
    }

    public static string? PastaDosPrints(string configurada) =>
        !string.IsNullOrWhiteSpace(configurada) ? Environment.ExpandEnvironmentVariables(configurada.Trim()) : PastaConhecida.Caminho(PastaConhecida.Capturas);

    private void Vigiar()
    {
        _vigia?.Dispose();
        _vigia = null;
        _pasta = PastaDosPrints(_cfg.Pasta);
        if (_pasta == null || !Directory.Exists(_pasta))
        {
            Log.Escrever("print: pasta de capturas não existe (o Recorte só cria quando salva o primeiro print)");
            return;
        }
        try
        {
            var w = new FileSystemWatcher(_pasta)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            w.Created += (_, e) => Chegou(e.FullPath);
            w.Renamed += (_, e) => Chegou(e.FullPath);
            w.Error += (_, e) => Log.Escrever("print: vigia da pasta: " + e.GetException().Message);
            w.EnableRaisingEvents = true;
            _vigia = w;
        }
        catch (Exception e) { Log.Escrever("print: não deu para vigiar a pasta: " + e.Message); }
    }

    public static bool EhImagem(string caminho) =>
        Path.GetExtension(caminho).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";

    // chega numa thread do sistema
    private void Chegou(string caminho)
    {
        if (_parado || !EhImagem(caminho) || !_recentes.Novo(caminho, DateTime.UtcNow)) return;
        S.NaInterface(() =>
        {
            float escala = S.Palco.Escala;
            Task.Run(() => Preparar(caminho, escala));
        });
    }

    private void Preparar(string caminho, float escala)
    {
        if (!Arquivo.EsperarFechar(caminho, TimeSpan.FromSeconds(15))) { Log.Escrever("print: o arquivo não ficou pronto"); return; }
        var mini = Retrato.DeArquivo(caminho, (int)MathF.Round(MINI_L * escala), (int)MathF.Round(MINI_A * escala), recortar: false);
        if (mini == null) return;
        S.NaInterface(() => { if (!_parado) Mostrar(caminho, mini); });
    }

    private void Mostrar(string caminho, Retrato mini)
    {
        var arraste = new Arraste();
        S.Ilha.Alertar(new Alerta
        {
            Tipo = "print",
            Som = "alerta",
            Segundos = Math.Clamp(_cfg.Segundos, 2, 30),
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(L, A));
                var tam = mini.TamanhoDip(p.Escala);
                float mx = (MINI_L - tam.X) / 2, my = (A - tam.Y) / 2;
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    Icone.Retangulo(dc, mx - 1, my - 1, tam.X + 2, tam.Y + 2, 9, Cor.Fraco);
                    mini.Desenhar(dc, p.Escala, mx, my, 8);
                    float x0 = MINI_L + 16, l = t.X - x0;
                    p.Texto.Desenhar(dc, "Print salvo", new Vector2(x0, 14), 17, Cor.Branco, largura: l);
                    p.Texto.Desenhar(dc, "Arraste a miniatura para outro app", new Vector2(x0, 40), 12.5f, Cor.Secundario, FontWeight.Normal, largura: l);
                    p.Texto.Desenhar(dc, Path.GetFileName(caminho), new Vector2(x0, 62), 11, Cor.Secundario, FontWeight.Normal, largura: l);
                });
                c.Botoes.Add(new Botao
                {
                    Area = new System.Drawing.RectangleF(mx, my, tam.X, tam.Y),
                    Arrastar = (x, y, soltou) =>
                    {
                        if (arraste.Mover(x, y, soltou, Control.MouseButtons.HasFlag(MouseButtons.Left)))
                            S.NaInterface(() => { try { Arrastar(caminho); } finally { arraste.Terminou(); } });
                    },
                });
                return c;
            },
        });
    }

    /// <summary>
    /// OLE DoDragDrop com o arquivo (CF_HDROP, o que navegador, Discord e Explorer pegam) e a imagem (bitmap e PNG,
    /// para quem só aceita imagem). DoDragDrop precisa de um Control: um invisível, criado uma vez.
    /// </summary>
    private void Arrastar(string caminho)
    {
        if (!File.Exists(caminho)) return;
        _origemDoArraste ??= new Control();
        _ = _origemDoArraste.Handle;
        using var imagem = Retrato.AbrirBitmap(caminho);
        var dados = new DataObject();
        dados.SetData(DataFormats.FileDrop, new[] { caminho });
        if (imagem != null) dados.SetImage(imagem);
        if (Path.GetExtension(caminho).Equals(".png", StringComparison.OrdinalIgnoreCase))
            try { dados.SetData("PNG", false, new MemoryStream(File.ReadAllBytes(caminho))); } catch { }
        using var fantasma = imagem == null ? null : Miniatura(imagem, 220);
        // a janela de toque da ilha segurava o mouse (SetCapture do arraste): solta para o OLE pegar, e o arraste do
        // núcleo acaba aqui (o OLE engole o botão solto)
        S.Ilha.SoltarArraste();
        try
        {
            if (fantasma != null)
                _origemDoArraste.DoDragDrop(dados, DragDropEffects.Copy, fantasma, new System.Drawing.Point(fantasma.Width / 2, fantasma.Height / 2), false);
            else
                _origemDoArraste.DoDragDrop(dados, DragDropEffects.Copy);
        }
        catch (Exception e) { Log.Escrever("print: arraste: " + e.Message); }
    }

    private static System.Drawing.Bitmap Miniatura(System.Drawing.Image img, int largura)
    {
        var (l, a) = Encaixar(img.Width, img.Height, largura, largura);
        var b = new System.Drawing.Bitmap(l, a, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(b);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(img, 0, 0, l, a);
        return b;
    }

    /// <summary>Tamanho que cabe em maxL x maxA mantendo a proporção.</summary>
    public static (int L, int A) Encaixar(int l, int a, int maxL, int maxA)
    {
        if (l <= 0 || a <= 0) return (maxL, maxA);
        float k = MathF.Min((float)maxL / l, (float)maxA / a);
        return (Math.Max(1, (int)MathF.Round(l * k)), Math.Max(1, (int)MathF.Round(a * k)));
    }

    public override string Autoteste()
    {
        var r = new Relatorio();
        r.Conferir("extensões de imagem", EhImagem(@"C:\x\Captura de tela 2026-11-01 150000.png") && EhImagem("a.JPG") && !EhImagem("a.png.tmp") && !EhImagem("a.txt"));
        var rec = new Recentes(TimeSpan.FromSeconds(4));
        var t0 = new DateTime(2026, 11, 1, 15, 0, 0, DateTimeKind.Utc);
        r.Conferir("Created e Renamed do mesmo arquivo viram um print só",
            rec.Novo(@"C:\p\a.png", t0) && !rec.Novo(@"C:\P\A.PNG", t0.AddSeconds(1)) && rec.Novo(@"C:\p\b.png", t0.AddSeconds(1)) && rec.Novo(@"C:\p\a.png", t0.AddSeconds(6)));
        r.Conferir("miniatura de ultrawide cabe na largura", Encaixar(3440, 1440, 150, 84) == (150, 63));
        r.Conferir("miniatura de monitor em pé cabe na altura", Encaixar(1080, 1920, 150, 84) == (47, 84));
        var ar = new Arraste();
        bool p1 = ar.Mover(10, 10, false, true), p2 = ar.Mover(11, 11, false, true), p3 = ar.Mover(20, 10, false, true), p4 = ar.Mover(40, 10, false, true);
        r.Conferir("arraste começa depois de 4 DIP e só uma vez", !p1 && !p2 && p3 && !p4);
        ar.Terminou();
        r.Conferir("depois do arraste, passar o mouse sem botão não arrasta de novo", !ar.Mover(12, 12, false, false) && !ar.Mover(30, 30, false, false));
        var ar2 = new Arraste();
        r.Conferir("clique sem mexer não arrasta", !ar2.Mover(5, 5, false, true) && !ar2.Mover(5, 5, true, false));
        var imagemDeTeste = new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255 };
        Retrato.ApagarFundoBranco(imagemDeTeste, 3, 3);
        r.Conferir("fundo branco ligado à borda some, o miolo escuro fica", imagemDeTeste[3] == 0 && imagemDeTeste[4 * 4 + 3] == 255);

        r.Tentar("pasta", () =>
        {
            var pasta = PastaDosPrints("");
            bool existe = pasta != null && Directory.Exists(pasta);
            int n = existe ? Directory.EnumerateFiles(pasta!).Count(EhImagem) : 0;
            r.Sonda($"pasta de capturas (FOLDERID_Screenshots): {PastaConhecida.Mascarar(pasta)}, {(existe ? $"existe, {n} imagens" : "não existe ainda")}");
        });
        return r.ToString();
    }
}

/// <summary>O mesmo arquivo chega como Created e como Renamed (e às vezes duas vezes): só o primeiro conta.</summary>
internal sealed class Recentes
{
    private readonly TimeSpan _janela;
    private readonly Dictionary<string, DateTime> _vistos = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _trava = new();

    public Recentes(TimeSpan janela) => _janela = janela;

    public bool Novo(string chave, DateTime agora)
    {
        lock (_trava)
        {
            foreach (var k in _vistos.Where(kv => agora - kv.Value > _janela).Select(kv => kv.Key).ToList()) _vistos.Remove(k);
            if (_vistos.ContainsKey(chave)) return false;
            _vistos[chave] = agora;
            return true;
        }
    }
}

/// <summary>
/// Quando um aperto vira arraste: depois de 4 DIP com o botão apertado, uma vez só. Depois do DoDragDrop o núcleo não
/// recebe o soltar (o OLE engole), então o "apertado" dele fica preso até o próximo clique e o movimento continua
/// chegando aqui: sem o botão de verdade apertado, não conta.
/// </summary>
internal sealed class Arraste
{
    private Vector2? _inicio;
    private bool _foi;

    /// <summary>true = começar o arraste agora.</summary>
    public bool Mover(float x, float y, bool soltou, bool botaoApertado)
    {
        if (soltou || !botaoApertado) { _inicio = null; return false; }
        if (_foi) return false;
        if (_inicio == null) { _inicio = new Vector2(x, y); return false; }
        if (Vector2.Distance(_inicio.Value, new Vector2(x, y)) < 4) return false;
        _foi = true;
        return true;
    }

    public void Terminou()
    {
        _foi = false;
        _inicio = null;
    }
}

public sealed class ConfigPrint
{
    /// <summary>Pasta vigiada; vazio = a pasta de capturas do Windows (Imagens\Capturas de Tela).</summary>
    [JsonPropertyName("pasta")] public string Pasta { get; set; } = "";
    /// <summary>Quanto tempo a miniatura fica na ilha.</summary>
    [JsonPropertyName("segundos")] public float Segundos { get; set; } = 7;
}
