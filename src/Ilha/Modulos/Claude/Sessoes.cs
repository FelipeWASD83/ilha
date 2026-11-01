using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ilha.Claude;

internal enum Fase { Nenhuma, Pensando, Trabalhando, Esperando, Terminou, Parada }

/// <summary>O que o Claude Code grava em ~/.claude/sessions/&lt;pid&gt;.json (não documentado: tudo é opcional).</summary>
internal sealed record ArquivoDeSessao
{
    public int Pid { get; init; }
    public string SessaoId { get; init; } = "";
    public string Cwd { get; init; } = "";
    public string Nome { get; init; } = "";
    /// <summary>busy, idle ou waiting.</summary>
    public string Status { get; init; } = "";
    public string EsperandoPor { get; init; } = "";
    /// <summary>Hora da última troca de status, em segundos desde 1970.</summary>
    public double StatusEm { get; init; }

    public static ArquivoDeSessao? Ler(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string id = Texto(r, "sessionId");
            if (id.Length == 0) return null;
            double em = Numero(r, "statusUpdatedAt") ?? Numero(r, "updatedAt") ?? 0;
            return new ArquivoDeSessao
            {
                Pid = (int)(Numero(r, "pid") ?? 0),
                SessaoId = id,
                Cwd = Texto(r, "cwd"),
                Nome = Texto(r, "name"),
                Status = Texto(r, "status").ToLowerInvariant(),
                EsperandoPor = Texto(r, "waitingFor"),
                StatusEm = em / 1000.0,
            };
        }
        catch (JsonException) { return null; }
    }

    private static string Texto(JsonElement r, string nome) =>
        r.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double? Numero(JsonElement r, string nome) =>
        r.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}

/// <summary>Um pedido de permissão que chegou pelo pipe (o hook PermissionRequest está esperando a resposta).</summary>
internal sealed class PedidoPermissao
{
    public required string Sessao;
    public required string Ferramenta;
    public string Resumo = "";
    public string Projeto = "";
    /// <summary>Quando chegou (segundos desde 1970): evento da sessão com hora maior que esta encerra o pedido.</summary>
    public double Chegou;
    /// <summary>A sessão entrou em "waiting" depois do pedido (só aí sair de waiting quer dizer que foi respondido).</summary>
    public bool ViuEsperando;
    /// <summary>Responde ao hook: "allow", "deny" ou null (sem decisão: o diálogo normal segue). Chamado uma vez só.</summary>
    public Action<string?> Responder = _ => { };

    private static long _contador;
    /// <summary>Identidade do pedido na assinatura do cartão (dois pedidos da mesma sessão nunca têm o mesmo número).</summary>
    public readonly long Numero = System.Threading.Interlocked.Increment(ref _contador);

    /// <summary>
    /// Quanto o Permitir fica surdo depois de aparecer (no alerta e no cartão): um clique dado para outra coisa, no
    /// instante em que o pedido tomou o lugar dela, não aprova um comando que ninguém leu. O Negar não tem carência.
    /// </summary>
    public const long CarenciaMs = 800;

    public static bool PassouCarencia(long apareceuMs, long agoraMs) => agoraMs - apareceuMs >= CarenciaMs;
}

internal sealed class Sessao
{
    public required string Id;
    public string Projeto = "";
    /// <summary>O nome da sessão no app (cabeçalho do painel), lido do arquivo de sessão; é o alvo da ponte.</summary>
    public string Nome = "";
    public bool Ligada;
    public Fase Fase;
    public string Texto = "";
    public double FaseDesde;
    public double UltimoEvento;
    /// <summary>Hora do pedido que está em andamento (0 = nenhum).</summary>
    public double Inicio;
    public double Duracao;
    public bool Pergunta;
    public string Status = "";
    public double StatusEm;
    public double IdleDesde;
    public int Pid;
    public bool TemArquivo;
    /// <summary>A fase de trabalho é um comando rodando (pode demorar minutos sem evento nenhum).</summary>
    public bool Rodando;
    public readonly List<PedidoPermissao> Pedidos = new();

    public bool Trabalhando => Fase is Fase.Pensando or Fase.Trabalhando;
    public bool EmAndamento => Trabalhando || Fase == Fase.Esperando || Pedidos.Count > 0;
}

/// <summary>O que uma mudança provocou (a atividade transforma isso em alerta, som e redesenho).</summary>
internal sealed class Efeitos
{
    public readonly List<(Sessao Sessao, double Duracao)> Terminaram = new();
    public readonly List<PedidoPermissao> Encerrados = new();
    public readonly List<Sessao> PediuPermissaoSemPonte = new();
    public bool Mudou;

