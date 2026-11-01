using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ilha.Mascote;

internal enum EstadoMascote { Parado, Dormindo, Dancando, Trabalhando }

/// <summary>O que o compositor faz por cima do desenho quando a folha não tem a animação do estado.</summary>
internal enum Efeito { Nenhum, Danca, Sono }

/// <summary>Onde o desenho fica dentro da cena, em pixels.</summary>
internal readonly record struct Arranjo(float Escala, int L, int A, int X, int Y, int CenaL, int CenaA, int Pulo, int ZEscala);

internal static class Escolha
{
    // nomes de tag aceitos para cada estado, já sem acento e em minúsculas, do preferido para o reserva
    private static readonly string[] Parado = { "idle", "parado", "parada" };
    private static readonly string[] Trabalho = { "trabalhando", "digitando", "rodando", "lendo", "pensando" };
    private static readonly string[] Danca = { "dancando", "danca", "dance", "dancing" };
    private static readonly string[] Sono = { "dormindo", "dormir", "sleep", "sleeping" };

    /// <summary>Claude trabalhando ganha de música; parado há muito tempo (sem mouse e teclado) dorme.</summary>
    public static EstadoMascote Estado(bool claude, bool musica, TimeSpan ocioso, TimeSpan dormirDepois)
    {
        if (claude) return EstadoMascote.Trabalhando;
        if (musica) return EstadoMascote.Dancando;
        if (dormirDepois > TimeSpan.Zero && ocioso >= dormirDepois) return EstadoMascote.Dormindo;
        return EstadoMascote.Parado;
    }

    /// <summary>
    /// Aparece na área de trabalho (no lugar da pílula vazia) e, em qualquer lugar, com música tocando ou o Claude Code
    /// trabalhando: aí ele tem prioridade mais baixa que tudo e fica na bolha ao lado da atividade principal.
    /// </summary>
    public static bool Aparece(bool temFolhas, bool claude, bool musica, bool areaDeTrabalho) =>
        temFolhas && (claude || musica || areaDeTrabalho);

    public static string Normalizar(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var b = new StringBuilder(d.Length);
        foreach (char c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) b.Append(char.ToLowerInvariant(c));
        return b.ToString().Trim();
    }

    /// <summary>
    /// A tag que toca no estado e o efeito do compositor que completa: sem tag de dança, dança o parado balançando;
    /// sem tag de dormir, dorme o parado respirando devagar com "z" subindo. Sem nem o parado, a primeira em ordem.
    /// </summary>
    public static (string? Tag, Efeito Efeito) Tag(IEnumerable<string> tags, EstadoMascote e)
    {
        var porNome = new Dictionary<string, string>();
        foreach (var t in tags) porNome.TryAdd(Normalizar(t), t);
        if (porNome.Count == 0) return (null, Efeito.Nenhum);
        string? Achar(string[] nomes) => nomes.Select(n => porNome.GetValueOrDefault(n)).FirstOrDefault(x => x != null);
        string parado = Achar(Parado) ?? porNome.OrderBy(k => k.Key, StringComparer.Ordinal).First().Value;
        return e switch
        {
            EstadoMascote.Trabalhando => (Achar(Trabalho) ?? parado, Efeito.Nenhum),
            EstadoMascote.Dancando => Achar(Danca) is { } d ? (d, Efeito.Nenhum) : (parado, Efeito.Danca),
            EstadoMascote.Dormindo => Achar(Sono) is { } s ? (s, Efeito.Nenhum) : (parado, Efeito.Sono),
            _ => (parado, Efeito.Nenhum),
        };
    }

    /// <summary>
    /// Encaixa um quadro de `w` x `h` numa cena de até `maxL` x `maxA` pixels. A dança reserva espaço em cima (pulo) e
    /// dos lados (balanço); o sono reserva em cima e à direita para os "z". `larguraFixa` (bolha) usa a cena inteira;
    /// senão a cena tem a largura do que aparece.
    /// </summary>
    public static Arranjo Arranjar(int w, int h, int maxL, int maxA, Efeito ef, bool larguraFixa)
    {
        float fl = ef switch { Efeito.Danca => 0.8f, Efeito.Sono => 0.7f, _ => 1f };
        float fa = ef switch { Efeito.Danca => 0.84f, Efeito.Sono => 0.68f, _ => 1f };
        float s = Montagem.Escala(w, h, maxL * fl, maxA * fa);
        int l = Math.Max(1, (int)MathF.Round(w * s)), a = Math.Max(1, (int)MathF.Round(h * s));
        // o "z" do sono acompanha o tamanho do pixel, sem passar de 30% da altura
        int z = Math.Max(1, Math.Min((int)MathF.Ceiling(s / 2), (int)(maxA * 0.3f / 4)));
        int lado = ef switch
        {
            Efeito.Danca => (int)MathF.Ceiling(a * 0.13f),
            _ => 0,
        };
        int zona = ef == Efeito.Sono ? 4 * z * 2 : 0;
        int cenaL = larguraFixa ? maxL : Math.Min(maxL, l + lado * 2 + zona);
        int cenaA = maxA;
        int reservaTopo = cenaA - a;
        int pulo = ef == Efeito.Danca ? Math.Max(1, Math.Min(reservaTopo, (int)MathF.Round(a * 0.14f))) : 0;
        int x = ef == Efeito.Sono ? Math.Max(0, (cenaL - zona - l) / 2) : (cenaL - l) / 2;
        // parado e trabalhando: centrado; dança e sono: encostado embaixo, a reserva fica em cima
        int precisa = ef == Efeito.Danca ? pulo : 5 * z;
        int y = ef == Efeito.Nenhum ? (cenaA - a) / 2 : Math.Max(0, cenaA - a - Math.Max(0, (reservaTopo - precisa) / 2));
        return new Arranjo(s, l, a, x, y, cenaL, cenaA, pulo, z);
    }
}

