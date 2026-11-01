using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Calculadora;

[Modulo("calculadora", "Calculadora", English = "Calculator")]
internal sealed class ModuloCalculadora : Modulo
{
    private ConfigCalculadora _cfg = new();
    private AtividadeCalculadora? _atv;

    public override Type? TipoConfig => typeof(ConfigCalculadora);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigCalculadora>(cfg);
        _atv = new AtividadeCalculadora(S, _cfg);
        S.Ilha.Adicionar(_atv);
        var ficou = S.Atalhos.Registrar("calculadora", new Atalho { Mods = Nativo.MOD_CONTROL | Nativo.MOD_ALT, Tecla = 0x4B }, () => _atv?.Alternar());
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "calculadora", Nome = Lang.T("Calculadora", "Calculator"), Ordem = 10, Abrir = () => _atv?.Alternar(),
            Icone = (dc, palco, r) =>
            {
                // calculator: body, display and a 3x2 keypad
                Icone.Retangulo(dc, r.X + 4, r.Y + 1, 16, 22, 3, Cor.Branco);
                Icone.Retangulo(dc, r.X + 6.5f, r.Y + 3.5f, 11, 5, 1.2f, new Vortice.Mathematics.Color4(0, 0, 0, 0.85f));
                for (int i = 0; i < 6; i++)
                    Icone.Circulo(dc, new System.Numerics.Vector2(r.X + 8 + i % 3 * 4, r.Y + 13 + i / 3 * 5), 1.4f, new Vortice.Mathematics.Color4(0, 0, 0, 0.85f));
            },
        });
        if (ficou == null && !(S.Config().AtalhosModulos.TryGetValue("calculadora", out var a) && a.Tecla == 0))
            S.Avisar(Lang.T("O atalho da calculadora (Ctrl + Alt + K) já é de outro programa. Troque em \"atalhosModulos\" → \"calculadora\" na configuração.",
                "The calculator shortcut (Ctrl + Alt + K) already belongs to another program. Change it in \"atalhosModulos\" → \"calculadora\" in the settings."));
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigCalculadora>(cfg);
        _atv?.TrocarConfig(_cfg);
    }

    public override void Contexto(Contexto ctx) => _atv?.Contexto(ctx);

    public override void Parar()
    {
        _atv?.Parar();
        _atv = null;
    }

    public override string Autoteste() => TestesCalculadora.Rodar();
}

public sealed class ConfigCalculadora
{
    // trigonometria em graus (como a nova aba abre); false = radianos
    [JsonPropertyName("graus")] public bool Graus { get; set; } = true;
    // buscar cotação na internet (AwesomeAPI, Frankfurter de reserva) para conversão de moeda
    [JsonPropertyName("cambio")] public bool Cambio { get; set; } = true;
    // quantas contas recentes aparecem no cartão (0 a 5)
    [JsonPropertyName("historico")] public int Historico { get; set; } = 5;
}

/// <summary>Uma conta do histórico: o que foi digitado e o que deu.</summary>
internal sealed class ItemHistorico
{
    [JsonPropertyName("conta")] public string Conta { get; set; } = "";
    [JsonPropertyName("resultado")] public string Resultado { get; set; } = "";
}

/// <summary>O que a calculadora lembra entre uma vez e outra (~/.ilha/calculadora.json): histórico, ans e a última cotação.</summary>
internal sealed class EstadoCalculadora
{
    [JsonPropertyName("historico")] public List<ItemHistorico> Historico { get; set; } = new();
    [JsonPropertyName("ans")] public double Ans { get; set; }
    [JsonPropertyName("cotacoes")] public Cotacoes? Cotacoes { get; set; }

    public const int MAXIMO = 5;

    /// <summary>A conta entra no topo; se já estava, sobe (sem repetir); passa de 5, a mais velha sai.</summary>
    public static void Lembrar(List<ItemHistorico> h, string conta, string resultado)
    {
        h.RemoveAll(x => x.Conta == conta);
        h.Insert(0, new ItemHistorico { Conta = conta, Resultado = resultado });
        if (h.Count > MAXIMO) h.RemoveRange(MAXIMO, h.Count - MAXIMO);
    }

    public static string Serializar(EstadoCalculadora e) => JsonSerializer.Serialize(e, new JsonSerializerOptions { WriteIndented = true });

