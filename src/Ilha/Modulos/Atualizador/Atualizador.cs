using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Ilha.Atualizacoes;

namespace Ilha;

/// <summary>O que o GitHub disse da release mais nova (também é o cache de 6 h em checagem.json).</summary>
internal sealed class InfoDaRelease
{
    [JsonPropertyName("repositorio")] public string Repositorio { get; set; } = "";
    [JsonPropertyName("atual")] public string Atual { get; set; } = "";
    [JsonPropertyName("ultima")] public string Ultima { get; set; } = "";
    [JsonPropertyName("nova")] public bool Nova { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("digest")] public string? Digest { get; set; }
    [JsonPropertyName("tamanho")] public long Tamanho { get; set; }
    [JsonPropertyName("notas")] public string Notas { get; set; } = "";
    [JsonPropertyName("pagina")] public string Pagina { get; set; } = "";
    [JsonPropertyName("checadoEm")] public DateTime ChecadoEm { get; set; }

    /// <summary>A primeira linha útil das notas (sem título, marcador nem negrito), para caber no cartão.</summary>
    public string Resumo()
    {
        foreach (var bruta in Notas.Split('\n'))
        {
            var l = bruta.Trim().TrimStart('-', '*', ' ').Replace("**", "").Replace("`", "").Trim();
            if (l.Length == 0 || bruta.TrimStart().StartsWith('#')) continue;
            if (l is "Novo" or "Mudou" or "Corrigido" or "Saiu" or "New" or "Changed" or "Fixed" or "Removed") continue;
            return l;
        }
        return "";
    }
}

/// <summary>
/// Atualização pelas releases do GitHub, no modelo do clave (servidor/atualizador.py), sem PowerShell:
/// 1. Procurar: releases/latest do repositório (público, sem token), cache de 6 h, asset "Ilha-&lt;versão&gt;.zip".
/// 2. Instalar: baixa numa pasta temporária, confere o SHA-256 com o "digest" do asset, extrai numa pasta de
///    preparação (recusando caminho fora dela), confere a versão, copia o pacote para a pasta "executor" e roda o
///    Ilha.exe NOVO de lá com --instalar-atualizacao. Ele avisa que está vivo, e só aí esta ilha fecha.
/// 3. O executor (TratarArgumentos) espera esta ilha sair, guarda os arquivos gerenciados em "anterior", copia os
///    novos (o que não vem no pacote fica: logos, fotos de fones, qualquer arquivo de fora), abre a versão nova e
///    espera ela gravar "abriu bem" em até 30 s. Sem o sinal, devolve a anterior e abre a versão velha.
/// A config e os dados ficam em ~/.ilha e nunca entram no pacote.
/// </summary>
internal sealed class Atualizador
{
    public const string ARGUMENTO = "--instalar-atualizacao";
    private const int PRAZO_ABRIR_S = 30;

    public static string Pasta => Path.Combine(Config.Pasta, "atualizacao");
    private static string Cache => Path.Combine(Pasta, "checagem.json");
    private static string Preparacao => Path.Combine(Pasta, "preparacao");
    private static string Executor => Path.Combine(Pasta, "executor");
    private static string Anterior => Path.Combine(Pasta, "anterior");
    private static string MarcaVivo => Path.Combine(Pasta, "executor-vivo.txt");
    private static string MarcaEsperando => Path.Combine(Pasta, "esperando.txt");
    private static string MarcaAbriu => Path.Combine(Pasta, "abriu-bem.txt");
    public static string Resultado => Path.Combine(Pasta, "resultado.json");
    private static string LogDaTroca => Path.Combine(Pasta, "atualizacao.log");

    /// <summary>O do módulo ligado (o menu da bandeja usa este).</summary>
    public static Atualizador? Principal { get; internal set; }

    public string Repositorio { get; set; }
    public InfoDaRelease? Ultima { get; private set; }
    public bool Instalando { get; private set; }
    /// <summary>Progresso do download, de 0 a 1 (chega na thread que chamou Instalar).</summary>
    public event Action<double>? Progresso;
    /// <summary>Baixou e conferiu: a partir daqui é a troca (a ilha vai fechar).</summary>
    public event Action? Trocando;

