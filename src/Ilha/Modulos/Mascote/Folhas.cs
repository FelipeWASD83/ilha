using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ilha.Mascote;

/// <summary>Um pedaço da imagem que entra num quadro: o retângulo na folha e onde ele cai no quadro (Aseprite com "trim").</summary>
internal sealed record Peca(string Camada, int X, int Y, int L, int A, int Dx, int Dy);

internal sealed record TagDaFolha(string Nome, int De, int Ate, string Direcao);

/// <summary>
/// A folha exportada do Aseprite (File &gt; Export Sprite Sheet): PNG + JSON em Hash ou em Array, com "Split Layers"
/// (um pedaço por camada e quadro), "Tags" (frameTags com forward, reverse, pingpong e pingpong_reverse) e a duração
/// de cada quadro. O nome do pedaço pode ser o padrão do Aseprite ("boneco (camada) 3.aseprite") ou "camada|3".
/// Também aceita um PNG sem JSON: uma tira de quadros do mesmo tamanho lado a lado.
/// </summary>
internal sealed class Folha
{
    public const int DURACAO_PADRAO = 100;

    private static readonly Regex NomeSeparado = new(@"^(?<camada>.+)\|(?<quadro>\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex NomePadrao = new(@"\((?<camada>[^()]*)\)\s*(?<quadro>\d+)(?:\.\w+)?$", RegexOptions.CultureInvariant);
    private static readonly Regex NomeSoQuadro = new(@"(?<quadro>\d+)(?:\.\w+)?$", RegexOptions.CultureInvariant);

    /// <summary>O PNG da folha (nome de arquivo, ao lado do JSON).</summary>
    public string Imagem { get; private set; } = "";
    public int Largura { get; private set; }
    public int Altura { get; private set; }
    /// <summary>De baixo pra cima.</summary>
    public List<string> Camadas { get; } = new();
    public Dictionary<string, float> Opacidade { get; } = new();
    public SortedDictionary<int, List<Peca>> Quadros { get; } = new();
    public Dictionary<int, int> Duracao { get; } = new();
    public Dictionary<string, TagDaFolha> Tags { get; } = new();

    /// <summary>Se o JSON é uma folha do Aseprite (tem "frames" e "meta"), e não um animacoes.json qualquer.</summary>
    public static bool EhFolha(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("frames", out _) && doc.RootElement.TryGetProperty("meta", out _);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Lê o JSON do Aseprite. `nomeDoJson` dá o nome da tag única quando a folha não tem tag nenhuma.</summary>
    public static Folha DoJson(string json, string nomeDoJson)
    {
        var f = new Folha();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var raiz = doc.RootElement;
        string semExtensao = Path.GetFileNameWithoutExtension(nomeDoJson);
        var meta = raiz.TryGetProperty("meta", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
        f.Imagem = Texto(meta, "image") is { Length: > 0 } img ? Path.GetFileName(img) : semExtensao + ".png";

        if (meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("layers", out var camadas) && camadas.ValueKind == JsonValueKind.Array)
            foreach (var c in camadas.EnumerateArray())
            {
                string? nome = Texto(c, "name");
                if (string.IsNullOrEmpty(nome) || f.Camadas.Contains(nome)) continue;
                f.Camadas.Add(nome);
                if (c.TryGetProperty("opacity", out var op) && op.TryGetDouble(out double o)) f.Opacidade[nome] = (float)Math.Clamp(o / 255.0, 0, 1);
            }

        // Hash: { "nome": {...} } na ordem do arquivo; Array: [ { "filename": "nome", ... } ]
        var lista = new List<(string Nome, JsonElement Q)>();
        if (raiz.TryGetProperty("frames", out var frames))
        {
            if (frames.ValueKind == JsonValueKind.Object)
                foreach (var p in frames.EnumerateObject()) lista.Add((p.Name, p.Value));
            else if (frames.ValueKind == JsonValueKind.Array)
                foreach (var q in frames.EnumerateArray()) lista.Add((Texto(q, "filename") ?? "", q));
        }

        var vistas = new List<string>();
        for (int i = 0; i < lista.Count; i++)
        {
            var (nomeDoPedaco, q) = lista[i];
            var (camada, quadro) = Nome(nomeDoPedaco, i);
            if (!q.TryGetProperty("frame", out var r)) continue;
            int x = Inteiro(r, "x"), y = Inteiro(r, "y"), l = Inteiro(r, "w"), a = Inteiro(r, "h");
            int dx = 0, dy = 0;
            if (q.TryGetProperty("spriteSourceSize", out var ss)) { dx = Inteiro(ss, "x"); dy = Inteiro(ss, "y"); }
            if (f.Largura == 0)
            {
                if (q.TryGetProperty("sourceSize", out var fonte)) { f.Largura = Inteiro(fonte, "w"); f.Altura = Inteiro(fonte, "h"); }
                if (f.Largura <= 0 || f.Altura <= 0) { f.Largura = l; f.Altura = a; }
            }
            if (!f.Quadros.TryGetValue(quadro, out var pecas)) f.Quadros[quadro] = pecas = new List<Peca>();
            pecas.RemoveAll(p => p.Camada == camada);
            pecas.Add(new Peca(camada, x, y, l, a, dx, dy));
            int ms = q.TryGetProperty("duration", out var d) && d.TryGetInt32(out int v) && v > 0 ? v : DURACAO_PADRAO;
            f.Duracao.TryAdd(quadro, ms);
            if (!vistas.Contains(camada)) vistas.Add(camada);
        }
        foreach (var c in vistas)
            if (!f.Camadas.Contains(c)) f.Camadas.Add(c);
        // cada quadro desenha as camadas na ordem do meta.layers (de baixo pra cima)
        foreach (var pecas in f.Quadros.Values) pecas.Sort((p1, p2) => f.Camadas.IndexOf(p1.Camada).CompareTo(f.Camadas.IndexOf(p2.Camada)));

        if (meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("frameTags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            foreach (var t in tags.EnumerateArray())
            {
                string? nome = Texto(t, "name");
                if (string.IsNullOrEmpty(nome) || f.Tags.ContainsKey(nome)) continue;
                int de = Inteiro(t, "from"), ate = Inteiro(t, "to");
                if (ate < de) (de, ate) = (ate, de);
                f.Tags[nome] = new TagDaFolha(nome, de, ate, (Texto(t, "direction") ?? "forward").ToLowerInvariant());
            }
        // folha sem tag: tudo é uma animação só, com o nome do arquivo (uma subpasta ou um arquivo por animação)
        if (f.Tags.Count == 0 && f.Quadros.Count > 0)
            f.Tags[semExtensao] = new TagDaFolha(semExtensao, f.Quadros.Keys.Min(), f.Quadros.Keys.Max(), "forward");
        return f;
    }

    /// <summary>PNG sem JSON: `quadros` quadros do mesmo tamanho lado a lado, todos com a duração padrão.</summary>
    public static Folha DaTira(string nomeDoPng, int larguraDaImagem, int alturaDaImagem, int quadros)
    {
        var f = new Folha { Imagem = Path.GetFileName(nomeDoPng) };
        quadros = Math.Max(1, quadros);
        int passo = Math.Max(1, larguraDaImagem / quadros);
        f.Largura = passo;
        f.Altura = alturaDaImagem;
        f.Camadas.Add("base");
        for (int i = 0; i < quadros; i++)
        {
            f.Quadros[i] = new List<Peca> { new("base", i * passo, 0, passo, alturaDaImagem, 0, 0) };
            f.Duracao[i] = DURACAO_PADRAO;
        }
        string nome = Path.GetFileNameWithoutExtension(nomeDoPng);
        f.Tags[nome] = new TagDaFolha(nome, 0, quadros - 1, "forward");
        return f;
    }

    public static (string Camada, int Quadro) Nome(string nome, int indice)
    {
        foreach (var r in new[] { NomeSeparado, NomePadrao })
        {
            var achou = r.Match(nome);
            if (achou.Success) return (achou.Groups["camada"].Value.Trim(), int.Parse(achou.Groups["quadro"].Value, CultureInfo.InvariantCulture));
        }
        var so = NomeSoQuadro.Match(nome);
        return ("base", so.Success ? int.Parse(so.Groups["quadro"].Value, CultureInfo.InvariantCulture) : indice);
    }

    /// <summary>Os quadros da tag na ordem em que tocam (pingpong vai e volta sem repetir as pontas).</summary>
    public int[] QuadrosDaTag(string nome)
    {
        var t = Tags[nome];
        var ordem = Enumerable.Range(t.De, t.Ate - t.De + 1).Where(Quadros.ContainsKey).ToList();
        if (t.Direcao is "reverse" or "pingpong_reverse") ordem.Reverse();
        if (t.Direcao is "pingpong" or "pingpong_reverse" && ordem.Count > 2)
            ordem.AddRange(Enumerable.Range(1, ordem.Count - 2).Select(i => ordem[ordem.Count - 1 - i]).ToList());
        return ordem.ToArray();
    }

    /// <summary>
    /// A duração de cada passo. O fps próprio da animação (animacoes.json) vale sempre; o fps geral só quando o Aseprite
    /// deixou todos os quadros no tempo padrão (100 ms, ninguém mexeu); senão vale a duração de cada quadro.
    /// </summary>
    public int[] Duracoes(int[] sequencia, double? fpsProprio, double? fpsGeral)
    {
        int? proprio = MsDoFps(fpsProprio), geral = MsDoFps(fpsGeral);
        bool semTempo = sequencia.All(q => Duracao.GetValueOrDefault(q, DURACAO_PADRAO) == DURACAO_PADRAO);
        return sequencia.Select(q => proprio ?? (semTempo && geral != null ? geral.Value : Duracao.GetValueOrDefault(q, DURACAO_PADRAO))).ToArray();
    }

    public static int? MsDoFps(double? fps) => fps is > 0 and < 1000 ? Math.Max(1, (int)Math.Round(1000 / fps.Value)) : null;

    internal static string? Texto(JsonElement e, string nome) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static int Inteiro(JsonElement e, string nome) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? (int)Math.Round(d) : 0;
}

/// <summary>O animacoes.json da pasta: fps geral e, por animação, fps, quantos quadros tem a tira e a âncora.</summary>
internal sealed class Ajustes
{
    public sealed record Animacao(double? Fps, int? Quadros, Vector2? Ancora);

    public double? Fps { get; private set; }
    public Dictionary<string, Animacao> Animacoes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Animacao? De(string nome) => Animacoes.TryGetValue(nome, out var a) ? a : null;

    public static Ajustes Ler(string? json)
    {
        var aj = new Ajustes();
        if (string.IsNullOrWhiteSpace(json)) return aj;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return aj;
            aj.Fps = Numero(r, "fps");
            if (r.TryGetProperty("animacoes", out var anims) && anims.ValueKind == JsonValueKind.Object)
                foreach (var p in anims.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Object) continue;
                    Vector2? ancora = null;
                    if (p.Value.TryGetProperty("ancora", out var an) && an.ValueKind == JsonValueKind.Array && an.GetArrayLength() == 2
                        && an[0].TryGetDouble(out double ax) && an[1].TryGetDouble(out double ay))
                        ancora = new Vector2((float)ax, (float)ay);
                    double? q = Numero(p.Value, "quadros");
                    aj.Animacoes[p.Name] = new Animacao(Numero(p.Value, "fps"), q is >= 1 ? (int)q.Value : null, ancora);
                }
        }
        catch (JsonException e) { Log.Escrever("mascote: animacoes.json ilegível: " + e.Message); }
        return aj;
    }

    private static double? Numero(JsonElement e, string nome) =>
        e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : null;
}

/// <summary>Uma animação pronta para tocar: os quadros já montados (camadas juntas), em BGRA sem pré-multiplicar.</summary>
internal sealed class Animacao
{
    public required string Nome;
    public required int Largura, Altura;
    /// <summary>Quadros diferentes, cada um Largura x Altura x 4 bytes.</summary>
    public required List<byte[]> Quadros;
    /// <summary>A ordem em que tocam (índices em Quadros) e quanto dura cada passo.</summary>
    public required int[] Sequencia, Ms;
    /// <summary>O ponto do corpo que fica fixo (meio do corpo, linha do chão), em pixels do quadro.</summary>
    public required Vector2 Ancora;

    public int Total => Ms.Sum();
}

/// <summary>Monta as animações a partir das folhas e da imagem (pura: a imagem chega já decodificada).</summary>
internal static class Montagem
{
    /// <summary>`pixels` é a imagem inteira da folha em BGRA sem pré-multiplicar.</summary>
    public static List<Animacao> Animacoes(Folha f, byte[] pixels, int larguraImg, int alturaImg, Ajustes aj)
    {
        var saida = new List<Animacao>();
        var montados = new Dictionary<int, byte[]>();
        foreach (var tag in f.Tags.Keys)
        {
            var seq = f.QuadrosDaTag(tag);
            if (seq.Length == 0 || f.Largura <= 0 || f.Altura <= 0) continue;
            var unicos = seq.Distinct().ToList();
            var quadros = new List<byte[]>();
            foreach (int q in unicos)
            {
                if (!montados.TryGetValue(q, out var px)) montados[q] = px = Quadro(f, q, pixels, larguraImg, alturaImg);
                quadros.Add(px);
            }
            var ajuste = aj.De(tag);
            saida.Add(new Animacao
            {
                Nome = tag, Largura = f.Largura, Altura = f.Altura, Quadros = quadros,
                Sequencia = seq.Select(q => unicos.IndexOf(q)).ToArray(),
                Ms = f.Duracoes(seq, ajuste?.Fps, aj.Fps),
                Ancora = ajuste?.Ancora ?? new Vector2(f.Largura / 2f, f.Altura),
            });
        }
        return saida;
    }

    /// <summary>Junta as camadas de um quadro (de baixo pra cima, com a opacidade de cada camada).</summary>
    public static byte[] Quadro(Folha f, int quadro, byte[] img, int li, int ai)
    {
        var q = new byte[f.Largura * f.Altura * 4];
        if (!f.Quadros.TryGetValue(quadro, out var pecas)) return q;
        foreach (var p in pecas)
        {
            float op = f.Opacidade.GetValueOrDefault(p.Camada, 1f);
            for (int y = 0; y < p.A; y++)
            {
                int sy = p.Y + y, dy = p.Dy + y;
                if (sy < 0 || sy >= ai || dy < 0 || dy >= f.Altura) continue;
                for (int x = 0; x < p.L; x++)
                {
                    int sx = p.X + x, dx = p.Dx + x;
                    if (sx < 0 || sx >= li || dx < 0 || dx >= f.Largura) continue;
                    int s = (sy * li + sx) * 4, d = (dy * f.Largura + dx) * 4;
                    Por(q, d, img[s], img[s + 1], img[s + 2], img[s + 3] / 255f * op);
                }
            }
        }
        return q;
    }

    // "over" com alfa sem pré-multiplicar
    private static void Por(byte[] q, int d, byte b, byte g, byte r, float sa)
    {
        if (sa <= 0) return;
        float da = q[d + 3] / 255f;
        float oa = sa + da * (1 - sa);
        if (oa <= 0) return;
        float k = da * (1 - sa);
        q[d] = (byte)Math.Round((b * sa + q[d] * k) / oa);
        q[d + 1] = (byte)Math.Round((g * sa + q[d + 1] * k) / oa);
        q[d + 2] = (byte)Math.Round((r * sa + q[d + 2] * k) / oa);
        q[d + 3] = (byte)Math.Round(oa * 255);
    }

    /// <summary>
    /// A tira que o compositor desliza: os quadros lado a lado, cada um ampliado para `l` x `a` por vizinho mais próximo
    /// (pixel art, nada de borrar) e já pré-multiplicados para o Direct2D.
    /// </summary>
    public static byte[] Tira(IReadOnlyList<byte[]> quadros, int w, int h, int l, int a)
    {
        int lt = quadros.Count * l;
        var saida = new byte[lt * a * 4];
        var mx = new int[l];
        for (int x = 0; x < l; x++) mx[x] = Math.Min(w - 1, x * w / l);
        for (int i = 0; i < quadros.Count; i++)
        {
            var q = quadros[i];
            for (int y = 0; y < a; y++)
            {
                int sy = Math.Min(h - 1, y * h / a);
                for (int x = 0; x < l; x++)
                {
                    int s = (sy * w + mx[x]) * 4, d = (y * lt + i * l + x) * 4;
                    int al = q[s + 3];
                    saida[d] = (byte)(q[s] * al / 255);
                    saida[d + 1] = (byte)(q[s + 1] * al / 255);
                    saida[d + 2] = (byte)(q[s + 2] * al / 255);
                    saida[d + 3] = (byte)al;
                }
            }
        }
        return saida;
    }

    /// <summary>
    /// Quanto ampliar um quadro de `w` x `h` para caber em `l` x `a`. Prefere número inteiro (todo pixel do desenho do
    /// mesmo tamanho) quando ele perde no máximo 30% do tamanho; senão usa o fracionário (vizinho mais próximo).
    /// </summary>
    public static float Escala(int w, int h, float l, float a)
    {
        if (w <= 0 || h <= 0) return 1;
        float s = MathF.Min(l / w, a / h);
        if (s <= 0) return 0.01f;
        float inteira = MathF.Floor(s);
        return inteira >= 1 && inteira >= s * 0.7f ? inteira : s;
    }
}

/// <summary>As animações de uma pasta de folhas (só o primeiro nível: a pasta é a do personagem, não a de todos).</summary>
internal sealed class Biblioteca
{
    private readonly Dictionary<string, Animacao> _porNome = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> Nomes => _porNome.Keys;
    public Animacao? this[string nome] => _porNome.TryGetValue(nome, out var a) ? a : null;
    public int Quantas => _porNome.Count;

    public const string AJUSTES = "animacoes.json";

    public static Biblioteca Carregar(string pasta)
    {
        var bib = new Biblioteca();
        if (string.IsNullOrWhiteSpace(pasta) || !Directory.Exists(pasta)) return bib;
        string? textoAjustes = null;
        try { if (File.Exists(Path.Combine(pasta, AJUSTES))) textoAjustes = File.ReadAllText(Path.Combine(pasta, AJUSTES)); }
        catch (IOException e) { Log.Escrever("mascote: " + e.Message); }
        var aj = Ajustes.Ler(textoAjustes);

        var comJson = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arq in Directory.EnumerateFiles(pasta, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFileName(arq), AJUSTES, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                string json = File.ReadAllText(arq);
                if (!Folha.EhFolha(json)) continue;
                var f = Folha.DoJson(json, Path.GetFileName(arq));
                string png = Path.Combine(pasta, f.Imagem);
                comJson.Add(Path.GetFullPath(png));
                bib.Juntar(f, png, aj);
            }
            catch (Exception e) when (e is IOException or JsonException or FormatException or UnauthorizedAccessException)
            {
                Log.Escrever($"mascote: folha {Path.GetFileName(arq)} não carregou: {e.Message}");
            }
        }
        foreach (var png in Directory.EnumerateFiles(pasta, "*.png").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (comJson.Contains(Path.GetFullPath(png))) continue;
            var dados = Png(png);
            if (dados == null) continue;
            string nome = Path.GetFileNameWithoutExtension(png);
            var f = Folha.DaTira(png, dados.Value.L, dados.Value.A, aj.De(nome)?.Quadros ?? 1);
            foreach (var an in Montagem.Animacoes(f, dados.Value.Px, dados.Value.L, dados.Value.A, aj)) bib._porNome.TryAdd(an.Nome, an);
        }
        return bib;
    }

    private void Juntar(Folha f, string png, Ajustes aj)
    {
        var dados = Png(png);
        if (dados == null) return;
        foreach (var an in Montagem.Animacoes(f, dados.Value.Px, dados.Value.L, dados.Value.A, aj)) _porNome.TryAdd(an.Nome, an);
    }

    /// <summary>Decodifica o PNG pelo GDI+ em BGRA sem pré-multiplicar.</summary>
    public static (byte[] Px, int L, int A)? Png(string arquivo)
    {
        try
        {
            using var bmp = new System.Drawing.Bitmap(arquivo);
            int l = bmp.Width, a = bmp.Height;
            var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, l, a), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var px = new byte[l * a * 4];
            for (int y = 0; y < a; y++) System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, px, y * l * 4, l * 4);
            bmp.UnlockBits(bd);
            return (px, l, a);
        }
        catch (Exception e) when (e is ArgumentException or IOException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            Log.Escrever($"mascote: imagem {Path.GetFileName(arquivo)} não carregou: {e.Message}");
            return null;
        }
    }
}
