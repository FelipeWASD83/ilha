using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Serialization;

namespace Ilha.Prateleira;

/// <summary>Um arquivo ou pasta guardado na prateleira: só o caminho (o original nunca é copiado nem movido).</summary>
public sealed class ItemDaPrateleira
{
    [JsonPropertyName("caminho")] public string Caminho { get; set; } = "";
    // null = ainda não conferido (acabou de chegar)
    [JsonPropertyName("pasta")] public bool? Pasta { get; set; }
    [JsonPropertyName("em")] public DateTime Em { get; set; }

    /// <summary>Existe agora? null = conferindo. Não vai para a config: é conferido de novo a cada vez que o cartão abre.</summary>
    [JsonIgnore] public bool? Disponivel { get; set; }
    /// <summary>A última conferência passou do tempo (drive de rede desligado, por exemplo).</summary>
    [JsonIgnore] public bool Lento { get; set; }
}

public sealed class ConfigPrateleira
{
    [Label("Segundos que o cartão fica aberto quando o arraste passa sem soltar", "Seconds the card stays open when a drag passes without dropping")]
    [JsonPropertyName("segundosAposSair")] public float SegundosAposSair { get; set; } = 1.5f;

    // a própria ilha escreve aqui (não aparece na tela de configurações)
    [JsonPropertyName("itens")] public List<ItemDaPrateleira> Itens { get; set; } = new();
}

