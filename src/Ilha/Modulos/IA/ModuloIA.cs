using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ilha.IA;

/// <summary>
/// Chat rápido com GPT e Claude pela API (streaming, histórico curto, print colado) e o tradutor de texto
/// selecionado. Os dois usam o mesmo provedor: troca de lado sozinha quando o crédito de um acaba. As chaves
/// ficam no Gerenciador de Credenciais (Ilha/openai e Ilha/anthropic), digitadas numa tela da própria ilha.
/// </summary>
[Modulo("ia", "IA e tradutor")]
internal sealed class ModuloIA : Modulo
{
    private ConfigIA _cfg = new();
    private ClienteIA? _cliente;
    private Tradutor? _tradutor;
    private AtividadeChat? _chat;
    private AtividadeTradutor? _trad;

    public override Type? TipoConfig => typeof(ConfigIA);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigIA>(cfg);
        _cliente = new ClienteIA(nome => Credenciais.Ler(nome));
        _tradutor = new Tradutor(_cliente);
        AplicarConfig();
        _chat = new AtividadeChat(S, _cliente, () => _cfg, c => { _cfg = c; Salvar(c); });
        _trad = new AtividadeTradutor(S, _tradutor);
        S.Ilha.Adicionar(_chat);
        S.Ilha.Adicionar(_trad);
        var aChat = S.Atalhos.Registrar("ia", new Atalho { Mods = Nativo.MOD_CONTROL | Nativo.MOD_ALT, Tecla = 0x4A }, () => _chat.Atalho()); // J
        var aTrad = S.Atalhos.Registrar("tradutor", new Atalho { Mods = Nativo.MOD_CONTROL | Nativo.MOD_ALT, Tecla = 0x54 }, () => _trad.Atalho(TeclaDoAtalho("tradutor", 0x54))); // T
        if (aChat == null) S.Avisar("O atalho do chat com IA (Ctrl + Alt + J) está ocupado por outro programa. Troque em atalhosModulos → ia.");
        if (aTrad == null) S.Avisar("O atalho do tradutor (Ctrl + Alt + T) está ocupado por outro programa. Troque em atalhosModulos → tradutor.");
        S.Ilha.EstadoMudou += AoMudarEstado;
    }

    private int TeclaDoAtalho(string nome, int padrao) =>
        S.Config().AtalhosModulos.TryGetValue(nome, out var a) ? a.Tecla : padrao;

    private void AplicarConfig()
    {
        if (_tradutor == null) return;
        _tradutor.LimiteGoogle = Math.Max(0, _cfg.LimiteGoogle);
        _tradutor.LimiteIA = Math.Clamp(_cfg.LimiteTradutorIA, 500, 100_000);
        _tradutor.ModeloIA = string.IsNullOrWhiteSpace(_cfg.TradutorModelo) ? "gpt-6-luna" : _cfg.TradutorModelo.Trim();
        _tradutor.ReservaIA = _cfg.TradutorReserva;
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigIA>(cfg);
        AplicarConfig();
        _chat?.ConfigMudou();
    }

    // a ilha recolheu: o que estava respondendo para, ninguém vai ver (a troca de cartão sem mudar de estado
    // chega pelo ExpandidaMudou das atividades)
    private void AoMudarEstado(Estado e)
    {
        if (S.Ilha.Expandida != _chat) _chat?.Interromper(fecharCampo: true);
        if (S.Ilha.Expandida != _trad) _trad?.Interromper(fecharCampo: true);
    }

    public override void Contexto(Contexto ctx)
    {
        if (!ctx.Competitivo) return;
        _chat?.Interromper(fecharCampo: true);
        _trad?.Interromper(fecharCampo: true);
    }

    public override void Parar()
    {
        S.Ilha.EstadoMudou -= AoMudarEstado;
        _chat?.Interromper(fecharCampo: true);
        _trad?.Interromper(fecharCampo: true);
    }

    public override string Autoteste() => TestesIA.Rodar();
}

