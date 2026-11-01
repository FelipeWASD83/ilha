using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ilha.Privacidade;

/// <summary>Uma subchave do ConsentStore: um app (pacote ou .exe) e quando ele começou e parou de usar a capacidade.</summary>
internal readonly record struct EntradaConsent(string Capacidade, string Subchave, bool NaoEmpacotado, long Inicio, long Fim);

/// <summary>Quem está usando a câmera e o microfone agora, já com os nomes amigáveis, na ordem em que começaram.</summary>
internal sealed class EstadoPrivacidade : IEquatable<EstadoPrivacidade>
{
    public static readonly EstadoPrivacidade Vazio = new(Array.Empty<string>(), Array.Empty<string>());

    public IReadOnlyList<string> Camera { get; }
    public IReadOnlyList<string> Microfone { get; }
    public bool Algum => Camera.Count > 0 || Microfone.Count > 0;

    public EstadoPrivacidade(IReadOnlyList<string> camera, IReadOnlyList<string> microfone)
    {
        Camera = camera;
        Microfone = microfone;
    }

    public string AppsDaCamera => string.Join(", ", Camera);
    public string AppsDoMicrofone => string.Join(", ", Microfone);

    /// <summary>As linhas do cartão: ("Câmera", "App") e ("Microfone", "App1, App2"), só as que estão em uso.</summary>
    public List<(bool Camera, string Rotulo, string Apps)> Linhas()
    {
        var l = new List<(bool, string, string)>();
        if (Camera.Count > 0) l.Add((true, Lang.T("Câmera:", "Camera:"), AppsDaCamera));
        if (Microfone.Count > 0) l.Add((false, Lang.T("Microfone:", "Microphone:"), AppsDoMicrofone));
        return l;
    }

    public bool Equals(EstadoPrivacidade? o) =>
        o != null && Camera.SequenceEqual(o.Camera, StringComparer.Ordinal) && Microfone.SequenceEqual(o.Microfone, StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as EstadoPrivacidade);

    public override int GetHashCode() => HashCode.Combine(AppsDaCamera, AppsDoMicrofone);
}

/// <summary>
/// Lógica pura do ConsentStore (HKCU\...\CapabilityAccessManager\ConsentStore\{webcam,microphone}). Não é API
/// documentada: é o armazenamento do CapabilityAccessManager, o mesmo de "apps que usaram recentemente" em
/// Privacidade. Cada app empacotado é uma subchave com o nome da família do pacote; os outros ficam em
/// NonPackaged\&lt;caminho do .exe com # no lugar de \&gt;. Em uso = LastUsedTimeStart != 0 e LastUsedTimeStop == 0.
/// </summary>
internal static class Consentimento
{
    public const string Raiz = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    public const string Camera = "webcam", Microfone = "microphone", NaoEmpacotados = "NonPackaged";

    public static bool EmUso(long inicio, long fim) => inicio != 0 && fim == 0;

    /// <summary>Valor do registro (REG_QWORD vem como long; aceita também DWORD e binário de 8 bytes). Faltando: 0.</summary>
    public static long Qword(object? v) => v switch
    {
        long l => l,
        int i => (uint)i,
        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
        _ => 0,
    };

    /// <summary>"C:#Program Files#App#app.exe" -> "C:\Program Files\App\app.exe".</summary>
    public static string CaminhoDaSubchave(string subchave) => subchave.Replace('#', '\\');

    public static string NomeDoExe(string subchave) => Path.GetFileName(CaminhoDaSubchave(subchave).TrimEnd('\\'));

    private static readonly Regex _sufixoUnreal = new(@"-Win(64|GDK)-(Shipping|Test|Development)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>O .exe sem caminho e sem extensão ("brave"); jogo de Unreal perde o "-Win64-Shipping".</summary>
    public static string NomeAmigavelExe(string subchave)
    {
        string n = NomeDoExe(subchave);
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        n = _sufixoUnreal.Replace(n, "");
        return n.Length > 0 ? n : subchave;
    }

    /// <summary>Nome do pacote tirado da família, quando o Windows não deu o nome de exibição: "Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "WindowsCamera".</summary>
    public static string NomeAmigavelPacote(string familia)
    {
        int sub = familia.LastIndexOf('_');
        string nome = sub > 0 ? familia[..sub] : familia;
        int ponto = nome.LastIndexOf('.');
        if (ponto >= 0 && ponto < nome.Length - 1) nome = nome[(ponto + 1)..];
        return nome.Length > 0 ? nome : familia;
    }

    /// <summary>A entrada é a da própria ilha (o .exe que está rodando agora)?</summary>
    public static bool EhProprio(EntradaConsent e, string? caminhoProprio) =>
        e.NaoEmpacotado && !string.IsNullOrEmpty(caminhoProprio)
        && string.Equals(CaminhoDaSubchave(e.Subchave), caminhoProprio, StringComparison.OrdinalIgnoreCase);

    /// <summary>A config manda ignorar este app? Casa com o nome do .exe (com ou sem .exe), o nome amigável ou a família do pacote.</summary>
    public static bool Ignorado(EntradaConsent e, string nomeAmigavel, IReadOnlyCollection<string> ignorar)
    {
        if (ignorar.Count == 0) return false;
        foreach (var i in ignorar)
        {
            var x = i.Trim();
            if (x.Length == 0) continue;
            if (string.Equals(x, nomeAmigavel, StringComparison.OrdinalIgnoreCase)) return true;
            if (e.NaoEmpacotado)
            {
                string exe = NomeDoExe(e.Subchave);
                if (string.Equals(x, exe, StringComparison.OrdinalIgnoreCase)) return true;
                if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && string.Equals(x, exe[..^4], StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(x, e.Subchave, StringComparison.OrdinalIgnoreCase)
                     || e.Subchave.StartsWith(x + "_", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Decide quem está usando cada capacidade. `rodando(nomeDoExe)` confere se um .exe não empacotado ainda tem
    /// processo (null = não confere): protege contra uma entrada que ficou "em uso" porque o app morreu sem o
    /// Windows gravar o fim. `nomeDoPacote(família)` dá o nome de exibição do pacote.
    /// </summary>
    public static EstadoPrivacidade Decidir(IEnumerable<EntradaConsent> entradas, string? caminhoProprio, Func<string, bool>? rodando,
        IReadOnlyCollection<string> ignorar, Func<string, string> nomeDoPacote)
    {
        var camera = new List<(long, string)>();
        var mic = new List<(long, string)>();
        foreach (var e in entradas)
        {
            if (!EmUso(e.Inicio, e.Fim)) continue;
            if (EhProprio(e, caminhoProprio)) continue;
            if (e.NaoEmpacotado && rodando != null && !rodando(NomeDoExe(e.Subchave))) continue;
            string nome = e.NaoEmpacotado ? NomeAmigavelExe(e.Subchave) : nomeDoPacote(e.Subchave);
            if (Ignorado(e, nome, ignorar)) continue;
            if (e.Capacidade == Camera) camera.Add((e.Inicio, nome));
            else if (e.Capacidade == Microfone) mic.Add((e.Inicio, nome));
        }
        return new EstadoPrivacidade(Ordenar(camera), Ordenar(mic));
    }

    // quem começou primeiro vem primeiro; o mesmo nome (dois .exe do mesmo app) aparece uma vez só
    private static string[] Ordenar(List<(long Inicio, string Nome)> l) =>
        l.OrderBy(x => x.Inicio).ThenBy(x => x.Nome, StringComparer.OrdinalIgnoreCase)
         .Select(x => x.Nome).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
