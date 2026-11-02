using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ilha.Tempo;

// ---------------------------------------------------------------- config e estado salvo

public enum Fase { Foco, Pausa, PausaLonga }

public sealed class TimerSalvo
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("nome")] public string Nome { get; set; } = "";
    [JsonPropertyName("duracaoMs")] public long DuracaoMs { get; set; }
    // horário absoluto de fim (Unix, ms, UTC) enquanto roda; parado, vale o restante
    [JsonPropertyName("fimMs")] public long FimMs { get; set; }
    [JsonPropertyName("restanteMs")] public long RestanteMs { get; set; }
    [JsonPropertyName("pausado")] public bool Pausado { get; set; }
}

public sealed class PomodoroSalvo
{
    [JsonPropertyName("nome")] public string Nome { get; set; } = "";
    [JsonPropertyName("fase"), JsonConverter(typeof(JsonStringEnumConverter<Fase>))] public Fase Fase { get; set; } = Fase.Foco;
    // focos completos desde que o pomodoro começou (a pausa longa vem a cada "focosAteLonga")
    [JsonPropertyName("focosFeitos")] public int FocosFeitos { get; set; }
    // duração da fase atual (a config pode mudar no meio; vale a partir da fase seguinte)
    [JsonPropertyName("duracaoMs")] public long DuracaoMs { get; set; }
    [JsonPropertyName("fimMs")] public long FimMs { get; set; }
    [JsonPropertyName("restanteMs")] public long RestanteMs { get; set; }
    [JsonPropertyName("pausado")] public bool Pausado { get; set; }
}

public sealed class CronometroSalvo
{
    // rodando: o início "virtual" (agora menos o já decorrido); parado: o acumulado
    [JsonPropertyName("inicioMs")] public long InicioMs { get; set; }
    [JsonPropertyName("acumuladoMs")] public long AcumuladoMs { get; set; }
    [JsonPropertyName("rodando")] public bool Rodando { get; set; }
    // tempo total no instante de cada volta
    [JsonPropertyName("voltas")] public List<long> Voltas { get; set; } = new();
}

public sealed class EstadoTempo
{
    [JsonPropertyName("timers")] public List<TimerSalvo> Timers { get; set; } = new();
    [JsonPropertyName("pomodoro")] public PomodoroSalvo? Pomodoro { get; set; }
    [JsonPropertyName("cronometro")] public CronometroSalvo? Cronometro { get; set; }
    [JsonPropertyName("proximoId")] public int ProximoId { get; set; } = 1;
}

public sealed class ConfigTempo
{
    [JsonPropertyName("focoMin")] public double FocoMin { get; set; } = 25;
    [JsonPropertyName("pausaMin")] public double PausaMin { get; set; } = 5;
    [JsonPropertyName("pausaLongaMin")] public double PausaLongaMin { get; set; } = 15;
    [JsonPropertyName("focosAteLonga")] public int FocosAteLonga { get; set; } = 4;
    // quanto tempo o alerta de "acabou" fica na ilha
    [JsonPropertyName("segundosDoAlerta")] public double SegundosDoAlerta { get; set; } = 10;
    // how long the timer melody keeps ringing (in a loop) when nobody dismisses it
    [JsonPropertyName("segundosDoAlarme")] public double SegundosDoAlarme { get; set; } = 60;
    // timers, pomodoro e cronômetro em andamento (a ilha grava sozinha; sobrevive a fechar e abrir)
    [JsonPropertyName("estado")] public EstadoTempo Estado { get; set; } = new();

    private static double Limite(double v, double min, double max, double padrao) => double.IsFinite(v) ? Math.Clamp(v, min, max) : padrao;

    /// <summary>Põe os valores dentro dos limites (config editada à mão).</summary>
    public void Sanear()
    {
        FocoMin = Limite(FocoMin, 1, 600, 25);
        PausaMin = Limite(PausaMin, 1, 600, 5);
        PausaLongaMin = Limite(PausaLongaMin, 1, 600, 15);
        FocosAteLonga = Math.Clamp(FocosAteLonga, 1, 20);
        SegundosDoAlerta = Limite(SegundosDoAlerta, 2, 60, 10);
        SegundosDoAlarme = Limite(SegundosDoAlarme, 5, 600, 60);
        Estado ??= new EstadoTempo();
    }

