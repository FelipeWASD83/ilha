using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ilha.Atualizacoes;

/// <summary>Erro com a explicação pronta para mostrar (em português, sem detalhe técnico desnecessário).</summary>
internal sealed class ErroDeAtualizacao : Exception
{
    public ErroDeAtualizacao(string mensagem) : base(mensagem) { }
}

/// <summary>Versões X.Y.Z: "v0.3.0", "0.3" e "0.3.0-beta" viram (0, 3, 0).</summary>
internal static class Versoes
{
    public static string Normalizar(string? tag) => (tag ?? "").Trim().TrimStart('v', 'V');

    public static int[] Partes(string? v)
    {
        var p = Normalizar(v).Split('.')
            .Select(x => int.TryParse(new string(x.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0)
            .ToList();
        while (p.Count < 3) p.Add(0);
        return p.Take(3).ToArray();
    }

    public static int Comparar(string? a, string? b)
    {
        var x = Partes(a);
        var y = Partes(b);
        for (int i = 0; i < 3; i++)
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return 0;
    }

    public static bool MaisNova(string? a, string? b) => Comparar(a, b) > 0;
    public static bool Iguais(string? a, string? b) => Comparar(a, b) == 0;
}

/// <summary>
/// O pacote.json que vai dentro do zip da release: a versão e a lista de todos os arquivos do pacote (caminhos
/// relativos com "/"). É a lista dos arquivos "gerenciados": só eles são trocados; o resto da pasta instalada
/// (logos, fotos de fones, qualquer coisa que a pessoa pôs lá) fica onde está.
/// </summary>
internal sealed class ManifestoDoPacote
{
    [JsonPropertyName("versao")] public string Versao { get; set; } = "";
    [JsonPropertyName("arquivos")] public List<string> Arquivos { get; set; } = new();
}

internal static class Pacote
{
    public const string MANIFESTO = "pacote.json";
    // tamanho máximo do pacote extraído: a ilha publicada tem poucos MB; acima disto é zip estragado ou malicioso
    private const long LIMITE_EXTRAIDO = 1L << 30;

    /// <summary>Pastas que nunca são trocadas nem apagadas, mesmo se um pacote vier com elas (marcas de terceiros que ficam só no PC).</summary>
    public static readonly string[] Protegidas = { "imagens/logos/", "imagens/fones/" };

    public static string NomeDoAsset(string versao) => $"Ilha-{Versoes.Normalizar(versao)}.zip";

    public static string Barra(string rel) => rel.Replace('\\', '/');

    public static bool Protegido(string rel)
    {
        var r = Barra(rel);
        return Protegidas.Any(p => r.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Caminho relativo seguro: sem raiz, sem "..", sem ":" (fluxo alternativo do NTFS), sem nome vazio.</summary>
    public static bool RelativoSeguro(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return false;
        var r = Barra(rel);
        if (r.StartsWith('/') || r.Contains(':') || Path.IsPathRooted(rel)) return false;
        var partes = r.Split('/');
        return partes.All(p => p.Length > 0 && p != "." && p != ".." && p.Trim().Length == p.Length && !p.EndsWith('.'));
    }

    public static string Sha256(string arquivo)
    {
        using var f = File.OpenRead(arquivo);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    /// <summary>Confere o arquivo com o "digest" do asset do GitHub ("sha256:&lt;hex&gt;"). Sem digest não instala.</summary>
    public static void ConferirDigest(string arquivo, string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new ErroDeAtualizacao("O GitHub não informou o SHA-256 do pacote; sem conferir, não instalo.");
        var esperado = digest[7..].Trim().ToLowerInvariant();
        if (esperado.Length != 64 || !esperado.All(Uri.IsHexDigit))
            throw new ErroDeAtualizacao("O SHA-256 informado pelo GitHub não está no formato esperado.");
        if (Sha256(arquivo) != esperado)
            throw new ErroDeAtualizacao("O pacote baixado não confere com o SHA-256 do GitHub. Tente de novo.");
    }

    /// <summary>
    /// Extrai o zip na pasta (que é esvaziada antes). Confere todas as entradas antes de escrever qualquer coisa:
    /// uma só apontando para fora da pasta, com raiz ou com ":" recusa o pacote inteiro.
    /// </summary>
    public static void Extrair(string zip, string destino)
    {
        var raiz = Path.GetFullPath(destino).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var z = ZipFile.OpenRead(zip);
        long total = 0;
        var alvos = new List<(ZipArchiveEntry Entrada, string Alvo, bool Pasta)>();
        foreach (var e in z.Entries)
        {
            var nome = e.FullName;
            bool pasta = nome.EndsWith('/') || nome.EndsWith('\\');
            var rel = pasta ? nome.TrimEnd('/', '\\') : nome;
            if (rel.Length == 0) continue;
            if (!RelativoSeguro(rel)) throw new ErroDeAtualizacao($"Pacote com caminho inválido ({nome}).");
            var alvo = Path.GetFullPath(Path.Combine(raiz, rel));
            if (!alvo.StartsWith(raiz, StringComparison.OrdinalIgnoreCase)) throw new ErroDeAtualizacao($"Pacote com caminho para fora da pasta ({nome}).");
            total += e.Length;
            if (total > LIMITE_EXTRAIDO) throw new ErroDeAtualizacao("O pacote extraído passaria de 1 GB; não parece ser da ilha.");
            alvos.Add((e, alvo, pasta));
        }
        if (Directory.Exists(destino)) Directory.Delete(destino, true);
        Directory.CreateDirectory(destino);
        foreach (var (e, alvo, pasta) in alvos)
        {
            if (pasta) { Directory.CreateDirectory(alvo); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(alvo)!);
            e.ExtractToFile(alvo, false);
        }
    }

    /// <summary>A pasta do pacote: a própria pasta extraída ou a única subpasta dela (zip feito com a pasta dentro).</summary>
    public static string Raiz(string pasta)
    {
        bool Eh(string d) => File.Exists(Path.Combine(d, "Ilha.exe")) && File.Exists(Path.Combine(d, MANIFESTO));
        if (Eh(pasta)) return pasta;
        var subs = Directory.GetDirectories(pasta);
        if (subs.Length == 1 && Directory.GetFiles(pasta).Length == 0 && Eh(subs[0])) return subs[0];
        throw new ErroDeAtualizacao("O pacote baixado não parece ser da ilha (falta o Ilha.exe ou o pacote.json).");
    }

    public static ManifestoDoPacote? LerManifesto(string pasta)
    {
        var arq = Path.Combine(pasta, MANIFESTO);
        if (!File.Exists(arq)) return null;
        try { return JsonSerializer.Deserialize<ManifestoDoPacote>(File.ReadAllText(arq)); }
        catch { return null; }
    }

    /// <summary>Todos os arquivos da pasta, relativos e com "/".</summary>
    public static List<string> Arquivos(string pasta)
    {
        var raiz = Path.GetFullPath(pasta).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(pasta, "*", SearchOption.AllDirectories)
            .Select(f => Barra(Path.GetFullPath(f)[raiz.Length..]))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Versão do Ilha.dll lida dos metadados (sem carregar o código).</summary>
    public static string? VersaoDoDll(string pasta)
    {
        try
        {
            var v = AssemblyName.GetAssemblyName(Path.Combine(pasta, "Ilha.dll")).Version;
            return v == null ? null : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { return null; }
    }

    /// <summary>
    /// Confere o pacote preparado: o pacote.json diz a versão da release, lista exatamente os arquivos que vieram,
    /// todos com caminho seguro, e o Ilha.dll é dessa versão. Devolve o manifesto.
    /// </summary>
    public static ManifestoDoPacote Conferir(string raiz, string versaoDaRelease)
    {
        var m = LerManifesto(raiz) ?? throw new ErroDeAtualizacao("O pacote.json do pacote está faltando ou ilegível.");
        if (!Versoes.Iguais(m.Versao, versaoDaRelease))
            throw new ErroDeAtualizacao($"O pacote diz versão {m.Versao}, mas a release é {versaoDaRelease}.");
        var dll = VersaoDoDll(raiz);
        if (dll == null || !Versoes.Iguais(dll, versaoDaRelease))
            throw new ErroDeAtualizacao($"O Ilha.dll do pacote é da versão {dll ?? "?"}, mas a release é {versaoDaRelease}.");
        if (m.Arquivos.Any(a => !RelativoSeguro(a)))
            throw new ErroDeAtualizacao("O pacote.json lista um caminho inválido.");
        var listados = new HashSet<string>(m.Arquivos.Select(Barra), StringComparer.OrdinalIgnoreCase);
        var presentes = new HashSet<string>(Arquivos(raiz), StringComparer.OrdinalIgnoreCase);
        if (!listados.SetEquals(presentes))
        {
            var faltam = listados.Except(presentes).Take(3).ToList();
            var sobram = presentes.Except(listados).Take(3).ToList();
            throw new ErroDeAtualizacao("O pacote não bate com a própria lista de arquivos" +
                (faltam.Count > 0 ? $" (faltam: {string.Join(", ", faltam)})" : "") +
                (sobram.Count > 0 ? $" (sobram: {string.Join(", ", sobram)})" : "") + ".");
        }
        if (!listados.Contains("Ilha.exe") || !listados.Contains("Ilha.dll"))
            throw new ErroDeAtualizacao("O pacote não tem o Ilha.exe e o Ilha.dll.");
        return m;
    }
}