public sealed class ConfigIA
{
    /// <summary>Modelo do chat (claude-* vai para a Anthropic, o resto para a OpenAI).</summary>
    [JsonPropertyName("modelo")] public string Modelo { get; set; } = "gpt-6-luna";
    /// <summary>Os modelos que o botão do chat alterna.</summary>
    [JsonPropertyName("modelos")] public List<string> Modelos { get; set; } = new() { "gpt-6-luna", "claude-sonnet-5-5", "gpt-6.1-sol" };
    /// <summary>none, low, medium, high (o 6.1 Sol não aceita none: vai low).</summary>
    [JsonPropertyName("esforco")] public string Esforco { get; set; } = "low";
    /// <summary>"auto" (o par do outro lado), um id do outro lado ou "nenhuma".</summary>
    [JsonPropertyName("reserva")] public string Reserva { get; set; } = "auto";
    [JsonPropertyName("trocasNoHistorico")] public int TrocasNoHistorico { get; set; } = 6;
    [JsonPropertyName("maxSaida")] public int MaxSaida { get; set; } = 8192;
    [JsonPropertyName("ladoMaiorDaImagem")] public int LadoMaiorDaImagem { get; set; } = 2576;
    [JsonPropertyName("tradutorModelo")] public string TradutorModelo { get; set; } = "gpt-6-luna";
    [JsonPropertyName("tradutorReserva")] public string TradutorReserva { get; set; } = "auto";
    /// <summary>Até quantos caracteres vai pelo Google (de graça); acima, pela IA.</summary>
    [JsonPropertyName("limiteGoogle")] public int LimiteGoogle { get; set; } = 1200;
    /// <summary>Texto maior que isso é cortado antes de ir para a IA (freio de gasto).</summary>
    [JsonPropertyName("limiteTradutorIA")] public int LimiteTradutorIA { get; set; } = 12000;
}

/// <summary>Nomes dos modelos para a tela.</summary>
internal static class Modelos
{
    private static readonly Dictionary<string, string> Conhecidos = new()
    {
        ["gpt-6-luna"] = "GPT-6 Luna", ["gpt-6.1-sol"] = "GPT-6.1 Sol", ["gpt-6-sol"] = "GPT-6 Sol", ["gpt-6-astra"] = "GPT-6 Astra",
        ["claude-haiku-5-5"] = "Claude Haiku 5.5", ["claude-sonnet-5-5"] = "Claude Sonnet 5.5", ["claude-opus-5-5"] = "Claude Opus 5.5",
    };

    public static string Rotulo(string id)
    {
        if (Conhecidos.TryGetValue(id, out var r)) return r;
        var m = Regex.Match(id, @"^claude-([a-z]+)-(\d+)(?:-(\d+))?$");
        if (m.Success)
            return "Claude " + char.ToUpper(m.Groups[1].Value[0]) + m.Groups[1].Value[1..] + " " + m.Groups[2].Value + (m.Groups[3].Success ? "." + m.Groups[3].Value : "");
        if (id.StartsWith("gpt-"))
        {
            var partes = id[4..].Split('-');
            return "GPT-" + partes[0] + string.Concat(partes.Skip(1).Select(p => " " + (p.Length > 0 ? char.ToUpper(p[0]) + p[1..] : p)));
        }
        return id;
    }

    /// <summary>O esforço que o modelo aceita (o 6.1 Sol e o Astra não têm none).</summary>
    public static string Esforco(string modelo, string esforco) =>
        esforco == "none" && (modelo.StartsWith("gpt-6.1-sol") || modelo.StartsWith("gpt-6-astra")) ? "low" : esforco;

    public static string Dinheiro(double? custo)
    {
        if (custo == null) return "";
        if (custo < 0.0001) return "< US$ 0,0001";
        return "US$ " + custo.Value.ToString(custo < 0.01 ? "0.0000" : "0.000", CultureInfo.GetCultureInfo("pt-BR"));
    }
}

/// <summary>Junta pedidos de redesenho num quadro a cada ~33 ms (o stream manda dezenas de pedaços por segundo).</summary>
internal sealed class Agendador : IDisposable
{
    private readonly System.Windows.Forms.Timer _t = new() { Interval = 33 };

    public Agendador(Action acao)
    {
        _t.Tick += (_, _) => { _t.Stop(); acao(); };
    }

    public void Pedir() { if (!_t.Enabled) _t.Start(); }

    public void Dispose() => _t.Dispose();
}

/// <summary>Limpeza do que o modelo escreve, só para a tela: negrito, títulos e cercas de código de Markdown.</summary>
internal static class Limpeza
{
    public static string ParaTela(string texto)
    {
        if (texto.Length == 0) return texto;
        var linhas = texto.Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.TrimStart().StartsWith("```"))
            .Select(l => Regex.Replace(l, @"^\s{0,3}#{1,6}\s+", ""));
        string t = string.Join("\n", linhas);
        t = Regex.Replace(t, @"\*\*(.+?)\*\*", "$1");
        t = Regex.Replace(t, @"__(.+?)__", "$1");
        t = Regex.Replace(t, @"(?m)^(\s*)\* ", "$1- ");
        return t;
    }
}