    public void Juntar(Efeitos e)
    {
        Terminaram.AddRange(e.Terminaram);
        Encerrados.AddRange(e.Encerrados);
        PediuPermissaoSemPonte.AddRange(e.PediuPermissaoSemPonte);
        Mudou |= e.Mudou;
    }
}

/// <summary>
/// Máquina de estados das sessões do Claude Code, sem nada de sistema dentro (testável): recebe eventos da fila, o
/// status dos arquivos de sessão, os pedidos de permissão e o relógio, e diz em que fase cada sessão está.
/// Todas as horas em segundos desde 1970 (a mesma escala do "t" da fila).
/// </summary>
internal sealed class Sessoes
{
    private readonly Dictionary<string, Sessao> _s = new();

    /// <summary>
    /// Por quanto tempo a fase "terminou" (ou "parou") fica guardada depois do fim. Ela não aparece na ilha (só o alerta
    /// do fim); serve para a sessão sem arquivo ser esquecida depois e o resumo do fim continuar à mão nesse meio tempo.
    /// </summary>
    public const double GUARDA_DO_FIM = 90;
    /// <summary>Idle sem "terminou" depois disto = parou por Esc ou erro (o Stop não roda nesses casos).</summary>
    public const double ESPERA_DO_IDLE = 3;
    /// <summary>Editar e ler são rápidos: sem evento novo por este tempo, ele voltou a pensar.</summary>
    public const double VOLTA_A_PENSAR = 6;
    /// <summary>Trabalhando sem evento nenhum e sem arquivo de sessão dizendo busy: esquecido (sessão morreu).</summary>
    public const double ESQUECIDO = 15 * 60;

    public IEnumerable<Sessao> Todas => _s.Values;
    public Sessao? Pegar(string id) => _s.TryGetValue(id, out var s) ? s : null;

    private Sessao Garantir(string id)
    {
        if (!_s.TryGetValue(id, out var s)) _s[id] = s = new Sessao { Id = id };
        return s;
    }

    private static void Mudar(Sessao s, Fase f, string texto, double t)
    {
        if (s.Fase != f) s.FaseDesde = t;
        s.Fase = f;
        s.Texto = texto;
    }

    // ------------------------------------------------------------------ eventos da fila