    public Atualizador(string repositorio) => Repositorio = repositorio;

    public static string VersaoAtual => Atualizacao.Versao;

    // ------------------------------------------------------------------ procurar

    private static HttpClient Cliente(TimeSpan prazo)
    {
        var c = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = prazo };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Ilha/{VersaoAtual}");
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    /// <summary>Lê a resposta do releases/latest (lógica pura, o autoteste usa).</summary>
    public static InfoDaRelease Interpretar(string json, string atual, string repositorio)
    {
        var rel = JsonNode.Parse(json) ?? throw new ErroDeAtualizacao(Lang.T("Resposta do GitHub vazia.", "Empty response from GitHub."));
        var ultima = Versoes.Normalizar(rel["tag_name"]?.GetValue<string>());
        if (ultima.Length == 0) throw new ErroDeAtualizacao(Lang.T("A release mais nova não tem versão (tag).", "The latest release has no version (tag)."));
        var nome = Pacote.NomeDoAsset(ultima);
        var asset = rel["assets"]?.AsArray().FirstOrDefault(a => string.Equals(a?["name"]?.GetValue<string>(), nome, StringComparison.OrdinalIgnoreCase));
        var info = new InfoDaRelease
        {
            Repositorio = repositorio, Atual = atual, Ultima = ultima, Nova = Versoes.MaisNova(ultima, atual),
            Notas = rel["body"]?.GetValue<string>() ?? "", Pagina = rel["html_url"]?.GetValue<string>() ?? "",
            ChecadoEm = DateTime.UtcNow,
        };
        if (asset == null)
        {
            if (info.Nova) throw new ErroDeAtualizacao(Lang.T($"A release {ultima} não tem o pacote {nome}.", $"Release {ultima} doesn't have the {nome} package."));
            return info;
        }
        info.Url = asset["browser_download_url"]?.GetValue<string>() ?? "";
        info.Digest = asset["digest"]?.GetValue<string>();
        info.Tamanho = asset["size"]?.GetValue<long>() ?? 0;
        return info;
    }

    private static string RepositorioValido(string repo)
    {
        repo = (repo ?? "").Trim().Trim('/');
        var partes = repo.Split('/');
        bool ok = partes.Length == 2 && partes.All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
        if (!ok) throw new ErroDeAtualizacao(Lang.T($"Repositório inválido na configuração (\"{repo}\"); o formato é dono/nome.", $"Invalid repository in settings (\"{repo}\"); the format is owner/name."));
        return repo;
    }

    /// <summary>
    /// Pergunta ao GitHub qual é a versão mais nova. Devolve ela se for mais nova que a desta ilha, null se já está
    /// na mais nova. Lança ErroDeAtualizacao (sem conexão, repositório errado, release sem pacote). Usa o cache de 6 h
    /// a não ser que `forcar`.
    /// </summary>
    public async Task<string?> Procurar(bool forcar = false)
    {
        var repo = RepositorioValido(Repositorio);
        var atual = VersaoAtual;
        if (!forcar && LerCache() is { } c && c.Repositorio == repo && c.Atual == atual && DateTime.UtcNow - c.ChecadoEm < TimeSpan.FromHours(6))
        {
            Ultima = c;
            return c.Nova ? c.Ultima : null;
        }
        var json = await ConsultarGitHub(repo);
        var info = Interpretar(json, atual, repo);
        Ultima = info;
        GravarCache(info);
        return info.Nova ? info.Ultima : null;
    }