    public static EstadoCalculadora Ler(string json)
    {
        var e = JsonSerializer.Deserialize<EstadoCalculadora>(json) ?? new EstadoCalculadora();
        e.Historico = e.Historico.Where(x => !string.IsNullOrWhiteSpace(x.Conta)).Take(MAXIMO).ToList();
        if (!double.IsFinite(e.Ans)) e.Ans = 0;
        if (e.Cotacoes != null && (e.Cotacoes.Real.Count < 2 || e.Cotacoes.Real.Values.Any(v => !double.IsFinite(v) || v <= 0))) e.Cotacoes = null;
        return e;
    }
}

/// <summary>
/// A calculadora na ilha. Ferramenta: nunca fica "em andamento"; o atalho abre o cartão expandido com o campo de
/// texto (S.Entrada), o resultado aparece a cada tecla, Enter copia e Esc fecha. Nada roda com ela fechada: a
/// cotação só é buscada quando uma conta de moeda aparece e a última tem mais de 10 minutos.
/// </summary>
internal sealed class AtividadeCalculadora : Atividade
{
    public override string Tipo => "calculadora";
    public override string Nome => Lang.T("Calculadora", "Calculator");
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    private static readonly HttpClient Http = Cambio.NovoHttp();
    private static readonly string ArquivoEstado = Path.Combine(Config.Pasta, "calculadora.json");
    private static (string conta, string resultado)[] Exemplos => Lang.English
        ? new[] { ("200 + 10%", "220"), ("10 km in mi", "6.21371 mi"), ("100 usd in brl", "dollar to real"), ("32 f in c", "0 °C"), ("1 GiB in GB", "1.07374 GB") }
        : new[] { ("200 + 10%", "220"), ("10 km em mi", "6,21371 mi"), ("100 usd em brl", "dólar em real"), ("32 f em c", "0 °C"), ("1 GiB em GB", "1,07374 GB") };

    private readonly Servicos S;
    private ConfigCalculadora _cfg;
    private EstadoCalculadora _estado = new();
    private bool _aberta, _parado, _copiado, _buscando;
    private string _texto = "";
    private int _cursor;
    private Resultado _res = new() { Tipo = TipoResultado.Vazio };
    private string _ultimoBom = "";
    private DateTime _falhouEm = DateTime.MinValue;
    private string? _erroRede;
    private CancellationTokenSource? _cts;

    private const float L = 360, Y_HIST = 122, LINHA = 22;

    public AtividadeCalculadora(Servicos s, ConfigCalculadora cfg)
    {
        S = s;
        _cfg = cfg;
        CarregarEstado();
    }

    public void TrocarConfig(ConfigCalculadora cfg)
    {
        _cfg = cfg;
        Recalcular();
        Redesenhar();
    }

    // ------------------------------------------------------------------ abrir e fechar

    public void Alternar()
    {
        if (_parado) return;
        if (_aberta) { S.Entrada.Fechar(); return; }
        if (S.Config().Pausada) return;
        // num jogo competitivo a ilha não rouba o teclado (e a ferramenta nem aparece)
        if (S.ContextoAtual().Competitivo) return;
        _texto = "";
        _cursor = 0;
        _copiado = false;
        _ultimoBom = "";
        Recalcular();
        // aberta antes de expandir: a cena já nasce com o cursor
        _aberta = true;
        S.Ilha.Expandir(this);
        if (S.Ilha.Expandida != this) { _aberta = false; return; }
        S.Entrada.Abrir("", AoMudar, AoEnter, AoFechar);
    }

    private void AoFechar()
    {
        if (!_aberta) return;
        _aberta = false;
        if (S.Ilha.Expandida == this) S.Ilha.Recolher();
    }

    /// <summary>
    /// A cena saiu da tela. Se foi porque a ilha recolheu por outro caminho (atalho principal, clique fora, outra
    /// ferramenta), o campo fecha junto, senão o teclado continuaria indo para a caixa invisível.
    /// </summary>
    private void VerSeAindaAberta()
    {
        if (!_aberta || S.Ilha.Expandida == this) return;
        _aberta = false;
        S.Entrada.Fechar();
    }

    public void Contexto(Contexto ctx)
    {
        if (_aberta && ctx.Competitivo) S.Entrada.Fechar();
    }

    public void Parar()
    {
        _parado = true;
        _cts?.Cancel();
        if (_aberta) S.Entrada.Fechar();
    }