    public Efeitos Aplicar(Evento ev, bool silencioso)
    {
        var ef = new Efeitos();
        if (ev.Sessao.Length == 0) return ef;
        if (ev.Tipo == "fim")
        {
            if (_s.Remove(ev.Sessao, out var morta))
            {
                ef.Encerrados.AddRange(morta.Pedidos);
                ef.Mudou = true;
            }
            return ef;
        }
        var s = Garantir(ev.Sessao);
        if (ev.Projeto.Length > 0) s.Projeto = ev.Projeto;
        s.Ligada = ev.Ligada;
        s.UltimoEvento = Math.Max(s.UltimoEvento, ev.T);

        // qualquer evento da sessão depois do pedido de permissão quer dizer que ele já foi respondido (pelo app),
        // menos a notificação, que é o próprio aviso de "precisa de permissão" chegando uns segundos depois
        if (ev.Tipo != "notificacao" && s.Pedidos.Count > 0)
        {
            foreach (var p in s.Pedidos.Where(p => ev.T > p.Chegou).ToList())
            {
                s.Pedidos.Remove(p);
                ef.Encerrados.Add(p);
            }
            // evento escrito antes do pedido (o PreToolUse da mesma ferramenta) não tira a sessão da espera
            if (s.Pedidos.Count > 0 && ev.T <= s.Pedidos.Max(p => p.Chegou)) { ef.Mudou = true; return ef; }
        }

        switch (ev.Tipo)
        {
            case "ouvindo":
                s.Inicio = ev.T;
                s.Pergunta = false;
                Mudar(s, Fase.Pensando, Lang.T("Pensando", "Thinking"), ev.T);
                break;
            case "lendo":
            case "digitando":
            case "rodando":
                if (s.Inicio <= 0) s.Inicio = ev.T;
                s.Rodando = ev.Tipo == "rodando";
                Mudar(s, Fase.Trabalhando, Neutro.DoEvento(ev), ev.T);
                break;
            case "compactou":
                Mudar(s, Fase.Pensando, Lang.T("Compactando a conversa", "Compacting the conversation"), ev.T);
                if (s.Inicio <= 0) s.Inicio = ev.T;
                break;
            case "falhou":
            case "bug":
                if (s.Trabalhando) s.Texto = Lang.T("Um comando falhou", "A command failed");
                break;
            case "vitoria":
                if (s.Trabalhando) s.Texto = Lang.T("Os testes passaram", "The tests passed");
                break;
            case "notificacao":
                if (ev.Aviso == "permission_prompt")
                {
                    Mudar(s, Fase.Esperando, Lang.T("Pedindo permissão", "Asking for permission"), ev.T);
                    if (s.Pedidos.Count == 0 && !silencioso) ef.PediuPermissaoSemPonte.Add(s);
                }
                else if (ev.Aviso == "idle_prompt")
                {
                    // aviso de 1 minuto parado depois de terminar: o fim já teve o alerta dele, e ficar "esperando" para
                    // sempre depois de cada resposta seria a ilha ocupada o tempo todo
                    if (s.Trabalhando) Mudar(s, Fase.Esperando, Lang.T("Esperando você", "Waiting for you"), ev.T);
                }
                else Mudar(s, Fase.Esperando, Lang.T("Precisa de você", "Needs you"), ev.T);
                break;
            case "terminou":
            {
                bool estava = s.Inicio > 0;
                double dur = ev.Duracao is double d && d > 0 ? d : estava ? Math.Max(0, ev.T - s.Inicio) : 0;
                s.Duracao = dur;
                s.Pergunta = ev.Pergunta;
                s.Inicio = 0;
                s.IdleDesde = 0;
                Mudar(s, Fase.Terminou, ev.Pergunta ? Lang.T("Fez uma pergunta", "Asked a question") : Lang.T("Terminou", "Finished"), ev.T);
                if (!silencioso && estava) ef.Terminaram.Add((s, dur));
                break;
            }
            default:
                // fala, balao, ligou, desligou, modo, inicio, ciume...: nada de texto; só conta como sinal de vida
                break;
        }
        ef.Mudou = true;
        return ef;
    }

    // ------------------------------------------------------------------ arquivos de sessão

    /// <summary>O arquivo de uma sessão mudou (ou foi lido na abertura).</summary>
    public Efeitos AplicarArquivo(ArquivoDeSessao a, double agora)
    {
        var ef = new Efeitos();
        var s = Garantir(a.SessaoId);
        bool novoStatus = a.Status != s.Status || a.StatusEm > s.StatusEm;
        s.TemArquivo = true;
        s.Pid = a.Pid;
        if (a.Nome.Length > 0) s.Nome = a.Nome;
        if (s.Projeto.Length == 0 && a.Cwd.Length > 0) s.Projeto = Path.GetFileName(a.Cwd.TrimEnd('\\', '/'));
        if (!novoStatus) return ef;
        string antes = s.Status;
        s.Status = a.Status;
        s.StatusEm = a.StatusEm;
        double t = a.StatusEm > 0 ? a.StatusEm : agora;
        ef.Mudou = true;
        switch (a.Status)
        {
            case "busy":
                s.IdleDesde = 0;
                // trabalho começou sem o evento da fila (ou antes dele); status velho, anterior à fase, não vale
                if (t > s.FaseDesde && s.Fase is Fase.Nenhuma or Fase.Terminou or Fase.Parada)
                {
                    s.Inicio = t;
                    Mudar(s, Fase.Pensando, Lang.T("Pensando", "Thinking"), t);
                }
                else if (s.Fase == Fase.Esperando && antes == "waiting")
                    Mudar(s, Fase.Pensando, Lang.T("Pensando", "Thinking"), t);
                break;
            case "waiting":
                s.IdleDesde = 0;
                bool permissao = a.EsperandoPor.Contains("permission", StringComparison.OrdinalIgnoreCase)
                    || a.EsperandoPor.Contains("permiss", StringComparison.OrdinalIgnoreCase) || s.Pedidos.Count > 0;
                Mudar(s, Fase.Esperando, permissao ? Lang.T("Pedindo permissão", "Asking for permission") : Lang.T("Esperando você", "Waiting for you"), t);
                foreach (var p in s.Pedidos) if (t >= p.Chegou - 1) p.ViuEsperando = true;
                break;
            case "idle":
                if (t >= s.FaseDesde && (s.Trabalhando || s.Fase == Fase.Esperando)) s.IdleDesde = t;
                break;
        }
        if (antes == "waiting" && a.Status != "waiting")
        {
            // saiu de waiting: o pedido foi respondido (no app ou aqui)
            foreach (var p in s.Pedidos.Where(p => p.ViuEsperando).ToList())
            {
                s.Pedidos.Remove(p);
                ef.Encerrados.Add(p);
            }
        }
        return ef;
    }