/// <summary>A lógica da lista, sem janela nem disco (o autoteste confere tudo daqui).</summary>
internal static class LogicaDaPrateleira
{
    public static readonly StringComparer Comparador = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Caminho de verdade, completo e sem barra sobrando no fim (menos na raiz). Relativo, vazio e item virtual do
    /// shell ("::{GUID}", Este Computador, Lixeira) voltam null. Não toca no disco.
    /// </summary>
    public static string? Normalizar(string? caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho)) return null;
        var c = caminho.Trim().Trim('"');
        if (c.Length == 0 || c.StartsWith("::", StringComparison.Ordinal) || c.Contains('\0')) return null;
        try
        {
            if (!Path.IsPathFullyQualified(c)) return null;
            c = Path.GetFullPath(c);
        }
        catch { return null; }
        var raiz = Path.GetPathRoot(c) ?? "";
        while (c.Length > raiz.Length && (c.EndsWith('\\') || c.EndsWith('/'))) c = c[..^1];
        return c;
    }

    /// <summary>Os caminhos que a prateleira aceita, sem repetir (a ordem de quem soltou fica).</summary>
    public static List<string> Aceitaveis(IEnumerable<string>? caminhos)
    {
        var r = new List<string>();
        if (caminhos == null) return r;
        var vistos = new HashSet<string>(Comparador);
        foreach (var c in caminhos)
            if (Normalizar(c) is { } n && vistos.Add(n)) r.Add(n);
        return r;
    }

    /// <summary>
    /// Põe os caminhos no topo, na ordem em que vieram. O que já estava na prateleira não duplica: sobe para o topo.
    /// Devolve quantos são novos de verdade.
    /// </summary>
    public static int Adicionar(List<ItemDaPrateleira> itens, IEnumerable<string> caminhos, DateTime agora)
    {
        var novos = Aceitaveis(caminhos);
        int contados = 0;
        var topo = new List<ItemDaPrateleira>();
        foreach (var c in novos)
        {
            int i = itens.FindIndex(x => Comparador.Equals(x.Caminho, c));
            if (i >= 0)
            {
                var velho = itens[i];
                itens.RemoveAt(i);
                velho.Em = agora;
                topo.Add(velho);
            }
            else
            {
                topo.Add(new ItemDaPrateleira { Caminho = c, Em = agora });
                contados++;
            }
        }
        itens.InsertRange(0, topo);
        return contados;
    }

    /// <summary>Resultado da conferência no disco. O que sumiu fica na lista, marcado. true = algo mudou.</summary>
    public static bool Marcar(List<ItemDaPrateleira> itens, string caminho, bool existe, bool? pasta, bool lento = false)
    {
        var item = itens.Find(x => Comparador.Equals(x.Caminho, caminho));
        if (item == null) return false;
        bool mudou = item.Disponivel != existe || item.Lento != lento;
        item.Disponivel = existe;
        item.Lento = lento;
        // a pasta que sumiu continua desenhada como pasta
        if (existe && pasta != null && item.Pasta != pasta) { item.Pasta = pasta; mudou = true; }
        return mudou;
    }

    public static bool Remover(List<ItemDaPrateleira> itens, string caminho) =>
        itens.RemoveAll(x => Comparador.Equals(x.Caminho, caminho)) > 0;

    /// <summary>Lida da config: tira caminho inválido e repetido (a config pode ter sido editada à mão).</summary>
    public static List<ItemDaPrateleira> Limpar(IEnumerable<ItemDaPrateleira>? lidos)
    {
        var r = new List<ItemDaPrateleira>();
        if (lidos == null) return r;
        var vistos = new HashSet<string>(Comparador);
        foreach (var x in lidos)
        {
            if (x == null || Normalizar(x.Caminho) is not { } n || !vistos.Add(n)) continue;
            r.Add(new ItemDaPrateleira { Caminho = n, Pasta = x.Pasta, Em = x.Em });
        }
        return r;
    }

    public static bool MesmosCaminhos(IReadOnlyList<ItemDaPrateleira> a, IReadOnlyList<ItemDaPrateleira> b) =>
        a.Count == b.Count && a.Select(x => x.Caminho).SequenceEqual(b.Select(x => x.Caminho), Comparador);

    /// <summary>O nome que aparece em negrito (a raiz de um drive é o próprio caminho).</summary>
    public static string Nome(string caminho)
    {
        var n = Path.GetFileName(caminho);
        return string.IsNullOrEmpty(n) ? caminho : n;
    }

    /// <summary>
    /// A pasta onde o item mora, encurtada pelo começo: o fim do caminho é o que identifica
    /// ("C:\…\Projetos\Abertura"). Cortar pelo fim, como as reticências do texto fazem, esconderia justamente isso.
    /// </summary>
    public static string PastaCurta(string caminho, int max)
    {
        string? pai;
        try { pai = Path.GetDirectoryName(caminho); } catch { return ""; }
        if (string.IsNullOrEmpty(pai)) return "";
        if (pai.Length <= max) return pai;
        var raiz = Path.GetPathRoot(pai) ?? "";
        var partes = pai[raiz.Length..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length == 0) return pai;
        string prefixo = raiz.Length > 0 ? raiz.TrimEnd('\\', '/') + "\\…\\" : "…\\";
        string fim = partes[^1];
        for (int i = partes.Length - 2; i >= 0; i--)
        {
            string tentativa = partes[i] + "\\" + fim;
            if (prefixo.Length + tentativa.Length > max) break;
            fim = tentativa;
        }
        return prefixo.Length + fim.Length <= max ? prefixo + fim : "…\\" + fim;
    }

    /// <summary>
    /// A PastaCurta mais comprida que cabe em `largura` pela medida de verdade (contar letras erra: "W" e "i" não têm a
    /// mesma largura, e o que sobrasse seria cortado pelo fim, justo o pedaço que importa). Busca binária no número de
    /// letras (o texto só cresce com ele): umas 8 medidas por caminho. Nem o último pedaço cabe: volta ele assim mesmo.
    /// </summary>
    public static string PastaQueCabe(string caminho, Func<string, float> medir, float largura)
    {
        string inteira = PastaCurta(caminho, int.MaxValue);
        if (inteira.Length == 0 || medir(inteira) <= largura) return inteira;
        int lo = 1, hi = inteira.Length - 1;
        string melhor = PastaCurta(caminho, 1);
        while (lo <= hi)
        {
            int meio = (lo + hi) / 2;
            string t = PastaCurta(caminho, meio);
            if (medir(t) <= largura) { melhor = t; lo = meio + 1; }
            else hi = meio - 1;
        }
        return melhor;
    }

    /// <summary>"vazia", "1 item", "12 itens" (o detalhe da entrada no menu).</summary>
    public static string Contagem(int n) => n == 0 ? Lang.T("vazia", "empty") : n == 1 ? "1 item" : Lang.T($"{n} itens", $"{n} items");

    /// <summary>A primeira linha visível depois de rolar `dentes` (positivo = para cima, como a rodinha).</summary>
    public static int Rolar(int primeiro, int dentes, int total, int visiveis) =>
        Math.Clamp(primeiro - dentes, 0, Math.Max(0, total - visiveis));

    /// <summary>
    /// "Shell IDList Array" (CFSTR_SHELLIDLIST): CIDA com a área de trabalho como pai (lista vazia, dois bytes zero) e o
    /// PIDL absoluto de cada item. É o que o Explorer põe no arraste junto do CF_HDROP; app que só lê o formato do shell
    /// também entende.
    /// </summary>
    public static byte[] MontarCida(IReadOnlyList<byte[]> pidls)
    {
        int n = pidls.Count;
        int cabecalho = 4 + 4 * (n + 1);
        int total = cabecalho + 2 + pidls.Sum(p => p.Length);
        var r = new byte[total];
        BitConverter.TryWriteBytes(r.AsSpan(0, 4), (uint)n);
        int pos = cabecalho;
        BitConverter.TryWriteBytes(r.AsSpan(4, 4), (uint)pos);
        pos += 2;
        for (int i = 0; i < n; i++)
        {
            BitConverter.TryWriteBytes(r.AsSpan(8 + 4 * i, 4), (uint)pos);
            pidls[i].CopyTo(r, pos);
            pos += pidls[i].Length;
        }
        return r;
    }
}

/// <summary>
/// Quando um aperto vira arraste: depois de 4 DIP com o botão apertado, uma vez só. O DoDragDrop engole o soltar, então
/// o "apertado" do núcleo fica preso e o movimento continua chegando: sem o botão de verdade apertado, não conta.
/// (Mesmo jeito do print.)
/// </summary>
internal sealed class InicioDeArraste
{
    private Vector2? _inicio;
    private bool _foi;

    /// <summary>true = começar o arraste agora.</summary>
    public bool Mover(float x, float y, bool soltou, bool botaoApertado)
    {
        if (soltou || !botaoApertado) { _inicio = null; return false; }
        if (_foi) return false;
        if (_inicio == null) { _inicio = new Vector2(x, y); return false; }
        if (Vector2.Distance(_inicio.Value, new Vector2(x, y)) < 4) return false;
        _foi = true;
        return true;
    }

    public void Terminou()
    {
        _foi = false;
        _inicio = null;
    }
}