    // ------------------------------------------------------------------ conta

    private void AoMudar(string texto)
    {
        // a Entrada avisa no TextChanged e de novo no KeyUp: só redesenha se o texto ou o cursor andaram
        int cursor = S.Entrada.Cursor;
        if (texto == _texto && cursor == _cursor) return;
        _cursor = cursor;
        if (texto != _texto)
        {
            _texto = texto;
            _copiado = false;
            Recalcular();
        }
        Redesenhar();
    }

    private void Recalcular()
    {
        _res = Interpretador.Interpretar(_texto, _cfg.Graus, _estado.Ans, _estado.Cotacoes);
        if (_res.Ok) _ultimoBom = _res.Principal;
        else if (_res.Tipo == TipoResultado.Vazio) _ultimoBom = "";
        if (_aberta && (_res.MoedaDe != null || _res.PrecisaCotacao)) PedirCotacao();
    }

    private void AoEnter(string texto)
    {
        if (!_res.Ok) return;
        if (!Copiar(_res.Copia)) return;
        _estado.Ans = _res.Valor;
        EstadoCalculadora.Lembrar(_estado.Historico, texto.Trim(), _res.Principal.StartsWith("= ") ? _res.Principal[2..] : _res.Principal);
        _copiado = true;
        SalvarEstado();
        Redesenhar();
    }

    private static bool Copiar(string s)
    {
        // a Entrada chama na thread da interface, que é STA (o Clipboard do Windows Forms exige)
        try
        {
            System.Windows.Forms.Clipboard.SetDataObject(s, true, 10, 30);
            return true;
        }
        catch (Exception e)
        {
            Log.Escrever("calculadora: não copiou: " + e.Message);
            return false;
        }
    }

    private void Reusar(int i)
    {
        if (!_aberta) return;
        var lista = Linhas();
        if (i < lista.Count) S.Entrada.Definir(lista[i].conta);
    }

    private List<(string conta, string resultado, bool exemplo)> LinhasComTipo()
    {
        int n = Math.Clamp(_cfg.Historico, 0, EstadoCalculadora.MAXIMO);
        var h = _estado.Historico.Take(n).Select(x => (x.Conta, x.Resultado, false)).ToList();
        if (h.Count == 0 && n > 0) h = Exemplos.Select(x => (x.conta, x.resultado, true)).ToList();
        return h;
    }

    private List<(string conta, string resultado)> Linhas() => LinhasComTipo().Select(x => (x.conta, x.resultado)).ToList();

    // ------------------------------------------------------------------ câmbio

