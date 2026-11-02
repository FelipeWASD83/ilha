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
[Modulo("ia", "IA e tradutor", English = "AI and translator")]
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
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "ia", Nome = Lang.T("Chat de IA", "AI chat"), Ordem = 12, Abrir = () => _chat.Atalho(),
            Icone = (dc, palco, r) =>
            {
                // speech bubble with three dots
                Icone.Retangulo(dc, r.X + 1, r.Y + 3, 22, 15, 5, Cor.Branco);
                Icone.Triangulo(dc, palco.G.Fabrica, new System.Numerics.Vector2(r.X + 6, r.Y + 16), new System.Numerics.Vector2(r.X + 12, r.Y + 16), new System.Numerics.Vector2(r.X + 5, r.Y + 22), Cor.Branco);
                for (int i = 0; i < 3; i++)
                    Icone.Circulo(dc, new System.Numerics.Vector2(r.X + 7 + i * 5, r.Y + 10.5f), 1.5f, new Vortice.Mathematics.Color4(0, 0, 0, 0.85f));
            },
        });
        S.Menu.Adicionar(new ItemDoMenu
        {
            // where recording was before it moved to the menu's corner
            Id = "tradutor", Nome = Lang.T("Tradutor", "Translator"), Ordem = 55, Abrir = () => _trad.AbrirParaDigitar(),
            Detalhe = () => "PT ↔ EN",
            Icone = (dc, palco, r) => DesenharIconeTradutor(dc, palco, r),
        });
        var aTrad = S.Atalhos.Registrar("tradutor", new Atalho { Mods = Nativo.MOD_CONTROL | Nativo.MOD_ALT, Tecla = 0x54 }, () => _trad.Atalho(TeclaDoAtalho("tradutor", 0x54))); // T
        if (aChat == null) S.Avisar(Lang.T("O atalho do chat com IA (Ctrl + Alt + J) está ocupado por outro programa. Troque em atalhosModulos → ia.",
            "The AI chat shortcut (Ctrl + Alt + J) is taken by another program. Change it in atalhosModulos → ia."));
        if (aTrad == null) S.Avisar(Lang.T("O atalho do tradutor (Ctrl + Alt + T) está ocupado por outro programa. Troque em atalhosModulos → tradutor.",
            "The translator shortcut (Ctrl + Alt + T) is taken by another program. Change it in atalhosModulos → tradutor."));
        S.Ilha.EstadoMudou += AoMudarEstado;
    }

    /// <summary>Two speech bubbles: a dim one with an "A" behind, a white one with a stroked 文 in front (all lines, no font).</summary>
    private static void DesenharIconeTradutor(Vortice.Direct2D1.ID2D1DeviceContext dc, Palco p, System.Drawing.RectangleF r)
    {
        float u = r.Width / 24f;
        System.Numerics.Vector2 P(float x, float y) => new(r.X + x * u, r.Y + y * u);
        var f = p.G.Fabrica;
        var tinta = new Vortice.Mathematics.Color4(0, 0, 0, 0.85f);
        var atras = new Vortice.Mathematics.Color4(1, 1, 1, 0.6f);
        Icone.Retangulo(dc, r.X + 1 * u, r.Y + 2 * u, 13 * u, 11 * u, 3.5f * u, atras);
        Icone.Triangulo(dc, f, P(3, 12.5f), P(6.5f, 12.5f), P(2.5f, 16), atras);
        Icone.Linha(dc, P(4.5f, 11), P(7.5f, 4.5f), 1.4f * u, tinta, f);
        Icone.Linha(dc, P(7.5f, 4.5f), P(10.5f, 11), 1.4f * u, tinta, f);
        Icone.Linha(dc, P(5.6f, 8.9f), P(9.4f, 8.9f), 1.3f * u, tinta, f);
        // a gap in the ball's own gray between the two bubbles
        Icone.Retangulo(dc, r.X + 8 * u, r.Y + 8 * u, 16 * u, 13 * u, 4.5f * u, new Vortice.Mathematics.Color4(0.06f, 0.06f, 0.06f, 1f));
        Icone.Retangulo(dc, r.X + 9 * u, r.Y + 9 * u, 14 * u, 11 * u, 3.5f * u, Cor.Branco);
        Icone.Triangulo(dc, f, P(17.5f, 19.5f), P(21, 19.5f), P(21.5f, 23), Cor.Branco);
        Icone.Linha(dc, P(16, 10.6f), P(16, 12.2f), 1.3f * u, tinta, f);
        Icone.Linha(dc, P(12.4f, 13), P(19.6f, 13), 1.3f * u, tinta, f);
        Icone.Linha(dc, P(13.6f, 13.6f), P(18.8f, 18.8f), 1.3f * u, tinta, f);
        Icone.Linha(dc, P(18.4f, 13.6f), P(13.2f, 18.8f), 1.3f * u, tinta, f);
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
    [Label("Modelo", "Model")]
    [JsonPropertyName("modelo")] public string Modelo { get; set; } = "gpt-6-luna";
    /// <summary>Os modelos que o botão do chat alterna.</summary>
    [Label("Modelos", "Models")]
    [JsonPropertyName("modelos")] public List<string> Modelos { get; set; } = new() { "gpt-6-luna", "claude-sonnet-5-5", "gpt-6.1-sol" };
    /// <summary>none, low, medium, high (o 6.1 Sol não aceita none: vai low).</summary>
    [Label("Esforço", "Effort")]
    [JsonPropertyName("esforco")] public string Esforco { get; set; } = "low";
    /// <summary>"auto" (o par do outro lado), um id do outro lado ou "nenhuma".</summary>
    [Label("Reserva", "Fallback")]
    [JsonPropertyName("reserva")] public string Reserva { get; set; } = "auto";
    [Label("Trocas no histórico", "Exchanges in history")]
    [JsonPropertyName("trocasNoHistorico")] public int TrocasNoHistorico { get; set; } = 6;
    [Label("Saída máxima (tokens)", "Max output (tokens)")]
    [JsonPropertyName("maxSaida")] public int MaxSaida { get; set; } = 8192;
    [Label("Lado maior da imagem (px)", "Longest image side (px)")]
    [JsonPropertyName("ladoMaiorDaImagem")] public int LadoMaiorDaImagem { get; set; } = 2576;
    [Label("Modelo do tradutor", "Translator model")]
    [JsonPropertyName("tradutorModelo")] public string TradutorModelo { get; set; } = "gpt-6-luna";
    [Label("Reserva do tradutor", "Translator fallback")]
    [JsonPropertyName("tradutorReserva")] public string TradutorReserva { get; set; } = "auto";
    /// <summary>Até quantos caracteres vai pelo Google (de graça); acima, pela IA.</summary>
    [Label("Limite do Google (caracteres)", "Google limit (characters)")]
    [JsonPropertyName("limiteGoogle")] public int LimiteGoogle { get; set; } = 1200;
    /// <summary>Texto maior que isso é cortado antes de ir para a IA (freio de gasto).</summary>
    [Label("Limite do tradutor por IA (caracteres)", "AI translator limit (characters)")]
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
        if (custo < 0.0001) return Lang.T("< US$ 0,0001", "< $0.0001");
        return Lang.T("US$ ", "$") + custo.Value.ToString(custo < 0.01 ? "0.0000" : "0.000", Lang.Culture);
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
