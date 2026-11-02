using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Ilha.LimiteClaude;

/// <summary>`Ilha.exe --testar limite`: leitura do arquivo, janela vencida, textos, avisos uma vez por janela e o script da status line.</summary>
internal static class AutotesteLimite
{
    private static readonly StringBuilder R = new();

    private static void Ok(bool cond, string o) => R.AppendLine((cond ? "ok      " : "FALHOU  ") + o);

    public static string Rodar()
    {
        R.Clear();
        // os JSON de teste são montados com interpolação: ponto decimal em qualquer cultura
        var cultura = System.Threading.Thread.CurrentThread.CurrentCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        // the texts are asserted in Portuguese: pin the language and give back the choice at the end
        string idioma = Lang.Choice;
        Lang.Set("pt");
        try
        {
            Bloco("leitura do arquivo", Leitura);
            Bloco("janelas vencidas", Vencidas);
            Bloco("textos", Textos);
            Bloco("avisos", Avisos);
            Bloco("script da status line", Script);
            Bloco("textos em inglês", EmIngles);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = cultura;
            Lang.Set(idioma);
        }
        return R.ToString().TrimEnd();
    }

    private static void Bloco(string nome, Action a)
    {
        try { a(); }
        catch (Exception e) { R.AppendLine($"FALHOU  {nome}: exceção {e.GetType().Name}: {e.Message}"); }
    }

    private const double T0 = 1_800_000_000;

    private static string Arquivo(double coletado, string? cinco, string? sete)
    {
        var partes = new[] { cinco == null ? null : $"\"five_hour\": {cinco}", sete == null ? null : $"\"seven_day\": {sete}" }.Where(x => x != null);
        return $"{{\"coletado_em\": {coletado}, \"rate_limits\": {{{string.Join(", ", partes)}}}}}";
    }

    private static void Leitura()
    {
        var l = LeituraDoUso.Ler(Arquivo(T0, $"{{\"used_percentage\": 34.4, \"resets_at\": {T0 + 3600}}}", $"{{\"used_percentage\": 12, \"resets_at\": {T0 + 86400}}}"), 5)!;
        Ok(l != null && l.ColetadoEm == T0 && l.Sessao!.Porcentagem == 34.4 && l.Sessao.VoltaEm == T0 + 3600 && l.Semana!.Porcentagem == 12 && l.Semana.VoltaEm == T0 + 86400,
            "lê as duas janelas e a hora da coleta");
        Ok(l!.Sessao!.Id == Janela.SESSAO && !l.Sessao.Semanal && l.Semana!.Semanal, "janela de 5 horas e semanal identificadas");
        var s = LeituraDoUso.Ler("{\"rate_limits\": {\"five_hour\": {\"used_percentage\": \"40.5\", \"resets_at\": \"1800003600\"}}}", 777)!;
        Ok(s.ColetadoEm == 777 && s.Sessao!.Porcentagem == 40.5 && s.Sessao.VoltaEm == 1800003600 && s.Semana == null, "número em texto aceito; sem coletado_em vale a hora do arquivo");
        var ms = LeituraDoUso.Ler(Arquivo(T0, $"{{\"used_percentage\": 150, \"resets_at\": {(T0 + 60) * 1000}}}", null), 0)!;
        Ok(ms.Sessao!.Porcentagem == 100 && ms.Sessao.VoltaEm == T0 + 60, "porcentagem acima de 100 limitada; reset em milissegundos convertido");
        var semReset = LeituraDoUso.Ler(Arquivo(T0, "{\"used_percentage\": 5}", null), 0)!;
        Ok(semReset.Sessao!.VoltaEm == null && semReset.ProximoReset(T0) == null, "janela sem resets_at vale, sem relógio de reset");
        Ok(LeituraDoUso.Ler("{\"coletado_em\": 1}", 0) is { Sessao: null, Semana: null }, "sem rate_limits: leitura sem janelas");
        Ok(LeituraDoUso.Ler(Arquivo(T0, "{\"used_percentage\": true}", "{\"resets_at\": 5}"), 0) is { Sessao: null, Semana: null }, "janela sem porcentagem de número: ignorada");
        Ok(LeituraDoUso.Ler("isto não é json", 0) == null && LeituraDoUso.Ler("[1, 2]", 0) == null && LeituraDoUso.Ler("", 0) == null, "arquivo quebrado: null (o módulo fica com o que tinha)");
    }

    private static LeituraDoUso Exemplo(double coletado = T0, double p5 = 34, double p7 = 12, double r5 = T0 + 3600, double r7 = T0 + 86400) =>
        LeituraDoUso.Ler(Arquivo(coletado, $"{{\"used_percentage\": {p5}, \"resets_at\": {r5}}}", $"{{\"used_percentage\": {p7}, \"resets_at\": {r7}}}"), 0)!;