    /// <summary>O arquivo da sessão sumiu: o processo fechou.</summary>
    public Efeitos ArquivoSumiu(string sessaoId)
    {
        var ef = new Efeitos();
        if (!_s.Remove(sessaoId, out var s)) return ef;
        ef.Encerrados.AddRange(s.Pedidos);
        ef.Mudou = true;
        return ef;
    }

    // ------------------------------------------------------------------ permissão

    public void PedidoChegou(PedidoPermissao p)
    {
        var s = Garantir(p.Sessao);
        if (s.Projeto.Length == 0) s.Projeto = p.Projeto;
        s.Pedidos.Add(p);
        if (s.Status == "waiting") p.ViuEsperando = true;
        Mudar(s, Fase.Esperando, Lang.T("Pedindo permissão", "Asking for permission"), p.Chegou);
        s.UltimoEvento = Math.Max(s.UltimoEvento, p.Chegou);
    }

    /// <summary>O hook desistiu (a conexão caiu) ou a ilha respondeu: tira o pedido da sessão.</summary>
    public bool PedidoSaiu(PedidoPermissao p)
    {
        var s = Pegar(p.Sessao);
        if (s == null || !s.Pedidos.Remove(p)) return false;
        if (s.Pedidos.Count == 0 && s.Fase == Fase.Esperando && s.Status != "waiting")
            Mudar(s, Fase.Pensando, Lang.T("Pensando", "Thinking"), Math.Max(s.FaseDesde, p.Chegou));
        return true;
    }

    // ------------------------------------------------------------------ relógio

    /// <summary>Resolve o que depende do tempo (idle virando "parou", editar virando pensar, sessão esquecida).</summary>
    public Efeitos Tique(double agora)
    {
        var ef = new Efeitos();
        foreach (var s in _s.Values.ToList())
        {
            if (PrazoDoIdle(s) is double pi && agora >= pi)
            {
                s.Duracao = s.Inicio > 0 ? Math.Max(0, s.IdleDesde - s.Inicio) : 0;
                s.Inicio = 0;
                s.IdleDesde = 0;
                Mudar(s, Fase.Parada, Lang.T("Parou", "Stopped"), agora);
                ef.Mudou = true;
            }
            else if (PrazoDePensar(s) is double pp && agora >= pp)
            {
                Mudar(s, Fase.Pensando, Lang.T("Pensando", "Thinking"), agora);
                ef.Mudou = true;
            }
            if (PrazoDeEsquecer(s) is double pe && agora >= pe)
            {
                s.Inicio = 0;
                Mudar(s, Fase.Nenhuma, "", agora);
                ef.Mudou = true;
            }
            if (PrazoDaJanela(s) is double pj && agora >= pj)
            {
                Mudar(s, Fase.Nenhuma, "", s.FaseDesde);
                ef.Mudou = true;
            }
            // sessão sem nada para mostrar, sem arquivo e sem pedido: esquece
            if (s.Fase == Fase.Nenhuma && !s.TemArquivo && s.Pedidos.Count == 0) _s.Remove(s.Id);
        }
        return ef;
    }

    // as condições de cada prazo ficam num lugar só: o Tique e o ProximoPrazo nunca discordam (um prazo vencido que o
    // Tique não resolve faria o relógio disparar sem parar)
    private static double? PrazoDoIdle(Sessao s) =>
        s.IdleDesde > 0 && s.Status == "idle" && (s.Trabalhando || s.Fase == Fase.Esperando) && s.Pedidos.Count == 0 ? s.IdleDesde + ESPERA_DO_IDLE : null;

    private static double? PrazoDePensar(Sessao s) =>
        s.Fase == Fase.Trabalhando && !s.Rodando && s.Status != "idle" ? Math.Max(s.FaseDesde, s.UltimoEvento) + VOLTA_A_PENSAR : null;

    private static double? PrazoDeEsquecer(Sessao s) =>
        s.Trabalhando && s.Status != "busy" ? Math.Max(s.UltimoEvento, s.FaseDesde) + ESQUECIDO : null;

    private static double? PrazoDaJanela(Sessao s) =>
        s.Fase is Fase.Terminou or Fase.Parada ? s.FaseDesde + GUARDA_DO_FIM : null;