/// <summary>
/// O estado do Claude Code a partir da fila de eventos dos hooks (um JSON por linha, com "t" em segundos, "tipo" e
/// "sessao"). ouvindo, pensando, lendo, digitando e rodando = trabalhando; terminou, notificacao (esperando por ele)
/// e fim = parado. Trabalho sem evento nenhum por 15 minutos é sessão que morreu (Esc e erro de API não avisam).
/// </summary>
internal sealed class SessoesDoClaude
{
    public const double ESQUECIDO = 15 * 60;
    private static readonly HashSet<string> Trabalho = new() { "ouvindo", "pensando", "lendo", "digitando", "rodando" };
    private static readonly HashSet<string> Parou = new() { "terminou", "notificacao", "fim" };

    private readonly Dictionary<string, (bool Trabalhando, double Ultimo)> _sessoes = new();
    private double _ultimoT;

    public int Sessoes => _sessoes.Count;

    /// <summary>Uma linha da fila. Evento repetido (a fila corta o começo quando cresce) é reconhecido pela hora e pulado.</summary>
    public void Receber(string linha)
    {
        if (string.IsNullOrWhiteSpace(linha)) return;
        double t;
        string tipo, sessao;
        try
        {
            using var doc = JsonDocument.Parse(linha);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return;
            t = r.TryGetProperty("t", out var vt) && vt.ValueKind == JsonValueKind.Number ? vt.GetDouble() : 0;
            tipo = Folha.Texto(r, "tipo") ?? "";
            sessao = Folha.Texto(r, "sessao") ?? "";
        }
        catch (JsonException) { return; }
        if (t <= _ultimoT) return;
        _ultimoT = t;
        if (tipo == "fim") { _sessoes.Remove(sessao); return; }
        if (Trabalho.Contains(tipo)) _sessoes[sessao] = (true, t);
        else if (Parou.Contains(tipo)) _sessoes[sessao] = (false, t);
        else if (_sessoes.TryGetValue(sessao, out var s)) _sessoes[sessao] = (s.Trabalhando, t);
    }

    public bool Trabalhando(double agora) => _sessoes.Values.Any(s => s.Trabalhando && agora - s.Ultimo < ESQUECIDO);

    /// <summary>Quando o último trabalho em andamento vence sozinho (null se nenhum).</summary>
    public double? Vencimento(double agora)
    {
        var vivos = _sessoes.Values.Where(s => s.Trabalhando && agora - s.Ultimo < ESQUECIDO).ToList();
        return vivos.Count == 0 ? null : vivos.Max(s => s.Ultimo) + ESQUECIDO;
    }

    public static double Agora() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}

/// <summary>
/// Lê só o que é novo num arquivo de linhas que outro programa vai acrescentando. Abre com FileShare.ReadWrite|Delete,
/// lê e fecha na hora: segurar o arquivo aberto faria o append e o os.replace de quem escreve falharem.
/// Linha pela metade fica para a próxima leitura; arquivo que encolheu (foi cortado ou trocado) volta ao começo.
/// </summary>
internal sealed class LeitorDeLinhas
{
    public const int PRIMEIRA_LEITURA_MAX = 256 * 1024;
    private long _posicao = -1;

    public List<string> Novas(string arquivo)
    {
        var linhas = new List<string>();
        try
        {
            using var fs = new FileStream(arquivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
            long tam = fs.Length;
            long desde = _posicao;
            bool cortarComeco = false;
            if (desde < 0 || desde > tam)
            {
                // primeira leitura ou arquivo trocado: só o fim (a fila é aparada pelo hook, mas não confio nisso)
                desde = Math.Max(0, tam - PRIMEIRA_LEITURA_MAX);
                cortarComeco = desde > 0;
            }
            if (tam == desde) { _posicao = tam; return linhas; }
            fs.Seek(desde, SeekOrigin.Begin);
            var bruto = new byte[tam - desde];
            int lidos = 0;
            while (lidos < bruto.Length)
            {
                int n = fs.Read(bruto, lidos, bruto.Length - lidos);
                if (n <= 0) break;
                lidos += n;
            }
            int corte = Array.LastIndexOf(bruto, (byte)'\n', Math.Max(0, lidos - 1)) + 1;
            if (lidos == 0) corte = 0;
            _posicao = desde + corte;
            int inicio = 0;
            if (cortarComeco)
            {
                int nl = Array.IndexOf(bruto, (byte)'\n', 0, corte);
                inicio = nl < 0 ? corte : nl + 1;
            }
            foreach (var l in Encoding.UTF8.GetString(bruto, inicio, corte - inicio).Split('\n'))
                if (l.Trim().Length > 0) linhas.Add(l.Trim());
        }
        catch (FileNotFoundException) { _posicao = -1; }
        catch (DirectoryNotFoundException) { _posicao = -1; }
        catch (IOException e) { Log.Escrever("mascote: fila de eventos: " + e.Message); }
        catch (UnauthorizedAccessException e) { Log.Escrever("mascote: fila de eventos: " + e.Message); }
        return linhas;
    }

    /// <summary>Esquece onde parou (outro arquivo configurado).</summary>
    public void Recomecar() => _posicao = -1;
}
