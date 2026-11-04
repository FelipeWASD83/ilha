using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Ilha.Configuracoes;

/// <summary>
/// The third party images (headphone photos, Claude/GPT logos and animation strips) the public release leaves out.
/// They live in the private repository FelipeWASD83/ilha-imagens and come down through this PC's GitHub CLI (`gh release
/// download` with its own login): no token in the island, so only an account with access to that repository gets them.
/// </summary>
internal static class ImageDownload
{
    public const string Repo = "FelipeWASD83/ilha-imagens";

    /// <summary>True in the release built with `compilar.ps1 -Publico` (no private modules, no images).</summary>
#if PUBLIC_BUILD
    public const bool PublicBuild = true;
#else
    public const bool PublicBuild = false;
#endif

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "imagens");

    /// <summary>Whether the logos and headphone photos are here (one PNG in each is enough to say so).</summary>
    public static bool Present(string? folder = null)
    {
        string f = folder ?? Folder;
        try
        {
            return new[] { "logos", "fones" }.All(p => Directory.Exists(Path.Combine(f, p)) && Directory.EnumerateFiles(Path.Combine(f, p), "*.png").Any());
        }
        catch (Exception) { return false; }
    }

    public sealed record Result(bool Ok, int Files, string Message);

    public const string Tag = "imagens", Asset = "imagens.zip";

    /// <summary>
    /// Downloads imagens.zip from the repository's fixed release "imagens" with `gh release download` (a release file
    /// comes from GitHub's CDN in seconds; the repository's zipball took 107 s for 21 MB on 04/11/2026) and unpacks it
    /// into <see cref="Folder"/>.
    /// </summary>
    public static async Task<Result> Download()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "ilha-imagens-" + Guid.NewGuid().ToString("N"));
        string tmp = Path.Combine(tmpDir, Asset);
        try
        {
            Directory.CreateDirectory(tmpDir);
            var psi = new ProcessStartInfo("gh")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "release", "download", Tag, "--repo", Repo, "--pattern", Asset, "--dir", tmpDir, "--clobber" },
            };
            Process p;
            try { p = Process.Start(psi) ?? throw new InvalidOperationException(); }
            catch (Exception)
            {
                return new(false, 0, Lang.T("Precisa do GitHub CLI (gh) instalado e logado numa conta com acesso ao repositório das imagens.",
                    "Needs the GitHub CLI (gh) installed and signed in to an account with access to the images repository."));
            }
            using (p)
            {
                var err = p.StandardError.ReadToEndAsync();
                var outText = p.StandardOutput.ReadToEndAsync();
                using var limit = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
                try { await p.WaitForExitAsync(limit.Token); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch (Exception) { }
                    return new(false, 0, Lang.T("O download passou de 5 minutos e foi cancelado.", "The download took over 5 minutes and was cancelled."));
                }
                string e = await err + await outText;
                if (p.ExitCode != 0 || !File.Exists(tmp))
                {
                    Log.Escrever("imagens: gh release download falhou: " + e.Trim());
                    return new(false, 0, e.Contains("auth login", StringComparison.OrdinalIgnoreCase)
                        ? Lang.T("O gh não está logado. Rode \"gh auth login\" e tente de novo.", "gh is not signed in. Run \"gh auth login\" and try again.")
                        : e.Contains("404") || e.Contains("Not Found", StringComparison.OrdinalIgnoreCase)
                            ? Lang.T("Esta conta do gh não tem acesso ao repositório das imagens (ele é privado).", "This gh account has no access to the images repository (it is private).")
                            : Lang.T("Não deu para baixar: ", "Could not download: ") + FirstLine(e));
                }
            }
            int n = Unpack(tmp, Folder);
            Log.Escrever($"imagens: {n} arquivos baixados de {Repo}");
            return n > 0
                ? new(true, n, Lang.T($"{n} imagens baixadas.", $"{n} images downloaded."))
                : new(false, 0, Lang.T("O pacote veio sem imagens.", "The package had no images."));
        }
        catch (Exception e)
        {
            Log.Escrever("imagens: " + e.Message);
            return new(false, 0, Lang.T("Não deu para baixar: ", "Could not download: ") + e.Message);
        }
        finally { try { Directory.Delete(tmpDir, true); } catch (Exception) { } }
    }

    private static string FirstLine(string s) => s.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";

    // fones/<name>.png, fones/FONTES.md, logos/<name>.png: nothing else, no subfolders
    private static readonly Regex Allowed = new(@"^(fones/[A-Za-z0-9._ -]+\.(png|md)|logos/[A-Za-z0-9._ -]+\.png)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Unpacks imagens.zip (or GitHub's zipball, every path under one "owner-repo-sha/" folder) into `dest`: only the PNGs of fones/ and
    /// logos/ and fones/FONTES.md, never outside `dest`. Each file goes through a temporary name, so a broken download
    /// does not leave half an image. Returns how many were written.
    /// </summary>
    internal static int Unpack(string zip, string dest)
    {
        int n = 0;
        string root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        using var z = ZipFile.OpenRead(zip);
        foreach (var entry in z.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            string path = entry.FullName.Replace('\\', '/');
            int slash = path.IndexOf('/');
            if (slash < 0) continue;
            // imagens.zip has fones/ and logos/ at its root; GitHub's zipball puts everything under "owner-repo-sha/"
            string head = path[..slash];
            string rel = head is "fones" or "logos" ? path : path[(slash + 1)..];
            if (!Allowed.IsMatch(rel) || rel.Contains("..")) continue;
            string target = Path.GetFullPath(Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string part = target + ".parcial";
            entry.ExtractToFile(part, true);
            File.Move(part, target, true);
            n++;
        }
        return n;
    }
}