    private static void Vencidas()
    {
        var l = Exemplo();
        Ok(TextoDoUso.Detalhe(l, T0 + 10) == "34% · sem. 12%", "menu: " + TextoDoUso.Detalhe(l, T0 + 10));
        Ok(l.ProximoReset(T0 + 10) == T0 + 3600, "o relógio vai para o reset mais próximo");
        Ok(l.Validas(T0 + 3600).Count() == 1 && l.SessaoValida(T0 + 3600) == null && TextoDoUso.Detalhe(l, T0 + 3600) == "sem. 12%", "passou o reset da sessão: a janela sai (menu: sem. 12%)");
        Ok(l.ProximoReset(T0 + 3600) == T0 + 86400, "depois do reset da sessão, o relógio vai para o da semana");
        Ok(!l.Validas(T0 + 86400).Any() && TextoDoUso.Detalhe(l, T0 + 86400) == null && l.ProximoReset(T0 + 86400) == null, "as duas venceram: nada no menu e nenhum relógio");
        Ok(TextoDoUso.Detalhe(null, T0) == null, "sem arquivo: nada no menu");
        Ok(TextoDoUso.Detalhe(LeituraDoUso.Ler(Arquivo(T0, null, "{\"used_percentage\": 7.6}"), 0), T0) == "sem. 8%", "só a semanal: sem. 8%");
        Ok(TextoDoUso.MaisApertada(l, T0 + 10)?.Porcentagem == 34 && TextoDoUso.MaisApertada(Exemplo(p5: 4, p7: 68), T0 + 10)?.Porcentagem == 68
            && TextoDoUso.MaisApertada(l, T0 + 3600)?.Porcentagem == 12 && TextoDoUso.MaisApertada(l, T0 + 86400) == null && TextoDoUso.MaisApertada(null, T0) == null,
            "menu ring: the window closer to its limit (4% session, 68% week: 68%), the one left after a reset, none when both ended");
    }

