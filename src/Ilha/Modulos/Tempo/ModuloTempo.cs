using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ilha.Tempo;

/// <summary>
/// Timers simultâneos, cronômetro com voltas e pomodoro. Ctrl + Alt + R abre o campo: "25m", "1h30", "90s", "1:30",
/// "10" (minutos), "pomodoro", "cron". Aparece também no modo competitivo. O que está rodando fica salvo na config do
/// módulo com o horário de fim absoluto, então fechar e abrir a ilha não perde nada.
/// </summary>
[Modulo("tempo", "Timers e cronômetro", English = "Timers and stopwatch")]
internal sealed class ModuloTempo : Modulo
{
    private const string ATALHO = "timer";
    private ConfigTempo _cfg = new();
    private AtividadeTempo? _atv;

    public override Type? TipoConfig => typeof(ConfigTempo);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigTempo>(cfg);
        _cfg.Sanear();
        _atv = new AtividadeTempo(S, _cfg, () => Salvar(_cfg));
        S.Ilha.Adicionar(_atv);
        _atv.Restaurar();
        var padrao = new Atalho { Mods = Nativo.MOD_CONTROL | Nativo.MOD_ALT, Tecla = 0x52 }; // R de relógio (o Ctrl + Alt + M já era de outro programa no PC dele)
        bool desligado = S.Config().AtalhosModulos.TryGetValue(ATALHO, out var trocado) && trocado.Tecla == 0;
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "tempo", Nome = Lang.T("Timer", "Timer"), Ordem = 14, Abrir = () => _atv?.Atalho(),
            Icone = (dc, palco, r) =>
            {
                // timer ring, two thirds done
                var c = new System.Numerics.Vector2(r.X + r.Width / 2, r.Y + r.Height / 2);
                Icone.Anel(dc, palco.G.Fabrica, c, 9, 3, 1, Cor.Fraco);
                Icone.Anel(dc, palco.G.Fabrica, c, 9, 3, 0.66f, Cor.Laranja);
            },
        });
        if (S.Atalhos.Registrar(ATALHO, padrao, () => _atv?.Atalho()) == null && !desligado)
            S.Avisar(Lang.T("O atalho dos timers já é de outro programa. Troque em \"atalhosModulos\" → \"timer\" na configuração.",
                "The timers shortcut already belongs to another program. Change it in \"atalhosModulos\" → \"timer\" in the settings."));
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        // só os ajustes: o estado em andamento é da ilha (o que está na memória vale mais que o arquivo)
        var novo = Ler<ConfigTempo>(cfg);
        novo.Sanear();
        _cfg.CopiarAjustes(novo);
        _atv?.ConfigMudou();
    }

    public override void Parar()
    {
        _atv?.Encerrar();
        _atv?.Dispose();
        _atv = null;
    }

    // ------------------------------------------------------------------ autoteste (sem janela, sem Serviços)

    // the texts checked here are the Portuguese ones: the language is pinned and the previous choice comes back
    public override string Autoteste()
    {
        string previous = Lang.Choice;
        try
        {
            Lang.Set("pt");
            var r = new StringBuilder(Rodar());
            Lang.Set("en");
            EnglishChecks(r);
            return r.ToString().TrimEnd();
        }
        finally { Lang.Set(previous); }
    }

    private static void EnglishChecks(StringBuilder r)
    {
        void Ok(bool cond, string what) => r.AppendLine((cond ? "ok      " : "FALHOU  ") + what);
        Ok(Interpretador.Ler("abc", out var e1) == null && e1 == "didn't get that", $"English error for \"abc\" ({e1})");
        Ok(Interpretador.Ler("100h", out var e2) == null && e2 == "99 hours at most", $"English error for \"100h\" ({e2})");
        Ok(Interpretador.Ler("5parsecs", out var e3) == null && e3 == "unknown unit \"parsecs\"", $"English error for \"5parsecs\" ({e3})");
        Ok(Formato.Volta(61_999) == "1:01.99", $"lap with a decimal point in English ({Formato.Volta(61_999)})");
        string hora = Formato.Hora(1_800_000_000_000);
        Ok(System.Text.RegularExpressions.Regex.IsMatch(hora, @"^\d{1,2}:\d{2} (AM|PM)$"), $"clock time in 12 h in English ({hora})");
        Ok(CicloPomodoro.NomeDaFase(Fase.PausaLonga) == "long break", "phase name in English");
        var rel = new Relogios(new EstadoTempo());
        var cfg = new ConfigTempo();
        rel.Criar(new Pedido(TipoPedido.Pomodoro, 0, ""), 0, cfg);
        Ok(rel.Criar(new Pedido(TipoPedido.Pomodoro, 0, ""), 0, cfg) == "a pomodoro is already running", "second pomodoro refused in English");
        // what is typed does not depend on the interface language
        var p = Interpretador.Ler("25m chá", out _);
        Ok(p is { Tipo: TipoPedido.Timer, DuracaoMs: 1_500_000, Nome: "chá" }, "\"25m chá\" still works in English");
        Ok(Interpretador.Ler("cronômetro", out _)?.Tipo == TipoPedido.Cronometro, "\"cronômetro\" still works in English");
    }

    private string Rodar()
    {
        var r = new StringBuilder();
        void Ok(bool cond, string o_que) => r.AppendLine((cond ? "ok      " : "FALHOU  ") + o_que);

        // --- interpretador: formas válidas
        var validos = new (string Texto, TipoPedido Tipo, long Ms, string Nome)[]
        {
            ("25m", TipoPedido.Timer, 1_500_000, ""),
            ("1h30", TipoPedido.Timer, 5_400_000, ""),
            ("90s", TipoPedido.Timer, 90_000, ""),
            ("1:30", TipoPedido.Timer, 90_000, ""),
            ("10", TipoPedido.Timer, 600_000, ""),
            ("1:30:00", TipoPedido.Timer, 5_400_000, ""),
            ("0:05", TipoPedido.Timer, 5_000, ""),
            ("25m chá", TipoPedido.Timer, 1_500_000, "chá"),
            ("  25m   chá verde  ", TipoPedido.Timer, 1_500_000, "chá verde"),
            ("chá 25m", TipoPedido.Timer, 1_500_000, "chá"),
            ("lasanha no forno 40 min", TipoPedido.Timer, 2_400_000, "lasanha no forno"),
            ("1h 30m", TipoPedido.Timer, 5_400_000, ""),
            ("1h30m15s", TipoPedido.Timer, 5_415_000, ""),
            ("2m30s", TipoPedido.Timer, 150_000, ""),
            ("2m30", TipoPedido.Timer, 150_000, ""),
            ("1,5h", TipoPedido.Timer, 5_400_000, ""),
            ("1.5m", TipoPedido.Timer, 90_000, ""),
            ("25 min", TipoPedido.Timer, 1_500_000, ""),
            ("25min", TipoPedido.Timer, 1_500_000, ""),
            ("10 minutos estudo", TipoPedido.Timer, 600_000, "estudo"),
            ("2 horas", TipoPedido.Timer, 7_200_000, ""),
            ("45 segundos", TipoPedido.Timer, 45_000, ""),
            ("5 ovo", TipoPedido.Timer, 300_000, "ovo"),
            ("1:30 macarrão", TipoPedido.Timer, 90_000, "macarrão"),
            ("99h", TipoPedido.Timer, 99L * 3_600_000, ""),
            ("pomodoro", TipoPedido.Pomodoro, 0, ""),
            ("Pomodoro estudo", TipoPedido.Pomodoro, 0, "estudo"),
            ("cronômetro", TipoPedido.Cronometro, 0, ""),
            ("cronometro", TipoPedido.Cronometro, 0, ""),
            ("CRON", TipoPedido.Cronometro, 0, ""),
            ("cron", TipoPedido.Cronometro, 0, ""),
            // English, understood whatever the interface language
            ("2 hours", TipoPedido.Timer, 7_200_000, ""),
            ("1 hour", TipoPedido.Timer, 3_600_000, ""),
            ("45 seconds", TipoPedido.Timer, 45_000, ""),
            ("1 second", TipoPedido.Timer, 1_000, ""),
            ("90 secs", TipoPedido.Timer, 90_000, ""),
            ("1 minute", TipoPedido.Timer, 60_000, ""),
            ("10 minutes study", TipoPedido.Timer, 600_000, "study"),
            ("1 hour 30 minutes", TipoPedido.Timer, 5_400_000, ""),
            ("25m tea", TipoPedido.Timer, 1_500_000, "tea"),
            ("tea 25 min", TipoPedido.Timer, 1_500_000, "tea"),
            ("timer 5m", TipoPedido.Timer, 300_000, ""),
            ("Timer 5m eggs", TipoPedido.Timer, 300_000, "eggs"),
            ("timer 1:30", TipoPedido.Timer, 90_000, ""),
            ("stopwatch", TipoPedido.Cronometro, 0, ""),
            ("Stopwatch", TipoPedido.Cronometro, 0, ""),
            ("focus", TipoPedido.Pomodoro, 0, ""),
            ("focus study", TipoPedido.Pomodoro, 0, "study"),
            ("foco", TipoPedido.Pomodoro, 0, ""),
            ("focus 25m", TipoPedido.Timer, 1_500_000, "focus"),
        };
        foreach (var v in validos)
        {
            var p = Interpretador.Ler(v.Texto, out var erro);
            Ok(p != null && p.Tipo == v.Tipo && p.DuracaoMs == v.Ms && p.Nome == v.Nome,
                $"lê \"{v.Texto}\" -> {v.Tipo} {v.Ms} ms \"{v.Nome}\"" + (p == null ? $" (deu erro: {erro})" : $" (deu {p.Tipo} {p.DuracaoMs} \"{p.Nome}\")"));
        }
        // nome comprido é cortado
        var longo = Interpretador.Ler("5m " + new string('a', 80), out _);
        Ok(longo != null && longo.Nome.Length == Interpretador.NomeMaximo, "nome comprido cortado em 40 letras");

        // --- interpretador: formas inválidas (todas precisam dar erro com mensagem)
        var invalidos = new[]
        {
            "", "   ", "abc", "0", "0m", "0:00", "-5", "25x", "25mchá", "1:75", "1:5", "1:30:75", "1:2:3:4", "a:b",
            "100h", "5m5m", "30s 1m", "1m30s 5", "h", "min", "0,4s", "1234567", "1,2345h", "25.", "chá",
            "timer", "timer tea", "hours", "5parsecs", "1 hour 2 hours",
        };
        foreach (var s in invalidos)
        {
            var p = Interpretador.Ler(s, out var erro);
            Ok(p == null && erro.Length > 0, $"recusa \"{s}\"" + (p != null ? $" (aceitou: {p.Tipo} {p.DuracaoMs} \"{p.Nome}\")" : $" ({erro})"));
        }

        // --- formatação
        var formatos = new (string Obtido, string Esperado)[]
        {
            (Formato.Contagem(1_500_000), "25:00"),
            (Formato.Contagem(1), "0:01"),
            (Formato.Contagem(1_000), "0:01"),
            (Formato.Contagem(1_001), "0:02"),
            (Formato.Contagem(0), "0:00"),
            (Formato.Contagem(-50), "0:00"),
            (Formato.Contagem(3_600_000), "1:00:00"),
            (Formato.Contagem(3_599_001), "1:00:00"),
            (Formato.Contagem(3_599_000), "59:59"),
            (Formato.Contagem(36_000_000 + 61_000), "10:01:01"),
            (Formato.Decorrido(61_999), "1:01"),
            (Formato.Decorrido(999), "0:00"),
            (Formato.Decorrido(3_725_000), "1:02:05"),
            (Formato.Volta(61_999), "1:01,99"),
            (Formato.Volta(5), "0:00,00"),
            (Formato.Volta(12_340), "0:12,34"),
            (Formato.Duracao(5_400_000), "1 h 30 min"),
            (Formato.Duracao(90_000), "1 min 30 s"),
            (Formato.Duracao(1_500_000), "25 min"),
            (Formato.Duracao(45_000), "45 s"),
            (Formato.Duracao(7_200_000), "2 h"),
            (Formato.Duracao(3_661_000), "1 h 1 min 1 s"),
            (Formato.Maiuscula("chá"), "Chá"),
            (Formato.Maiuscula("ônibus"), "Ônibus"),
        };
        foreach (var (obtido, esperado) in formatos) Ok(obtido == esperado, $"formata \"{esperado}\"" + (obtido == esperado ? "" : $" (deu \"{obtido}\")"));

        // --- ciclo do pomodoro
        var cfg = new ConfigTempo();
        var fases = new List<Fase>();
        var (f, feitos) = (Fase.Foco, 0);
        for (int i = 0; i < 9; i++) { (f, feitos) = CicloPomodoro.Proxima(f, feitos, 4); fases.Add(f); }
        var esperadas = new[] { Fase.Pausa, Fase.Foco, Fase.Pausa, Fase.Foco, Fase.Pausa, Fase.Foco, Fase.PausaLonga, Fase.Foco, Fase.Pausa };
        Ok(fases.SequenceEqual(esperadas), "pomodoro 25/5 com a longa no 4º foco: " + string.Join(" ", fases));
        Ok(feitos == 5, $"focos contados ({feitos})");
        (f, feitos) = (Fase.Foco, 0);
        var com2 = new List<Fase>();
        for (int i = 0; i < 4; i++) { (f, feitos) = CicloPomodoro.Proxima(f, feitos, 2); com2.Add(f); }
        Ok(com2.SequenceEqual(new[] { Fase.Pausa, Fase.Foco, Fase.PausaLonga, Fase.Foco }), "pausa longa a cada 2 focos (configurável)");
        Ok(CicloPomodoro.DuracaoMs(Fase.Foco, cfg) == 1_500_000 && CicloPomodoro.DuracaoMs(Fase.Pausa, cfg) == 300_000
            && CicloPomodoro.DuracaoMs(Fase.PausaLonga, cfg) == 900_000, "durações padrão 25, 5 e 15 min");
        Ok(CicloPomodoro.NumeroDoFoco(Fase.Foco, 0, 4) == 1 && CicloPomodoro.NumeroDoFoco(Fase.Foco, 5, 4) == 2
            && CicloPomodoro.NumeroDoFoco(Fase.Pausa, 1, 4) == 1 && CicloPomodoro.NumeroDoFoco(Fase.PausaLonga, 4, 4) == 4, "número do foco na rodada");

        const long T0 = 1_800_000_000_000;
        const long MIN = 60_000;
        var rel = new Relogios(new EstadoTempo());
        // ausente vira um valor que reprova as conferências, em vez de exceção
        PomodoroSalvo Po() => rel.E.Pomodoro ?? new PomodoroSalvo { FimMs = -1, DuracaoMs = -1 };
        CronometroSalvo Cr() => rel.E.Cronometro ?? new CronometroSalvo { InicioMs = -1 };
        Ok(rel.Criar(new Pedido(TipoPedido.Pomodoro, 0, ""), T0, cfg) == null && Po().FimMs == T0 + 25 * MIN, "pomodoro começa no foco de 25 min");
        Ok(rel.Criar(new Pedido(TipoPedido.Pomodoro, 0, ""), T0, cfg) != null, "segundo pomodoro recusado");
        var ev = rel.Vencer(T0 + 25 * MIN - 1, cfg);
        Ok(ev.Count == 0 && Po().Fase == Fase.Foco, "foco não acaba 1 ms antes");
        ev = rel.Vencer(T0 + 25 * MIN, cfg);
        Ok(ev.Count == 1 && ev[0].NovaFase == Fase.Pausa && ev[0].Saltos == 1 && Po().FimMs == T0 + 30 * MIN, "foco vira pausa de 5 min sozinho");
        // ilha fechada por 30 min: foco 2 (30..55) e pausa 2 (55..60): está na pausa 2
        ev = rel.Vencer(T0 + 56 * MIN, cfg);
        Ok(ev.Count == 1 && ev[0].Saltos == 2 && Po().Fase == Fase.Pausa && Po().FocosFeitos == 2
            && Po().FimMs == T0 + 60 * MIN, "alcança as fases que passaram com a ilha fechada");
        rel.AlternarPomodoro(T0 + 57 * MIN);
        Ok(Po().Pausado && Relogios.Restante(Po(), T0 + 90 * MIN) == 3 * MIN && rel.Vencer(T0 + 90 * MIN, cfg).Count == 0, "pomodoro pausado não anda");
        rel.AlternarPomodoro(T0 + 90 * MIN);
        Ok(Po().FimMs == T0 + 93 * MIN, "pomodoro continua de onde parou");
        rel.PularFase(T0 + 91 * MIN, cfg);
        Ok(Po().Fase == Fase.Foco && Po().FimMs == T0 + 116 * MIN, "pular vai para o próximo foco");
        Po().FocosFeitos = 3;
        rel.PularFase(T0 + 100 * MIN, cfg);
        Ok(Po().Fase == Fase.PausaLonga && Po().FimMs == T0 + 115 * MIN, "quarto foco leva à pausa longa de 15 min");
        // fechada por um ano: não trava no laço e a fase atual começa agora
        rel.Vencer(T0 + 365L * 24 * 60 * MIN, cfg);
        Ok(Po().FimMs > T0 + 365L * 24 * 60 * MIN, "ilha fechada por muito tempo não trava o pomodoro");
        rel.CancelarPomodoro();
        Ok(rel.Vazio, "pomodoro cancelado");

        // --- timers
        var t1 = rel.NovoTimer(25 * MIN, "chá", T0);
        var t2 = rel.NovoTimer(5 * MIN, "", T0);
        Ok(rel.ProximoFim() == T0 + 5 * MIN && rel.Principal(T0) == new ItemRef(TipoItem.Timer, t2.Id), "o que acaba primeiro aparece na pílula");
        rel.Alternar(t2, T0 + MIN);
        Ok(t2.Pausado && t2.RestanteMs == 4 * MIN && rel.ProximoFim() == T0 + 25 * MIN && rel.Principal(T0 + MIN) == new ItemRef(TipoItem.Timer, t1.Id), "pausado sai da conta do próximo fim");
        rel.Alternar(t2, T0 + 10 * MIN);
        Ok(!t2.Pausado && t2.FimMs == T0 + 14 * MIN, "continuar refaz o horário de fim");
        ev = rel.Vencer(T0 + 14 * MIN, cfg);
        Ok(ev.Count == 1 && ev[0].Timer == t2 && rel.E.Timers.Count == 1, "timer acabado sai da lista e vira evento");
        var t3 = rel.MaisUmMinuto(t2, T0 + 15 * MIN);
        Ok(t3.DuracaoMs == MIN && t3.FimMs == T0 + 16 * MIN && t3.Id != t2.Id, "+1 min cria outro timer de 1 minuto");
        Ok(Math.Abs(Relogios.Progresso(t1, T0 + 5 * MIN) - 0.2f) < 1e-4, "progresso do anel");
        ev = rel.Vencer(T0 + 30 * MIN, cfg);
        Ok(ev.Count == 2 && ev[0].Timer == t3 && ev[1].Timer == t1, "vários acabados saem na ordem em que acabaram");
        Ok(rel.Cancelar(999) == false, "cancelar id que não existe não faz nada");
        for (int i = 0; i < Relogios.MaximoTimers; i++) rel.NovoTimer(MIN, "", T0);
        Ok(rel.Criar(new Pedido(TipoPedido.Timer, MIN, ""), T0, cfg) != null, $"limite de {Relogios.MaximoTimers} timers");
        rel.E.Timers.Clear();

        // --- cronômetro
        Ok(rel.Criar(new Pedido(TipoPedido.Cronometro, 0, ""), T0, cfg) == null, "cronômetro começa");
        Ok(rel.Volta(T0 + 5_000) && rel.Volta(T0 + 12_340), "duas voltas");
        rel.AlternarCronometro(T0 + 15_000);
        Ok(!rel.Volta(T0 + 16_000), "pausado não marca volta");
        Ok(Relogios.Decorrido(Cr(), T0 + 60_000) == 15_000, "pausado não anda");
        Ok(rel.Criar(new Pedido(TipoPedido.Cronometro, 0, ""), T0 + 60_000, cfg) == null && Cr().Rodando, "\"cron\" de novo continua o pausado");
        Ok(Relogios.Decorrido(Cr(), T0 + 62_000) == 17_000, "continua de onde parou");
        Ok(rel.Criar(new Pedido(TipoPedido.Cronometro, 0, ""), T0, cfg) != null, "\"cron\" com ele rodando avisa");
        Ok(Relogios.DuracaoDaVolta(Cr(), 0) == 5_000 && Relogios.DuracaoDaVolta(Cr(), 1) == 7_340, "duração de cada volta");
        Ok(rel.ProximoFim() == null && rel.AlgoRodando, "cronômetro não marca alarme, mas conta como rodando");
        long a1 = rel.Assinatura(T0 + 62_000), a2 = rel.Assinatura(T0 + 62_900), a3 = rel.Assinatura(T0 + 63_000);
        Ok(a1 == a2 && a2 != a3, "redesenha só quando o segundo mostrado muda");

        // --- persistência: serializa como a ilha grava e lê de volta
        rel.NovoTimer(30 * MIN, "chá", T0);
        var tp = rel.NovoTimer(10 * MIN, "pausado", T0);
        rel.Alternar(tp, T0 + 2 * MIN);
        rel.Criar(new Pedido(TipoPedido.Pomodoro, 0, "estudo"), T0, cfg);
        rel.Vencer(T0 + 26 * MIN, cfg);
        var salvo = new ConfigTempo { FocoMin = 50, PausaMin = 10, Estado = rel.E };
        string json = JsonSerializer.Serialize(salvo, Config.Json);
        var lido = JsonSerializer.Deserialize<ConfigTempo>(json, Config.Json)!;
        lido.Sanear();
        var e2 = lido.Estado;
        Ok(lido.FocoMin == 50 && lido.PausaMin == 10 && lido.FocosAteLonga == 4, "ajustes voltam");
        Ok(e2.Timers.Count == rel.E.Timers.Count && e2.Timers.Zip(rel.E.Timers).All(z =>
                z.First.Id == z.Second.Id && z.First.Nome == z.Second.Nome && z.First.DuracaoMs == z.Second.DuracaoMs
                && z.First.FimMs == z.Second.FimMs && z.First.RestanteMs == z.Second.RestanteMs && z.First.Pausado == z.Second.Pausado),
            "timers voltam com o horário de fim absoluto e o pausado");
        Ok(e2.Pomodoro is { Fase: Fase.Pausa, FocosFeitos: 1, Nome: "estudo" } pe && pe.FimMs == Po().FimMs && pe.DuracaoMs == 5 * MIN, "pomodoro volta na fase certa");
        Ok(json.Contains("\"fase\": \"Pausa\""), "fase gravada por nome");
        Ok(e2.Cronometro is { } ce && ce.Voltas.SequenceEqual(Cr().Voltas) && ce.InicioMs == Cr().InicioMs && ce.Rodando, "cronômetro volta com as voltas");
        Ok(e2.ProximoId == rel.E.ProximoId, "próximo id volta");
        var rel2 = new Relogios(e2);
        ev = rel2.Vencer(T0 + 40 * MIN, lido);
        Ok(ev.Any(x => x.Timer?.Nome == "chá") && e2.Timers.Any(t => t.Nome == "pausado"), "depois de reabrir, o que acabou vira alerta e o pausado fica");

        // config velha, vazia ou estragada não derruba nada
        var vazia = JsonSerializer.Deserialize<ConfigTempo>("{\"ligado\": true}", Config.Json)!;
        vazia.Sanear();
        Ok(vazia.FocoMin == 25 && vazia.Estado.Timers.Count == 0 && vazia.Estado.Pomodoro == null, "config sem nada usa o padrão");
        var nula = JsonSerializer.Deserialize<ConfigTempo>("{\"estado\": null, \"focoMin\": -3, \"focosAteLonga\": 0}", Config.Json)!;
        nula.Sanear();
        Ok(nula.Estado != null && nula.FocoMin == 1 && nula.FocosAteLonga == 1, "valores fora do limite e estado nulo são corrigidos");
        var estragada = JsonSerializer.Deserialize<ConfigTempo>(
            "{\"estado\": {\"timers\": [{\"id\": 3, \"duracaoMs\": 60000, \"fimMs\": 5}, {\"id\": 3, \"duracaoMs\": 60000}, {\"id\": 1, \"duracaoMs\": -5}]," +
            " \"pomodoro\": {\"fase\": \"Foco\", \"duracaoMs\": 0}, \"cronometro\": {\"voltas\": [100, -2], \"acumuladoMs\": -9}, \"proximoId\": 1}}", Config.Json)!;
        estragada.Sanear();
        var re = new Relogios(estragada.Estado);
        re.Sanear();
        Ok(re.E.Timers.Count == 2 && re.E.Timers.Select(t => t.Id).Distinct().Count() == 2 && re.E.ProximoId > re.E.Timers.Max(t => t.Id),
            "timer inválido sai e id repetido é trocado");
        Ok(re.E.Pomodoro == null && re.E.Cronometro!.Voltas.SequenceEqual(new long[] { 100 }) && re.E.Cronometro.AcumuladoMs == 0, "pomodoro e cronômetro estragados são limpos");
        try
        {
            JsonSerializer.Deserialize<ConfigTempo>("{\"estado\": {\"pomodoro\": {\"fase\": \"Almoco\"}}}", Config.Json);
            Ok(false, "fase desconhecida deveria dar erro de leitura (o Ler do módulo cai no padrão)");
        }
        catch (JsonException) { Ok(true, "fase desconhecida dá erro de leitura (o Ler do módulo cai no padrão)"); }

        // --- o som do alarme vem junto com a ilha (só lê se o arquivo existe)
        var som = Sons.Arquivo("timer");
        Ok(som != null && som.EndsWith("timer.wav", StringComparison.OrdinalIgnoreCase), "som \"timer\" encontrado" + (som == null ? " (faltando)" : ""));

        // --- vão da hora na pílula, entre o anel e o tempo
        var m1 = AtividadeTempo.MeioLivre(40, 0);
        Ok(m1 is { } v1 && v1.De >= AtividadeTempo.FIM_DO_ANEL && v1.Ate <= AtividadeTempo.COMPACTA_L - 40, $"meio livre com \"24:59\" fica entre o anel e o tempo ({m1})");
        var m2 = AtividadeTempo.MeioLivre(55, 14);
        Ok(m2 is { } v2 && m1 is { } w1 && v2.Ate < w1.Ate && v2.Ate <= AtividadeTempo.COMPACTA_L - 55 - AtividadeTempo.VAO_MAIS - 14,
            $"\"+N\" e hora no tempo encolhem o meio livre ({m2})");
        Ok(AtividadeTempo.MeioLivre(140, 0) == null, "tempo encostando no anel: sem meio livre");

        return r.ToString();
    }
}
