using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Ilha.Nota;

/// <summary>
/// A nota de caixa de entrada: cada anotação vira um item de lista no fim do arquivo, "- dd/MM/yyyy HH:mm texto",
/// em UTF-8 sem BOM e com LF. Lógica pura (o autoteste usa direto, sem a ilha).
/// </summary>
internal static class CaixaDeEntrada
{
    private static readonly UTF8Encoding Utf8SemBom = new(false);
    // duas anotações seguidas não se misturam (conferir o último byte e escrever é um passo só para a ilha)
    private static readonly object _trava = new();

    public const int TENTATIVAS = 10;
    public const int ESPERA_MS = 50;

    /// <summary>
    /// O item de lista, já com o "\n" do fim. As linhas de continuação entram com dois espaços, então o texto de
    /// várias linhas continua um item só. null = não tem nada para anotar.
    /// </summary>
    public static string? Formatar(string texto, DateTime quando)
    {
        var linhas = texto.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int ini = 0, fim = linhas.Length - 1;
        while (ini <= fim && linhas[ini].Trim().Length == 0) ini++;
        while (fim >= ini && linhas[fim].Trim().Length == 0) fim--;
        if (ini > fim) return null;

        var sb = new StringBuilder();
        sb.Append("- ").Append(quando.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)).Append(' ');
        sb.Append(linhas[ini].Trim());
        for (int i = ini + 1; i <= fim; i++)
        {
            sb.Append('\n');
            // linha em branco no meio fica vazia (sem espaço sobrando); a seguinte, recuada, ainda é do mesmo item
            string l = linhas[i].TrimEnd();
            if (l.Length > 0) sb.Append("  ").Append(l);
        }
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// O caminho da config pronto para usar, ou o motivo de não servir. Variáveis de ambiente (%USERPROFILE%) valem.
    /// </summary>
    public static string? Caminho(string? configurado, out string? problema)
    {
        problema = null;
        string c = (configurado ?? "").Trim().Trim('"').Trim();
        if (c.Length == 0) { problema = "vazio"; return null; }
        try
        {
            c = Environment.ExpandEnvironmentVariables(c);
            if (!Path.IsPathFullyQualified(c)) { problema = Lang.T("O caminho em \"arquivo\" precisa ser completo, com a letra do disco.", "The path in \"arquivo\" must be complete, with the drive letter."); return null; }
            if (c.EndsWith('\\') || c.EndsWith('/')) { problema = Lang.T("\"arquivo\" aponta para uma pasta; ponha o caminho de um arquivo .md.", "\"arquivo\" points to a folder; put the path of a .md file."); return null; }
            return Path.GetFullPath(c);
        }
        catch (Exception e)
        {
            problema = Lang.T("Caminho inválido em \"arquivo\": ", "Invalid path in \"arquivo\": ") + e.Message;
            return null;
        }
    }

    /// <summary>
    /// Põe o item no fim do arquivo (cria a pasta e o arquivo se faltarem). Um FileStream em Append que divide o
    /// arquivo com quem estiver lendo ou escrevendo (Obsidian, git, a sincronia) e uma única Write; se o arquivo não
    /// termina em "\n", entra um antes. Arquivo preso por um instante: tenta de novo. Nunca arquivo temporário com
    /// troca de nome (mudaria a identidade do arquivo que o Obsidian está vigiando). Bloqueia: chamar fora da
    /// thread da interface. Devolve null se deu certo, senão o erro em uma frase.
    /// </summary>
    public static string? Anexar(string caminho, string item, int tentativas = TENTATIVAS, int esperaMs = ESPERA_MS)
    {
        lock (_trava)
        {
            IOException? ultimo = null;
            for (int i = 0; i < Math.Max(1, tentativas); i++)
            {
                if (i > 0) Thread.Sleep(esperaMs);
                try
                {
                    if (Directory.Exists(caminho)) return Lang.T("o caminho aponta para uma pasta, não para um arquivo.", "the path points to a folder, not a file.");
                    var pasta = Path.GetDirectoryName(caminho);
                    if (!string.IsNullOrEmpty(pasta)) Directory.CreateDirectory(pasta);
                    using var fs = new FileStream(caminho, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    bool faltaQuebra = fs.Length > 0 && UltimoByte(caminho, fs.Length) != (byte)'\n';
                    var bytes = Utf8SemBom.GetBytes(faltaQuebra ? "\n" + item : item);
                    fs.Write(bytes, 0, bytes.Length);
                    return null;
                }
                catch (IOException e) when (e is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException or DriveNotFoundException))
                {
                    ultimo = e;
                }
                catch (Exception e)
                {
                    return Explicar(e);
                }
            }
            return ultimo == null
                ? Lang.T("não deu para escrever no arquivo.", "couldn't write to the file.")
                : Lang.T("o arquivo continuou preso por outro programa (", "the file stayed locked by another program (") + ultimo.Message.TrimEnd('.', ' ') + ").";
        }
    }

