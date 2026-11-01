using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Ilha.Claude;

/// <summary>
/// Uma linha da fila de eventos que os hooks do Claude Code escrevem (um JSON por linha). Só os campos que a ilha usa;
/// texto livre (fala, balão, título) nunca é guardado aqui, por privacidade.
/// </summary>
internal sealed record Evento
{
    public double T { get; init; }
    public string Tipo { get; init; } = "";
    public string Sessao { get; init; } = "";
    public string Projeto { get; init; } = "";
    public bool Ligada { get; init; }
    public bool Limpa { get; init; }
    /// <summary>"Editando x.cs", "Lendo y", a descrição do comando... Só vira texto na tela pelo <see cref="Neutro"/>.</summary>
    public string Detalhe { get; init; } = "";
    /// <summary>Tipo do aviso de uma notificação (permission_prompt, idle_prompt...).</summary>
    public string Aviso { get; init; } = "";
    public bool Pergunta { get; init; }
    public double? Duracao { get; init; }

    /// <summary>Lê uma linha da fila; null se não for um evento (linha vazia, JSON quebrado, sem tipo ou sem hora).</summary>
    public static Evento? Ler(string linha)
    {
        if (string.IsNullOrWhiteSpace(linha)) return null;
        try
        {
            using var doc = JsonDocument.Parse(linha);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            double t = Numero(r, "t") ?? 0;
            string tipo = Texto(r, "tipo");
            if (t <= 0 || tipo.Length == 0) return null;
            return new Evento
            {
                T = t,
                Tipo = tipo,
                Sessao = Texto(r, "sessao"),
                Projeto = Texto(r, "projeto"),
                Ligada = Logico(r, "ligada"),
                Limpa = Logico(r, "limpa"),
                Detalhe = Texto(r, "detalhe"),
                Aviso = Texto(r, "aviso"),
                Pergunta = Logico(r, "pergunta"),
                Duracao = Numero(r, "duracao"),
            };
        }
        catch (JsonException) { return null; }
    }

    private static string Texto(JsonElement r, string nome) =>
        r.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Logico(JsonElement r, string nome) =>
        r.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.True;

    private static double? Numero(JsonElement r, string nome) =>
        r.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}

/// <summary>
/// Lê a fila do ponto onde parou. A fila não é consumida (outro programa pode ler o mesmo arquivo), então cada leitor guarda a
/// própria posição. Quando o hook corta o começo do arquivo (reescreve só as últimas linhas), o arquivo encolhe ou é
/// trocado: aí relê do começo e pula o que já viu pela hora do evento.
/// </summary>
internal sealed class LeitorDeFila
{
    public long Posicao { get; private set; }
    private double _maiorT;
    private readonly HashSet<double> _vistos = new();
    private readonly Queue<double> _ordem = new();
    private const int LEMBRAR = 1024;

    /// <summary>O arquivo pode ter sido trocado (Created/Renamed do vigia): a próxima leitura volta ao começo.</summary>
    public void Recomecar() => Posicao = -1;

    /// <summary>Lê o que é novo no fluxo (já aberto pelo chamador) e devolve os eventos que ainda não tinham saído.</summary>
    public List<Evento> Ler(Stream s)
    {
        long tamanho = s.Length;
        long desde = Posicao;
        bool doComeco = false;
        if (desde < 0 || tamanho < desde)
        {
            desde = 0;
            doComeco = true;
        }
        var eventos = new List<Evento>();
        if (tamanho == desde) { Posicao = desde; return eventos; }
        s.Seek(desde, SeekOrigin.Begin);
        var bruto = new byte[tamanho - desde];
        int lidos = 0;
        while (lidos < bruto.Length)
        {
            int n = s.Read(bruto, lidos, bruto.Length - lidos);
            if (n <= 0) break;
            lidos += n;
        }
        // linha pela metade (o hook ainda escrevendo) fica para a próxima leitura
        int corte = Array.LastIndexOf(bruto, (byte)'\n', Math.Max(0, lidos - 1)) + 1;
        if (lidos == 0) corte = 0;
        Posicao = desde + corte;
        foreach (var linha in Encoding.UTF8.GetString(bruto, 0, corte).Split('\n'))
        {
            var ev = Evento.Ler(linha.TrimEnd('\r'));
            if (ev == null) continue;
            // relendo depois de um corte: o que tem hora até a maior já vista é repetido
            if (doComeco && ev.T <= _maiorT) continue;
            if (!Lembrar(ev.T)) continue;
            if (ev.T > _maiorT) _maiorT = ev.T;
            eventos.Add(ev);
        }
        return eventos;
    }

