using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Ilha.Uso;

/// <summary>
/// Uso do PC: CPU, RAM, GPU NVIDIA (uso, temperatura, VRAM) e rede. Não fica fixo na ilha: aparece pelo atalho ou
/// quando algo passa de um limite, e some depois de 30 s com tudo abaixo. Lê a cada 1 s só com a atividade na tela;
/// fora dela, uma checagem dos limites a cada 5 s (microssegundos de trabalho). No modo competitivo não lê nada.
/// </summary>
[Modulo("uso", "Uso do PC")]
internal sealed class ModuloUso : Modulo
{
    private ConfigUso _cfg = new();
    private readonly EstadoUso _e = new();
    private readonly Sensores _sens = new();
    private readonly System.Windows.Forms.Timer _relogio = new();
    private AtividadeUso? _atv;
    private Nvml? _nvml;
    private bool _parado, _competitivo, _iniciandoGpu;
    private double _ultimaAmostra = double.NaN, _ultimaBase = double.NaN, _ultimaTentativaGpu = double.NaN;

    public override Type? TipoConfig => typeof(ConfigUso);

    private static double Agora => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = ConfigUso.Normalizar(Ler<ConfigUso>(cfg));
        AplicarLimites();
        _atv = new AtividadeUso(_e);
        _atv.CenasMudaram += Ritmo;
        S.Ilha.Adicionar(_atv);
        S.Atalhos.Registrar("uso", new Atalho { Mods = Nativo.MOD_CONTROL | Nativo.MOD_ALT, Tecla = 0x55 }, AlternarPeloAtalho);
        _relogio.Tick += (_, _) => Tique();
        IniciarGpu();
        Ritmo();
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = ConfigUso.Normalizar(Ler<ConfigUso>(cfg));
        AplicarLimites();
        if (!_cfg.AvisarLimites) _e.Limites.Zerar();
        Sincronizar();
        Ritmo();
    }

    public override void Contexto(Contexto ctx)
    {
        if (ctx.Competitivo == _competitivo) return;
        _competitivo = ctx.Competitivo;
        // no jogo competitivo a atividade não aparece: nem lê, e sai do estourado para não voltar velha depois
        if (_competitivo) _e.Limites.Zerar();
        Sincronizar();
        Ritmo();
    }

    public override void Parar()
    {
        _parado = true;
        _relogio.Stop();
        _relogio.Dispose();
        _e.Limites.Zerar();
        Sincronizar();
        _sens.Gpu = null;
        _nvml?.Dispose();
        _nvml = null;
    }

    private void AlternarPeloAtalho()
    {
        if (_parado || _atv == null) return;
        if (S.Ilha.Expandida == _atv) S.Ilha.Recolher();
        else S.Ilha.Expandir(_atv);
    }

    private void AplicarLimites()
    {
        _e.Limites.Definir(new[]
        {
            new Limites.Regra(Medida.GpuTemp, _cfg.LimiteTemperaturaGpu, 0),
            new Limites.Regra(Medida.Cpu, _cfg.LimiteCpu, _cfg.SegundosCpu),
            new Limites.Regra(Medida.Gpu, _cfg.LimiteGpu, _cfg.SegundosGpu),
            new Limites.Regra(Medida.Ram, _cfg.LimiteRam, 0),
        });
        _e.Limites.SegundosParaSumir = _cfg.SegundosParaSumir;
        _e.Limites.Lacuna = Math.Max(20, _cfg.IntervaloChecagem * 3);
    }

    // ------------------------------------------------------------------ ritmo da leitura

    /// <summary>1 s com a atividade na tela; o intervalo da checagem dos limites fora dela; parado no competitivo.</summary>
    private void Ritmo()
    {
        if (_parado || _competitivo || _atv == null) { _relogio.Stop(); return; }
        bool naTela = _atv.Cenas > 0;
        if (!naTela && !_cfg.AvisarLimites) { _relogio.Stop(); return; }
        int ms = naTela ? 1000 : _cfg.IntervaloChecagem * 1000;
        double agora = Agora;
        // apareceu depois de um tempo sem ler: base agora e primeira leitura em 300 ms, em vez de mostrar a média
        // de vários segundos (ou nada) por 1 s inteiro
        if (naTela && !(agora - _ultimaAmostra < 1.5) && !(agora - _ultimaBase < 1.5))
        {
            _sens.Base();
            _ultimaBase = agora;
            ms = 300;
        }
        if (_relogio.Enabled && _relogio.Interval == ms) return;
        if (_relogio.Enabled && ms > _relogio.Interval && _relogio.Interval == 300) return; // deixa a primeira leitura acontecer
        _relogio.Interval = ms;
        _relogio.Start();
    }

    private void Tique()
    {
        if (_parado || _atv == null) return;
        // a primeira leitura curta (300 ms) é de uma vez só; o Ritmo do fim põe o intervalo certo
        if (_relogio.Interval == 300) _relogio.Stop();
        if (S.Config().Pausada) { Ritmo(); return; }

        double agora = Agora;
        var a = _sens.Ler(comRede: _atv.Cenas > 0);
        double dt = double.IsNaN(_ultimaAmostra) ? 1 : agora - _ultimaAmostra;
        _ultimaAmostra = agora;
        // buraco grande (competitivo, pausa, suspensão): o histórico velho não emenda com o novo
        if (dt > 15) _e.LimparHistorico();
        _e.Registrar(a, Math.Clamp((int)Math.Round(dt), 1, 10));

        AtualizarGpu();

        if (_cfg.AvisarLimites) _e.Limites.Avaliar(agora, m => EstadoUso.Valor(a, m));
        Sincronizar();
        if (_atv.Cenas > 0) _atv.Atualizar();
        Ritmo();
    }

    /// <summary>
    /// Liga ou desliga a atividade conforme os limites. Com o cartão aberto pelo atalho, ela só some depois que ele
    /// fecha: desligar uma atividade expandida faz a ilha recolher o cartão na cara de quem está olhando.
    /// </summary>
    private void Sincronizar()
    {
        if (_atv == null) return;
        bool quer = _cfg.AvisarLimites && !_competitivo && !_parado && _e.Limites.Ligada;
        if (quer && !_atv.Ativa)
        {
            _atv.SemChegada = S.Ilha.Expandida == _atv;
            _atv.Mostrar();
            if (_e.Ultima is Amostra a) Log.Escrever($"uso: {EstadoUso.Rotulo(_e.Limites.Motivo ?? Medida.Cpu, a)} passou do limite");
        }
        else if (!quer && _atv.Ativa && (S.Ilha.Expandida != _atv || _competitivo || _parado)) _atv.Esconder();
    }

    // ------------------------------------------------------------------ GPU

    /// <summary>nvmlInit custa ~70 ms: roda numa thread do pool e entrega a placa na thread da interface.</summary>
    private void IniciarGpu()
    {
        if (_iniciandoGpu || _parado) return;
        _iniciandoGpu = true;
        _ultimaTentativaGpu = Agora;
        var n = _nvml ?? new Nvml();
        Task.Run(() =>
        {
            bool ok;
            try { ok = n.Iniciar(); }
            catch (Exception e) { ok = false; Log.Escrever("uso: NVML falhou ao iniciar: " + e.Message); }
            S.NaInterface(() =>
            {
                _iniciandoGpu = false;
                if (_parado) { n.Dispose(); return; }
                _nvml = n;
                _sens.Gpu = n;
                Log.Escrever(ok ? $"uso: GPU {n.Nome} pela NVML" : $"uso: sem GPU NVIDIA ({n.Motivo})");
                AtualizarGpu();
            });
        });
    }

    private void AtualizarGpu()
    {
        bool tem = _nvml is { Pronta: true };
        if (tem != _e.TemGpu || (tem && _e.NomeGpu != _nvml!.Nome))
        {
            _e.TemGpu = tem;
            _e.NomeGpu = tem ? _nvml!.Nome : "";
            if (!tem) Log.Escrever($"uso: GPU sumiu ({_nvml?.Motivo})");
            _atv?.Refazer();
        }
        // driver reiniciado: tenta de novo a cada minuto (só se a DLL existe)
        if (!tem && _nvml is { TemDll: true } && !_iniciandoGpu && !(Agora - _ultimaTentativaGpu < 60)) IniciarGpu();
    }

    // ------------------------------------------------------------------ autoteste

    public override string Autoteste()
    {
        var r = new StringBuilder();
        void Ok(bool cond, string texto) => r.AppendLine((cond ? "ok      " : "FALHOU  ") + texto);

        // contas de delta
        Ok(Contas.Cpu(new TemposCpu(100, 300, 100), new TemposCpu(150, 400, 200)) is double c1 && Math.Abs(c1 - 75) < 1e-9, "CPU: 50 ocioso de 200 = 75%");
        Ok(Contas.Cpu(new TemposCpu(0, 0, 0), new TemposCpu(100, 100, 0)) == 0, "CPU: tudo ocioso = 0%");
        Ok(Contas.Cpu(new TemposCpu(0, 0, 0), new TemposCpu(0, 60, 40)) == 100, "CPU: nada ocioso = 100%");
        Ok(Contas.Cpu(new TemposCpu(5, 5, 5), new TemposCpu(5, 5, 5)) == null, "CPU: leitura repetida = sem valor");
        Ok(Contas.Cpu(new TemposCpu(500, 500, 500), new TemposCpu(10, 10, 10)) == null, "CPU: contador que voltou = sem valor");

        var antes = new Dictionary<ulong, Octetos> { [1] = new(1000, 500), [2] = new(5000, 5000), [3] = new(7, 7) };
        var depois = new Dictionary<ulong, Octetos> { [1] = new(3048, 1524), [2] = new(100, 100), [4] = new(999999, 999999) };
        var (d, s) = Contas.Rede(antes, depois, 2);
        Ok(d == 1024 && s == 512, $"rede: soma por interface, placa que reiniciou e a que apareceu não contam ({d} e {s} B/s)");
        Ok(Contas.Rede(antes, depois, 0) == (0, 0), "rede: intervalo zero = 0");

        // formatação
        Ok(Formato.Taxa(0) == "0 KB/s", "taxa 0 = " + Formato.Taxa(0));
        Ok(Formato.Taxa(512) == "0,5 KB/s", "taxa 512 B/s = " + Formato.Taxa(512));
        Ok(Formato.Taxa(150 * 1024) == "150 KB/s", "taxa 150 KB/s = " + Formato.Taxa(150 * 1024));
        Ok(Formato.Taxa(999.7 * 1024) == "1,0 MB/s", "taxa 999,7 KB/s vira MB = " + Formato.Taxa(999.7 * 1024));
        Ok(Formato.Taxa(1.5 * 1048576) == "1,5 MB/s", "taxa 1,5 MB/s = " + Formato.Taxa(1.5 * 1048576));
        Ok(Formato.Taxa(250 * 1048576) == "250 MB/s", "taxa 250 MB/s = " + Formato.Taxa(250 * 1048576));
        Ok(Formato.Taxa(1.2 * 1073741824) == "1,2 GB/s", "taxa 1,2 GB/s = " + Formato.Taxa(1.2 * 1073741824));
        Ok(Formato.Taxa(-5) == "0 KB/s", "taxa negativa = 0");
        Ok(Formato.Bytes(512UL * 1048576) == "512 MB", "memória 512 MB = " + Formato.Bytes(512UL * 1048576));
        Ok(Formato.Bytes(12UL * 1073741824) == "12,0 GB", "memória 12 GB = " + Formato.Bytes(12UL * 1073741824));
        Ok(Formato.Par(15246875853UL, 34226438144UL) == "14,2 / 31,9 GB", "usada/total = " + Formato.Par(15246875853UL, 34226438144UL));
        Ok(Formato.Porcento(93.4) == "93%" && Formato.Porcento(140) == "100%", "porcentagem arredonda e limita");
        Ok(Formato.Graus(85.6) == "86 °C", "graus = " + Formato.Graus(85.6));
        var am = new Amostra(93.2, 1, 2, 97.4, 86, 5UL << 30, 12UL << 30, 0, 0);
        Ok(EstadoUso.Rotulo(Medida.GpuTemp, am) == "GPU 86 °C" && EstadoUso.Rotulo(Medida.Gpu, am) == "GPU 97%"
            && EstadoUso.Rotulo(Medida.Cpu, am) == "CPU 93%" && EstadoUso.Rotulo(Medida.Ram, am) == "RAM 50%", "rótulos da pílula");
        Ok(EstadoUso.Rotulo(Medida.GpuTemp, am with { GpuTemp = null }) == "GPU", "rótulo sem GPU não quebra");
        Ok(AtividadeUso.NomeCurto("NVIDIA GeForce RTX 3060") == "RTX 3060", "nome curto da placa");

        // histórico
        var h = new Historico(5);
        for (int i = 1; i <= 7; i++) h.Adicionar(i);
        Ok(string.Join(",", h.Ultimos(5)) == "3,4,5,6,7" && h.Contagem == 5, "histórico guarda só os últimos 5: " + string.Join(",", h.Ultimos(5)));
        h.Adicionar(9, 3);
        Ok(string.Join(",", h.Ultimos(3)) == "9,9,9" && h.Maximo(5) == 9 && string.Join(",", h.Ultimos(12)) == "6,7,9,9,9", "histórico repete a checagem de 5 s e não estoura");
        h.Limpar();
        Ok(h.Ultimos(12).Length == 0, "histórico limpo");

        // limites com histerese
        var lim = new Limites();
        lim.Definir(new[]
        {
            new Limites.Regra(Medida.GpuTemp, 83, 0), new Limites.Regra(Medida.Cpu, 90, 10),
            new Limites.Regra(Medida.Gpu, 95, 10), new Limites.Regra(Medida.Ram, 90, 0),
        });
        double? cpu = null, gpu = null, temp = null, ram = 40;
        double? V(Medida m) => m switch { Medida.Cpu => cpu, Medida.Gpu => gpu, Medida.GpuTemp => temp, _ => ram };
        cpu = 95;
        bool m0 = lim.Avaliar(0, V), m5 = lim.Avaliar(5, V);
        Ok(!m0 && !m5 && !lim.Ligada, "CPU 95% por 5 s ainda não liga");
        Ok(lim.Avaliar(10, V) && lim.Ligada && lim.Motivo == Medida.Cpu, "CPU 95% por 10 s liga, motivo CPU");
        cpu = 50;
        Ok(!lim.Avaliar(15, V) && lim.Ligada, "abaixo do limite continua ligada (histerese)");
        cpu = 93;
        lim.Avaliar(20, V);
        cpu = 50;
        lim.Avaliar(25, V);
        Ok(!lim.Avaliar(50, V) && lim.Ligada, "pico acima no meio recomeça os 30 s (25 s abaixo, ainda ligada)");
        Ok(lim.Avaliar(55, V) && !lim.Ligada && lim.Motivo == null, "30 s abaixo desliga");
        temp = 85;
        Ok(lim.Avaliar(100, V) && lim.Motivo == Medida.GpuTemp, "temperatura acima liga na hora");
        cpu = 95;
        lim.Avaliar(105, V);
        lim.Avaliar(115, V);
        Ok(lim.Motivo == Medida.GpuTemp && lim.Estouradas.Count == 2, "temperatura e CPU estouradas: motivo é a temperatura");
        temp = 70;
        lim.Avaliar(116, V);
        Ok(lim.Motivo == Medida.Cpu, "temperatura voltou: motivo passa para a CPU");
        lim.Zerar();
        cpu = 50; temp = null; gpu = null; ram = 95;
        Ok(lim.Avaliar(200, V) && lim.Motivo == Medida.Ram, "RAM 95% liga na hora");
        lim.Zerar();
        ram = 40; gpu = null; temp = null; cpu = 50;
        for (int t = 300; t < 400; t += 5) lim.Avaliar(t, V);
        Ok(!lim.Ligada, "sem placa NVIDIA (GPU nula) nunca liga nem dá erro");
        lim.Definir(new[] { new Limites.Regra(Medida.Cpu, 0, 10) });
        cpu = 100;
        for (int t = 400; t < 500; t += 5) lim.Avaliar(t, V);
        Ok(!lim.Ligada, "limite 0 desliga a medida");
        lim.Definir(new[] { new Limites.Regra(Medida.Cpu, 90, 10) });
        lim.Zerar();
        lim.Avaliar(600, V);
        lim.Avaliar(605, V);
        lim.Avaliar(700, V);
        bool aposLacuna = lim.Ligada;
        lim.Avaliar(705, V);
        lim.Avaliar(710, V);
        Ok(!aposLacuna && lim.Ligada, "buraco de 95 s entre leituras zera a contagem dos 10 s");

        // config
        var cfg = ConfigUso.Normalizar(new ConfigUso { LimiteCpu = double.NaN, SegundosCpu = -3, IntervaloChecagem = 0, LimiteTemperaturaGpu = 500 });
        Ok(cfg.LimiteCpu == 90 && cfg.SegundosCpu == 0 && cfg.IntervaloChecagem == 2 && cfg.LimiteTemperaturaGpu == 120, "config fora da faixa volta para o padrão ou é limitada");

        // sonda real, só leitura: uma amostra de cada
        r.Append(Sonda());
        return r.ToString().TrimEnd();
    }

    private static unsafe string Sonda()
    {
        var r = new StringBuilder();
        void Ok(bool cond, string texto) => r.AppendLine((cond ? "ok      " : "FALHOU  ") + texto);
        var sw = new Stopwatch();

        var t0 = Win.Cpu();
        var redeAntes = Win.Rede();
        long ta = Stopwatch.GetTimestamp();
        System.Threading.Thread.Sleep(500);
        sw.Restart();
        var t1 = Win.Cpu();
        double usCpu = sw.Elapsed.TotalMilliseconds * 1000;
        var pct = t0 is TemposCpu x0 && t1 is TemposCpu x1 ? Contas.Cpu(x0, x1) : null;
        Ok(pct is >= 0 and <= 100, $"sonda CPU: {(pct is double p ? Formato.Porcento(p) : "?")} em 0,5 s (GetSystemTimes, {usCpu:0} µs)");

        sw.Restart();
        var mem = Win.Memoria();
        double usMem = sw.Elapsed.TotalMilliseconds * 1000;
        Ok(mem is { Total: > 0 } && mem.Value.Usada <= mem.Value.Total, $"sonda RAM: {(mem is { } m ? Formato.Par(m.Usada, m.Total) : "?")} (GlobalMemoryStatusEx, {usMem:0} µs)");

        var nomes = new List<string>();
        sw.Restart();
        var rede = Win.Rede(nomes);
        double usRede = sw.Elapsed.TotalMilliseconds * 1000;
        long tb = Stopwatch.GetTimestamp();
        if (rede != null && redeAntes != null)
        {
            var (d, s) = Contas.Rede(redeAntes, rede, (tb - ta) / (double)Stopwatch.Frequency);
            ulong rec = 0, env = 0;
            foreach (var o in rede.Values) { rec += o.Recebidos; env += o.Enviados; }
            Ok(true, $"sonda rede: {rede.Count} interface(s) física(s) ligada(s) [{string.Join("; ", nomes)}], desceu {Formato.Taxa(d)}, subiu {Formato.Taxa(s)}; total {Formato.Bytes(rec)} recebidos, {Formato.Bytes(env)} enviados (GetIfTable2, {usRede:0} µs)");
        }
        else Ok(false, "sonda rede: GetIfTable2 falhou");

        Ok(sizeof(ulong) * 7 + 8 == 64, "MEMORYSTATUSEX tem 64 bytes");

        using var n = new Nvml();
        sw.Restart();
        bool ok = n.Iniciar();
        double msInit = sw.Elapsed.TotalMilliseconds;
        var sens = new Sensores { Gpu = ok ? n : null };
        sens.Ler();
        sw.Restart();
        for (int i = 0; i < 20; i++) sens.Ler();
        double usTela = sw.Elapsed.TotalMilliseconds * 1000 / 20;
        sens.Ler(comRede: false);
        sw.Restart();
        for (int i = 0; i < 20; i++) sens.Ler(comRede: false);
        double usFundo = sw.Elapsed.TotalMilliseconds * 1000 / 20;
        Ok(true, $"sonda: amostra na tela (CPU, RAM, rede{(ok ? ", GPU" : "")}) {usTela:0} µs; checagem de fundo (sem rede) {usFundo:0.0} µs, em média de 20");
        if (!ok) { Ok(true, $"sonda GPU: sem NVML ({n.Motivo}); a GPU some do cartão sem erro"); return r.ToString(); }
        n.Ler();
        sw.Restart();
        var g = n.Ler();
        double usGpu = sw.Elapsed.TotalMilliseconds * 1000;
        Ok(g.Uso is >= 0 and <= 100 && g.Temperatura is > 0 and < 130 && g.VramTotal > 0 && g.VramUsada <= g.VramTotal,
            $"sonda GPU: {n.Nome}, {(g.Uso is double u ? Formato.Porcento(u) : "?")}, {(g.Temperatura is double tt ? Formato.Graus(tt) : "?")}, " +
            $"VRAM {(g.VramUsada is ulong vu && g.VramTotal is ulong vt ? Formato.Par(vu, vt) : "?")} " +
            $"(memória pela {(n.MemoriaV2Disponivel ? "v2" : "v1")}; nvmlInit {msInit:0} ms, leitura {usGpu:0} µs)");
        return r.ToString();
    }
}

