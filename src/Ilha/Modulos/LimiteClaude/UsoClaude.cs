using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Ilha.LimiteClaude;

/// <summary>Uma janela do limite do plano: a de 5 horas ou a semanal.</summary>
internal sealed record Janela(string Id, double Porcentagem, double? VoltaEm)
{
    public const string SESSAO = "five_hour";
    public const string SEMANA = "seven_day";

    public bool Semanal => Id == SEMANA;
    /// <summary>Duração de cada janela: serve de reset estimado quando o arquivo não traz o resets_at.</summary>
    public double Duracao => Semanal ? 7 * 86400 : 5 * 3600;
    public bool Venceu(double agora) => VoltaEm is double v && agora >= v;
}

/// <summary>
/// O que o ferramentas/statusline_uso.py gravou em ~/.ilha/uso-claude.json: o rate_limits da status line do Claude
/// Code (porcentagem de 0 a 100 e resets_at em segundos desde 1970) e a hora da coleta.
/// </summary>
internal sealed record LeituraDoUso(double ColetadoEm, Janela? Sessao, Janela? Semana)
{
    /// <summary>Lê o arquivo (null = não é o formato). `arquivoEm` vale quando falta o coletado_em.</summary>
    public static LeituraDoUso? Ler(string json, double arquivoEm)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            double coletado = Numero(r, "coletado_em") is double c && c > 0 ? c : arquivoEm;
            if (!r.TryGetProperty("rate_limits", out var rl) || rl.ValueKind != JsonValueKind.Object) return new LeituraDoUso(coletado, null, null);
            return new LeituraDoUso(coletado, LerJanela(rl, Janela.SESSAO), LerJanela(rl, Janela.SEMANA));
        }
        catch (JsonException) { return null; }
    }

    private static Janela? LerJanela(JsonElement rl, string id)
    {
        if (!rl.TryGetProperty(id, out var j) || j.ValueKind != JsonValueKind.Object) return null;
        if (Numero(j, "used_percentage") is not double p || !double.IsFinite(p)) return null;
        double? volta = Numero(j, "resets_at") is double v && v > 0 ? v : null;
        // a documentação diz segundos; milissegundos (13 dígitos) seriam um formato novo: converte em vez de errar por mil anos
        if (volta > 1e11) volta /= 1000;
        return new Janela(id, Math.Clamp(p, 0, 100), volta);
    }

    private static double? Numero(JsonElement o, string nome)
    {
        if (!o.TryGetProperty(nome, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }

    /// <summary>As janelas que ainda valem agora (a que passou do reset sai: o número dela é de uma janela que acabou).</summary>
    public IEnumerable<Janela> Validas(double agora) =>
        new[] { Sessao, Semana }.Where(j => j != null && !j.Venceu(agora)).Select(j => j!);

    public Janela? SessaoValida(double agora) => Sessao is { } j && !j.Venceu(agora) ? j : null;
    public Janela? SemanaValida(double agora) => Semana is { } j && !j.Venceu(agora) ? j : null;

    /// <summary>O próximo reset ainda no futuro (o módulo agenda um relógio para ele; null = nenhum).</summary>
    public double? ProximoReset(double agora) =>
        new[] { Sessao?.VoltaEm, Semana?.VoltaEm }.Where(v => v is double x && x > agora).Min();
}

/// <summary>Os textos da ilha: porcentagem, "volta às 14:30", "atualizado há 12 min", o detalhe do menu.</summary>
internal static class TextoDoUso
{
    public static string Porcento(double p) => $"{Math.Round(Math.Clamp(p, 0, 100)):0}%";

    /// <summary>The valid window closer to its limit (the higher percentage); the session wins a tie. Null = none.</summary>
    public static Janela? MaisApertada(LeituraDoUso? l, double agora)
    {
        var s = l?.SessaoValida(agora);
        var w = l?.SemanaValida(agora);
        if (s == null) return w;
        if (w == null) return s;
        return w.Porcentagem > s.Porcentagem ? w : s;
    }

    public static DateTime Local(double epoch) => DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000)).ToLocalTime().DateTime;

    /// <summary>"volta às 14:30"; a semanal com o dia: "volta sexta às 09:00" ("hoje" e "amanhã" quando é o caso).</summary>
    public static string Volta(Janela j, DateTime agora)
    {
        if (j.VoltaEm is not double v) return Lang.T("sem horário de reset", "no reset time");
        return Lang.T("volta ", "resets ") + Quando(Local(v), agora, j.Semanal);
    }

    public static string Quando(DateTime quando, DateTime agora, bool semanal)
    {
        string hora = quando.ToString(Lang.T("HH:mm", "h:mm tt"), Lang.Culture);
        string as_ = Lang.T(" às ", " at ");
        int dias = (quando.Date - agora.Date).Days;
        if (dias == 0) return semanal ? Lang.T("hoje", "today") + as_ + hora : as_.TrimStart() + hora;
        if (dias == 1) return Lang.T("amanhã", "tomorrow") + as_ + hora;
        if (dias > 1 && dias < 7) return Dia(quando) + as_ + hora;
        return quando.ToString(Lang.T("d/M", "M/d"), Lang.Culture) + as_ + hora;
    }

    /// <summary>"sexta", "sábado" (sem o "-feira"); "Friday" em inglês.</summary>
    public static string Dia(DateTime d) => Lang.Culture.DateTimeFormat.GetDayName(d.DayOfWeek).Split('-')[0];

    /// <summary>"atualizado agora", "atualizado há 12 min", "atualizado há 3 h", "atualizado há 2 dias".</summary>
    public static string Idade(double segundos)
    {
        if (!(segundos >= 60)) return Lang.T("atualizado agora", "updated just now");
        int min = (int)(segundos / 60);
        if (min < 60) return Lang.T($"atualizado há {min} min", $"updated {min} min ago");
        int h = min / 60;
        if (h < 24) return Lang.T($"atualizado há {h} h", $"updated {h} h ago");
        int d = h / 24;
        return d == 1 ? Lang.T("atualizado há 1 dia", "updated 1 day ago") : Lang.T($"atualizado há {d} dias", $"updated {d} days ago");
    }

    /// <summary>A linha pequena da entrada do menu: "34% · sem. 12%" (null = nada para mostrar).</summary>
    public static string? Detalhe(LeituraDoUso? l, double agora)
    {
        if (l == null) return null;
        var s = l.SessaoValida(agora);
        var w = l.SemanaValida(agora);
        string sem = Lang.T("sem. ", "wk ");
        if (s != null && w != null) return $"{Porcento(s.Porcentagem)} · {sem}{Porcento(w.Porcentagem)}";
        if (s != null) return Porcento(s.Porcentagem);
        if (w != null) return sem + Porcento(w.Porcentagem);
        return null;
    }
}

