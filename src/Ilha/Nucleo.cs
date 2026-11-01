using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Ilha;

internal static class Atualizacao
{
    public static string Versao => Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";
}

/// <summary>Liga as peças: config, compositor, janelas, ilha, visibilidade, atalho, bandeja e a demonstração.</summary>
internal sealed class Nucleo : IDisposable
{
    public Config Config { get; private set; }
    public Ilha Ilha { get; }
    public Demonstracao Demonstracao { get; }

    private readonly Grafico _g;
    private readonly JanelaVisual _vis;
    private readonly JanelaToque _toque;
    private readonly Visibilidade _visib;
    private readonly Teclas _teclas;
    private readonly Bandeja _bandeja;
    private readonly FileSystemWatcher? _vigiaConfig;
    private readonly Timer _releitura = new() { Interval = 300 };

    public Nucleo()
    {
        Config = Config.Carregar();
        Log.Escrever($"ilha {Atualizacao.Versao} abrindo");
        using (var pr = System.Diagnostics.Process.GetCurrentProcess()) Log.Escrever($"memória antes do gráfico: {pr.WorkingSet64 / 1048576} MB, {pr.PrivateMemorySize64 / 1048576} MB privados");
        Grafico.UsarGpu = Config.DesenharNaGpu;
        _g = new Grafico();
        _vis = new JanelaVisual();
        _toque = new JanelaToque();
        Ilha = new Ilha(_g, Config, _vis, _toque);
        Ilha.DefinirPainel(new Painel());
        Aquecer();
        Demonstracao = new Demonstracao(Ilha);

        _visib = new Visibilidade(Config);
        _visib.Mudou += ctx => Ilha.Contexto(ctx);
        Ilha.Contexto(_visib.Atual);

        _vis.TelaMudou += () => Ilha.Reposicionar();
        _vis.Acordou += () => { Ilha.Reposicionar(forcar: true); _visib.Avaliar(); };
        _vis.Atalho += id => { if (Teclas.Eh(id)) Ilha.Atalho(); };

        _teclas = new Teclas(_vis.Handle);
        _bandeja = new Bandeja(this);
        RegistrarAtalho();

        // a config é um JSON editável: salvou, vale na hora
        try
        {
            Directory.CreateDirectory(Config.Pasta);
            _vigiaConfig = new FileSystemWatcher(Config.Pasta, "config.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            _vigiaConfig.Changed += (_, _) => Programa.NaInterface(() => { _releitura.Stop(); _releitura.Start(); });
            _vigiaConfig.Created += (_, _) => Programa.NaInterface(() => { _releitura.Stop(); _releitura.Start(); });
            _vigiaConfig.Renamed += (_, _) => Programa.NaInterface(() => { _releitura.Stop(); _releitura.Start(); });
            _vigiaConfig.EnableRaisingEvents = true;
        }
        catch (Exception e) { Log.Escrever("não vigia a config: " + e.Message); }
        _releitura.Tick += (_, _) => { _releitura.Stop(); Recarregar(); };
    }

    /// <summary>
    /// Carrega as fontes e compila o caminho do desenho já ao abrir: sem isso, a primeira coisa que aparece
    /// demora uns 100 ms para começar a animar.
    /// </summary>
    private void Aquecer()
    {
        var p = Ilha.Palco;
        foreach (var tam in new[] { 11f, 12f, 13f, 14f, 15f, 16f, 17f, 18f, 20f, 26f, 42f, 48f })
            p.Texto.Medir("0:00 Ág", tam, Vortice.DirectWrite.FontWeight.SemiBold, tabular: true);
        // desenha de verdade (os glifos ficam no cache do Direct2D) em todos os pesos e tamanhos que as cenas usam
        var sup = _g.NovaSuperficie(new System.Numerics.Vector2(400, 60), (dc, t) =>
        {
            var pesos = new[] { Vortice.DirectWrite.FontWeight.Normal, Vortice.DirectWrite.FontWeight.Medium, Vortice.DirectWrite.FontWeight.SemiBold };
            foreach (var peso in pesos)
                foreach (var tam in new[] { 11f, 12f, 13f, 14f, 15f, 16f, 17f, 18f, 20f, 26f, 42f, 48f })
                {
                    p.Texto.Desenhar(dc, "0123456789:% -", System.Numerics.Vector2.Zero, tam, Cor.Branco, peso, tabular: true);
                    p.Texto.Desenhar(dc, "Timer acabou Música Lendo ção", System.Numerics.Vector2.Zero, tam, Cor.Branco, peso);
                }
            foreach (var lado in new[] { 18f, 20f, 30f }) Imagem.Desenhar(dc, p, "logo-claude.png", 0, 0, lado);
        });
        sup.Superf.Dispose();
        Ilha.Aquecer();
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        Log.Escrever($"memória depois de abrir: {proc.WorkingSet64 / 1048576} MB (working set), {proc.PrivateMemorySize64 / 1048576} MB privados, heap .NET {GC.GetTotalMemory(false) / 1048576} MB");
    }

    private void RegistrarAtalho()
    {
        var ficou = _teclas.Registrar(Config.Atalho);
        if (ficou == null)
        {
            Log.Escrever("nenhum atalho global livre");
            _bandeja.Avisar($"O atalho {Teclas.Nome(Config.Atalho)} já é de outro programa. Troque em \"atalho\" na configuração.");
        }
        else if (ficou.Mods != Config.Atalho.Mods || ficou.Tecla != Config.Atalho.Tecla)
        {
            Log.Escrever($"atalho {Teclas.Nome(Config.Atalho)} ocupado; usando {Teclas.Nome(ficou)}");
            _bandeja.Avisar($"{Teclas.Nome(Config.Atalho)} já é de outro programa. A ilha ficou com {Teclas.Nome(ficou)}.");
        }
        else Log.Escrever("atalho: " + Teclas.Nome(ficou));
    }

    private bool _releuDeNovo;

    private void Recarregar()
    {
        // JSON com erro (ou ainda sendo salvo pelo editor): tenta mais uma vez e, se continuar, mantém a config que vale
        var nova = Config.TentarLer(out var erro);
        if (nova == null)
        {
            if (!_releuDeNovo) { _releuDeNovo = true; _releitura.Start(); return; }
            _releuDeNovo = false;
            Log.Escrever("config com erro, mantida a anterior: " + erro);
            _bandeja.Avisar("A configuração tem um erro e não foi aplicada. A ilha continua com a anterior.");
            return;
        }
        _releuDeNovo = false;
        if (nova.DesenharNaGpu != Config.DesenharNaGpu)
        {
            Log.Escrever("desenharNaGpu mudou: vale na próxima vez que a ilha abrir");
            _bandeja.Avisar("\"desenharNaGpu\" vale depois de fechar e abrir a ilha de novo.");
        }
        bool atalho = nova.Atalho.Mods != Config.Atalho.Mods || nova.Atalho.Tecla != Config.Atalho.Tecla;
        Config = nova;
        _visib.TrocarConfig(nova);
        _visib.Avaliar();
        Ilha.TrocarConfig(nova);
        _bandeja.Atualizar(nova.Pausada);
        if (atalho) RegistrarAtalho();
        Log.Escrever("config relida");
    }

    public void Pausar(bool sim)
    {
        Config.Pausada = sim;
        Config.Salvar();
        Ilha.TrocarConfig(Config);
        _bandeja.Atualizar(sim);
    }

    public void AlternarColada()
    {
        Config.Colada = !Config.Colada;
        Config.Salvar();
        Ilha.TrocarConfig(Config);
    }

    public void Sair()
    {
        Log.Escrever("saindo");
        Application.ExitThread();
    }

    public void Dispose()
    {
        _vigiaConfig?.Dispose();
        _releitura.Dispose();
        _bandeja.Dispose();
        _teclas.Dispose();
        _visib.Dispose();
        Ilha.Dispose();
        _vis.DestroyHandle();
        _toque.DestroyHandle();
        _g.Dispose();
    }
}
