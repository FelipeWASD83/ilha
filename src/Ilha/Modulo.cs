using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ilha;

/// <summary>Marca uma classe de módulo (mídia, volume, timer...): ela é achada sozinha ao abrir, sem registro em lugar nenhum.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class ModuloAttribute : Attribute
{
    public string Id { get; }
    public string Nome { get; }
    public bool LigadoPorPadrao { get; set; } = true;
    /// <summary>The module name in English (the settings window shows Nome or this, following the interface language).</summary>
    public string? English { get; set; }
    public string Name => Lang.T(Nome, English ?? Nome);

    public ModuloAttribute(string id, string nome)
    {
        Id = id;
        Nome = nome;
    }
}

/// <summary>
/// Um pedaço da ilha que pode ser ligado e desligado. Cada módulo mora na própria pasta (src/Ilha/Modulos/&lt;Nome&gt;/),
/// tem a própria seção na config ("modulos" -> id) e conversa com o resto só pelos <see cref="Servicos"/>.
/// </summary>
internal abstract class Modulo
{
    public string Id { get; internal set; } = "";
    public string Nome { get; internal set; } = "";
    protected Servicos S { get; private set; } = null!;

    internal void Ligar(Servicos s, JsonObject cfg)
    {
        S = s;
        Iniciar(cfg);
    }

    /// <summary>Liga o módulo: cria as atividades (S.Ilha.Adicionar), registra atalhos, assina eventos do sistema.</summary>
    protected abstract void Iniciar(JsonObject cfg);

    /// <summary>A seção do módulo na config mudou (o arquivo foi salvo).</summary>
    public virtual void ConfigMudou(JsonObject cfg) { }

    /// <summary>A janela da frente, o modo competitivo, a tela cheia ou o Não Incomodar mudaram.</summary>
    public virtual void Contexto(Contexto ctx) { }

    /// <summary>Desligando (saindo da ilha ou módulo desligado na config): soltar eventos, timers, threads.</summary>
    public virtual void Parar() { }

    /// <summary>
    /// `Ilha.exe --testar &lt;id&gt;`: confere a lógica do módulo sem abrir janela nenhuma e sem os Serviços (S é nulo aqui).
    /// Devolve o relatório; linhas que começam com "FALHOU" contam como falha.
    /// </summary>
    public virtual string Autoteste() => "ok      sem autoteste próprio";

    /// <summary>A classe da config do módulo (usada pela tela de configurações para montar o formulário).</summary>
    public virtual Type? TipoConfig => null;

    protected static T Ler<T>(JsonObject cfg) where T : new()
    {
        try { return cfg.Deserialize<T>(Config.Json) ?? new T(); }
        catch (Exception e) { Log.Escrever($"config de módulo ilegível ({typeof(T).Name}): {e.Message}"); return new T(); }
    }

    /// <summary>Grava a config do módulo de volta no config.json (mantém o "ligado").</summary>
    protected void Salvar<T>(T cfg) => S.SalvarModulo(Id, cfg);
}

/// <summary>O que o núcleo oferece aos módulos.</summary>
internal sealed class Servicos
{
    public required Ilha Ilha { get; init; }
    public Palco Palco => Ilha.Palco;
    public required Sons Sons { get; init; }
    public required Atalhos Atalhos { get; init; }
    public required Entrada Entrada { get; init; }
    public required Indicadores Indicadores { get; init; }
    /// <summary>As entradas do menu principal da ilha (cada módulo põe as suas no Iniciar).</summary>
    public required MenuDaIlha Menu { get; init; }
    public required Func<Contexto> ContextoAtual { get; init; }
    public required Func<Config> Config { get; init; }
    public required Action<string, object?> SalvarModulo { get; init; }
    /// <summary>Balão do ícone da bandeja.</summary>
    public required Action<string> Avisar { get; init; }

    /// <summary>Roda na thread da interface (eventos do sistema chegam em outras threads).</summary>
    public void NaInterface(Action a) => Programa.NaInterface(a);
}

internal static class CatalogoDeModulos
{
    public static List<(ModuloAttribute Info, Type Tipo)> Descobrir() =>
        typeof(Modulo).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(Modulo).IsAssignableFrom(t))
            .Select(t => (Info: t.GetCustomAttribute<ModuloAttribute>(), Tipo: t))
            .Where(x => x.Info != null)
            .Select(x => (x.Info!, x.Tipo))
            .OrderBy(x => x.Item1.Id)
            .ToList();

    public static Modulo Criar((ModuloAttribute Info, Type Tipo) m)
    {
        var mod = (Modulo)Activator.CreateInstance(m.Tipo)!;
        mod.Id = m.Info.Id;
        mod.Nome = m.Info.Nome;
        return mod;
    }

    /// <summary>`--testar [id]`: roda o autoteste de um módulo (ou de todos), sem janela. Código 0 = tudo certo.</summary>
    public static int Testar(string? id)
    {
        int falhas = 0;
        foreach (var m in Descobrir().Where(x => id == null || x.Info.Id == id))
        {
            Console.WriteLine($"== {m.Info.Id} ({m.Info.Nome})");
            string rel;
            try { rel = Criar(m).Autoteste(); }
            catch (Exception e) { rel = "FALHOU  exceção: " + e; }
            foreach (var l in rel.Split('\n'))
            {
                Console.WriteLine("  " + l.TrimEnd());
                if (l.TrimStart().StartsWith("FALHOU")) falhas++;
            }
        }
        return falhas == 0 ? 0 : 1;
    }
}
