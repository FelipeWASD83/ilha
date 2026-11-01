using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ilha.ContaGotas;

/// <summary>
/// Conta-gotas: o atalho (Ctrl + Alt + P) ou a entrada do menu abrem uma lupa embaixo do cursor; o clique pega a cor
/// do pixel de verdade, copia o hex e mostra o cartão com RGB, HSL e as últimas cores. Nada roda antes do atalho
/// e nada fica aberto depois. Não abre no modo competitivo, com tela cheia na frente nem com a ilha pausada.
/// </summary>
[Modulo("contagotas", "Conta-gotas")]
internal sealed class ModuloContaGotas : Modulo
{
    private ConfigContaGotas _cfg = new();
    private AtividadeContaGotas? _atv;
    private bool _parado;

    public override Type? TipoConfig => typeof(ConfigContaGotas);

    protected override void Iniciar(JsonObject cfg)
    {
        _parado = false;
        _cfg = Ler<ConfigContaGotas>(cfg);
        _cfg.Cores = Historico.Limpar(_cfg.Cores, _cfg.Guardar);
        _atv = new AtividadeContaGotas(() => _cfg.Cores, () => _cfg.HexMinusculo, () => S.NaInterface(Pegar), t => Copiar(t));
        // nunca liga (não fica na pílula), mas precisa estar na ilha para o Expandir
        S.Ilha.Adicionar(_atv);
        var ficou = S.Atalhos.Registrar("contagotas", new Atalho { Mods = Nativo.MOD_CONTROL | Nativo.MOD_ALT, Tecla = 0x50 }, Pegar);
        if (ficou == null && !(S.Config().AtalhosModulos.TryGetValue("contagotas", out var a) && a.Tecla == 0))
            S.Avisar("O atalho do conta-gotas (Ctrl + Alt + P) já é de outro programa. Troque em \"atalhosModulos\" → \"contagotas\" na configuração.");
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "contagotas",
            Nome = "Conta-gotas",
            // o clique no menu chega no meio do tratamento do mouse da ilha: a lupa abre logo depois
            Abrir = () => S.NaInterface(Pegar),
            Detalhe = () => _cfg.Cores.Count > 0 ? (_cfg.HexMinusculo ? _cfg.Cores[0].ToLowerInvariant() : _cfg.Cores[0]) : null,
            Icone = (dc, palco, r) => IconeContaGotas.Desenhar(dc, palco, r, _cfg.Cores.Count > 0 && CorDaTela.TentarLer(_cfg.Cores[0], out var c) ? c : null),
            Ordem = 50,
        });
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigContaGotas>(cfg);
        _cfg.Cores = Historico.Limpar(_cfg.Cores, _cfg.Guardar);
        _atv?.Recarregar();
        S.Menu.Redesenhar();
    }

    public override void Contexto(Contexto ctx)
    {
        if (ctx.Competitivo) Seletor.Atual?.Cancelar();
    }

    public override void Parar()
    {
        _parado = true;
        Seletor.Atual?.Cancelar();
        _atv = null;
    }

    /// <summary>Abre a lupa (atalho, menu ou "Pegar outra" do cartão).</summary>
    private void Pegar()
    {
        if (_parado || _atv == null) return;
        if (Seletor.Atual != null) { Seletor.Atual.Cancelar(); return; }
        var ctx = S.ContextoAtual();
        string? motivo = S.Config().Pausada ? "ilha pausada"
            : ctx.Competitivo ? "modo competitivo"
            : ctx.D3DExclusivo ? "tela cheia exclusiva na frente"
            : ctx.TelaCheia ? "tela cheia na frente"
            : null;
        if (motivo != null) { Log.Escrever($"conta-gotas: não abre ({motivo})"); return; }
        // o campo de texto da ilha (ou o foco do clique) devolve o teclado para a janela de antes, que é para onde
        // ele volta quando a lupa fechar
        if (S.Entrada.Aberta) S.Entrada.Fechar();
        if (S.Ilha.Expandida != null) S.Ilha.Recolher();
        Seletor.Comecar(S.NaInterface, Pegou, () => { });
    }

    private void Pegou(CorDaTela cor)
    {
        if (_parado || _atv == null) return;
        bool copiou = Copiar(_atv.Formato("hex", cor));
        Historico.Lembrar(_cfg.Cores, cor, _cfg.Guardar);
        Salvar(_cfg);
        Log.Escrever($"conta-gotas: {cor.Hex()}");
        _atv.Mostrar(cor, copiou ? "hex" : null);
        S.Menu.Redesenhar();
        // o clique do conta-gotas não pode contar como "clique fora" para o cartão que vai abrir agora: o Seletor só
        // entrega com o botão já solto, e aqui se limpa o "apertou desde a última consulta"
        Nativo.GetAsyncKeyState(0x01);
        Nativo.GetAsyncKeyState(0x02);
        S.Ilha.Expandir(_atv);
    }

    private static bool Copiar(string texto)
    {
        // sempre na thread da interface (STA), que o Clipboard do Windows Forms exige
        try
        {
            System.Windows.Forms.Clipboard.SetDataObject(texto, true, 10, 30);
            return true;
        }
        catch (Exception e)
        {
            Log.Escrever("conta-gotas: não copiou: " + e.Message);
            return false;
        }
    }

    public override string Autoteste() => TestesContaGotas.Rodar();
}

public sealed class ConfigContaGotas
{
    [DisplayName("Cores guardadas")]
    [Description("Quantas das últimas cores ficam no cartão")]
    [Range(1, 24)]
    [JsonPropertyName("guardar")] public int Guardar { get; set; } = 12;

    [DisplayName("Hex em minúsculas")]
    [JsonPropertyName("hexMinusculo")] public bool HexMinusculo { get; set; }

    [DisplayName("Últimas cores")]
    [Description("Uma por linha, a mais nova primeiro (#RRGGBB)")]
    [JsonPropertyName("cores")] public List<string> Cores { get; set; } = new();
}