    public void CopiarAjustes(ConfigTempo de)
    {
        FocoMin = de.FocoMin;
        PausaMin = de.PausaMin;
        PausaLongaMin = de.PausaLongaMin;
        FocosAteLonga = de.FocosAteLonga;
        SegundosDoAlerta = de.SegundosDoAlerta;
        SegundosDoAlarme = de.SegundosDoAlarme;
    }
}

// ---------------------------------------------------------------- o que se digita no campo

internal enum TipoPedido { Timer, Pomodoro, Cronometro }

internal sealed record Pedido(TipoPedido Tipo, long DuracaoMs, string Nome);

/// <summary>
/// Lê o que foi digitado: "25m", "1h30", "90s", "1:30" (minuto e segundo), "1:30:00", "10" (minutos), "1,5h",
/// "25 min", "pomodoro", "cronômetro"/"cron". Um nome pode vir depois ("25m chá") ou antes ("chá 25m").
/// English words are understood whatever the interface language: "stopwatch", "2 hours", "45 seconds", "timer 5m",
/// "focus" (pomodoro, only when no duration is found).
/// </summary>
internal static class Interpretador
{
    public const long MaximoMs = 99L * 3_600_000;
    public const int NomeMaximo = 40;

    private static readonly Regex DoisPontos = new(@"^(\d{1,3}):(\d{1,2})(?::(\d{1,2}))?(?=\s|$)", RegexOptions.CultureInvariant);

    public static Pedido? Ler(string? texto, out string erro)
    {
        string s = Regex.Replace((texto ?? "").Trim(), @"\s+", " ");
        if (s.Length == 0) { erro = Lang.T("digite um tempo", "type a time"); return null; }
        var palavras = s.Split(' ');
        // a leading "timer" is just the word for what is being created ("timer 25m tea"); the name shown is the same
        if (palavras.Length > 1 && SemAcento(palavras[0]) == "timer")
        {
            s = string.Join(' ', palavras.Skip(1));
            palavras = s.Split(' ');
        }
        string primeira = SemAcento(palavras[0]);
        string resto = string.Join(' ', palavras.Skip(1));
        if (primeira is "pomodoro" or "pomo") { erro = ""; return new Pedido(TipoPedido.Pomodoro, 0, Nome(resto)); }
        if (primeira is "cronometro" or "cron" or "crono" or "stopwatch") { erro = ""; return new Pedido(TipoPedido.Cronometro, 0, ""); }

        if (Duracao(s, out long ms, out int fim, out erro))
        {
            string depois = s[fim..];
            // o nome precisa estar separado por espaço ("25mchá" não vale)
            if (depois.Length == 0 || depois[0] == ' ') return new Pedido(TipoPedido.Timer, ms, Nome(depois));
            erro = Lang.T("não entendi", "didn't get that");
            return null;
        }
        string primeiroErro = erro;
        // nome antes, tempo no fim; só quando não começa por número (senão "30s 1m" viraria o timer "1m" chamado "30s")
        for (int k = 1; k < palavras.Length && !char.IsAsciiDigit(s[0]); k++)
        {
            string cauda = string.Join(' ', palavras.Skip(k));
            if (Duracao(cauda, out ms, out fim, out _) && fim == cauda.Length)
            {
                erro = "";
                return new Pedido(TipoPedido.Timer, ms, Nome(string.Join(' ', palavras.Take(k))));
            }
        }
        // "focus"/"foco" start a pomodoro only when nothing above found a duration ("focus 25m" stays a named timer)
        if (primeira is "focus" or "foco") { erro = ""; return new Pedido(TipoPedido.Pomodoro, 0, Nome(resto)); }
        erro = primeiroErro;
        return null;
    }

    private static string Nome(string s)
    {
        s = s.Trim();
        return s.Length <= NomeMaximo ? s : s[..NomeMaximo].TrimEnd();
    }

