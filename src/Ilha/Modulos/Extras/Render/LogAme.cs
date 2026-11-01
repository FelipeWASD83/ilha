using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ilha.Extras;

/// <summary>Um encode que o Media Encoder registrou (com sucesso no AMEEncodingLog.txt, com erro ou parado no AMEEncodingErrorLog.txt).</summary>
internal sealed record EncodeAme(string? Origem, string? Saida, string? Tempo, string Estado, string? Mensagem, bool Falhou);

/// <summary>
/// Lê as entradas dos logs do AME. Cada encode são linhas " - Chave: valor" (arquivo de origem, de saída, preset,
/// vídeo, áudio, tempo de encode) seguidas de uma linha "data hora : estado" ("File Successfully Encoded",
/// "Encoding Failed"...); no log de erro vêm depois linhas com a mensagem. "Queue Started" e "Queue Stopped" não
/// têm detalhes e são ignoradas. As chaves podem vir traduzidas: sem a chave conhecida, vale a posição.
/// </summary>
internal static class LogAme
{
    private static readonly Regex Detalhe = new(@"^\s*-\s*(?<chave>[^:]+?)\s*:\s*(?<valor>.*?)\s*$");
    private static readonly Regex Estado = new(@"^\s*(?<quando>\d[\d/.\-]*\s+\d{1,2}:\d{2}(?::\d{2})?(?:\s*[AaPp]\.?\s?[Mm]\.?)?)\s+:\s+(?<estado>.+?)\s*$");

    private static string? Achar(List<(string Chave, string Valor)> d, string[] chaves, int posicao)
    {
        foreach (var (k, v) in d)
            if (chaves.Any(c => k.Contains(c, StringComparison.OrdinalIgnoreCase))) return v;
        return posicao < d.Count ? d[posicao].Valor : null;
    }

    public static List<EncodeAme> Ler(IEnumerable<string> linhas, bool logDeErro)
    {
        var r = new List<EncodeAme>();
        var detalhes = new List<(string, string)>();
        EncodeAme? ultima = null;
        int linhasDaMensagem = 0;
        foreach (var bruta in linhas)
        {
            var linha = bruta.TrimEnd('\r');
            if (linha.Trim().Length == 0) continue;
            var e = Estado.Match(linha);
            if (e.Success)
            {
                ultima = null;
                if (detalhes.Count == 0) continue;
                string? origem = Achar(detalhes, new[] { "source", "origem", "quelle", "origen", "sorgente" }, 0);
                string? saida = Achar(detalhes, new[] { "output", "saída", "saida", "ausgabe", "sortie", "salida", "uscita" }, 1);
                string? tempo = Achar(detalhes, new[] { "encoding time", "tempo", "time" }, int.MaxValue);
                ultima = new EncodeAme(origem, saida, tempo, e.Groups["estado"].Value, null, logDeErro);
                r.Add(ultima);
                linhasDaMensagem = 0;
                detalhes.Clear();
                continue;
            }
            var d = Detalhe.Match(linha);
            if (d.Success && !linha.TrimStart().StartsWith("--"))
            {
                // a linha " - " começa um encode novo: a mensagem do anterior acabou
                ultima = null;
                detalhes.Add((d.Groups["chave"].Value, d.Groups["valor"].Value));
                continue;
            }
            // mensagem do erro: as duas primeiras linhas ("Export Error" e o motivo), sem as linhas de traço
            if (ultima != null && linhasDaMensagem < 2 && !linha.Trim().All(c => c == '-' || c == '='))
            {
                int i = r.Count - 1;
                r[i] = ultima = ultima with { Mensagem = ultima.Mensagem == null ? linha.Trim() : ultima.Mensagem + ": " + linha.Trim() };
                linhasDaMensagem++;
            }
        }
        return r;
    }

    /// <summary>"C:\Videos\Final.mp4" vira "Final.mp4" (aceita barra dos dois lados).</summary>
    public static string NomeDoArquivo(string? caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho)) return "";
        var c = caminho.Trim().TrimEnd('\\', '/');
        int i = Math.Max(c.LastIndexOf('\\'), c.LastIndexOf('/'));
        return i >= 0 ? c[(i + 1)..] : c;
    }
}

/// <summary>
/// Lê só o que foi acrescentado num arquivo de texto desde a última vez (a posição em bytes, linha completa só).
/// Arquivo que encolheu (apagado e recriado) recomeça do zero. UTF-16 com BOM é respeitado.
/// </summary>
internal sealed class LeitorIncremental
{
    public string Caminho { get; }
    private long _posicao;
    private Encoding _codificacao = new UTF8Encoding(false);

    public LeitorIncremental(string caminho, bool doFim)
    {
        Caminho = caminho;
        if (doFim) try { _posicao = File.Exists(caminho) ? new FileInfo(caminho).Length : 0; } catch { _posicao = 0; }
    }

