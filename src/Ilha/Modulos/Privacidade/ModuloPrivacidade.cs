using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ilha.Privacidade;

/// <summary>
/// Bolinha verde = câmera em uso, laranja = microfone em uso, como as do iPhone. Detecta pelas APIs do Windows
/// (sessões de captura do WASAPI e o monitor de câmera do Media Foundation, avisados pelo sistema, sem polling; o
/// ConsentStore do registro parou de ser gravado nesta versão do Windows). Entrega o estado ao núcleo
/// (S.Indicadores: o contorno em volta da hora na pílula, ou as bolinhas) e mantém a atividade "privacidade" para
/// quando a ilha não tem mais nada.
/// </summary>
[Modulo("privacidade", "Câmera e microfone", English = "Camera and microphone")]
internal sealed class ModuloPrivacidade : Modulo
{
    private ConfigPrivacidade _cfg = new();
    private AtividadePrivacidade? _atv;
    private DetectorDePrivacidade? _detector;

    public override Type? TipoConfig => typeof(ConfigPrivacidade);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigPrivacidade>(cfg);
        _atv = new AtividadePrivacidade();
        S.Ilha.Adicionar(_atv);
        _detector = new DetectorDePrivacidade(ListaIgnorar(), e => S.NaInterface(() => Aplicar(e)));
        _detector.Iniciar();
    }

    private string[] ListaIgnorar() => (_cfg.Ignorar ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();

    private void Aplicar(EstadoPrivacidade e)
    {
        if (_detector == null) return;
        S.Indicadores.Definir(e.Camera.Count > 0, e.AppsDaCamera, e.Microfone.Count > 0, e.AppsDoMicrofone);
        _atv?.Atualizar(e);
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigPrivacidade>(cfg);
        _detector?.Ignorar(ListaIgnorar());
    }

    public override void Parar()
    {
        _detector?.Dispose();
        _detector = null;
        S.Indicadores.Definir(false, "", false, "");
        _atv?.Parar();
    }

    public override string Autoteste()
    {
        var r = new StringBuilder();
        void Conferir(string nome, bool ok, string detalhe = "") =>
            r.AppendLine((ok ? "ok      " : "FALHOU  ") + nome + (detalhe.Length > 0 ? $" ({detalhe})" : ""));

        // nomes
        Conferir("nome do .exe sem caminho", Consentimento.NomeDoExe(@"C:\Program Files (x86)\Steam\jogo\bin\cs2.exe") == "cs2.exe" && Consentimento.NomeDoExe("cs2.exe") == "cs2.exe");
        Conferir("nome amigável do .exe", Consentimento.NomeAmigavelExe("brave.exe") == "brave");
        string unreal = Consentimento.NomeAmigavelExe("Bodycam-Win64-Shipping.exe");
        Conferir("jogo de Unreal perde o -Win64-Shipping", unreal == "Bodycam", unreal);
        Conferir("exe com barra no fim não vira vazio", Consentimento.NomeAmigavelExe(@"C:\estranho\") == "estranho");

        // decisão com processos de mentira
        var exes = new Dictionary<uint, string>
        {
            [10] = "AppDeVideo.exe", [20] = "chat.exe", [21] = "navegador.exe", [22] = "navegador.exe", [24] = "jogo.exe",
        };
        string? Exe(uint pid) => exes.TryGetValue(pid, out var e) ? e : null;
        var camera = new[] { (10u, 3L) };
        var mic = new[] { (20u, 2L), (21u, 1L), (22u, 4L), (23u, 5L), (24u, 6L) };
        var est = Consentimento.Decidir(camera, mic, Exe, new[] { "jogo" });
        Conferir("câmera pelo processo", est.AppsDaCamera == "AppDeVideo", est.AppsDaCamera);
        Conferir("microfone: ordem de início, sem repetir, sem o processo que morreu, sem o ignorado",
            est.AppsDoMicrofone == "navegador, chat", est.AppsDoMicrofone);
        var ign = Consentimento.Decidir(camera, mic, Exe, new[] { "appdevideo", "chat.exe", " ", "NAVEGADOR" });
        Conferir("ignorar pelo nome, pelo .exe e sem diferenciar maiúscula", ign.AppsDaCamera == "" && ign.AppsDoMicrofone == "jogo", ign.AppsDoMicrofone);
        Conferir("nada em uso", !Consentimento.Decidir(Array.Empty<(uint, long)>(), new[] { (23u, 1L) }, Exe, Array.Empty<string>()).Algum);

        // estado e linhas do cartão
        var a = new EstadoPrivacidade(new[] { "App" }, new[] { "App1", "App2" });
        var b = new EstadoPrivacidade(new[] { "App" }, new[] { "App1", "App2" });
        Conferir("estados iguais se comparam iguais", a.Equals(b) && a.GetHashCode() == b.GetHashCode());
        Conferir("estado diferente muda", !a.Equals(new EstadoPrivacidade(new[] { "App" }, new[] { "App1" })) && !a.Equals(EstadoPrivacidade.Vazio));
        string previousLanguage = Lang.Choice;
        Lang.Set("pt");
        var linhas = a.Linhas().Select(x => x.Rotulo + " " + x.Apps).ToList();
        Conferir("linhas do cartão", linhas.Count == 2 && linhas[0] == "Câmera: App" && linhas[1] == "Microfone: App1, App2", string.Join(" | ", linhas));
        Lang.Set("en");
        var lines = a.Linhas().Select(x => x.Rotulo + " " + x.Apps).ToList();
        Conferir("linhas do cartão em inglês", lines.Count == 2 && lines[0] == "Camera: App" && lines[1] == "Microphone: App1, App2", string.Join(" | ", lines));
        Lang.Set(previousLanguage);
        Conferir("só microfone, uma linha", new EstadoPrivacidade(Array.Empty<string>(), new[] { "X" }).Linhas().Count == 1);
        Conferir("largura das bolinhas", AtividadePrivacidade.LarguraDasBolinhas(true, true) == 19 && AtividadePrivacidade.LarguraDasBolinhas(false, true) == 7);

        // sondas de verdade, só leitura: a lista de processos e o detector ligado de verdade por um instante
        int processos = DetectorDePrivacidade.Processos().Count;
        Conferir("sonda: lista de processos (Toolhelp)", processos > 10, $"{processos} processos");
        using (var chegou = new System.Threading.ManualResetEventSlim(false))
        {
            IReadOnlyCollection<uint> micAgora = Array.Empty<uint>(), camAgora = Array.Empty<uint>();
            var uso = new UsoAoVivo((m, c) => { micAgora = m; camAgora = c; chegou.Set(); });
            uso.Start();
            bool leu = chegou.Wait(5000);
            Conferir("sonda: microfones e câmera lidos pelas APIs do Windows (WASAPI e Media Foundation)", leu && uso.Microphones > 0,
                $"{uso.Microphones} microfone(s), monitor de câmera {(uso.CameraOn ? "ligado" : "DESLIGADO")}, em uso agora: microfone [{string.Join(",", micAgora)}] câmera [{string.Join(",", camAgora)}]");
            Conferir("sonda: monitor de câmera do Media Foundation liga", uso.CameraOn);
            uso.Dispose();
        }
        return r.ToString().TrimEnd();
    }
}

public sealed class ConfigPrivacidade
{
    /// <summary>Apps que nunca acendem as bolinhas: nome do .exe (com ou sem .exe), nome que aparece no cartão ou família do pacote.</summary>
    [Label("Apps ignorados", "Ignored apps")]
    [JsonPropertyName("ignorar")] public List<string> Ignorar { get; set; } = new();
}