    /// <summary>A próxima hora em que o <see cref="Tique"/> pode mudar alguma coisa (null = nada pendente).</summary>
    public double? ProximoPrazo()
    {
        double? min = null;
        void Talvez(double? t) { if (t != null && (min == null || t < min)) min = t; }
        foreach (var s in _s.Values)
        {
            Talvez(PrazoDoIdle(s));
            Talvez(PrazoDePensar(s));
            Talvez(PrazoDeEsquecer(s));
            Talvez(PrazoDaJanela(s));
        }
        return min;
    }

    /// <summary>Visível na ilha: só trabalhando ou pedindo atenção (permissão, pergunta, esperando por ele).</summary>
    public static bool Visivel(Sessao s) => s.EmAndamento;

    /// <summary>A sessão que a ilha mostra: a que pede permissão primeiro (é a que precisa dele), depois a mais recente.</summary>
    public Sessao? Principal() => Ordenadas().FirstOrDefault();

    public List<Sessao> Ordenadas() => _s.Values
        .Where(Visivel)
        .OrderByDescending(s => s.Pedidos.Count > 0)
        .ThenByDescending(s => Math.Max(s.UltimoEvento, s.FaseDesde))
        .ToList();
}

/// <summary>
/// Texto que pode aparecer na ilha para cada evento. Privacidade: nunca o texto de fala ou balão, e numa sessão com
/// ligada=true só o que o hook escreve de forma mecânica (o nome do arquivo); descrição de comando, que pode vir
/// noutra voz, vira "Rodando um comando".
/// </summary>
internal static class Neutro
{
    private static readonly Regex ArquivoMecanico = new(@"^(Lendo|Editando|Escrevendo) [^\s\\/]{1,80}$", RegexOptions.CultureInvariant);

    public static string DoEvento(Evento ev)
    {
        string d = ev.Detalhe.Trim();
        switch (ev.Tipo)
        {
            case "ouvindo": return Lang.T("Pensando", "Thinking");
            case "compactou": return Lang.T("Compactando a conversa", "Compacting the conversation");
            case "rodando":
                if (ev.Ligada || d.Length == 0) return Lang.T("Rodando um comando", "Running a command");
                return Curto(d);
            case "digitando":
                if (ArquivoMecanico.IsMatch(d) && !d.StartsWith("Lendo", StringComparison.Ordinal)) return NoIdioma(d);
                if (!ev.Ligada && (d.StartsWith("Editando", StringComparison.Ordinal) || d.StartsWith("Escrevendo", StringComparison.Ordinal))) return NoIdioma(Curto(d));
                return Lang.T("Editando arquivos", "Editing files");
            case "lendo":
                if (ArquivoMecanico.IsMatch(d) && d.StartsWith("Lendo", StringComparison.Ordinal)) return NoIdioma(d);
                if (d.StartsWith("Procurando", StringComparison.Ordinal)) return ev.Ligada ? Lang.T("Procurando nos arquivos", "Searching the files") : NoIdioma(Curto(d));
                if (!ev.Ligada && d.StartsWith("Lendo", StringComparison.Ordinal)) return NoIdioma(Curto(d));
                return Lang.T("Lendo arquivos", "Reading files");
            default: return "";
        }
    }

    // the hook writes its details in Portuguese ("Editando x.cs"): in English only the leading verb is translated
    private static readonly (string Pt, string En)[] Verbos = { ("Lendo", "Reading"), ("Editando", "Editing"), ("Escrevendo", "Writing"), ("Procurando", "Searching") };

    private static string NoIdioma(string d)
    {
        if (!Lang.English) return d;
        foreach (var (pt, en) in Verbos)
            if (d.StartsWith(pt + " ", StringComparison.Ordinal)) return en + d[pt.Length..];
        return d;
    }

    private static string Curto(string s)
    {
        s = Regex.Replace(s, @"\s+", " ");
        return s.Length <= 80 ? s : s[..79] + "…";
    }

    /// <summary>"45 s", "3 min 20 s", "1 h 05 min".</summary>
    public static string Duracao(double segundos)
    {
        int s = (int)Math.Round(Math.Max(0, segundos));
        if (s < 60) return $"{s} s";
        if (s < 3600) return s % 60 == 0 ? $"{s / 60} min" : $"{s / 60} min {s % 60} s";
        return $"{s / 3600} h {(s % 3600) / 60:00} min";
    }
}
