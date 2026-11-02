using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Ilha.LiveWallpaper;

/// <summary>
/// Fundo Vivo (the live wallpaper app, github.com/FelipeWASD83/fundo-vivo) on the island: a "Fundo" menu entry opens the
/// picker with the wallpapers he chose, and a click puts one on a monitor. Off by default without the app: the entry
/// says it was not found.
/// </summary>
[Modulo("fundo", "Fundo Vivo", English = "Fundo Vivo")]
internal sealed class FundoModule : Modulo
{
    private ConfigFundo _cfg = new();
    private PickerActivity? _atv;

    public override Type? TipoConfig => typeof(ConfigFundo);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigFundo>(cfg);
        var atv = new PickerActivity(S, () => _cfg, c => { _cfg = c; Salvar(c); });
        _atv = atv;
        S.Ilha.Adicionar(atv);
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "fundo", Nome = Lang.T("Fundo", "Wallpaper"), Ordem = 58,
            Abrir = atv.OpenCard,
            Icone = (dc, p, r) => Icon(dc, p.G.Fabrica, r),
            Detalhe = atv.MenuDetail,
        });
    }

    public override void ConfigMudou(JsonObject cfg) => _cfg = Ler<ConfigFundo>(cfg);

    public override void Parar()
    {
        if (_atv != null && S.Ilha.Expandida == _atv) S.Ilha.Recolher();
    }

    /// <summary>The menu icon: a little screen with a dusk sky, a sun and two mountains.</summary>
    internal static void Icon(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r)
    {
        float x = r.X, y = r.Y;
        var screen = new RoundedRectangle(new System.Drawing.RectangleF(x + 1.5f, y + 3.5f, 21, 16), 3.4f, 3.4f);
        using (var stops = dc.CreateGradientStopCollection(new[]
               {
                   new GradientStop { Position = 0, Color = new Color4(0.36f, 0.42f, 0.98f, 1) },
                   new GradientStop { Position = 0.62f, Color = new Color4(0.86f, 0.46f, 0.80f, 1) },
                   new GradientStop { Position = 1, Color = new Color4(1f, 0.62f, 0.42f, 1) },
               }))
        using (var sky = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = new Vector2(x, y + 3.5f), EndPoint = new Vector2(x, y + 19.5f) }, stops))
            dc.FillRoundedRectangle(ref screen, sky);
        Icone.Circulo(dc, new Vector2(x + 16.3f, y + 8.6f), 2.3f, new Color4(1f, 0.93f, 0.70f, 1));
        // the mountains stay inside the rounded corners: their feet stop short of the screen's bottom corners
        using (var g = f.CreatePathGeometry())
        {
            using (var s = g.Open())
            {
                s.BeginFigure(new Vector2(x + 3.4f, y + 19.5f), FigureBegin.Filled);
                s.AddLine(new Vector2(x + 9, y + 11.2f));
                s.AddLine(new Vector2(x + 12.2f, y + 15.4f));
                s.AddLine(new Vector2(x + 15.6f, y + 12.6f));
                s.AddLine(new Vector2(x + 20.6f, y + 19.5f));
                s.EndFigure(FigureEnd.Closed);
                s.Close();
            }
            using var b = dc.CreateSolidColorBrush(new Color4(0.16f, 0.13f, 0.27f, 1));
            dc.FillGeometry(g, b);
        }
        using var rim = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 0.85f));
        var outline = new RoundedRectangle(new System.Drawing.RectangleF(x + 1.5f, y + 3.5f, 21, 16), 3.4f, 3.4f);
        dc.DrawRoundedRectangle(outline, rim, 1.3f);
        Icone.Retangulo(dc, x + 9, y + 20.6f, 6, 1.6f, 0.8f, new Color4(1, 1, 1, 0.85f));
    }

    public override string Autoteste()
    {
        var sb = new StringBuilder();
        void Check(bool ok, string name) => sb.AppendLine((ok ? "ok      " : "FALHOU  ") + name);

        // a fake Fundo Vivo home: the config with a field the island does not know, which must survive the write
        string home = Path.Combine(Path.GetTempPath(), $"ilha-teste-fundo-{Environment.ProcessId}");
        string? before = Environment.GetEnvironmentVariable("FUNDOVIVO_HOME");
        try
        {
            Environment.SetEnvironmentVariable("FUNDOVIVO_HOME", home);
            Directory.CreateDirectory(Path.Combine(home, "roaming"));
            var video = Path.Combine(home, "meu video.mp4");
            File.WriteAllBytes(video, new byte[16]);
            File.WriteAllText(FundoVivoBridge.ConfigFile, "{\n  \"version\": 1,\n  \"fpsLimit\": 60,\n  \"scanWallpaperEngine\": false,\n" +
                "  \"monitors\": { \"OUTRO\": { \"itemId\": \"x\", \"type\": \"web\", \"file\": \"x\", \"title\": \"Outro\", \"monitorName\": \"B\" } },\n" +
                "  \"userItems\": [ { \"id\": \"" + FundoVivoBridge.IdFor(video).Replace("\\", "\\\\") + "\", \"title\": \"Meu vídeo\", \"type\": \"video\", \"file\": \"" +
                video.Replace("\\", "\\\\") + "\", \"tags\": [\"x\"] } ]\n}");
            var cfg = FundoVivoBridge.ReadConfig();
            var lib = FundoVivoBridge.Scan(cfg);
            Check(lib.Count == 1 && lib[0].Title == "Meu vídeo" && lib[0].Type == "video" && lib[0].Id == FundoVivoBridge.IdFor(video),
                "biblioteca: o arquivo dele, sem varrer o Wallpaper Engine quando a opção está desligada");
            var mon = new MonitorEntry(@"\\?\DISPLAY#TESTE#1", "Monitor de teste", 3440, 1440, true, 0);
            FundoVivoBridge.Assign(mon, lib[0]);
            var after = FundoVivoBridge.ReadConfig()!;
            Check(after["monitors"]?[mon.Id]?["file"]?.GetValue<string>() == video && after["monitors"]?[mon.Id]?["type"]?.GetValue<string>() == "video"
                  && after["monitors"]?[mon.Id]?["monitorName"]?.GetValue<string>() == "Monitor de teste",
                "aplicar grava o monitor no formato do Fundo Vivo");
            Check(after["fpsLimit"]?.GetValue<int>() == 60 && after["monitors"]?["OUTRO"]?["title"]?.GetValue<string>() == "Outro"
                  && after["userItems"]?[0]?["tags"]?[0]?.GetValue<string>() == "x",
                "aplicar não perde nada do resto da config (outro monitor, campos que a ilha não conhece)");
            Check(!Directory.EnumerateFiles(Path.Combine(home, "roaming"), "*.tmp").Any(), "nenhum arquivo temporário sobrando");
            Check(FundoVivoBridge.Assigned(after)[mon.Id] == lib[0].Id, "a ilha lê o que está em cada monitor");

            var proj = Path.Combine(home, "we", "123");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "index.html"), "<html></html>");
            File.WriteAllText(Path.Combine(proj, "preview.gif"), "GIF");
            File.WriteAllText(Path.Combine(proj, "project.json"), "{ \"title\": \"Chuva\", \"type\": \"Web\", \"file\": \"index.html\", \"preview\": \"preview.gif\", }");
            var it = FundoVivoBridge.Project(proj);
            Check(it is { Title: "Chuva", Type: "web" } && it.Preview!.EndsWith("preview.gif"), "projeto do Wallpaper Engine: título, tipo e prévia (vírgula sobrando aceita)");
            File.WriteAllText(Path.Combine(proj, "project.json"), "{ \"title\": \"Cena\", \"type\": \"scene\", \"file\": \"scene.json\" }");
            Check(FundoVivoBridge.Project(proj) == null, "cena do Wallpaper Engine fica de fora, como no Fundo Vivo");
        }
        catch (Exception e) { Check(false, "pasta falsa do Fundo Vivo: " + e.Message); }
        finally
        {
            Environment.SetEnvironmentVariable("FUNDOVIVO_HOME", before);
            try { Directory.Delete(home, true); } catch { }
        }

        // read-only probes of this PC
        var mons = FundoVivoBridge.Monitors();
        Check(mons.Count > 0 && mons.Count(m => m.Primary) == 1, "monitores: " + string.Join(", ", mons.Select(m => $"{m.Label} {m.Width}x{m.Height}{(m.Primary ? " (principal)" : "")}")));
        var real = FundoVivoBridge.ReadConfig();
        if (real != null)
        {
            var assigned = FundoVivoBridge.Assigned(real);
            Check(assigned.Keys.All(k => mons.Any(m => string.Equals(m.Id, k, StringComparison.OrdinalIgnoreCase))) || assigned.Count == 0,
                $"os monitores da config do Fundo Vivo são os mesmos ids que a ilha calcula ({assigned.Count})");
            var lib = FundoVivoBridge.Scan(real);
            Check(true, $"biblioteca real: {lib.Count} wallpapers ({lib.Count(i => i.Type == "video")} vídeo, {lib.Count(i => i.Type == "web")} web, {lib.Count(i => i.Preview != null)} com prévia)");
            var sample = lib.FirstOrDefault(i => i.Preview != null);
            if (sample != null)
            {
                var t = ThumbCache.Load(sample);
                Check(t != null && t.Width > 0, $"miniatura de \"{sample.Title}\": {t?.Width}x{t?.Height}");
            }
        }
        else Check(true, "Fundo Vivo sem config neste PC");
        Check(true, "FundoVivo.exe: " + (FundoVivoBridge.FindExe(null) ?? "não encontrado") + (FundoVivoBridge.EngineRunning ? " (motor rodando)" : " (motor fechado)"));
        return sb.ToString().TrimEnd();
    }
}

public sealed class ConfigFundo
{
    [Label("Wallpapers no seletor", "Wallpapers in the picker")]
    [Help("Os ids da biblioteca do Fundo Vivo. O botão Escolher do cartão muda isso.", "Fundo Vivo library ids. The card's Choose button changes this.")]
    [JsonPropertyName("favoritos")] public List<string> Favoritos { get; set; } = new();

    [Label("Caminho do FundoVivo.exe", "FundoVivo.exe path")]
    [Help("Vazio = acha sozinho (o que está aberto, o de iniciar com o Windows ou o atalho do Menu Iniciar).", "Empty = found on its own (the running one, the startup entry or the Start menu shortcut).")]
    [JsonPropertyName("exe")] public string Exe { get; set; } = "";
}
