using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ilha;

public sealed class Atalho
{
    [JsonPropertyName("mods")] public int Mods { get; set; } = Nativo.MOD_CONTROL | Nativo.MOD_ALT;
    [JsonPropertyName("tecla")] public int Tecla { get; set; } = 0x20; // espaço
}

public sealed class ConfigSons
{
    [JsonPropertyName("ligados")] public bool Ligados { get; set; } = true;
    // 0 a 1
    [JsonPropertyName("volume")] public double Volume { get; set; } = 0.6;
}

public sealed class Mola
{
    // abaixo de 1 passa do ponto e volta (o overshoot); 1 para sem passar
    [JsonPropertyName("amortecimento")] public float Amortecimento { get; set; } = 0.68f;
    [JsonPropertyName("periodoMs")] public float PeriodoMs { get; set; } = 55f;
}

/// <summary>The values of `modoCompetitivo`.</summary>
public static class ModosCompetitivos
{
    public const string Placar = "placar", Esconder = "esconder", Nada = "nada";

    public static string Normalizar(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        Esconder => Esconder,
        Nada => Nada,
        _ => Placar,
    };
}

public sealed class Config
{
    [JsonPropertyName("pausada")] public bool Pausada { get; set; }
    // interface language: "auto" (follows Windows), "pt" or "en"
    [JsonPropertyName("language")] public string Language { get; set; } = "auto";
    // só a cópia instalada (ferramentas\instalar.ps1) grava no Run do Windows
    [JsonPropertyName("iniciarComWindows")] public bool IniciarComWindows { get; set; } = true;
    // false (padrão) = pílula flutuando um pouco abaixo da borda, como no iPhone; true = colada na borda, estilo notch
    [JsonPropertyName("colada")] public bool Colada { get; set; }
    // distância da borda de cima no modo flutuante, em pontos (escala do Windows)
    [JsonPropertyName("distanciaDoTopo")] public float DistanciaDoTopo { get; set; } = 10f;
    [JsonPropertyName("tamanho")] public float Tamanho { get; set; } = 1f;
    [JsonPropertyName("deslocamentoX")] public int DeslocamentoX { get; set; }
    [JsonPropertyName("deslocamentoY")] public int DeslocamentoY { get; set; }
    [JsonPropertyName("sombra")] public bool Sombra { get; set; } = true;
    [JsonPropertyName("atalho")] public Atalho Atalho { get; set; } = new();
    // a pílula vazia quando não tem nada para mostrar, em qualquer janela que não seja conteúdo em tela cheia
    [JsonPropertyName("pilulaVazia")] public bool PilulaVazia { get; set; } = true;
    // nome antigo (só valia na área de trabalho): lido uma vez e passado para pilulaVazia
    [JsonPropertyName("ociosaNaAreaDeTrabalho"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? OciosaAntiga { get; set; }
    // programas cuja tela cheia é conteúdo (vídeo, navegador, apresentação): escondem a ilha. Jogo não está aqui e não
    // esconde (jogo competitivo esconde pela outra lista); tela cheia exclusiva (Direct3D) conta sempre como jogo
    [JsonPropertyName("appsDeConteudo")] public List<string> AppsDeConteudo { get; set; } = new()
    {
        "brave.exe", "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "vivaldi.exe", "vlc.exe", "mpv.exe",
        "mpc-hc64.exe", "mpc-be64.exe", "PotPlayerMini64.exe", "Microsoft.Media.Player.exe", "Video.UI.exe",
        "POWERPNT.EXE", "Netflix.exe", "stremio.exe",
    };
    [JsonPropertyName("dividir")] public bool Dividir { get; set; } = true;
    // com o Não Incomodar (ou uma sessão de Foco) ligado: sem chegada nem alerta, menos alarme de timer
    [JsonPropertyName("respeitarNaoPerturbe")] public bool RespeitarNaoPerturbe { get; set; } = true;
    [JsonPropertyName("alertaSegundos")] public float AlertaSegundos { get; set; } = 4f;
    // depois que o mouse sai do cartão aberto, quanto esperar antes de recolher (o mouse voltando cancela)
    [JsonPropertyName("atrasoRecolherMs")] public int AtrasoRecolherMs { get; set; } = 600;
    // borda fina e clara que aparece só com fundo escuro atrás (não existe no modo colado)
    [JsonPropertyName("bordaAdaptativa")] public bool BordaAdaptativa { get; set; } = true;
    // a hora no meio da pílula compacta, quando a atividade deixa o meio livre (nunca na área de trabalho)
    [JsonPropertyName("relogio")] public bool Relogio { get; set; } = true;
    // menu entries hidden from the main menu (by id); the module keeps working, only its tile goes away
    [JsonPropertyName("menuEscondido")] public List<string> MenuEscondido { get; set; } = new();
    // ordem entre as atividades; alerta não entra: ele sempre passa por cima de todas
    [JsonPropertyName("prioridades")] public List<string> Prioridades { get; set; } = new() { "volume", "claude", "ia", "timer", "midia", "uso" };
    // nome do .exe (com ou sem ".exe"), sem diferença de maiúscula
    [JsonPropertyName("jogosCompetitivos")] public List<string> JogosCompetitivos { get; set; } = new()
    {
        "cs2.exe", "RocketLeague.exe", "VALORANT-Win64-Shipping.exe", "r5apex.exe", "r5apex_dx12.exe",
        "FortniteClient-Win64-Shipping.exe", "Overwatch.exe", "League of Legends.exe", "RainbowSix.exe",
        "TslGame.exe", "cod.exe", "Deadlock.exe", "dota2.exe", "MarvelRivals-Win64-Shipping.exe", "bf6.exe",
        "Bodycam-Win64-Shipping.exe",
    };
    // games added to the default list after the first versions (4.1.0: Bodycam). A saved config has its own list, so
    // each one is added to it once; jogosOferecidos remembers that, and one he removes stays removed.
    internal static readonly string[] JogosNovos = { "Bodycam-Win64-Shipping.exe" };
    [JsonPropertyName("jogosOferecidos")] public List<string> JogosOferecidos { get; set; } = new();

    /// <summary>Adds the newer default games once. True when the list changed (the caller saves).</summary>
    internal bool AcrescentarJogosNovos()
    {
        bool mudou = false;
        foreach (var j in JogosNovos)
        {
            if (JogosOferecidos.Exists(o => string.Equals(o, j, StringComparison.OrdinalIgnoreCase))) continue;
            JogosOferecidos.Add(j);
            mudou = true;
            if (!JogosCompetitivos.Exists(o => string.Equals(Path.GetFileNameWithoutExtension(o), Path.GetFileNameWithoutExtension(j), StringComparison.OrdinalIgnoreCase)))
                JogosCompetitivos.Add(j);
        }
        return mudou;
    }
    // what a competitive game in front does: "placar" (the pill becomes the live score, the default), "esconder" (the
    // island hides, only the timer stays) or "nada" (the game is treated like any other window)
    [JsonPropertyName("modoCompetitivo")] public string ModoCompetitivo { get; set; } = ModosCompetitivos.Placar;
    // desenhar texto e ícone pela placa de vídeo em vez do processador (gasta ~110 MB a mais de RAM)
    [JsonPropertyName("desenharNaGpu")] public bool DesenharNaGpu { get; set; }
    [JsonPropertyName("mola")] public Mola Mola { get; set; } = new();
    [JsonPropertyName("sons")] public ConfigSons Sons { get; set; } = new();
    // atalhos dos módulos por nome ("calculadora", "tradutor", "ia", "timer"...); o que não estiver aqui usa o padrão do módulo
    [JsonPropertyName("atalhosModulos")] public Dictionary<string, Atalho> AtalhosModulos { get; set; } = new();
    // uma seção por módulo, com "ligado" e o que o módulo quiser guardar
    [JsonPropertyName("modulos")] public System.Text.Json.Nodes.JsonObject Modulos { get; set; } = new();

    // Fora do AppData de propósito: processo aberto de dentro do app do Claude (pacote MSIX) enxerga uma
    // cópia virtual do AppData, e a config de lá não seria a mesma da ilha aberta pelo Menu Iniciar.
    public static readonly string Pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ilha");
    public static string Arquivo => Path.Combine(Pasta, "config.json");

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static float Limite(float v, float min, float max, float padrao) => float.IsFinite(v) ? Math.Clamp(v, min, max) : padrao;

    /// <summary>Lê o arquivo. null com o erro se não der (arquivo com erro de digitação, preso por outro programa).</summary>
    public static Config? TentarLer(out string? erro)
    {
        erro = null;
        try
        {
            var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(Arquivo), Json);
            if (c == null) { erro = "arquivo vazio"; return null; }
            c.Atalho ??= new();
            c.Mola ??= new();
            c.Prioridades ??= new();
            c.JogosCompetitivos ??= new();
            c.JogosOferecidos ??= new();
            c.ModoCompetitivo = ModosCompetitivos.Normalizar(c.ModoCompetitivo);
            c.AppsDeConteudo ??= new();
            if (c.OciosaAntiga is bool antiga) { c.PilulaVazia = antiga; c.OciosaAntiga = null; }
            c.Sons ??= new();
            c.Sons.Volume = double.IsFinite(c.Sons.Volume) ? Math.Clamp(c.Sons.Volume, 0, 1) : 0.6;
            c.AtalhosModulos ??= new();
            c.Modulos ??= new();
            c.Tamanho = Limite(c.Tamanho, 0.5f, 3f, 1f);
            c.DistanciaDoTopo = Limite(c.DistanciaDoTopo, 0f, 200f, 10f);
            c.AlertaSegundos = Limite(c.AlertaSegundos, 1f, 60f, 4f);
            c.AtrasoRecolherMs = Math.Clamp(c.AtrasoRecolherMs, 0, 5000);
            c.Mola.Amortecimento = Limite(c.Mola.Amortecimento, 0.3f, 1.5f, 0.68f);
            c.Mola.PeriodoMs = Limite(c.Mola.PeriodoMs, 20f, 200f, 55f);
            return c;
        }
        catch (Exception e) { erro = e.Message; return null; }
    }

    /// <summary>Na abertura: config com erro não impede a ilha de abrir (guarda a ruim e segue com o padrão).</summary>
    public static Config Carregar()
    {
        if (!File.Exists(Arquivo))
        {
            var nova = new Config();
            nova.AcrescentarJogosNovos();
            nova.Salvar();
            return nova;
        }
        var c = TentarLer(out var erro);
        if (c != null)
        {
            if (c.AcrescentarJogosNovos()) { Log.Escrever("config: jogos competitivos novos acrescentados à lista"); c.Salvar(); }
            return c;
        }
        try { File.Copy(Arquivo, Arquivo + ".estragada", true); } catch { }
        Log.Escrever("config ilegível, usando o padrão: " + erro);
        return new Config();
    }

    public void Salvar()
    {
        try
        {
            Directory.CreateDirectory(Pasta);
            var tmp = Arquivo + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, Arquivo, true);
        }
        catch (Exception e) { Log.Escrever("não salvou a config: " + e.Message); }
    }
}

internal static class Log
{
    private static readonly object _trava = new();

    public static string Arquivo => Path.Combine(Config.Pasta, "ilha.log");

    public static void Escrever(string texto)
    {
        lock (_trava)
        {
            try
            {
                Directory.CreateDirectory(Config.Pasta);
                if (File.Exists(Arquivo) && new FileInfo(Arquivo).Length > 1024 * 1024)
                    File.Move(Arquivo, Arquivo + ".1", true);
                File.AppendAllText(Arquivo, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {texto}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