    private static int UltimoByte(string caminho, long tamanho)
    {
        using var leitura = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        leitura.Seek(Math.Min(tamanho, leitura.Length) - 1, SeekOrigin.Begin);
        return leitura.ReadByte();
    }

    private static string Explicar(Exception e) => e switch
    {
        UnauthorizedAccessException => Lang.T("sem permissão para escrever no arquivo.", "no permission to write to the file."),
        DirectoryNotFoundException or DriveNotFoundException => Lang.T("a pasta do arquivo não existe (disco desconectado?).", "the file's folder does not exist (disconnected drive?)."),
        PathTooLongException => Lang.T("o caminho do arquivo é comprido demais.", "the file path is too long."),
        _ => e.Message.TrimEnd('.', ' ') + ".",
    };

    /// <summary>A primeira linha do texto, curta, para o alerta de confirmação.</summary>
    public static string Resumo(string texto, int maximo = 60)
    {
        foreach (var l in texto.Replace("\r\n", "\n").Split('\n'))
        {
            var t = l.Trim();
            if (t.Length == 0) continue;
            return t.Length <= maximo ? t : t[..(maximo - 1)].TrimEnd() + "…";
        }
        return "";
    }

    // ------------------------------------------------------------------ autoteste

    public static string Testar()
    {
        var r = new List<string>();
        void Conferir(string nome, bool ok, string detalhe = "") => r.Add((ok ? "ok      " : "FALHOU  ") + nome + (ok || detalhe.Length == 0 ? "" : ": " + detalhe));
        static string Ver(string? s) => s == null ? "null" : s.Replace("\n", "\\n").Replace("\r", "\\r");

        var quando = new DateTime(2026, 11, 1, 9, 5, 0);
        // formatação
        string? a = Formatar("comprar café", quando);
        Conferir("uma linha", a == "- 01/11/2026 09:05 comprar café\n", Ver(a));
        a = Formatar("  espaços em volta  \r\n\r\n", quando);
        Conferir("espaço e linhas vazias nas pontas saem", a == "- 01/11/2026 09:05 espaços em volta\n", Ver(a));
        a = Formatar("lista do mercado\r\nleite\r\npão  ", quando);
        Conferir("várias linhas (CRLF da caixa de texto) viram um item com recuo", a == "- 01/11/2026 09:05 lista do mercado\n  leite\n  pão\n", Ver(a));
        a = Formatar("um\n\ndois", quando);
        Conferir("linha vazia no meio fica vazia, sem espaço", a == "- 01/11/2026 09:05 um\n\n  dois\n", Ver(a));
        a = Formatar("título\n  já recuado", quando);
        Conferir("recuo próprio da linha é mantido", a == "- 01/11/2026 09:05 título\n    já recuado\n", Ver(a));
        a = Formatar("velho\rmac", quando);
        Conferir("CR sozinho também quebra", a == "- 01/11/2026 09:05 velho\n  mac\n", Ver(a));
        Conferir("vazio não anota", Formatar("", quando) == null && Formatar(" \r\n \t", quando) == null);
        var cultura = CultureInfo.CurrentCulture;
        try
        {
            // o alemão separa a data com ponto: o "/" do formato não pode seguir a cultura
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            a = Formatar("x", new DateTime(2026, 12, 31, 23, 59, 0));
        }
        finally { CultureInfo.CurrentCulture = cultura; }
        Conferir("data com barra mesmo em outra cultura", a == "- 31/12/2026 23:59 x\n", Ver(a));
        Conferir("resumo pega a primeira linha", Resumo("\r\n  primeira  \r\nsegunda") == "primeira");
        Conferir("resumo corta o comprido", Resumo(new string('a', 100), 10) == "aaaaaaaaa…");

        // caminho
        Conferir("caminho vazio = módulo parado", Caminho("  ", out var p1) == null && p1 == "vazio");
        Conferir("caminho relativo é recusado", Caminho("nota.md", out var p2) == null && p2 != null && p2 != "vazio");
        Conferir("caminho entre aspas serve", Caminho("\"C:\\x\\y.md\"", out _) == "C:\\x\\y.md");
        string idioma = Lang.Choice;
        try
        {
            Lang.Set("pt");
            Caminho("nota.md", out var pt);
            Lang.Set("en");
            Caminho("nota.md", out var en);
            Conferir("problem with the path in both languages", pt != null && pt.StartsWith("O caminho") && en != null && en.StartsWith("The path"), $"{pt} | {en}");
        }
        finally { Lang.Set(idioma); }

        // escrita de verdade numa pasta temporária
        string pasta = Path.Combine(Path.GetTempPath(), "ilha-teste-nota-" + Guid.NewGuid().ToString("N"));
        try
        {
            string arq = Path.Combine(pasta, "sub", "Caixa.md");
            string? erro = Anexar(arq, Formatar("primeira", quando)!);
            var b = File.ReadAllBytes(arq);
            Conferir("cria a pasta e o arquivo", erro == null && File.Exists(arq), erro ?? "");
            Conferir("arquivo novo sem BOM", b.Length > 0 && b[0] == (byte)'-', b.Length > 0 ? b[0].ToString() : "vazio");

            erro = Anexar(arq, Formatar("segunda\r\nlinha", quando)!);
            string t = File.ReadAllText(arq, Utf8SemBom);
            Conferir("acrescenta no fim", erro == null && t == "- 01/11/2026 09:05 primeira\n- 01/11/2026 09:05 segunda\n  linha\n", Ver(t));
            Conferir("só LF, nenhum CR", !t.Contains('\r'));

            string arq2 = Path.Combine(pasta, "sem-quebra.md");
            File.WriteAllBytes(arq2, Utf8SemBom.GetBytes("# Caixa\n\nalgo sem quebra"));
            Anexar(arq2, Formatar("nova", quando)!);
            t = File.ReadAllText(arq2, Utf8SemBom);
            Conferir("arquivo sem \\n no fim ganha um antes", t == "# Caixa\n\nalgo sem quebra\n- 01/11/2026 09:05 nova\n", Ver(t));

            string arq3 = Path.Combine(pasta, "com-bom.md");
            File.WriteAllBytes(arq3, new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x', (byte)'\r', (byte)'\n' });
            Anexar(arq3, Formatar("depois do crlf", quando)!);
            b = File.ReadAllBytes(arq3);
            string esperado3 = "x\r\n- 01/11/2026 09:05 depois do crlf\n";
            var eb = Utf8SemBom.GetBytes(esperado3);
            bool igual = b.Length == eb.Length + 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
            for (int i = 0; igual && i < eb.Length; i++) igual = b[i + 3] == eb[i];
            Conferir("BOM que já existia fica, nenhum BOM novo, CRLF do fim conta como quebra", igual, Ver(Encoding.UTF8.GetString(b)));

            string arq4 = Path.Combine(pasta, "vazio.md");
            File.WriteAllBytes(arq4, Array.Empty<byte>());
            Anexar(arq4, Formatar("no vazio", quando)!);
            t = File.ReadAllText(arq4, Utf8SemBom);
            Conferir("arquivo vazio não ganha linha em branco", t == "- 01/11/2026 09:05 no vazio\n", Ver(t));

            // o Obsidian (ou o git) com o arquivo aberto para ler e escrever não atrapalha
            using (new FileStream(arq4, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                erro = Anexar(arq4, Formatar("com outro aberto", quando)!);
            Conferir("divide o arquivo com quem está com ele aberto", erro == null, erro ?? "");

            // preso de verdade por um instante: as tentativas esperam e conseguem
            var preso = new FileStream(arq4, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var solta = new Thread(() => { Thread.Sleep(150); preso.Dispose(); }) { IsBackground = true };
            solta.Start();
            erro = Anexar(arq4, Formatar("depois de preso", quando)!);
            solta.Join();
            t = File.ReadAllText(arq4, Utf8SemBom);
            Conferir("arquivo preso por 150 ms: tenta de novo e anota", erro == null && t.EndsWith("- 01/11/2026 09:05 depois de preso\n"), erro ?? Ver(t));

            // preso o tempo todo: desiste com erro, sem exceção
            using (new FileStream(arq4, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                erro = Anexar(arq4, Formatar("não entra", quando)!, tentativas: 3, esperaMs: 10);
            t = File.ReadAllText(arq4, Utf8SemBom);
            Conferir("preso o tempo todo: devolve erro e não escreve", erro != null && !t.Contains("não entra"), erro ?? "sem erro");
        }
        catch (Exception e) { Conferir("escrita no arquivo temporário", false, e.ToString()); }
        finally
        {
            try { Directory.Delete(pasta, true); } catch { }
        }
        return string.Join("\n", r);
    }
}
