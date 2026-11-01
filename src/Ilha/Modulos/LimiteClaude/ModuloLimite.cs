using System;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Vortice.DirectWrite;

namespace Ilha.LimiteClaude;

/// <summary>
/// Limite do plano do Claude (janela de 5 horas e semanal) a partir da única fonte confiável: o rate_limits que o
/// Claude Code manda para a status line, gravado em ~/.ilha/uso-claude.json pelo ferramentas/statusline_uso.py.
/// Vigia o arquivo por evento (FileSystemWatcher), sem polling. Entrada no menu, cartão com as duas barras e um aviso
/// por janela quando o uso passa do limiar.
/// </summary>
[Modulo("limite", "Limite do Claude", English = "Claude limit")]
internal sealed class ModuloLimite : Modulo
{
    public const string NOME_DO_ARQUIVO = "uso-claude.json";

    private ConfigLimite _cfg = new();
    private AtividadeLimite? _atv;
    private FileSystemWatcher? _vigia;
    private readonly System.Windows.Forms.Timer _ler = new() { Interval = 150 };
    private readonly System.Windows.Forms.Timer _reset = new();
    private readonly AvisosDoLimite _avisos = new();
    private LeituraDoUso? _leitura;
    private bool _existe, _primeira = true, _parado, _avisouQuebrado;
    private int _tentativas;

    public override Type? TipoConfig => typeof(ConfigLimite);