    public static string SemAcento(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    // 0 hora, 1 minuto, 2 segundo, -1 não é unidade
    private static int Unidade(string palavra) => SemAcento(palavra) switch
    {
        "h" or "hr" or "hrs" or "hora" or "horas" or "hour" or "hours" => 0,
        "m" or "min" or "mins" or "minuto" or "minutos" or "minute" or "minutes" => 1,
        "s" or "seg" or "segs" or "sec" or "secs" or "segundo" or "segundos" or "second" or "seconds" => 2,
        _ => -1,
    };

    private static readonly double[] Fator = { 3_600_000, 60_000, 1_000 };

    /// <summary>Lê uma duração no começo de `s`. `fim` é onde ela termina (o resto é o nome).</summary>
    public static bool Duracao(string s, out long ms, out int fim, out string erro)
    {
        ms = 0;
        fim = 0;
        erro = Lang.T("não entendi", "didn't get that");
        var dp = DoisPontos.Match(s);
        if (dp.Success)
        {
            var g = dp.Groups;
            long a = long.Parse(g[1].Value, CultureInfo.InvariantCulture);
            if (g[2].Value.Length != 2 || (g[3].Success && g[3].Value.Length != 2)) { erro = Lang.T("use dois dígitos depois dos dois-pontos (1:05)", "use two digits after the colon (1:05)"); return false; }
            long b = long.Parse(g[2].Value, CultureInfo.InvariantCulture);
            if (b >= 60) { erro = Lang.T("depois dos dois-pontos vai até 59", "after the colon it goes up to 59"); return false; }
            if (g[3].Success)
            {
                long c = long.Parse(g[3].Value, CultureInfo.InvariantCulture);
                if (c >= 60) { erro = Lang.T("depois dos dois-pontos vai até 59", "after the colon it goes up to 59"); return false; }
                ms = (a * 3600 + b * 60 + c) * 1000;
            }
            else ms = (a * 60 + b) * 1000;
            fim = dp.Length;
            return Validar(ms, out erro);
        }
        // primeira palavra com dois-pontos que não casou com m:ss nem h:mm:ss ("1:2:3:4", "a:b")
        int espaco = s.IndexOf(' ');
        if ((espaco < 0 ? s : s[..espaco]).Contains(':')) return false;

        int i = 0, ordem = -1;
        double total = 0;
        bool algum = false;
        while (true)
        {
            int j = i;
            while (j < s.Length && s[j] == ' ') j++;
            int ini = j;
            while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
            if (j == ini) break;
            if (j - ini > 6) { erro = Lang.T("número grande demais", "number too big"); return false; }
            if (j + 1 < s.Length && (s[j] == ',' || s[j] == '.') && char.IsAsciiDigit(s[j + 1]))
            {
                j++;
                int dec = j;
                while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
                if (j - dec > 3) { erro = Lang.T("casas decimais demais", "too many decimal places"); return false; }
            }
            double valor = double.Parse(s[ini..j].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
            int depoisNumero = j;
            while (j < s.Length && s[j] == ' ') j++;
            int iniLetras = j;
            while (j < s.Length && char.IsLetter(s[j])) j++;
            string palavra = s[iniLetras..j];
            int u = palavra.Length > 0 ? Unidade(palavra) : -1;
            if (u >= 0)
            {
                if (u <= ordem) { erro = Lang.T("as partes vão de hora para minuto para segundo, uma vez cada", "parts go hours, then minutes, then seconds, once each"); return false; }
                total += valor * Fator[u];
                ordem = u;
                i = j;
                algum = true;
                continue;
            }
            if (palavra.Length > 0 && iniLetras == depoisNumero) { erro = Lang.T($"não conheço a unidade \"{palavra}\"", $"unknown unit \"{palavra}\""); return false; }
            // número solto: minutos no começo ("10"), minutos depois de hora ("1h30"), segundos depois de minuto ("2m30")
            int implicita = ordem switch { -1 => 1, 0 => 1, 1 => 2, _ => -1 };
            if (implicita < 0) { erro = Lang.T("sobrou um número depois dos segundos", "extra number after the seconds"); return false; }
            total += valor * Fator[implicita];
            i = depoisNumero;
            algum = true;
            break;
        }
        if (!algum) return false;
        ms = (long)Math.Round(total);
        fim = i;
        return Validar(ms, out erro);
    }

    private static bool Validar(long ms, out string erro)
    {
        if (ms < 1000) { erro = Lang.T("o tempo precisa ter pelo menos 1 segundo", "the time needs at least 1 second"); return false; }
        if (ms > MaximoMs) { erro = Lang.T("no máximo 99 horas", "99 hours at most"); return false; }
        erro = "";
        return true;
    }
}

// ---------------------------------------------------------------- formatação

internal static class Formato
{
    private static string Relogio(long s) => s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";

    /// <summary>Contagem regressiva: arredonda para cima (mostra 0:01 até acabar de verdade).</summary>
    public static string Contagem(long ms) => Relogio(SegundosContagem(ms));
    public static long SegundosContagem(long ms) => ms <= 0 ? 0 : (ms + 999) / 1000;

    /// <summary>Cronômetro: arredonda para baixo.</summary>
    public static string Decorrido(long ms) => Relogio(Math.Max(0, ms) / 1000);

    /// <summary>Volta do cronômetro, com centésimos.</summary>
    public static string Volta(long ms)
    {
        long cs = Math.Max(0, ms) / 10;
        return Relogio(cs / 100) + Lang.Culture.NumberFormat.NumberDecimalSeparator + (cs % 100).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>Duração por extenso curto: "25 min", "1 h 30 min", "1 min 30 s".</summary>
    public static string Duracao(long ms)
    {
        long s = (Math.Max(0, ms) + 500) / 1000;
        var partes = new List<string>();
        if (s >= 3600) partes.Add($"{s / 3600} h");
        if (s / 60 % 60 > 0) partes.Add($"{s / 60 % 60} min");
        if (s % 60 > 0) partes.Add($"{s % 60} s");
        return partes.Count == 0 ? "0 s" : string.Join(' ', partes);
    }

    public static string Hora(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString(Lang.T("HH:mm", "h:mm tt"), Lang.Culture);

    public static string Maiuscula(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Lang.Culture) + s[1..];
}

// ---------------------------------------------------------------- pomodoro

internal static class CicloPomodoro
{
    /// <summary>A fase que vem depois desta: foco vira pausa (a longa a cada `focosAteLonga` focos); pausa vira foco.</summary>
    public static (Fase Fase, int FocosFeitos) Proxima(Fase atual, int focosFeitos, int focosAteLonga)
    {
        if (atual != Fase.Foco) return (Fase.Foco, focosFeitos);
        int f = focosFeitos + 1;
        return (f % Math.Max(1, focosAteLonga) == 0 ? Fase.PausaLonga : Fase.Pausa, f);
    }

    public static long DuracaoMs(Fase f, ConfigTempo c) => (long)Math.Round(60_000 * f switch
    {
        Fase.Foco => c.FocoMin,
        Fase.Pausa => c.PausaMin,
        _ => c.PausaLongaMin,
    });

    /// <summary>Qual foco da rodada é (1 a N): o atual, no foco; o que acabou de terminar, na pausa.</summary>
    public static int NumeroDoFoco(Fase f, int focosFeitos, int focosAteLonga)
    {
        int n = Math.Max(1, focosAteLonga);
        return f == Fase.Foco ? focosFeitos % n + 1 : (Math.Max(1, focosFeitos) - 1) % n + 1;
    }

    public static string NomeDaFase(Fase f) => f switch { Fase.Foco => Lang.T("foco", "focus"), Fase.Pausa => Lang.T("pausa", "break"), _ => Lang.T("pausa longa", "long break") };
}

// ---------------------------------------------------------------- os relógios (sem interface, tudo testável)

internal enum TipoItem { Timer, Pomodoro, Cronometro }

internal readonly record struct ItemRef(TipoItem Tipo, int Id);

/// <summary>Algo que aconteceu ao passar do horário: um timer acabou ou o pomodoro mudou de fase.</summary>
internal sealed record Evento(TimerSalvo? Timer, Fase? NovaFase, int FocosFeitos, int Saltos);

/// <summary>
/// Os timers, o pomodoro e o cronômetro. Toda conta usa horário absoluto (Unix em ms) passado de fora: nada de contar
/// ticks, então dormir, travar ou fechar a ilha não atrasa nada.
/// </summary>
internal sealed class Relogios
{
    public const int MaximoTimers = 20;
    public const int MaximoVoltas = 999;

    public EstadoTempo E { get; }

    public Relogios(EstadoTempo e) => E = e;

    public bool Vazio => E.Timers.Count == 0 && E.Pomodoro == null && E.Cronometro == null;
    public int Quantos => E.Timers.Count + (E.Pomodoro != null ? 1 : 0) + (E.Cronometro != null ? 1 : 0);

    public bool AlgoRodando =>
        E.Timers.Any(t => !t.Pausado) || E.Pomodoro is { Pausado: false } || E.Cronometro is { Rodando: true };

    public static long Restante(TimerSalvo t, long agora) => t.Pausado ? Math.Max(0, t.RestanteMs) : Math.Max(0, t.FimMs - agora);
    public static long Restante(PomodoroSalvo p, long agora) => p.Pausado ? Math.Max(0, p.RestanteMs) : Math.Max(0, p.FimMs - agora);
    public static long Decorrido(CronometroSalvo c, long agora) => c.Rodando ? Math.Max(0, agora - c.InicioMs) : Math.Max(0, c.AcumuladoMs);

    public static float Progresso(TimerSalvo t, long agora) => t.DuracaoMs <= 0 ? 1f : 1f - (float)Restante(t, agora) / t.DuracaoMs;
    public static float Progresso(PomodoroSalvo p, long agora) => p.DuracaoMs <= 0 ? 1f : 1f - (float)Restante(p, agora) / p.DuracaoMs;
    // o anel do cronômetro dá uma volta por minuto
    public static float Progresso(CronometroSalvo c, long agora) => Decorrido(c, agora) % 60_000 / 60_000f;

    public TimerSalvo? Timer(int id) => E.Timers.Find(t => t.Id == id);

    /// <summary>Cria o que foi pedido. Devolve a mensagem de erro, ou null se deu certo.</summary>
    public string? Criar(Pedido p, long agora, ConfigTempo cfg)
    {
        switch (p.Tipo)
        {
            case TipoPedido.Pomodoro:
                if (E.Pomodoro != null) return Lang.T("já tem um pomodoro", "a pomodoro is already running");
                long d = CicloPomodoro.DuracaoMs(Fase.Foco, cfg);
                E.Pomodoro = new PomodoroSalvo { Nome = p.Nome, Fase = Fase.Foco, DuracaoMs = d, FimMs = agora + d };
                return null;
            case TipoPedido.Cronometro:
                if (E.Cronometro == null) { E.Cronometro = new CronometroSalvo { InicioMs = agora, Rodando = true }; return null; }
                if (E.Cronometro.Rodando) return Lang.T("o cronômetro já está rodando", "the stopwatch is already running");
                AlternarCronometro(agora);
                return null;
            default:
                if (E.Timers.Count >= MaximoTimers) return Lang.T($"no máximo {MaximoTimers} timers", $"{MaximoTimers} timers at most");
                NovoTimer(p.DuracaoMs, p.Nome, agora);
                return null;
        }
    }

    public TimerSalvo NovoTimer(long duracaoMs, string nome, long agora)
    {
        var t = new TimerSalvo { Id = E.ProximoId++, Nome = nome, DuracaoMs = duracaoMs, FimMs = agora + duracaoMs };
        E.Timers.Add(t);
        return t;
    }

    /// <summary>O "+1 min" do alerta: o timer que acabou volta com mais um minuto (mesmo nome).</summary>
    public TimerSalvo MaisUmMinuto(TimerSalvo acabou, long agora) => NovoTimer(60_000, acabou.Nome, agora);

    public void Alternar(TimerSalvo t, long agora)
    {
        if (t.Pausado) { t.FimMs = agora + t.RestanteMs; t.Pausado = false; }
        else { t.RestanteMs = Math.Max(0, t.FimMs - agora); t.Pausado = true; }
    }

    public bool Cancelar(int id) => E.Timers.RemoveAll(t => t.Id == id) > 0;

    public void AlternarPomodoro(long agora)
    {
        var p = E.Pomodoro;
        if (p == null) return;
        if (p.Pausado) { p.FimMs = agora + p.RestanteMs; p.Pausado = false; }
        else { p.RestanteMs = Math.Max(0, p.FimMs - agora); p.Pausado = true; }
    }

    /// <summary>Pula para a próxima fase agora (continua rodando).</summary>
    public void PularFase(long agora, ConfigTempo cfg)
    {
        var p = E.Pomodoro;
        if (p == null) return;
        (p.Fase, p.FocosFeitos) = CicloPomodoro.Proxima(p.Fase, p.FocosFeitos, cfg.FocosAteLonga);
        p.DuracaoMs = CicloPomodoro.DuracaoMs(p.Fase, cfg);
        p.FimMs = agora + p.DuracaoMs;
        p.Pausado = false;
    }

    public void CancelarPomodoro() => E.Pomodoro = null;

    public void AlternarCronometro(long agora)
    {
        var c = E.Cronometro;
        if (c == null) return;
        if (c.Rodando) { c.AcumuladoMs = Math.Max(0, agora - c.InicioMs); c.Rodando = false; }
        else { c.InicioMs = agora - c.AcumuladoMs; c.Rodando = true; }
    }

    public bool Volta(long agora)
    {
        var c = E.Cronometro;
        if (c == null || !c.Rodando) return false;
        if (c.Voltas.Count >= MaximoVoltas) c.Voltas.RemoveAt(0);
        c.Voltas.Add(Decorrido(c, agora));
        return true;
    }

    /// <summary>Duração da volta `i` (a diferença para a anterior).</summary>
    public static long DuracaoDaVolta(CronometroSalvo c, int i) => c.Voltas[i] - (i > 0 ? c.Voltas[i - 1] : 0);

    public void ZerarCronometro() => E.Cronometro = null;

    /// <summary>O que passou do horário até `agora`: tira os timers que acabaram e avança o pomodoro.</summary>
    public List<Evento> Vencer(long agora, ConfigTempo cfg)
    {
        var ev = new List<Evento>();
        foreach (var t in E.Timers.Where(t => !t.Pausado && t.FimMs <= agora).OrderBy(t => t.FimMs).ToList())
        {
            E.Timers.Remove(t);
            ev.Add(new Evento(t, null, 0, 0));
        }
        var p = E.Pomodoro;
        if (p is { Pausado: false } && p.FimMs <= agora)
        {
            int saltos = 0;
            while (p.FimMs <= agora && saltos < 10_000)
            {
                (p.Fase, p.FocosFeitos) = CicloPomodoro.Proxima(p.Fase, p.FocosFeitos, cfg.FocosAteLonga);
                p.DuracaoMs = CicloPomodoro.DuracaoMs(p.Fase, cfg);
                p.FimMs += p.DuracaoMs;
                saltos++;
            }
            // ficou fechada por muito tempo: a fase atual começa agora
            if (p.FimMs <= agora) p.FimMs = agora + p.DuracaoMs;
            ev.Add(new Evento(null, p.Fase, p.FocosFeitos, saltos));
        }
        return ev;
    }

    /// <summary>O próximo horário em que algo acaba (para o alarme), ou null se nada está correndo para um fim.</summary>
    public long? ProximoFim()
    {
        long? m = null;
        foreach (var t in E.Timers)
            if (!t.Pausado && (m == null || t.FimMs < m)) m = t.FimMs;
        if (E.Pomodoro is { Pausado: false } p && (m == null || p.FimMs < m)) m = p.FimMs;
        return m;
    }

    /// <summary>
    /// O que a pílula compacta mostra: o que acaba primeiro entre os que correm; senão o cronômetro rodando; senão
    /// o primeiro parado.
    /// </summary>
    public ItemRef? Principal(long agora)
    {
        ItemRef? melhor = null;
        long menor = long.MaxValue;
        foreach (var t in E.Timers)
            if (!t.Pausado && Restante(t, agora) < menor) { menor = Restante(t, agora); melhor = new ItemRef(TipoItem.Timer, t.Id); }
        if (E.Pomodoro is { Pausado: false } p && Restante(p, agora) < menor) melhor = new ItemRef(TipoItem.Pomodoro, 0);
        if (melhor != null) return melhor;
        if (E.Cronometro is { Rodando: true }) return new ItemRef(TipoItem.Cronometro, 0);
        if (E.Pomodoro != null) return new ItemRef(TipoItem.Pomodoro, 0);
        if (E.Timers.Count > 0) return new ItemRef(TipoItem.Timer, E.Timers[0].Id);
        if (E.Cronometro != null) return new ItemRef(TipoItem.Cronometro, 0);
        return null;
    }

    /// <summary>Os itens na ordem da lista do cartão: pomodoro, cronômetro, timers na ordem em que foram criados.</summary>
    public List<ItemRef> Itens()
    {
        var l = new List<ItemRef>();
        if (E.Pomodoro != null) l.Add(new ItemRef(TipoItem.Pomodoro, 0));
        if (E.Cronometro != null) l.Add(new ItemRef(TipoItem.Cronometro, 0));
        foreach (var t in E.Timers) l.Add(new ItemRef(TipoItem.Timer, t.Id));
        return l;
    }

    /// <summary>Muda quando algum número mostrado na tela muda (o segundo de cada relógio e o que está pausado).</summary>
    public long Assinatura(long agora)
    {
        unchecked
        {
            long h = 17;
            foreach (var t in E.Timers) h = h * 31 + Formato.SegundosContagem(Restante(t, agora)) * 2 + (t.Pausado ? 1 : 0);
            if (E.Pomodoro is { } p) h = h * 31 + Formato.SegundosContagem(Restante(p, agora)) * 2 + (p.Pausado ? 1 : 0);
            if (E.Cronometro is { } c) h = h * 31 + Decorrido(c, agora) / 1000 * 2 + (c.Rodando ? 1 : 0);
            return h;
        }
    }

    /// <summary>Limpa o que veio da config (editada à mão ou de versão antiga): nada negativo, ids sem repetição.</summary>
    public void Sanear()
    {
        E.Timers ??= new List<TimerSalvo>();
        E.Timers.RemoveAll(t => t == null || t.DuracaoMs <= 0 || t.DuracaoMs > Interpretador.MaximoMs || (t.Pausado && t.RestanteMs < 0));
        foreach (var t in E.Timers)
        {
            t.Nome ??= "";
            if (t.Pausado) t.RestanteMs = Math.Min(t.RestanteMs, t.DuracaoMs);
        }
        if (E.Timers.Count > MaximoTimers) E.Timers.RemoveRange(MaximoTimers, E.Timers.Count - MaximoTimers);
        var vistos = new HashSet<int>();
        int max = 0;
        foreach (var t in E.Timers) max = Math.Max(max, t.Id);
        foreach (var t in E.Timers)
        {
            if (t.Id <= 0 || !vistos.Add(t.Id)) { t.Id = ++max; vistos.Add(t.Id); }
        }
        E.ProximoId = Math.Max(E.ProximoId, max + 1);
        if (E.Pomodoro is { } p)
        {
            p.Nome ??= "";
            if (p.DuracaoMs <= 0 || p.RestanteMs < 0 || !Enum.IsDefined(p.Fase)) E.Pomodoro = null;
            else if (p.FocosFeitos < 0) p.FocosFeitos = 0;
        }
        if (E.Cronometro is { } c)
        {
            c.Voltas ??= new List<long>();
            c.Voltas.RemoveAll(v => v < 0);
            if (c.AcumuladoMs < 0) c.AcumuladoMs = 0;
            if (c.Voltas.Count > MaximoVoltas) c.Voltas.RemoveRange(0, c.Voltas.Count - MaximoVoltas);
        }
    }
}