    private static void Textos()
    {
        Ok(TextoDoUso.Porcento(33.6) == "34%" && TextoDoUso.Porcento(-3) == "0%" && TextoDoUso.Porcento(100.4) == "100%", "porcentagem arredondada e limitada");
        Ok(TextoDoUso.Idade(30) == "atualizado agora" && TextoDoUso.Idade(-90) == "atualizado agora" && TextoDoUso.Idade(double.NaN) == "atualizado agora", "idade: agora (inclusive relógio adiantado)");
        Ok(TextoDoUso.Idade(12 * 60 + 5) == "atualizado há 12 min" && TextoDoUso.Idade(3 * 3600 + 100) == "atualizado há 3 h", "idade: 12 min, 3 h");
        Ok(TextoDoUso.Idade(26 * 3600) == "atualizado há 1 dia" && TextoDoUso.Idade(3 * 86400) == "atualizado há 3 dias", "idade: 1 dia, 3 dias");
        var agora = new DateTime(2026, 11, 2, 10, 0, 0); // segunda
        Ok(TextoDoUso.Quando(new DateTime(2026, 11, 2, 14, 30, 0), agora, false) == "às 14:30", "sessão no mesmo dia: às 14:30");
        Ok(TextoDoUso.Quando(new DateTime(2026, 11, 2, 14, 30, 0), agora, true) == "hoje às 14:30", "semana no mesmo dia: hoje às 14:30");
        Ok(TextoDoUso.Quando(new DateTime(2026, 11, 3, 1, 5, 0), agora, false) == "amanhã às 01:05", "passando da meia-noite: amanhã às 01:05");
        string sexta = TextoDoUso.Quando(new DateTime(2026, 11, 6, 9, 0, 0), agora, true);
        Ok(sexta == "sexta às 09:00", "semana: dia da semana sem o -feira (" + sexta + ")");
        Ok(TextoDoUso.Quando(new DateTime(2026, 11, 14, 9, 0, 0), agora, true) == "14/11 às 09:00", "mais de uma semana: a data");
        var j = new Janela(Janela.SEMANA, 50, new DateTimeOffset(new DateTime(2026, 11, 6, 9, 0, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 6, 9, 0, 0))).ToUnixTimeSeconds());
        Ok(TextoDoUso.Volta(j, agora) == "volta sexta às 09:00", "volta: " + TextoDoUso.Volta(j, agora));
        Ok(TextoDoUso.Volta(new Janela(Janela.SESSAO, 5, null), agora) == "sem horário de reset", "sem reset: diz que não tem horário");
        int[] lim = { 80, 95 };
        Ok(AtividadeLimite.CorDe(50, lim).Equals(Cor.Coral) && AtividadeLimite.CorDe(85, lim).Equals(Cor.Laranja) && AtividadeLimite.CorDe(96, lim).Equals(Cor.Vermelho)
            && AtividadeLimite.CorDe(99, new[] { 80 }).Equals(Cor.Laranja), "cor da barra pelos limiares");
    }

    private static void EmIngles()
    {
        Lang.Set("en");
        try
        {
            var agora = new DateTime(2026, 11, 2, 10, 0, 0); // Monday
            Ok(TextoDoUso.Quando(new DateTime(2026, 11, 2, 14, 30, 0), agora, false) == "at 2:30 PM", "en: same day: at 2:30 PM");
            Ok(TextoDoUso.Quando(new DateTime(2026, 11, 6, 9, 0, 0), agora, true) == "Friday at 9:00 AM", "en: weekday: Friday at 9:00 AM");
            Ok(TextoDoUso.Quando(new DateTime(2026, 11, 14, 9, 0, 0), agora, true) == "11/14 at 9:00 AM", "en: more than a week: 11/14");
            Ok(TextoDoUso.Idade(12 * 60 + 5) == "updated 12 min ago" && TextoDoUso.Idade(3 * 86400) == "updated 3 days ago", "en: age");
            Ok(TextoDoUso.Detalhe(Exemplo(), T0 + 10) == "34% · wk 12%", "en: menu detail");
            Ok(TextoDoUso.Volta(new Janela(Janela.SESSAO, 5, null), agora) == "no reset time", "en: no reset time");
        }
        finally { Lang.Set("pt"); }
    }

    private static void Avisos()
    {
        Ok(AvisosDoLimite.Limiares(80, 95).SequenceEqual(new[] { 80, 95 }) && AvisosDoLimite.Limiares(80, 0).SequenceEqual(new[] { 80 })
            && AvisosDoLimite.Limiares(0, 95).SequenceEqual(new[] { 95 }) && AvisosDoLimite.Limiares(90, 80).SequenceEqual(new[] { 90 })
            && AvisosDoLimite.Limiares(0, 0).Length == 0 && AvisosDoLimite.Limiares(150, 0).SequenceEqual(new[] { 100 }), "limiares: 0 desliga, o segundo só acima do primeiro");
        int[] lim = { 80, 95 };
        var av = new AvisosDoLimite();
        Ok(av.Pendentes(Exemplo(p5: 70), T0 + 1, lim).Count == 0, "70%: nada");
        var p = av.Pendentes(Exemplo(p5: 82), T0 + 1, lim);
        Ok(p.Count == 1 && p[0].Janela.Id == Janela.SESSAO && p[0].Limiar == 80, "82% na sessão: avisa o de 80");
        av.Marcar(p[0].Janela, p[0].Limiar, T0, lim);
        Ok(av.Pendentes(Exemplo(p5: 85), T0 + 60, lim).Count == 0, "mesma janela, 85%: não repete");
        Ok(av.Pendentes(Exemplo(p5: 85, r5: T0 + 3600 + 40), T0 + 60, lim).Count == 0, "reset uns segundos diferente: ainda é a mesma janela");
        p = av.Pendentes(Exemplo(p5: 96), T0 + 120, lim);
        Ok(p.Count == 1 && p[0].Limiar == 95, "96%: avisa o segundo (95)");
        av.Marcar(p[0].Janela, p[0].Limiar, T0, lim);
        Ok(av.Pendentes(Exemplo(p5: 99), T0 + 200, lim).Count == 0, "99% na mesma janela: nada mais");
        Ok(av.Pendentes(Exemplo(p5: 99), T0 + 3600, lim).Count == 0, "janela vencida: não avisa");
        p = av.Pendentes(Exemplo(p5: 81, r5: T0 + 3600 + 18000), T0 + 3700, lim);
        Ok(p.Count == 1 && p[0].Limiar == 80, "janela nova (reset 5 h depois): avisa de novo");

        var pulo = new AvisosDoLimite();
        p = pulo.Pendentes(Exemplo(p5: 97), T0 + 1, lim);
        Ok(p.Count == 1 && p[0].Limiar == 95, "pulou de 70 para 97: um aviso só, o de 95");
        pulo.Marcar(p[0].Janela, p[0].Limiar, T0, lim);
        Ok(pulo.Pendentes(Exemplo(p5: 98), T0 + 10, lim).Count == 0, "e o de 80 já conta como avisado");

        var duas = new AvisosDoLimite();
        p = duas.Pendentes(Exemplo(p5: 90, p7: 81), T0 + 1, lim);
        Ok(p.Count == 2 && p.Any(x => x.Janela.Semanal && x.Limiar == 80) && p.Any(x => !x.Janela.Semanal && x.Limiar == 80), "sessão e semana avisam cada uma por si");
        duas.MarcarTudo(Exemplo(p5: 90, p7: 81), T0 + 1, lim);
        Ok(duas.Pendentes(Exemplo(p5: 92, p7: 84), T0 + 5, lim).Count == 0, "leitura do começo (marcar tudo): sem aviso depois");

        var sem = new AvisosDoLimite();
        LeituraDoUso SemReset(double coletado, double pct) => LeituraDoUso.Ler(Arquivo(coletado, $"{{\"used_percentage\": {pct}}}", null), 0)!;
        p = sem.Pendentes(SemReset(T0, 85), T0, lim);
        sem.Marcar(p[0].Janela, p[0].Limiar, T0, lim);
        Ok(sem.Pendentes(SemReset(T0 + 3600, 88), T0 + 3600, lim).Count == 0, "sem resets_at: estima a janela pela coleta (1 h depois não repete)");
        Ok(sem.Pendentes(SemReset(T0 + 6 * 3600, 85), T0 + 6 * 3600, lim).Count == 1, "sem resets_at: 6 h depois já é outra janela");

        Ok(new AvisosDoLimite().Pendentes(Exemplo(p5: 99), T0, Array.Empty<int>()).Count == 0, "avisos desligados (limiares 0): nada");
    }

    // ------------------------------------------------------------------ o script da status line

    private static string? AcharScript()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "ferramentas", "statusline_uso.py");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    private static (int Codigo, string Saida) Python(string args, string entrada, int esperaMs = 60_000)
    {
        var psi = new ProcessStartInfo("python", "-I " + args)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(entrada);
        p.StandardInput.Close();
        var saida = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(esperaMs)) { try { p.Kill(true); } catch { } return (-1, ""); }
        return (p.ExitCode, saida.Result);
    }

    private static void Script()
    {
        var script = AcharScript();
        if (script == null) { R.AppendLine("ok      statusline_uso.py não encontrado perto do exe (pulado)"); return; }
        (int Codigo, string Saida) r;
        try { r = Python($"\"{script}\" --testar", ""); }
        catch (System.ComponentModel.Win32Exception) { R.AppendLine("ok      python não está no PATH (pulado)"); return; }
        var falhas = r.Saida.Split('\n').Where(x => x.StartsWith("FALHOU")).ToList();
        Ok(r.Codigo == 0, $"statusline_uso.py --testar: {r.Saida.Split('\n').Count(x => x.StartsWith("ok      "))} ok" + (falhas.Count > 0 ? " · " + string.Join(" | ", falhas).Trim() : ""));

        // de ponta a ponta: o arquivo que o script grava é o que a ilha lê
        string pasta = Path.Combine(Path.GetTempPath(), "ilha-limite-teste-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(pasta);
        try
        {
            string saida = Path.Combine(pasta, ModuloLimite.NOME_DO_ARQUIVO), cfg = Path.Combine(pasta, "config.json");
            double agora = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string entrada = "{\"model\": {\"display_name\": \"Opus\"}, \"cwd\": \"C:/x\", \"rate_limits\": {\"five_hour\": {\"used_percentage\": 61.5, \"resets_at\": "
                + (agora + 7200) + "}, \"seven_day\": {\"used_percentage\": 18, \"resets_at\": " + (agora + 400000) + "}}}";
            r = Python($"\"{script}\" --saida \"{saida}\" --config \"{cfg}\"", entrada);
            var l = File.Exists(saida) ? LeituraDoUso.Ler(File.ReadAllText(saida), 0) : null;
            Ok(r.Codigo == 0 && r.Saida == "" && l != null && l.Sessao!.Porcentagem == 61.5 && l.Sessao.VoltaEm == agora + 7200 && l.Semana!.Porcentagem == 18
                && Math.Abs(l.ColetadoEm - agora) < 120, "ponta a ponta: o script grava e a ilha lê o mesmo dado");
            string gravado = File.Exists(saida) ? File.ReadAllText(saida) : "";
            Ok(!gravado.Contains("display_name") && !gravado.Contains("cwd"), "o arquivo leva só o uso (nada do resto da sessão)");
            r = Python($"\"{script}\" --saida \"{saida}\" --config \"{cfg}\"", "{\"model\": {\"display_name\": \"Opus\"}}");
            Ok(r.Codigo == 0 && File.ReadAllText(saida) == gravado, "sessão sem rate_limits: o arquivo fica como estava");
        }
        finally
        {
            try { Directory.Delete(pasta, true); } catch { }
        }
    }
}
