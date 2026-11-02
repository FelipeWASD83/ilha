using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ilha.Privacidade;

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
/// Lógica pura: do processo em uso (o id que o WASAPI e o monitor de câmera dão, e o .exe dele na lista de
/// processos) ao nome que a ilha mostra, com a lista de ignorados da config.
/// </summary>
internal static partial class Consentimento
{
    /// <summary>"C:\Program Files\App\app.exe" (ou só "app.exe") -> "app.exe".</summary>
    public static string NomeDoExe(string caminho) => Path.GetFileName(caminho.Replace('#', '\\').TrimEnd('\\'));

    private static readonly Regex _sufixoUnreal = new(@"-Win(64|GDK)-(Shipping|Test|Development)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>O .exe sem caminho e sem extensão ("brave"); jogo de Unreal perde o "-Win64-Shipping".</summary>
    public static string NomeAmigavelExe(string caminho)
    {
        string n = NomeDoExe(caminho);
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        n = _sufixoUnreal.Replace(n, "");
        return n.Length > 0 ? n : caminho;
    }

    /// <summary>A config manda ignorar este app? Casa com o nome do .exe (com ou sem .exe) ou com o nome que aparece.</summary>
    public static bool Ignorado(string exe, string nomeAmigavel, IReadOnlyCollection<string> ignorar)
    {
        if (ignorar.Count == 0) return false;
        string arquivo = NomeDoExe(exe);
        string semExtensao = arquivo.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? arquivo[..^4] : arquivo;
        foreach (var i in ignorar)
        {
            var x = i.Trim();
            if (x.Length == 0) continue;
            if (string.Equals(x, nomeAmigavel, StringComparison.OrdinalIgnoreCase) || string.Equals(x, arquivo, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x, semExtensao, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Decide quem aparece em cada capacidade. `exeDe(pid)` dá o .exe pela lista de processos (null = o processo já
    /// morreu: não conta). `Desde` é a ordem em que a ilha viu cada processo começar: quem começou primeiro vem
    /// primeiro, e o mesmo nome (dois processos do mesmo app) aparece uma vez só.
    /// </summary>
    public static EstadoPrivacidade Decidir(IEnumerable<(uint Pid, long Desde)> camera, IEnumerable<(uint Pid, long Desde)> microfone,
        Func<uint, string?> exeDe, IReadOnlyCollection<string> ignorar)
    {
        string[] Nomes(IEnumerable<(uint Pid, long Desde)> l) => l
            .Select(x => (x.Desde, Exe: exeDe(x.Pid)))
            .Where(x => x.Exe != null)
            .Select(x => (x.Desde, x.Exe, Nome: NomeAmigavelExe(x.Exe!)))
            .Where(x => !Ignorado(x.Exe!, x.Nome, ignorar))
            .OrderBy(x => x.Desde).ThenBy(x => x.Nome, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Nome).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new EstadoPrivacidade(Nomes(camera), Nomes(microfone));
    }
}
