using System;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ilha.Nota;

/// <summary>
/// Nota rápida: o atalho (Ctrl + Alt + N) ou a entrada do menu abrem um campo na ilha, e o Enter põe o texto no fim
/// de uma nota de caixa de entrada (um .md do cofre do Obsidian, por exemplo). O arquivo vem só da config; vazio,
/// o módulo não escreve nada e o cartão explica o que falta.
/// </summary>
[Modulo("nota", "Nota rápida", English = "Quick note")]
internal sealed class ModuloNota : Modulo
{
    private ConfigNota _cfg = new();
    private AtividadeNota? _atv;

    public override Type? TipoConfig => typeof(ConfigNota);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigNota>(cfg);
        var atv = _atv = new AtividadeNota(S, () => _cfg.Arquivo);
        S.Ilha.Adicionar(atv);
        var ficou = S.Atalhos.Registrar("nota", new Atalho { Mods = Nativo.MOD_CONTROL | Nativo.MOD_ALT, Tecla = 0x4E }, () => _atv?.Alternar());
        if (ficou == null && !(S.Config().AtalhosModulos.TryGetValue("nota", out var a) && a.Tecla == 0))
            S.Avisar(Lang.T("O atalho da nota rápida (Ctrl + Alt + N) já é de outro programa. Troque em \"atalhosModulos\" → \"nota\" na configuração.",
                "The quick note shortcut (Ctrl + Alt + N) already belongs to another program. Change it in \"atalhosModulos\" → \"nota\" in the settings."));
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "nota",
            Nome = Lang.T("Nota", "Note"),
            Abrir = atv.Abrir,
            Detalhe = atv.DetalheDoMenu,
            Icone = (dc, palco, r) => AtividadeNota.Simbolo(dc, palco, r.X, r.Y, MathF.Min(r.Width, r.Height)),
            Ordem = 20,
        });
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigNota>(cfg);
        _atv?.ConfigMudou();
        S.Menu.Redesenhar();
    }

    public override void Contexto(Contexto ctx) => _atv?.Contexto(ctx);

    public override void Parar()
    {
        _atv?.Parar();
        _atv = null;
    }

    public override string Autoteste() => CaixaDeEntrada.Testar();
}

public sealed class ConfigNota
{
    [Label("Arquivo da nota", "Note file")]
    [Help("Caminho completo do .md onde cada nota entra como um item de lista no fim (vazio: a nota rápida não escreve nada)",
        "Full path of the .md file where each note goes in as a list item at the end (empty: the quick note writes nothing)")]
    [JsonPropertyName("arquivo")] public string Arquivo { get; set; } = "";
}