    private static string Arquivo => Path.Combine(Config.Pasta, NOME_DO_ARQUIVO);
    private int[] Limiares => AvisosDoLimite.Limiares(_cfg.LimiarAlerta, _cfg.SegundoLimiar);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigLimite>(cfg);
        _atv = new AtividadeLimite(() => _leitura, () => _existe, () => Limiares);
        S.Ilha.Adicionar(_atv);
        var atv = _atv;
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "limite",
            // a célula do menu tem 76 DIP de texto: "Limite do Claude" não caberia sem reticências
            Nome = Lang.T("Limite", "Limit"),
            Abrir = () => S.Ilha.Expandir(atv),
            Icone = (dc, palco, r) => atv.Anel(dc, palco, new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2 - 2, 3f),
            Detalhe = () => TextoDoUso.Detalhe(_leitura, AtividadeLimite.Agora()),
        });
        _ler.Tick += (_, _) => Carregar();
        _reset.Tick += (_, _) => { _reset.Stop(); Mostrar(); };
        Vigiar();
        Carregar();
        // a seção nasce só com "ligado": grava os campos com os padrões para quem usa achar onde mexer
        if (!cfg.ContainsKey("limiarAlerta")) Salvar(_cfg);
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigLimite>(cfg);
        _atv?.Atualizar();
        S.Menu.Redesenhar();
    }

    /// <summary>Saiu do jogo ou desligou o Não Incomodar: o aviso que ficou para trás aparece agora.</summary>
    public override void Contexto(Contexto ctx)
    {
        if (!_parado && !_primeira && _leitura != null) Avisar();
    }

    public override void Parar()
    {
        _parado = true;
        if (_vigia != null)
        {
            _vigia.EnableRaisingEvents = false;
            _vigia.Dispose();
            _vigia = null;
        }
        _ler.Stop();
        _ler.Dispose();
        _reset.Stop();
        _reset.Dispose();
        _atv?.Dispose();
    }

    // ------------------------------------------------------------------ arquivo

    private void Vigiar()
    {
        try
        {
            Directory.CreateDirectory(Config.Pasta);
            _vigia = new FileSystemWatcher(Config.Pasta, NOME_DO_ARQUIVO)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            // o script grava num temporário e troca de nome: chega como Renamed
            _vigia.Changed += (_, _) => Sinal();
            _vigia.Created += (_, _) => Sinal();
            _vigia.Deleted += (_, _) => Sinal();
            _vigia.Renamed += (_, _) => Sinal();
            _vigia.Error += (_, e) =>
            {
                try { Log.Escrever("limite: vigia do arquivo: " + e.GetException().Message); } catch { }
                Sinal();
            };
            _vigia.EnableRaisingEvents = true;
        }
        catch (Exception e) { Log.Escrever("limite: não deu para vigiar " + NOME_DO_ARQUIVO + ": " + e.Message); }
    }

    /// <summary>Numa thread do pool, vindo do FileSystemWatcher: junta a rajada de eventos de uma gravação numa leitura só.</summary>
    private void Sinal()
    {
        try
        {
            S.NaInterface(() =>
            {
                if (_parado) return;
                _ler.Stop();
                _ler.Interval = 150;
                _ler.Start();
            });
        }
        catch (Exception) { }
    }

    private void Carregar()
    {
        _ler.Stop();
        if (_parado) return;
        string? texto = null;
        double arquivoEm = 0;
        bool existe;
        try
        {
            var fi = new FileInfo(Arquivo);
            existe = fi.Exists;
            if (existe)
            {
                using var fs = new FileStream(Arquivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                texto = sr.ReadToEnd();
                arquivoEm = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // pego no meio da troca de nome: tenta de novo daqui a pouco
            if (_tentativas++ < 3) { _ler.Interval = 300; _ler.Start(); }
            else { _tentativas = 0; Log.Escrever("limite: não deu para ler " + NOME_DO_ARQUIVO + ": " + e.Message); }
            return;
        }
        _tentativas = 0;

        var nova = texto == null ? null : LeituraDoUso.Ler(texto, arquivoEm);
        if (existe && nova == null)
        {
            // arquivo quebrado: fica com o que já tinha
            if (!_avisouQuebrado) Log.Escrever("limite: " + NOME_DO_ARQUIVO + " não está no formato esperado");
            _avisouQuebrado = true;
        }
        else
        {
            _avisouQuebrado = false;
            _leitura = nova;
        }
        _existe = existe;

        if (_leitura != null)
        {
            // a primeira leitura é o que já estava lá antes de a ilha abrir: monta o estado sem avisar
            if (_primeira) _avisos.MarcarTudo(_leitura, AtividadeLimite.Agora(), Limiares);
            else Avisar();
        }
        _primeira = false;
        Mostrar();
    }

    /// <summary>Redesenha cartão e menu e agenda o relógio para o próximo reset (é quando uma janela sai).</summary>
    private void Mostrar()
    {
        if (_parado) return;
        _atv?.Atualizar();
        S.Menu.Redesenhar();
        _reset.Stop();
        double agora = AtividadeLimite.Agora();
        if (_leitura?.ProximoReset(agora) is not double prox) return;
        _reset.Interval = (int)Math.Clamp((prox - agora) * 1000 + 500, 500, int.MaxValue / 2);
        _reset.Start();
    }

    // ------------------------------------------------------------------ avisos

    private void Avisar()
    {
        if (_leitura == null || _atv == null) return;
        var limiares = Limiares;
        if (limiares.Length == 0) return;
        var l = _leitura;
        foreach (var (j, limiar) in _avisos.Pendentes(l, AtividadeLimite.Agora(), limiares))
        {
            var a = NovoAlerta(j);
            // barrado agora (jogo competitivo, Não Incomodar, pausada): não marca, tenta de novo quando o contexto mudar
            if (!S.Ilha.PodeMostrarAlerta(a)) continue;
            S.Ilha.Alertar(a);
            _avisos.Marcar(j, limiar, l.ColetadoEm, limiares);
            Log.Escrever($"limite: {j.Id} em {TextoDoUso.Porcento(j.Porcentagem)} (limiar {limiar}%)");
        }
    }

    private Alerta NovoAlerta(Janela j)
    {
        var atv = _atv!;
        string pct = TextoDoUso.Porcento(j.Porcentagem);
        string titulo = j.Semanal ? Lang.T($"Claude: {pct} da semana", $"Claude: {pct} of the week") : Lang.T($"Claude: {pct} da sessão", $"Claude: {pct} of the session");
        string sub = (j.Semanal ? Lang.T("limite semanal · ", "weekly limit · ") : Lang.T("limite de 5 horas · ", "5-hour limit · ")) + TextoDoUso.Volta(j, DateTime.Now);
        var cor = AtividadeLimite.CorDe(j.Porcentagem, Limiares);
        float progresso = (float)(j.Porcentagem / 100);
        return new Alerta
        {
            Tipo = "limite",
            Som = "alerta",
            Dono = atv,
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(300, 52));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    Icone.Anel(dc, p.G.Fabrica, new Vector2(22, 26), 14, 4, progresso, cor);
                    p.Texto.Desenhar(dc, titulo, new Vector2(56, 4), 17, Cor.Branco, largura: t.X - 56);
                    p.Texto.Desenhar(dc, sub, new Vector2(56, 28), 13, Cor.Secundario, FontWeight.Normal, largura: t.X - 56);
                });
                return c;
            },
        };
    }

    // ------------------------------------------------------------------ autoteste

    public override string Autoteste() => AutotesteLimite.Rodar();
}

/// <summary>Config do módulo (config.json, "modulos" > "limite").</summary>
public sealed class ConfigLimite
{
    /// <summary>Avisa uma vez por janela quando o uso passa disto (0 desliga os avisos).</summary>
    [Label("Limiar do alerta (%)", "Alert threshold (%)")]
    [JsonPropertyName("limiarAlerta")] public int LimiarAlerta { get; set; } = 80;
    /// <summary>Segundo aviso na mesma janela (0 desliga; só vale acima do primeiro).</summary>
    [Label("Segundo limiar (%)", "Second threshold (%)")]
    [JsonPropertyName("segundoLimiar")] public int SegundoLimiar { get; set; } = 95;
    /// <summary>
    /// A status line que já existia antes, que o ferramentas/statusline_uso.py roda depois de gravar o uso (é ele que
    /// lê este campo; a ilha não usa).
    /// </summary>
    [Label("Status line anterior", "Previous status line")]
    [JsonPropertyName("statuslineAnterior")] public string StatuslineAnterior { get; set; } = "";
}
