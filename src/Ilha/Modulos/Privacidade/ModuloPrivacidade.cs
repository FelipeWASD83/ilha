using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ilha.Privacidade;

/// <summary>
/// Bolinha verde = câmera em uso, laranja = microfone em uso, como as do iPhone. Detecta pelo ConsentStore do
/// Windows (o mesmo registro que alimenta "apps que usaram recentemente" em Privacidade), avisado pelo próprio
/// registro, sem polling. Entrega o estado ao núcleo (S.Indicadores, que desenha as bolinhas no canto da pílula
/// quando outra atividade está na frente) e mantém a atividade "privacidade" para quando a ilha não tem mais nada.
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

        // nomes das subchaves
        Conferir("caminho de NonPackaged", Consentimento.CaminhoDaSubchave("C:#Program Files#App Teste#app.exe") == @"C:\Program Files\App Teste\app.exe");
        Conferir("nome do .exe sem caminho", Consentimento.NomeDoExe("C:#Program Files (x86)#Steam#jogo#bin#cs2.exe") == "cs2.exe");
        Conferir("nome amigável do .exe", Consentimento.NomeAmigavelExe("C:#Program Files#BraveSoftware#Brave-Browser#Application#brave.exe") == "brave");
        string unreal = Consentimento.NomeAmigavelExe("D:#Jogos#Bodycam#Binaries#Win64#Bodycam-Win64-Shipping.exe");
        Conferir("jogo de Unreal perde o -Win64-Shipping", unreal == "Bodycam", unreal);
        Conferir("exe com # no fim não vira vazio", Consentimento.NomeAmigavelExe("C:#estranho#") == "estranho");
        Conferir("pacote pela família", Consentimento.NomeAmigavelPacote("Microsoft.WindowsCamera_8wekyb3d8bbwe") == "WindowsCamera");
        Conferir("pacote sem editor", Consentimento.NomeAmigavelPacote("Claude_pzs8sxrjxfjjc") == "Claude");
        Conferir("pacote com número no editor", Consentimento.NomeAmigavelPacote("5319275A.WhatsAppDesktop_cv1g1gvanyjgm") == "WhatsAppDesktop");

        // em uso ou não
        Conferir("nunca usado não está em uso", !Consentimento.EmUso(0, 0));
        Conferir("começou e não parou está em uso", Consentimento.EmUso(134368205896275911, 0));
        Conferir("começou e parou não está em uso", !Consentimento.EmUso(134368205896275911, 134368206037152521));
        Conferir("só o fim gravado não está em uso", !Consentimento.EmUso(0, 134368206037152521));
        Conferir("QWORD do registro", Consentimento.Qword(134368205896275911L) == 134368205896275911L);
        Conferir("valor faltando vale 0", Consentimento.Qword(null) == 0 && Consentimento.Qword("x") == 0);
        Conferir("binário de 8 bytes", Consentimento.Qword(BitConverter.GetBytes(42L)) == 42);

        // decisão com valores de mentira
        const string proprio = @"C:\Programas\Ilha\Ilha.exe";
        var entradas = new List<EntradaConsent>
        {
            new("webcam", "Fabricante.AppDeVideo_abc123", false, 300, 0),
            new("webcam", "C:#Programas#Antigo#antigo.exe", true, 100, 200),
            new("microphone", "C:#Programas#Chat#chat.exe", true, 500, 0),
            new("microphone", "C:#Programas#Navegador#navegador.exe", true, 400, 0),
            new("microphone", "C:#Programas#Outro Lugar#navegador.exe", true, 450, 0),
            new("microphone", "C:#Programas#Travado#travado.exe", true, 50, 0),
            new("microphone", "C:#Programas#Ilha#Ilha.exe", true, 10, 0),
            new("microphone", "C:#Jogos#jogo#jogo.exe", true, 20, 0),
            new("microphone", "Fabricante.AppDeVideo_abc123", false, 600, 0),
            new("microphone", "Fabricante.Gravador_abc123", false, 0, 0),
        };
        var rodando = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chat.exe", "navegador.exe", "jogo.exe", "Ilha.exe" };
        string Pacote(string f) => f.StartsWith("Fabricante.AppDeVideo") ? "App de Vídeo" : Consentimento.NomeAmigavelPacote(f);
        var est = Consentimento.Decidir(entradas, proprio, rodando.Contains, new[] { "jogo" }, Pacote);
        Conferir("câmera: só o que não parou", est.AppsDaCamera == "App de Vídeo", est.AppsDaCamera);
        Conferir("microfone: ordem de início, sem repetir, sem a ilha, sem o travado, sem o ignorado",
            est.AppsDoMicrofone == "navegador, chat, App de Vídeo", est.AppsDoMicrofone);
        var semConferir = Consentimento.Decidir(entradas, proprio, null, Array.Empty<string>(), Pacote);
        Conferir("sem lista de processos o travado conta", semConferir.Microfone.Contains("travado") && semConferir.Microfone.Contains("jogo"));
        var ign = Consentimento.Decidir(entradas, proprio, null, new[] { "Fabricante.AppDeVideo", "chat.exe", " ", "NAVEGADOR" }, Pacote);
        Conferir("ignorar por família, por .exe e sem diferenciar maiúscula", ign.AppsDaCamera == "" && ign.AppsDoMicrofone == "jogo, travado", ign.AppsDoMicrofone);
        Conferir("nada em uso", !Consentimento.Decidir(new[] { entradas[1], entradas[^1] }, proprio, null, Array.Empty<string>(), Pacote).Algum);

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

        // sondas de verdade, só leitura
        try
        {
            var todas = DetectorDePrivacidade.LerEntradas();
            int cam = todas.Count(e => e.Capacidade == Consentimento.Camera), mic = todas.Count(e => e.Capacidade == Consentimento.Microfone);
            Conferir("sonda: ConsentStore lido", todas.Count > 0, $"{cam} apps na câmera, {mic} no microfone, {todas.Count(e => e.NaoEmpacotado)} não empacotados");
            var agora = DetectorDePrivacidade.Estado(Array.Empty<string>(), Environment.ProcessPath);
            r.AppendLine($"ok      sonda: em uso agora: câmera [{agora.AppsDaCamera}] microfone [{agora.AppsDoMicrofone}]");
            var pac = todas.FirstOrDefault(e => !e.NaoEmpacotado && e.Subchave.Contains('_'));
            if (pac.Subchave != null)
                r.AppendLine($"ok      sonda: nome de pacote: {Consentimento.NomeAmigavelPacote(pac.Subchave)} -> {DetectorDePrivacidade.NomeDoPacote(pac.Subchave)}");
            int processos = DetectorDePrivacidade.Processos().Count;
            Conferir("sonda: lista de processos (Toolhelp)", processos > 10, $"{processos} nomes");
        }
        catch (Exception e) { Conferir("sonda: ConsentStore", false, e.Message); }

        foreach (var cap in new[] { Consentimento.Camera, Consentimento.Microfone })
        {
            var v = VigiaDeChave.Abrir(Consentimento.Raiz + "\\" + cap, () => { });
            Conferir($"sonda: notificação armada em {cap} (REG_NOTIFY_THREAD_AGNOSTIC)", v is { Valida: true }, v == null ? "chave ausente ou erro" : $"erro {v.UltimoErro}");
            v?.Dispose();
        }

        // o aviso chega e rearma: numa chave de rascunho da própria ilha (criada e apagada aqui), nunca no ConsentStore
        const string pai = @"Software\Ilha", rascunho = pai + @"\autoteste-privacidade";
        bool paiExistia;
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(pai)) paiExistia = k != null;
        try
        {
            using (Microsoft.Win32.Registry.CurrentUser.CreateSubKey(rascunho)) { }
            using var sinais = new System.Threading.SemaphoreSlim(0);
            var vigia = VigiaDeChave.Abrir(rascunho, () => sinais.Release());
            bool primeiro = false, segundo = false;
            if (vigia != null)
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(rascunho, true)) k?.SetValue("LastUsedTimeStart", 1L, Microsoft.Win32.RegistryValueKind.QWord);
                primeiro = sinais.Wait(2000);
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(rascunho, true)) k?.CreateSubKey("sub")?.SetValue("LastUsedTimeStop", 2L, Microsoft.Win32.RegistryValueKind.QWord);
                segundo = sinais.Wait(2000);
                vigia.Dispose();
            }
            Conferir("aviso do registro chega e rearma (chave de rascunho)", primeiro && segundo, $"primeiro {primeiro}, segundo (subchave) {segundo}");
        }
        catch (Exception e) { Conferir("aviso do registro (chave de rascunho)", false, e.Message); }
        finally
        {
            try
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(rascunho, false);
                if (!paiExistia) Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(pai, false);
            }
            catch { }
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
