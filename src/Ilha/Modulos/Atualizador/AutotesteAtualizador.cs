using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Ilha.Atualizacoes;

internal sealed partial class ModuloAtualizador
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string linha, out int n);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);

    private static string[] Separar(string linha)
    {
        var p = CommandLineToArgvW("Ilha.exe " + linha, out int n);
        try { return Enumerable.Range(1, n - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(p); }
    }

    /// <summary>Retrato de uma pasta: cada arquivo relativo com o SHA-256 (para comparar antes e depois).</summary>
    private static string Retrato(string pasta) =>
        string.Join("\n", Pacote.Arquivos(pasta).Select(r => r + "=" + Pacote.Sha256(Path.Combine(pasta, r))));

    private static void Escrever(string pasta, string rel, string texto)
    {
        var a = Path.Combine(pasta, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(a)!);
        File.WriteAllText(a, texto);
    }

    private static void ZipCom(string zip, params (string Nome, string Texto)[] entradas)
    {
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (nome, texto) in entradas)
        {
            using var s = z.CreateEntry(nome).Open();
            var b = Encoding.UTF8.GetBytes(texto);
            s.Write(b, 0, b.Length);
        }
    }

    private static void Manifesto(string pasta, string versao)
    {
        var arquivos = Pacote.Arquivos(pasta).Where(a => a != Pacote.MANIFESTO).Append(Pacote.MANIFESTO).ToList();
        File.WriteAllText(Path.Combine(pasta, Pacote.MANIFESTO), JsonSerializer.Serialize(new ManifestoDoPacote { Versao = versao, Arquivos = arquivos }));
    }

    public override string Autoteste()
    {
        // the refusal checks look for Portuguese words; English has its own cases at the end
        string idioma = Lang.Choice;
        Lang.Set("pt");
        try { return AutotestePt() + "\n" + AutotesteIngles(); }
        finally { Lang.Set(idioma); }
    }

    private static string AutotesteIngles()
    {
        var r = new StringBuilder();
        void Conferir(string nome, bool ok) => r.AppendLine((ok ? "ok      " : "FALHOU  ") + "english: " + nome);
        Lang.Set("en");
        try { Atualizador.Interpretar("""{"tag_name":"v9.0.0","assets":[]}""", "0.2.0", "x/y"); Conferir("release without the package is refused", false); }
        catch (ErroDeAtualizacao e) { Conferir("release without the package is refused in English", e.Message == "Release 9.0.0 doesn't have the Ilha-9.0.0.zip package.") ; }
        var info = Atualizador.Interpretar("""{"tag_name":"v9.0.0","body":"## 9.0.0\n\n**New**\n- English interface.\n","assets":[{"name":"Ilha-9.0.0.zip","browser_download_url":"https://x/Ilha-9.0.0.zip","size":1}]}""", "0.2.0", "x/y");
        Conferir("English changelog heading is skipped in the summary", info.Resumo() == "English interface.");
        Lang.Set("pt");
        return r.ToString().TrimEnd();
    }

    private static string AutotestePt()
    {
        var r = new StringBuilder();
        void Conferir(string nome, bool ok, string detalhe = "") =>
            r.AppendLine((ok ? "ok      " : "FALHOU  ") + nome + (detalhe.Length > 0 ? $" ({detalhe})" : ""));
        bool Recusa(Action a, string contem = "")
        {
            try { a(); return false; }
            catch (ErroDeAtualizacao e) { return contem.Length == 0 || e.Message.Contains(contem); }
        }

        // versões
        Conferir("0.3.0 > 0.2.9", Versoes.MaisNova("0.3.0", "0.2.9"));
        Conferir("v1.10.0 > 1.9.9 (número, não texto)", Versoes.MaisNova("v1.10.0", "1.9.9"));
        Conferir("0.2 = 0.2.0", Versoes.Iguais("0.2", "0.2.0"));
        Conferir("0.3.0-beta = 0.3.0", Versoes.Iguais("0.3.0-beta", "0.3.0"));
        Conferir("0.2.0 não é mais nova que 0.2.0", !Versoes.MaisNova("0.2.0", "0.2.0"));
        Conferir("2.0.0 > 1.99.99", Versoes.MaisNova("2.0.0", "1.99.99"));

        // agenda da procura automática
        var agora = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        Conferir("agenda: nunca procurou espera 3 min", Espera(null, agora) == TimeSpan.FromMinutes(3));
        Conferir("agenda: procurou há 1 h espera 23 h", Espera(agora.AddHours(-1), agora) == TimeSpan.FromHours(23));
        Conferir("agenda: atrasada espera só 3 min", Espera(agora.AddDays(-3), agora) == TimeSpan.FromMinutes(3));
        var depoisDaFalha = Espera(UltimaDepoisDeFalha(agora), agora);
        Conferir("agenda: depois de falhar tenta em 6 h, não em 3 min", depoisDaFalha == TimeSpan.FromHours(6), depoisDaFalha.ToString());

        // resposta do GitHub
        const string json = """
            {"tag_name":"v0.3.0","html_url":"https://github.com/x/y/releases/tag/v0.3.0",
             "body":"## 0.3.0 (02/11/2026)\n\n**Novo**\n- Atualizador dentro da ilha.\n",
             "assets":[{"name":"outro.zip","browser_download_url":"https://x/outro.zip","size":1,"digest":"sha256:00"},
                       {"name":"Ilha-0.3.0.zip","browser_download_url":"https://github.com/x/y/releases/download/v0.3.0/Ilha-0.3.0.zip","size":1234,"digest":"sha256:abc"}]}
            """;
        var info = Atualizador.Interpretar(json, "0.2.0", "x/y");
        Conferir("release: versão e 'nova'", info.Ultima == "0.3.0" && info.Nova, info.Ultima);
        Conferir("release: pega o asset Ilha-<versão>.zip, não o primeiro .zip", info.Url.EndsWith("/Ilha-0.3.0.zip") && info.Tamanho == 1234 && info.Digest == "sha256:abc", info.Url);
        Conferir("release: resumo das notas sem título nem marcador", info.Resumo() == "Atualizador dentro da ilha.", info.Resumo());
        Conferir("release igual à atual não é nova", !Atualizador.Interpretar(json, "0.3.0", "x/y").Nova);
        Conferir("release nova sem o pacote é recusada", Recusa(() => Atualizador.Interpretar("""{"tag_name":"v9.0.0","assets":[]}""", "0.2.0", "x/y"), "não tem o pacote"));

        var temp = Path.Combine(Path.GetTempPath(), "ilha-teste-atualizador-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            // SHA-256
            var arq = Path.Combine(temp, "dados.bin");
            File.WriteAllBytes(arq, Encoding.ASCII.GetBytes("abc"));
            var hash = Pacote.Sha256(arq);
            Conferir("SHA-256 do vetor de teste \"abc\"", hash == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
            Conferir("digest certo passa", !Recusa(() => Pacote.ConferirDigest(arq, "sha256:" + hash)));
            Conferir("digest em maiúscula passa", !Recusa(() => Pacote.ConferirDigest(arq, "SHA256:" + hash.ToUpperInvariant())));
            Conferir("digest diferente é recusado", Recusa(() => Pacote.ConferirDigest(arq, "sha256:" + new string('0', 64)), "não confere"));
            Conferir("sem digest é recusado", Recusa(() => Pacote.ConferirDigest(arq, null), "não informou"));
            Conferir("digest de outro algoritmo é recusado", Recusa(() => Pacote.ConferirDigest(arq, "sha1:" + hash[..40]), "não informou"));

            // extração
            var dest = Path.Combine(temp, "extraido");
            foreach (var (nome, ruim) in new[] { ("../fora.txt", "caminho para fora (..)"), ("a/../../fora.txt", ".. no meio"), ("C:/Windows/fora.txt", "caminho com raiz"), ("/fora.txt", "barra no começo"), ("a.txt:fluxo", "fluxo alternativo do NTFS") })
            {
                var z = Path.Combine(temp, "ruim.zip");
                File.Delete(z);
                ZipCom(z, ("bom.txt", "1"), (nome, "x"));
                bool recusou = Recusa(() => Pacote.Extrair(z, dest));
                bool nada = !File.Exists(Path.Combine(dest, "bom.txt")) && !File.Exists(Path.Combine(temp, "fora.txt"));
                Conferir($"extração recusa {ruim}", recusou && nada);
            }
            var bomZip = Path.Combine(temp, "bom.zip");
            ZipCom(bomZip, ("a.txt", "1"), ("sub/b.txt", "2"), ("sub/vazia/", ""));
            Pacote.Extrair(bomZip, dest);
            Conferir("extração de zip bom", File.ReadAllText(Path.Combine(dest, "sub", "b.txt")) == "2" && Directory.Exists(Path.Combine(dest, "sub", "vazia")));

            // pacote de mentira com o Ilha.dll de verdade (a versão vem dele)
            string versao = Atualizador.VersaoAtual;
            var pac = Path.Combine(temp, "pacote");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Ilha.dll"), Path.Combine(Directory.CreateDirectory(pac).FullName, "Ilha.dll"));
            Escrever(pac, "Ilha.exe", "exe novo");
            Escrever(pac, "sons/alerta.wav", "som novo");
            Escrever(pac, "novo.dll", "dll que só existe na nova");
            Manifesto(pac, versao);
            var zipPac = Path.Combine(temp, Pacote.NomeDoAsset(versao));
            ZipFile.CreateFromDirectory(pac, zipPac);
            var prep = Path.Combine(temp, "preparacao");
            Pacote.ConferirDigest(zipPac, "sha256:" + Pacote.Sha256(zipPac));
            Pacote.Extrair(zipPac, prep);
            var raiz = Pacote.Raiz(prep);
            Conferir("pacote: conferido na versão certa", !Recusa(() => Pacote.Conferir(raiz, versao)), versao);
            Conferir("pacote: tag diferente da versão é recusada", Recusa(() => Pacote.Conferir(raiz, "99.0.0"), "release é 99.0.0"));
            Escrever(raiz, "intruso.txt", "não está na lista");
            Conferir("pacote: arquivo fora da lista é recusado", Recusa(() => Pacote.Conferir(raiz, versao), "sobram"));
            File.Delete(Path.Combine(raiz, "intruso.txt"));
            var aninhado = Path.Combine(temp, "aninhado");
            Directory.CreateDirectory(aninhado);
            Troca.CopiarPasta(pac, Path.Combine(aninhado, "Ilha"));
            Conferir("pacote: aceita a pasta dentro do zip", Pacote.Raiz(aninhado) == Path.Combine(aninhado, "Ilha"));

            // troca numa pasta instalada de mentira
            var inst = Path.Combine(temp, "instalada");
            var ant = Path.Combine(temp, "anterior");
            Escrever(inst, "Ilha.exe", "exe velho");
            Escrever(inst, "Ilha.dll", "dll velho");
            Escrever(inst, "sons/alerta.wav", "som velho");
            Escrever(inst, "velho.dll", "só na velha");
            Escrever(inst, "imagens/logos/claude.png", "logo");
            Escrever(inst, "imagens/fones/airpods.png", "foto");
            Escrever(inst, "anotacao-da-pessoa.txt", "meu");
            File.WriteAllText(Path.Combine(inst, Pacote.MANIFESTO), JsonSerializer.Serialize(new ManifestoDoPacote
            {
                Versao = "0.0.1",
                Arquivos = new() { "Ilha.exe", "Ilha.dll", "sons/alerta.wav", "velho.dll", "pacote.json", "imagens/logos/claude.png", "../../fora.txt" },
            }));
            var antes = Retrato(inst);
            var novo = Pacote.Conferir(raiz, versao);
            var plano = Troca.Planejar(inst, novo.Arquivos, Pacote.LerManifesto(inst)?.Arquivos);
            Conferir("plano: guarda os gerenciados velhos e não as logos", plano.Guardar.Contains("velho.dll") && !plano.Guardar.Any(Pacote.Protegido) && !plano.Guardar.Contains("anotacao-da-pessoa.txt"), string.Join(",", plano.Guardar));
            Conferir("plano: caminho estranho da lista velha fica de fora", !plano.Guardar.Any(g => g.Contains("..")));
            Troca.Aplicar(plano, inst, raiz, ant);
            Conferir("troca: exe e som novos", File.ReadAllText(Path.Combine(inst, "Ilha.exe")) == "exe novo" && File.ReadAllText(Path.Combine(inst, "sons", "alerta.wav")) == "som novo");
            Conferir("troca: dll da velha saiu, a nova entrou", !File.Exists(Path.Combine(inst, "velho.dll")) && File.Exists(Path.Combine(inst, "novo.dll")));
            Conferir("troca: logos, fotos de fones e arquivo da pessoa ficaram",
                File.ReadAllText(Path.Combine(inst, "imagens", "logos", "claude.png")) == "logo" && File.Exists(Path.Combine(inst, "imagens", "fones", "airpods.png"))
                && File.ReadAllText(Path.Combine(inst, "anotacao-da-pessoa.txt")) == "meu");
            Conferir("troca: a velha foi guardada em anterior", File.ReadAllText(Path.Combine(ant, "Ilha.exe")) == "exe velho" && File.Exists(Path.Combine(ant, "velho.dll")));
            Troca.Reverter(plano, inst, ant);
            Conferir("volta: a pasta ficou exatamente como antes", Retrato(inst) == antes);

            // falha no meio da troca (arquivo do pacote sumiu da preparação): desfaz sozinho
            var planoRuim = Troca.Planejar(inst, novo.Arquivos.Append("sumiu.dll"), Pacote.LerManifesto(inst)?.Arquivos);
            bool desfez = Recusa(() => Troca.Aplicar(planoRuim, inst, raiz, ant), "restaurada");
            Conferir("falha no meio: lança e restaura a velha", desfez && Retrato(inst) == antes);

            // pacote que traz logo: não sobrescreve a que já está
            Escrever(raiz, "imagens/logos/claude.png", "logo do pacote");
            var planoLogo = Troca.Planejar(inst, new[] { "imagens/logos/claude.png", "imagens/logos/gpt.png" }, null);
            Conferir("pasta protegida: só completa o que falta", planoLogo.Copiar.SequenceEqual(new[] { "imagens/logos/gpt.png" }) && planoLogo.Guardar.Count == 0, string.Join(",", planoLogo.Copiar));
        }
        catch (Exception e) { Conferir("testes com arquivos", false, e.Message); }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }

        // linha de comando do executor
        foreach (var caso in new[] { @"C:\Users\Fulano de Tal\AppData\Local\Programs\Ilha", @"C:\pasta com barra\", "sem-espaco", "aspas \"no\" meio", "" })
        {
            var volta = Separar(Argumentos.Juntar(Atualizador.ARGUMENTO, caso, "123"));
            Conferir($"argumento volta igual: [{caso}]", volta.Length == 3 && volta[1] == caso && volta[2] == "123", string.Join(" | ", volta));
        }
        Conferir("TratarArgumentos ignora o que não é dele", !Atualizador.TratarArgumentos(new[] { "--demo", "timer" }));

        // pacote de verdade (opcional): ILHA_TESTE_PACOTE=<zip gerado pelo publicar-versao.ps1 -SoGerar>
        var real = Environment.GetEnvironmentVariable("ILHA_TESTE_PACOTE");
        if (!string.IsNullOrEmpty(real) && File.Exists(real))
        {
            var t = Path.Combine(Path.GetTempPath(), "ilha-teste-pacote-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var v = Versoes.Normalizar(Path.GetFileNameWithoutExtension(real).Replace("Ilha-", ""));
                Pacote.ConferirDigest(real, "sha256:" + Pacote.Sha256(real));
                Pacote.Extrair(real, t);
                var m = Pacote.Conferir(Pacote.Raiz(t), v);
                bool semMarcas = !m.Arquivos.Any(a => a.StartsWith("imagens/logos/", StringComparison.OrdinalIgnoreCase) || a.StartsWith("imagens/fones/", StringComparison.OrdinalIgnoreCase));
                bool semCarimbo = !m.Arquivos.Any(a => a.Contains("Carimbo", StringComparison.OrdinalIgnoreCase));
                Conferir($"pacote real {Path.GetFileName(real)}: confere, {m.Arquivos.Count} arquivos", true);
                Conferir("pacote real: sem logos, fotos de fones e carimbo", semMarcas && semCarimbo);
            }
            catch (Exception e) { Conferir("pacote real", false, e.Message); }
            finally { try { Directory.Delete(t, true); } catch { } }
        }

        // sonda só leitura: o que o GitHub responde hoje (informativo, não conta como falha)
        try
        {
            var achou = Task.Run(async () =>
            {
                try
                {
                    // sem gravar o cache: a sonda só lê
                    var i = Atualizador.Interpretar(await Atualizador.ConsultarGitHub(new ConfigAtualizador().Repositorio), Atualizador.VersaoAtual, "");
                    return $"última {i.Ultima}, {(i.Nova ? "mais nova que esta" : "esta já é a mais nova")}, digest {(i.Digest != null ? "presente" : "ausente")}";
                }
                catch (ErroDeAtualizacao e) { return e.Message; }
            }).GetAwaiter().GetResult();
            r.AppendLine("info    GitHub agora: " + achou);
        }
        catch (Exception e) { r.AppendLine("info    GitHub agora: " + e.Message); }
        return r.ToString().TrimEnd();
    }
}