    /// <summary>O JSON do releases/latest do repositório (só lê, não grava nada).</summary>
    public static async Task<string> ConsultarGitHub(string repositorio)
    {
        var repo = RepositorioValido(repositorio);
        try
        {
            using var http = Cliente(TimeSpan.FromSeconds(20));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest");
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new ErroDeAtualizacao(Lang.T($"O repositório {repo} ainda não tem nenhuma versão publicada (ou não existe).", $"The {repo} repository has no published version yet (or doesn't exist)."));
            if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429)
                throw new ErroDeAtualizacao(Lang.T("O GitHub limitou as consultas por agora. Tente de novo mais tarde.", "GitHub is rate limiting requests right now. Try again later."));
            if (!resp.IsSuccessStatusCode) throw new ErroDeAtualizacao(Lang.T($"O GitHub respondeu {(int)resp.StatusCode}.", $"GitHub responded {(int)resp.StatusCode}."));
            return await resp.Content.ReadAsStringAsync();
        }
        catch (ErroDeAtualizacao) { throw; }
        catch (Exception e) { throw new ErroDeAtualizacao(Lang.T($"Sem conexão com o GitHub ({e.Message}).", $"No connection to GitHub ({e.Message}).")); }
    }

    private static InfoDaRelease? LerCache()
    {
        try { return JsonSerializer.Deserialize<InfoDaRelease>(File.ReadAllText(Cache)); }
        catch { return null; }
    }

    private static void GravarCache(InfoDaRelease i)
    {
        try { GravarAtomico(Cache, JsonSerializer.Serialize(i, Config.Json)); }
        catch (Exception e) { Log.Escrever("atualizador: não gravou o cache: " + e.Message); }
    }

    private static void GravarAtomico(string arquivo, string texto)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(arquivo)!);
        var tmp = arquivo + ".tmp";
        File.WriteAllText(tmp, texto, new UTF8Encoding(false));
        File.Move(tmp, arquivo, true);
    }

    // ------------------------------------------------------------------ instalar

    /// <summary>
    /// Baixa, confere, prepara e entrega a troca ao executor; quando ele confirma que está vivo, pede para esta ilha
    /// fechar. Lança ErroDeAtualizacao com a explicação se algo der errado (aí nada foi trocado).
    /// </summary>
    public async Task Instalar()
    {
        if (Instalando) throw new ErroDeAtualizacao(Lang.T("Já tem uma atualização em andamento.", "An update is already in progress."));
        if (!Inicio.Instalada)
            throw new ErroDeAtualizacao(Lang.T($"Só a ilha instalada ({Inicio.PastaInstalada}) se atualiza sozinha. Esta é uma cópia de desenvolvimento: atualize pelo git e rode o ferramentas\\instalar.ps1.",
                $"Only the installed island ({Inicio.PastaInstalada}) updates itself. This is a development copy: update through git and run ferramentas\\instalar.ps1."));
        Instalando = true;
        string? temp = null;
        try
        {
            var versao = await Procurar(forcar: true) ?? throw new ErroDeAtualizacao(Lang.T($"A ilha já está na versão mais nova ({VersaoAtual}).", $"The island is already on the latest version ({VersaoAtual})."));
            var info = Ultima!;
            if (string.IsNullOrEmpty(info.Url)) throw new ErroDeAtualizacao(Lang.T($"A release {versao} não tem o pacote para baixar.", $"Release {versao} has no package to download."));
            if (!info.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new ErroDeAtualizacao(Lang.T("O endereço do pacote não é https.", "The package address is not https."));

            temp = Path.Combine(Path.GetTempPath(), "ilha-atualizacao-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(temp);
            var zip = Path.Combine(temp, Pacote.NomeDoAsset(versao));
            await Baixar(info.Url, zip, info.Tamanho);

            string raiz = await Task.Run(() =>
            {
                Pacote.ConferirDigest(zip, info.Digest);
                LimparPasta(Preparacao);
                Pacote.Extrair(zip, Preparacao);
                var r = Pacote.Raiz(Preparacao);
                Pacote.Conferir(r, versao);
                Troca.CopiarPasta(r, Executor);
                return r;
            });
            Trocando?.Invoke();
            await EntregarAoExecutor(raiz, versao);
        }
        catch (ErroDeAtualizacao) { throw; }
        catch (Exception e) { throw new ErroDeAtualizacao(Lang.T($"A atualização falhou: {e.Message}", $"The update failed: {e.Message}")); }
        finally
        {
            Instalando = false;
            if (temp != null) try { Directory.Delete(temp, true); } catch { }
        }
    }

    private async Task Baixar(string url, string destino, long tamanho)
    {
        using var http = Cliente(TimeSpan.FromMinutes(10));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) throw new ErroDeAtualizacao(Lang.T($"O download do pacote falhou (o GitHub respondeu {(int)resp.StatusCode}).", $"The package download failed (GitHub responded {(int)resp.StatusCode})."));
        long total = resp.Content.Headers.ContentLength ?? tamanho;
        await using var entrada = await resp.Content.ReadAsStreamAsync();
        await using var saida = File.Create(destino);
        var buf = new byte[81920];
        long lido = 0;
        int ultimo = -1;
        int n;
        while ((n = await entrada.ReadAsync(buf)) > 0)
        {
            await saida.WriteAsync(buf.AsMemory(0, n));
            lido += n;
            if (total > 0)
            {
                int pct = (int)(lido * 100 / total);
                if (pct != ultimo) { ultimo = pct; Progresso?.Invoke(Math.Clamp(lido / (double)total, 0, 1)); }
            }
        }
        if (tamanho > 0 && lido != tamanho) throw new ErroDeAtualizacao(Lang.T("O download veio incompleto. Tente de novo.", "The download came in incomplete. Try again."));
    }

    private async Task EntregarAoExecutor(string preparacao, string versao)
    {
        foreach (var m in new[] { MarcaVivo, MarcaAbriu, MarcaEsperando, Resultado })
            try { File.Delete(m); } catch { }
        var exe = Path.Combine(Executor, "Ilha.exe");
        var psi = new ProcessStartInfo(exe)
        {
            Arguments = Argumentos.Juntar(ARGUMENTO, Inicio.PastaInstalada.TrimEnd('\\'), Environment.ProcessId.ToString(), preparacao.TrimEnd('\\')),
            UseShellExecute = true,
            WorkingDirectory = Executor,
        };
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception e) { throw new ErroDeAtualizacao(Lang.T($"A versão {versao} não abriu ({e.Message}). O Smart App Control pode ter barrado; a ilha continua na {VersaoAtual}.",
            $"Version {versao} didn't open ({e.Message}). Smart App Control may have blocked it; the island stays on {VersaoAtual}.")); }
        if (p == null) throw new ErroDeAtualizacao(Lang.T($"A versão {versao} não abriu.", $"Version {versao} didn't open."));
        using (p)
        {
            var prazo = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < prazo)
            {
                if (File.Exists(MarcaVivo)) break;
                if (p.HasExited)
                    throw new ErroDeAtualizacao(Lang.T($"A versão {versao} fechou na hora (código {p.ExitCode}). O Smart App Control pode ter barrado; a ilha continua na {VersaoAtual}.",
                        $"Version {versao} closed right away (code {p.ExitCode}). Smart App Control may have blocked it; the island stays on {VersaoAtual}."));
                await Task.Delay(200);
            }
            if (!File.Exists(MarcaVivo))
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
                throw new ErroDeAtualizacao(Lang.T($"A versão {versao} não respondeu. A ilha continua na {VersaoAtual}.", $"Version {versao} didn't respond. The island stays on {VersaoAtual}."));
            }
        }
        Log.Escrever($"atualizador: o executor da {versao} está vivo; fechando a {VersaoAtual} para a troca");
        // o mesmo pedido do "Ilha.exe --sair": o Programa fecha o núcleo direito
        try { using var ev = EventWaitHandle.OpenExisting(@"Local\Ilha.Sair"); ev.Set(); }
        catch { System.Windows.Forms.Application.Exit(); }
    }

    private static void LimparPasta(string pasta)
    {
        if (Directory.Exists(pasta)) Troca.ComRepeticao(() => Directory.Delete(pasta, true), 8);
    }

    /// <summary>Apaga a preparação e o executor da última atualização (se o executor já fechou).</summary>
    public static void LimparSobras()
    {
        foreach (var d in new[] { Preparacao, Executor })
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        try { File.Delete(MarcaVivo); } catch { }
    }

    // ------------------------------------------------------------------ abriu bem

    /// <summary>
    /// A ilha abriu de verdade: se um executor está esperando a versão nova, grava o sinal para ele não voltar a
    /// anterior. Chamar depois que o núcleo montou tudo (Programa.cs, logo antes do Application.Run).
    /// </summary>
    public static void AbriuBem()
    {
        try
        {
            if (!File.Exists(MarcaEsperando)) return;
            GravarAtomico(MarcaAbriu, $"{VersaoAtual} {Environment.ProcessId}");
        }
        catch (Exception e) { Log.Escrever("atualizador: não gravou o sinal de abriu bem: " + e.Message); }
    }

    // ------------------------------------------------------------------ o executor (processo novo)

    /// <summary>
    /// `Ilha.exe --instalar-atualizacao &lt;destino&gt; &lt;pid&gt; &lt;preparacao&gt;`: faz a troca e sai. Chamar no começo do
    /// Main, antes de tudo (antes do mutex da instância única). Devolve true se o argumento era este.
    /// </summary>
    public static bool TratarArgumentos(string[] args)
    {
        int i = Array.IndexOf(args, ARGUMENTO);
        if (i < 0) return false;
        // the executor runs before the core: the reasons it writes for the new island follow the configured language
        try { Lang.Set(Config.TentarLer(out _)?.Language); } catch { }
        if (i + 3 >= args.Length || !int.TryParse(args[i + 2], out int pid))
        {
            Anotar("argumentos incompletos; nada feito");
            return true;
        }
        try { Executar(args[i + 1], pid, args[i + 3]); }
        catch (Exception e) { Anotar("erro inesperado: " + e); }
        return true;
    }

    private static void Anotar(string texto)
    {
        try
        {
            Directory.CreateDirectory(Pasta);
            File.AppendAllText(LogDaTroca, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {texto}{Environment.NewLine}");
        }
        catch { }
    }

    private static void GravarResultado(bool ok, string versao, string de, string motivo)
    {
        var j = new JsonObject { ["ok"] = ok, ["versao"] = versao, ["de"] = de, ["motivo"] = motivo, ["em"] = DateTime.Now.ToString("s") };
        try { GravarAtomico(Resultado, j.ToJsonString(Config.Json)); } catch { }
    }

    private static void Executar(string destino, int pid, string preparacao)
    {
        using var trava = new Mutex(true, @"Local\Ilha.Atualizando", out bool primeiro);
        if (!primeiro) { Anotar("outra troca já está rodando; nada feito"); return; }
        string versao = VersaoAtual;
        Anotar($"executor {versao} começou: destino {destino}, ilha velha pid {pid}");
        GravarAtomico(MarcaVivo, Environment.ProcessId.ToString());

        // só troca a pasta instalada, nunca outra que alguém passe na linha de comando
        if (!string.Equals(Path.GetFullPath(destino).TrimEnd('\\'), Path.GetFullPath(Inicio.PastaInstalada).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(destino, "Ilha.exe")))
        {
            Anotar("o destino não é a ilha instalada; nada feito");
            GravarResultado(false, versao, "", Lang.T("o destino não é a ilha instalada", "the destination is not the installed island"));
            return;
        }

        try
        {
            using var velha = Process.GetProcessById(pid);
            if (!velha.WaitForExit(30_000))
            {
                Anotar("a ilha velha não fechou em 30 s; encerrando");
                velha.Kill();
                velha.WaitForExit(5_000);
            }
        }
        catch (ArgumentException) { } // já tinha saído
        catch (Exception e)
        {
            Anotar("não deu para esperar a ilha velha: " + e.Message);
            GravarResultado(false, versao, "", Lang.T("a ilha velha não fechou", "the old island didn't close"));
            AbrirIlha(destino);
            return;
        }

        string de = Pacote.LerManifesto(destino)?.Versao is { Length: > 0 } vm ? vm : Pacote.VersaoDoDll(destino) ?? "?";
        PlanoDeTroca plano;
        try
        {
            var novo = Pacote.Conferir(preparacao, versao);
            plano = Troca.Planejar(destino, novo.Arquivos, Pacote.LerManifesto(destino)?.Arquivos);
            Troca.Aplicar(plano, destino, preparacao, Anterior);
            Anotar($"arquivos trocados: {plano.Guardar.Count} guardados, {plano.Copiar.Count} copiados");
        }
        catch (Exception e)
        {
            Anotar("a troca falhou: " + e.Message);
            GravarResultado(false, versao, de, e.Message);
            AbrirIlha(destino);
            return;
        }

        try { File.Delete(MarcaAbriu); } catch { }
        GravarAtomico(MarcaEsperando, versao);
        bool abriu = false;
        string motivo = Lang.T($"a versão {versao} não deu sinal em {PRAZO_ABRIR_S} s", $"version {versao} gave no signal in {PRAZO_ABRIR_S} s");
        Process? nova = null;
        try
        {
            nova = AbrirIlha(destino);
            var prazo = DateTime.UtcNow.AddSeconds(PRAZO_ABRIR_S);
            while (DateTime.UtcNow < prazo)
            {
                if (SinalDe(versao)) { abriu = true; break; }
                if (nova == null || nova.HasExited) { motivo = Lang.T($"a versão {versao} fechou ao abrir", $"version {versao} closed on opening") + (nova != null ? Lang.T($" (código {nova.ExitCode})", $" (code {nova.ExitCode})") : ""); break; }
                Thread.Sleep(250);
            }
            if (!abriu && SinalDe(versao)) abriu = true;
        }
        catch (Exception e) { motivo = Lang.T($"a versão {versao} não abriu ({e.Message})", $"version {versao} didn't open ({e.Message})"); }
        try { File.Delete(MarcaEsperando); } catch { }

        if (abriu)
        {
            Anotar($"a versão {versao} abriu bem");
            GravarResultado(true, versao, de, "");
            nova?.Dispose();
            return;
        }

        Anotar(motivo + "; voltando para a anterior");
        try
        {
            if (nova != null && !nova.HasExited) { nova.Kill(); nova.WaitForExit(5_000); }
        }
        catch { }
        nova?.Dispose();
        try
        {
            Troca.Reverter(plano, destino, Anterior);
            Anotar($"a versão {de} voltou");
        }
        catch (Exception e)
        {
            Anotar("a volta falhou: " + e.Message);
            motivo += Lang.T($"; a volta falhou ({e.Message}), a versão anterior está em {Anterior}", $"; rolling back failed ({e.Message}), the previous version is in {Anterior}");
        }
        GravarResultado(false, versao, de, motivo);
        AbrirIlha(destino);
    }

    private static bool SinalDe(string versao)
    {
        try { return File.Exists(MarcaAbriu) && Versoes.Iguais(File.ReadAllText(MarcaAbriu).Split(' ')[0], versao); }
        catch { return false; }
    }

    private static Process? AbrirIlha(string destino)
    {
        try
        {
            return Process.Start(new ProcessStartInfo(Path.Combine(destino, "Ilha.exe")) { UseShellExecute = true, WorkingDirectory = destino });
        }
        catch (Exception e)
        {
            Anotar("não abriu a ilha: " + e.Message);
            return null;
        }
    }
}

/// <summary>Monta a linha de comando do Windows (as regras do CommandLineToArgvW: aspas e barras antes delas).</summary>
internal static class Argumentos
{
    public static string Juntar(params string[] args) => string.Join(" ", args.Select(Citar));

    public static string Citar(string a)
    {
        if (a.Length > 0 && !a.Any(c => c is ' ' or '\t' or '\n' or '\v' or '"')) return a;
        var sb = new StringBuilder("\"");
        int barras = 0;
        foreach (var c in a)
        {
            if (c == '\\') { barras++; continue; }
            if (c == '"') { sb.Append('\\', barras * 2 + 1).Append('"'); barras = 0; continue; }
            sb.Append('\\', barras).Append(c);
            barras = 0;
        }
        sb.Append('\\', barras * 2).Append('"');
        return sb.ToString();
    }
}