    private void PedirCotacao()
    {
        if (!_cfg.Cambio || _buscando || _parado) return;
        var c = _estado.Cotacoes;
        if (c != null && DateTime.Now - c.BuscadoEm < Cambio.Validade && DateTime.Now >= c.BuscadoEm) return;
        if (DateTime.Now - _falhouEm < TimeSpan.FromSeconds(30)) return;
        _buscando = true;
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            Cotacoes? nova = null;
            string? erro = null;
            try { nova = await Cambio.Buscar(Http, cts.Token); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
            catch (Exception e) { erro = e.Message; }
            S.NaInterface(() =>
            {
                _buscando = false;
                if (_parado) return;
                if (nova != null)
                {
                    _estado.Cotacoes = nova;
                    _erroRede = null;
                    Log.Escrever($"calculadora: cotações de {nova.Fonte} ({nova.Real.Count - 1} moedas)");
                    SalvarEstado();
                }
                else
                {
                    _falhouEm = DateTime.Now;
                    _erroRede = erro;
                    Log.Escrever("calculadora: " + erro);
                }
                Recalcular();
                Redesenhar();
            });
        });
    }

    internal static string Hora(DateTime quando, string fonte, DateTime agora)
    {
        // a do BCE é do dia; a da AwesomeAPI tem hora
        var cultura = Lang.Culture;
        string dia = Lang.T("dd/MM", "MM/dd"), hora = Lang.T("HH:mm", "h:mm tt");
        if (fonte == "BCE") return quando.ToString(dia, cultura);
        return quando.Date == agora.Date ? quando.ToString(hora, cultura) : quando.ToString(dia + " " + hora, cultura);
    }

    private string LinhaDeBaixo(out Color4 cor)
    {
        cor = Cor.Secundario;
        if (_copiado) { cor = Cor.Verde; return Lang.T("Copiado: ", "Copied: ") + _res.Copia; }
        if (_res.Tipo == TipoResultado.Vazio) return Lang.T("Contas, unidades (10 km em mi) e moedas (100 usd em brl)", "Math, units (10 km in mi) and currencies (100 usd in brl)");
        if (_res.PrecisaCotacao)
        {
            if (!_cfg.Cambio) return Lang.T("A cotação está desligada (\"cambio\" na configuração).", "Exchange rates are off (\"cambio\" in the settings).");
            if (!_buscando && _erroRede != null) return _erroRede;
            return Lang.T("Buscando a cotação...", "Fetching the exchange rate...");
        }
        if (_res.Tipo == TipoResultado.Moeda && _estado.Cotacoes is { } c)
        {
            var agora = DateTime.Now;
            string quando = $"{Cambio.NomeDaFonte(c.Fonte)} {Hora(c.QuandoDe(_res.MoedaRef), c.Fonte, agora)}";
            bool velha = agora - c.BuscadoEm >= Cambio.Validade;
            string sufixo = velha && _erroRede != null ? Lang.T($"sem rede, cotação de {quando}", $"offline, rate from {quando}")
                : velha && _buscando ? Lang.T($"atualizando ({quando})", $"updating ({quando})") : quando;
            return _res.Detalhe.Length > 0 ? $"{_res.Detalhe} · {sufixo}" : sufixo;
        }
        return _res.Detalhe;
    }

    // ------------------------------------------------------------------ estado em disco

    private void CarregarEstado()
    {
        try
        {
            if (File.Exists(ArquivoEstado)) _estado = EstadoCalculadora.Ler(File.ReadAllText(ArquivoEstado));
        }
        catch (Exception e)
        {
            Log.Escrever("calculadora: estado ilegível, começando do zero: " + e.Message);
            _estado = new EstadoCalculadora();
        }
    }

    private void SalvarEstado()
    {
        try
        {
            Directory.CreateDirectory(Config.Pasta);
            string tmp = ArquivoEstado + ".tmp";
            File.WriteAllText(tmp, EstadoCalculadora.Serializar(_estado));
            File.Move(tmp, ArquivoEstado, overwrite: true);
        }
        catch (Exception e) { Log.Escrever("calculadora: não salvou o estado: " + e.Message); }
    }

    // ------------------------------------------------------------------ desenho

    protected override Cena Montar(Palco p, Forma f)
    {
        if (f is Forma.Compacta or Forma.Bolha)
        {
            // não costuma aparecer (a calculadora não fica em andamento), mas toda forma tem cena
            var c = p.NovaCena(f == Forma.Bolha ? new Vector2(24, 24) : new Vector2(150, 24));
            p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
            {
                Simbolo(dc, p, 2, 2, 20);
                if (f == Forma.Compacta)
                {
                    string s = _ultimoBom.Length > 0 ? _ultimoBom : Lang.T("Calculadora", "Calculator");
                    var tam = p.Texto.Medir(s, 13, FontWeight.SemiBold, tabular: true);
                    float l = MathF.Min(tam.X, t.X - 30);
                    p.Texto.Desenhar(dc, s, new Vector2(t.X - l, (t.Y - tam.Y) / 2), 13, Cor.Branco, tabular: true, largura: t.X - 30);
                }
            });
            return c;
        }

        int linhasNoCartao = Math.Clamp(_cfg.Historico, 0, EstadoCalculadora.MAXIMO) > 0 ? EstadoCalculadora.MAXIMO : 0;
        var cena = p.NovaCena(new Vector2(L, linhasNoCartao > 0 ? Y_HIST + LINHA * linhasNoCartao : Y_HIST - 10));
        p.Desenho(cena, Vector2.Zero, cena.Tamanho, (dc, t) =>
        {
            p.Texto.Desenhar(dc, Lang.T("Calculadora", "Calculator"), new Vector2(0, 0), 12, Cor.Secundario);
            string dica = (_cfg.Graus ? Lang.T("graus", "degrees") : Lang.T("radianos", "radians")) + Lang.T(" · Enter copia · Esc fecha", " · Enter copies · Esc closes");
            var td = p.Texto.Medir(dica, 11, FontWeight.Normal);
            p.Texto.Desenhar(dc, dica, new Vector2(t.X - td.X, 1), 11, Cor.Secundario, FontWeight.Normal);

            // a cópia do módulo, não a S.Entrada: ela é dividida com outras ferramentas e pode ter o texto de outra
            p.Texto.DesenharCampo(dc, _texto, _cursor, false, new Vector2(0, 20), t.X, 18, Cor.Branco, Lang.T("Conta ou conversão", "Calculation or conversion"), comCursor: _aberta);

            // errado no meio da digitação ("2+"): o último resultado bom fica, apagado, em vez de piscar
            string grande = _res.Ok ? _res.Principal : _ultimoBom;
            var corGrande = _res.Ok ? Cor.Branco : new Color4(1f, 1f, 1f, 0.32f);
            if (grande.Length > 0) p.Texto.Desenhar(dc, grande, new Vector2(-1, 46), 30, corGrande, FontWeight.SemiBold, tabular: true, largura: t.X);

            string baixo = LinhaDeBaixo(out var corBaixo);
            p.Texto.Desenhar(dc, baixo, new Vector2(0, 92), 12, corBaixo, FontWeight.Normal, largura: t.X);

            if (linhasNoCartao > 0) Icone.Retangulo(dc, 0, Y_HIST - 7, t.X, 1, 0.5f, Cor.Fraco);
            var linhas = LinhasComTipo();
            for (int i = 0; i < linhas.Count; i++)
            {
                var (conta, resultado, exemplo) = linhas[i];
                float y = Y_HIST + i * LINHA + 2;
                var corConta = exemplo ? Cor.Secundario : new Color4(1f, 1f, 1f, 0.86f);
                var tr = p.Texto.Medir(resultado, 13, FontWeight.Normal, tabular: true);
                float lr = MathF.Min(tr.X, 150);
                p.Texto.Desenhar(dc, conta, new Vector2(0, y), 13, corConta, FontWeight.Normal, largura: t.X - lr - 14);
                p.Texto.Desenhar(dc, resultado, new Vector2(t.X - lr, y), 13, Cor.Secundario, FontWeight.Normal, tabular: true, largura: 150);
            }
        });
        for (int i = 0; i < linhasNoCartao; i++)
        {
            int k = i;
            cena.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(0, Y_HIST + i * LINHA, L, LINHA), Acao = () => Reusar(k) });
        }
        cena.AoSoltar += () => S.NaInterface(VerSeAindaAberta);
        return cena;
    }

    /// <summary>O ícone da calculadora: um quadrado arredondado com "+ − × =" em quatro casas.</summary>
    private static void Simbolo(Vortice.Direct2D1.ID2D1DeviceContext dc, Palco p, float x, float y, float l)
    {
        Icone.Retangulo(dc, x, y, l, l, l * 0.26f, Cor.Laranja);
        float m = l / 2, q = l / 4, e = MathF.Max(1.4f, l * 0.08f), b = l * 0.13f;
        var F = p.G.Fabrica;
        var preto = new Color4(0f, 0f, 0f, 0.85f);
        // +
        Icone.Linha(dc, new Vector2(x + q - b, y + q), new Vector2(x + q + b, y + q), e, preto, F);
        Icone.Linha(dc, new Vector2(x + q, y + q - b), new Vector2(x + q, y + q + b), e, preto, F);
        // −
        Icone.Linha(dc, new Vector2(x + m + q - b, y + q), new Vector2(x + m + q + b, y + q), e, preto, F);
        // ×
        Icone.Linha(dc, new Vector2(x + q - b * 0.8f, y + m + q - b * 0.8f), new Vector2(x + q + b * 0.8f, y + m + q + b * 0.8f), e, preto, F);
        Icone.Linha(dc, new Vector2(x + q + b * 0.8f, y + m + q - b * 0.8f), new Vector2(x + q - b * 0.8f, y + m + q + b * 0.8f), e, preto, F);
        // =
        Icone.Linha(dc, new Vector2(x + m + q - b, y + m + q - b * 0.45f), new Vector2(x + m + q + b, y + m + q - b * 0.45f), e, preto, F);
        Icone.Linha(dc, new Vector2(x + m + q - b, y + m + q + b * 0.45f), new Vector2(x + m + q + b, y + m + q + b * 0.45f), e, preto, F);
    }
}
