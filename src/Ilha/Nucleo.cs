using System;
using System.Collections.Generic;
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
    private readonly Sons _sons;
    private readonly Atalhos _atalhos;
    private readonly Entrada _entrada;
    private readonly Indicadores _indicadores = new();
    private readonly Servicos _servicos;
    private readonly Dictionary<string, Modulo> _modulos = new();
    private readonly Dictionary<string, string> _cfgModulos = new();

    public IReadOnlyDictionary<string, Modulo> ModulosLigados => _modulos;
    public Servicos Servicos => _servicos;
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
        _visib.Mudou += ctx =>
        {
            Ilha.Contexto(ctx);
            foreach (var m in _modulos.Values)
                try { m.Contexto(ctx); } catch (Exception e) { Log.Escrever($"módulo {m.Id} (contexto): {e}"); }
        };
        Ilha.Contexto(_visib.Atual);
        _sons = new Sons(() => Config, () => _visib.Atual);
        Ilha.Sons = _sons;
        Ilha.Indicadores = _indicadores;
        _entrada = new Entrada();
        _atalhos = new Atalhos(_vis.Handle, () => Config);

        _vis.TelaMudou += () => Ilha.Reposicionar();
        _vis.Acordou += () => { Ilha.Reposicionar(forcar: true); _visib.Avaliar(); };
        _vis.Atalho += id => { if (Teclas.Eh(id)) Ilha.Atalho(); else _atalhos.Disparou(id); };

        _teclas = new Teclas(_vis.Handle);
        _servicos = new Servicos
        {
            Ilha = Ilha, Sons = _sons, Atalhos = _atalhos, Entrada = _entrada, Indicadores = _indicadores,
            ContextoAtual = () => _visib.Atual, Config = () => Config, SalvarModulo = SalvarModulo,
            Avisar = texto => _bandeja?.Avisar(texto),
        };
        _bandeja = new Bandeja(this);
        RegistrarAtalho();
        LigarModulos();
        Inicio.Definir(Config.IniciarComWindows);

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
        });
        sup.Superf.Dispose();
        Ilha.Aquecer();
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        Log.Escrever($"memória depois de abrir: {proc.WorkingSet64 / 1048576} MB (working set), {proc.PrivateMemorySize64 / 1048576} MB privados, heap .NET {GC.GetTotalMemory(false) / 1048576} MB");
    }

    // ------------------------------------------------------------------ módulos

    private System.Text.Json.Nodes.JsonObject SecaoDoModulo(string id, bool padrao)
    {
        if (Config.Modulos[id] is not System.Text.Json.Nodes.JsonObject s)
        {
            s = new System.Text.Json.Nodes.JsonObject { ["ligado"] = padrao };
            Config.Modulos[id] = s;
        }
        return s;
    }

    private static bool Ligado(System.Text.Json.Nodes.JsonObject s, bool padrao) =>
        s["ligado"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out bool b) ? b : padrao;

    /// <summary>Liga os módulos marcados como ligados (e desliga os que foram desligados na config).</summary>
    private readonly Dictionary<string, string> _falhou = new();

    private void LigarModulos()
    {
        bool novaSecao = false;
        foreach (var m in CatalogoDeModulos.Descobrir())
        {
            bool tinha = Config.Modulos.ContainsKey(m.Info.Id);
            var secao = SecaoDoModulo(m.Info.Id, m.Info.LigadoPorPadrao);
            novaSecao |= !tinha;
            bool quer = Ligado(secao, m.Info.LigadoPorPadrao);
            string texto = secao.ToJsonString();
            if (_modulos.TryGetValue(m.Info.Id, out var vivo))
            {
                if (!quer)
                {
                    try { vivo.Parar(); } catch (Exception e) { Log.Escrever($"módulo {vivo.Id} (parar): {e}"); }
                    // o que o módulo pediu ao núcleo sai junto: atalhos e atividades
                    _atalhos.RemoverDoDono(vivo.Id);
                    Ilha.RemoverDoDono(vivo.Id);
                    _modulos.Remove(m.Info.Id);
                    Log.Escrever($"módulo {m.Info.Id} desligado");
                }
                else if (_cfgModulos.GetValueOrDefault(m.Info.Id) != texto)
                {
                    try { vivo.ConfigMudou(secao); } catch (Exception e) { Log.Escrever($"módulo {vivo.Id} (config): {e}"); }
                }
                _cfgModulos[m.Info.Id] = texto;
                continue;
            }
            if (!quer) { _falhou.Remove(m.Info.Id); continue; }
            // falhou com esta mesma seção: só tenta de novo quando ela mudar (senão cada config salva religaria)
            if (_falhou.GetValueOrDefault(m.Info.Id) == texto) continue;
            Modulo? mod = null;
            try
            {
                mod = CatalogoDeModulos.Criar(m);
                // marca de quem são os atalhos e as atividades pedidos durante o Iniciar
                _atalhos.Dono = mod.Id;
                Ilha.DonoAtual = mod.Id;
                try { mod.Ligar(_servicos, secao); }
                finally { _atalhos.Dono = null; Ilha.DonoAtual = null; }
                mod.Contexto(_visib.Atual);
                _modulos[mod.Id] = mod;
                _cfgModulos[mod.Id] = texto;
                _falhou.Remove(mod.Id);
                Log.Escrever($"módulo {mod.Id} ligado");
            }
            catch (Exception e)
            {
                Log.Escrever($"módulo {m.Info.Id} não ligou: {e}");
                _falhou[m.Info.Id] = texto;
                if (mod != null)
                {
                    // o que ele chegou a pedir antes de falhar não fica órfão
                    try { mod.Parar(); } catch (Exception e2) { Log.Escrever($"módulo {m.Info.Id} (parar depois da falha): {e2}"); }
                    _atalhos.RemoverDoDono(m.Info.Id);
                    Ilha.RemoverDoDono(m.Info.Id);
                }
            }
        }
        if (novaSecao) Config.Salvar();
    }

    private void SalvarModulo(string id, object? dados)
    {
        var secao = dados == null ? new System.Text.Json.Nodes.JsonObject()
            : System.Text.Json.JsonSerializer.SerializeToNode(dados, dados.GetType(), Config.Json) as System.Text.Json.Nodes.JsonObject ?? new();
        bool ligado = Config.Modulos[id] is System.Text.Json.Nodes.JsonObject antes ? Ligado(antes, true) : true;
        secao["ligado"] = ligado;
        Config.Modulos[id] = secao;
        _cfgModulos[id] = secao.ToJsonString();
        Config.Salvar();
    }

    public void LigarModulo(string id, bool ligado)
    {
        var m = CatalogoDeModulos.Descobrir().Find(x => x.Info.Id == id);
        if (m.Tipo == null) return;
        SecaoDoModulo(id, m.Info.LigadoPorPadrao)["ligado"] = ligado;
        Config.Salvar();
        LigarModulos();
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
        _atalhos.Reaplicar();
        LigarModulos();
        if (nova.IniciarComWindows != Inicio.Ligado()) Inicio.Definir(nova.IniciarComWindows);
        Log.Escrever("config relida");
    }

    public void Pausar(bool sim)
    {
        Config.Pausada = sim;
        Config.Salvar();
        Ilha.TrocarConfig(Config);
        _bandeja.Atualizar(sim);
    }

    public void AlternarInicio()
    {
        Config.IniciarComWindows = !Config.IniciarComWindows;
        Config.Salvar();
        Inicio.Definir(Config.IniciarComWindows);
        if (!Inicio.Instalada)
            _bandeja.Avisar("Iniciar com o Windows só vale na ilha instalada (ferramentas\\instalar.ps1). A escolha ficou salva.");
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
        foreach (var m in _modulos.Values)
            try { m.Parar(); } catch (Exception e) { Log.Escrever($"módulo {m.Id} (parar): {e}"); }
        _modulos.Clear();
        _atalhos.Dispose();
        _entrada.Dispose();
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