    private bool Lembrar(double t)
    {
        if (!_vistos.Add(t)) return false;
        _ordem.Enqueue(t);
        if (_ordem.Count > LEMBRAR) _vistos.Remove(_ordem.Dequeue());
        return true;
    }
}

/// <summary>
/// Vigia o arquivo da fila por FileSystemWatcher (sem polling). A cada mudança espera 40 ms (o hook escreve em
/// rajadas), abre com FileShare.ReadWrite|Delete, lê só os bytes novos e fecha na hora: o hook faz append e às vezes
/// os.replace, que falha com qualquer handle aberto, então o arquivo nunca fica preso entre leituras.
/// </summary>
internal sealed class VigiaDaFila : IDisposable
{
    private readonly string _arquivo;
    private readonly Action<List<Evento>, bool> _chegaram;
    private readonly LeitorDeFila _leitor = new();
    private readonly FileSystemWatcher? _vigia;
    private readonly Timer _espera;
    private readonly object _trava = new();
    private bool _primeira = true;
    private bool _fechado;

    /// <param name="chegaram">Chamado numa thread do sistema com os eventos novos; o bool diz se é a leitura inicial (silenciosa).</param>
    public VigiaDaFila(string arquivo, Action<List<Evento>, bool> chegaram)
    {
        _arquivo = Path.GetFullPath(arquivo);
        _chegaram = chegaram;
        _espera = new Timer(_ => Ler(), null, Timeout.Infinite, Timeout.Infinite);
        var pasta = Path.GetDirectoryName(_arquivo);
        if (pasta != null && Directory.Exists(pasta))
        {
            _vigia = new FileSystemWatcher(pasta, Path.GetFileName(_arquivo))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                InternalBufferSize = 16384,
            };
            _vigia.Changed += (_, _) => Agendar();
            _vigia.Created += (_, _) => { lock (_trava) _leitor.Recomecar(); Agendar(); };
            _vigia.Renamed += (_, _) => { lock (_trava) _leitor.Recomecar(); Agendar(); };
            _vigia.Error += (_, e) => { Log.Escrever("claude: vigia da fila perdeu eventos: " + e.GetException().Message); Agendar(); };
            _vigia.EnableRaisingEvents = true;
        }
        else Log.Escrever("claude: a pasta da fila de eventos não existe");
        // a primeira leitura monta o estado das sessões com o histórico, sem alerta
        _espera.Change(0, Timeout.Infinite);
    }

    private void Agendar()
    {
        if (!_fechado) _espera.Change(40, Timeout.Infinite);
    }

    private void Ler()
    {
        List<Evento>? novos = null;
        bool primeira;
        lock (_trava)
        {
            if (_fechado) return;
            primeira = _primeira;
            for (int tentativa = 0; tentativa < 3 && novos == null; tentativa++)
            {
                try
                {
                    using var fs = new FileStream(_arquivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                    novos = _leitor.Ler(fs);
                }
                catch (FileNotFoundException) { novos = new(); }
                catch (DirectoryNotFoundException) { novos = new(); }
                catch (IOException) { Thread.Sleep(40); }
                catch (UnauthorizedAccessException) { Thread.Sleep(40); }
            }
            if (novos == null) { Agendar(); return; }
            _primeira = false;
        }
        if (novos.Count > 0 || primeira) _chegaram(novos, primeira);
    }

    public void Dispose()
    {
        lock (_trava) _fechado = true;
        if (_vigia != null) { _vigia.EnableRaisingEvents = false; _vigia.Dispose(); }
        _espera.Dispose();
    }
}