    public List<string> Novas()
    {
        var r = new List<string>();
        try
        {
            using var f = new FileStream(Caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (f.Length < _posicao) _posicao = 0;
            if (_posicao == 0)
            {
                var bom = new byte[3];
                int n = f.Read(bom, 0, 3);
                if (n >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) { _codificacao = Encoding.Unicode; _posicao = 2; }
                else if (n == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) _posicao = 3;
            }
            if (f.Length <= _posicao) return r;
            f.Position = _posicao;
            var bytes = new byte[f.Length - _posicao];
            int lidos = 0;
            while (lidos < bytes.Length)
            {
                int k = f.Read(bytes, lidos, bytes.Length - lidos);
                if (k <= 0) break;
                lidos += k;
            }
            // só até a última quebra de linha: o resto ainda está sendo escrito
            int fim = UltimaQuebra(bytes, lidos);
            if (fim < 0) return r;
            r.AddRange(_codificacao.GetString(bytes, 0, fim).Split('\n'));
            _posicao += fim;
        }
        catch (FileNotFoundException) { _posicao = 0; }
        catch (DirectoryNotFoundException) { _posicao = 0; }
        catch (Exception e) { Log.Escrever($"render: log {Path.GetFileName(Caminho)}: {e.Message}"); }
        return r;
    }

    private int UltimaQuebra(byte[] b, int n)
    {
        if (_codificacao is UnicodeEncoding)
        {
            for (int i = (n & ~1) - 2; i >= 0; i -= 2) if (b[i] == (byte)'\n' && b[i + 1] == 0) return i + 2;
            return -1;
        }
        for (int i = n - 1; i >= 0; i--) if (b[i] == (byte)'\n') return i + 1;
        return -1;
    }
}

/// <summary>
/// Vigia Documentos\Adobe\Adobe Media Encoder\&lt;versão&gt;\AMEEncoding*Log.txt por evento. A pasta (ou o log)
/// pode ainda não existir: aí vigia o ancestral mais próximo que existe e se rearma quando a pasta aparece.
/// </summary>
internal sealed class VigiaAme : IDisposable
{
    public const string LogOk = "AMEEncodingLog.txt", LogErro = "AMEEncodingErrorLog.txt";

    private readonly string _raiz;
    private readonly Action<EncodeAme> _terminou;
    private FileSystemWatcher? _vigia;
    private readonly Dictionary<string, LeitorIncremental> _leitores = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _espera;
    private readonly HashSet<string> _pendentes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _trava = new();
    private bool _parado;

    public static string? Raiz()
    {
        var docs = PastaConhecida.Caminho(PastaConhecida.Documentos);
        return docs == null ? null : Path.Combine(docs, "Adobe", "Adobe Media Encoder");
    }

    /// <param name="terminou">chamado numa thread do sistema</param>
    public VigiaAme(string raiz, Action<EncodeAme> terminou)
    {
        _raiz = raiz;
        _terminou = terminou;
        _espera = new System.Threading.Timer(_ => Processar());
    }

    public void Iniciar() => Armar();

    private void Armar()
    {
        lock (_trava)
        {
            if (_parado) return;
            _vigia?.Dispose();
            _vigia = null;
            try
            {
                if (Directory.Exists(_raiz))
                {
                    // o que já está nos logs é passado: começa do fim
                    foreach (var arq in Directory.EnumerateFiles(_raiz, "AMEEncoding*Log.txt", SearchOption.AllDirectories))
                        _leitores.TryAdd(arq, new LeitorIncremental(arq, doFim: true));
                    var w = new FileSystemWatcher(_raiz, "AMEEncoding*Log.txt")
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    };
                    w.Changed += (_, e) => Mexeu(e.FullPath);
                    w.Created += (_, e) => Mexeu(e.FullPath);
                    w.Renamed += (_, e) => Mexeu(e.FullPath);
                    w.Error += (_, e) => { Log.Escrever("render: vigia do AME: " + e.GetException().Message); Armar(); };
                    w.EnableRaisingEvents = true;
                    _vigia = w;
                    return;
                }
                // a pasta do AME ainda não existe: espera ela nascer no ancestral mais próximo
                var acima = Path.GetDirectoryName(_raiz);
                while (acima != null && !Directory.Exists(acima)) acima = Path.GetDirectoryName(acima);
                if (acima == null) return;
                var p = new FileSystemWatcher(acima) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.DirectoryName };
                p.Created += (_, _) => { if (_raiz.StartsWith(acima, StringComparison.OrdinalIgnoreCase)) Armar(); };
                p.Renamed += (_, _) => Armar();
                p.EnableRaisingEvents = true;
                _vigia = p;
            }
            catch (Exception e) { Log.Escrever("render: não deu para vigiar o AME: " + e.Message); }
        }
    }

    private void Mexeu(string caminho)
    {
        lock (_trava)
        {
            if (_parado) return;
            _pendentes.Add(caminho);
            // o AME escreve a entrada em várias vezes: junta numa leitura só
            _espera.Change(700, System.Threading.Timeout.Infinite);
        }
    }

    private void Processar()
    {
        List<string> arquivos;
        lock (_trava)
        {
            if (_parado) return;
            arquivos = _pendentes.ToList();
            _pendentes.Clear();
        }
        foreach (var arq in arquivos)
        {
            LeitorIncremental leitor;
            lock (_trava)
            {
                // log que nasceu agora é todo novo
                if (!_leitores.TryGetValue(arq, out leitor!)) _leitores[arq] = leitor = new LeitorIncremental(arq, doFim: false);
            }
            bool erro = Path.GetFileName(arq).Equals(LogErro, StringComparison.OrdinalIgnoreCase);
            foreach (var enc in LogAme.Ler(leitor.Novas(), erro))
                try { _terminou(enc); } catch (Exception e) { Log.Escrever("render: " + e.Message); }
        }
    }

    public void Dispose()
    {
        lock (_trava)
        {
            _parado = true;
            _vigia?.Dispose();
            _vigia = null;
        }
        _espera.Dispose();
    }
}