/// <summary>
/// Um aviso por janela e por limiar: passou de 80% na janela que volta às 14:30, avisa uma vez; a próxima janela
/// avisa de novo. Sem nada de sistema dentro (testável).
/// </summary>
internal sealed class AvisosDoLimite
{
    // (janela, limiar) -> o reset da janela que já avisou
    private readonly Dictionary<(string, int), double> _avisado = new();
    // o reset pode vir alguns segundos diferente entre respostas da mesma janela
    public const double TOLERANCIA = 1800;

    /// <summary>Os limiares ligados, do menor para o maior (0 desliga; o segundo só vale acima do primeiro).</summary>
    public static int[] Limiares(int primeiro, int segundo)
    {
        var l = new List<int>();
        if (primeiro > 0) l.Add(Math.Min(primeiro, 100));
        if (segundo > 0 && (l.Count == 0 || segundo > l[0])) l.Add(Math.Min(segundo, 100));
        return l.Distinct().OrderBy(x => x).ToArray();
    }

    private static double Identidade(Janela j, double coletado) => j.VoltaEm ?? coletado + j.Duracao;

    // sem resets_at a identidade é estimada pela coleta e anda com ela: vale como a mesma janela por uma janela inteira
    private bool JaAvisou(Janela j, int limiar, double coletado) =>
        _avisado.TryGetValue((j.Id, limiar), out var ate) && Identidade(j, coletado) <= ate + (j.VoltaEm == null ? j.Duracao : TOLERANCIA);

    /// <summary>
    /// O aviso que cada janela pede agora: o maior limiar passado que ainda não avisou nesta janela (pulou de 70 para
    /// 96: um aviso só, o de 95).
    /// </summary>
    public List<(Janela Janela, int Limiar)> Pendentes(LeituraDoUso l, double agora, int[] limiares)
    {
        var r = new List<(Janela, int)>();
        foreach (var j in l.Validas(agora))
        {
            int? maior = null;
            foreach (int x in limiares) if (j.Porcentagem >= x) maior = x;
            if (maior is int m && !JaAvisou(j, m, l.ColetadoEm)) r.Add((j, m));
        }
        return r;
    }

    /// <summary>Avisou (ou a leitura do começo, que não avisa): marca este limiar e os de baixo nesta janela.</summary>
    public void Marcar(Janela j, int limiar, double coletado, int[] limiares)
    {
        double id = Identidade(j, coletado);
        foreach (int x in limiares.Where(x => x <= limiar)) _avisado[(j.Id, x)] = id;
    }

    public void MarcarTudo(LeituraDoUso l, double agora, int[] limiares)
    {
        foreach (var (j, m) in Pendentes(l, agora, limiares)) Marcar(j, m, l.ColetadoEm, limiares);
    }
}