/// <summary>Config do módulo (config.json, "modulos" > "uso"). Limite 0 desliga aquela medida.</summary>
public sealed class ConfigUso
{
    // aparece sozinha quando algo passa do limite; false = só pelo atalho
    [JsonPropertyName("avisarLimites")] public bool AvisarLimites { get; set; } = true;
    [JsonPropertyName("limiteCpu")] public double LimiteCpu { get; set; } = 90;
    [JsonPropertyName("segundosCpu")] public double SegundosCpu { get; set; } = 10;
    [JsonPropertyName("limiteGpu")] public double LimiteGpu { get; set; } = 95;
    [JsonPropertyName("segundosGpu")] public double SegundosGpu { get; set; } = 10;
    [JsonPropertyName("limiteTemperaturaGpu")] public double LimiteTemperaturaGpu { get; set; } = 83;
    [JsonPropertyName("limiteRam")] public double LimiteRam { get; set; } = 90;
    // tudo abaixo do limite por este tempo e a atividade some
    [JsonPropertyName("segundosParaSumir")] public double SegundosParaSumir { get; set; } = 30;
    // de quanto em quanto tempo os limites são checados com a atividade fora da tela
    [JsonPropertyName("intervaloChecagem")] public int IntervaloChecagem { get; set; } = 5;

    private static double Faixa(double v, double min, double max, double padrao) => double.IsFinite(v) ? Math.Clamp(v, min, max) : padrao;

    internal static ConfigUso Normalizar(ConfigUso c) => new()
    {
        AvisarLimites = c.AvisarLimites,
        LimiteCpu = Faixa(c.LimiteCpu, 0, 100, 90),
        SegundosCpu = Faixa(c.SegundosCpu, 0, 600, 10),
        LimiteGpu = Faixa(c.LimiteGpu, 0, 100, 95),
        SegundosGpu = Faixa(c.SegundosGpu, 0, 600, 10),
        LimiteTemperaturaGpu = Faixa(c.LimiteTemperaturaGpu, 0, 120, 83),
        LimiteRam = Faixa(c.LimiteRam, 0, 100, 90),
        SegundosParaSumir = Faixa(c.SegundosParaSumir, 0, 600, 30),
        IntervaloChecagem = Math.Clamp(c.IntervaloChecagem, 2, 60),
    };
}
