using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Ilha.Extras;

/// <summary>
/// Casa o nome que o dispositivo anuncia ("AirPods Pro 3 de Fulano", "WH-1000XM6", "Galaxy Buds4 Pro (A1B2)")
/// com uma foto de imagens/fones/*.png pelo nome do arquivo ("airpods-pro-3.png", "sony-wh-1000xm6.png").
/// Marca e cor no nome do arquivo são opcionais; número que falta no nome do dispositivo (geração) é tolerado,
/// e no empate ganha o número maior (o modelo mais novo).
/// </summary>
internal static class Fotos
{
    public static string Pasta => Path.Combine(AppContext.BaseDirectory, "imagens", "fones");

    // palavras do nome do arquivo que o dispositivo pode não dizer
    private static readonly HashSet<string> Opcionais = new(StringComparer.Ordinal)
    {
        "apple", "sony", "samsung", "bose", "jbl", "beats", "hyperx", "razer", "steelseries", "logitech", "corsair",
        "nothing", "g", "preto", "branco", "lilas", "azul", "rosa", "vermelho",
    };

    private static readonly Dictionary<string, string> Sinonimos = new(StringComparer.Ordinal)
    {
        ["headphones"] = "fones", ["headphone"] = "fones", ["fone"] = "fones", ["headset"] = "fones",
        ["earbud"] = "earbuds",
    };

    public static List<string> Palavras(string texto)
    {
        var sem = new StringBuilder();
        foreach (var ch in texto.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sem.Append(char.ToLowerInvariant(ch));
        var r = new List<string>();
        var atual = new StringBuilder();
        foreach (var ch in sem.ToString())
        {
            if (char.IsLetterOrDigit(ch)) { atual.Append(ch); continue; }
            if (atual.Length > 0) { r.Add(atual.ToString()); atual.Clear(); }
        }
        if (atual.Length > 0) r.Add(atual.ToString());
        return r.Select(p => Sinonimos.TryGetValue(p, out var s) ? s : p).ToList();
    }

    /// <summary>Os .png da pasta de fotos (vazio se a pasta não existe, como num clone do git).</summary>
    public static List<string> Arquivos()
    {
        try { return Directory.Exists(Pasta) ? Directory.GetFiles(Pasta, "*.png").Select(Path.GetFileName).OfType<string>().ToList() : new(); }
        catch { return new(); }
    }

    private static int MaiorNumero(IEnumerable<string> palavras) =>
        palavras.Select(p => int.TryParse(p, out int n) ? n : 0).DefaultIfEmpty(0).Max();

    /// <summary>
    /// A foto para o dispositivo, ou null. `trocas`: pedaço do nome -> arquivo (config, vale primeiro).
    /// `variante`: palavra preferida quando só a cor muda (ex.: "preto" para o g733-preto.png).
    /// </summary>
    public static string? Escolher(string nome, IReadOnlyCollection<string> arquivos, IReadOnlyDictionary<string, string>? trocas = null, string? variante = null)
    {
        if (trocas != null)
            foreach (var (pedaco, arq) in trocas)
                if (pedaco.Length > 0 && nome.Contains(pedaco, StringComparison.OrdinalIgnoreCase))
                {
                    var achado = arquivos.FirstOrDefault(a => string.Equals(a, arq, StringComparison.OrdinalIgnoreCase));
                    if (achado != null) return achado;
                }
        var doNome = new HashSet<string>(Palavras(nome));
        var candidatos = new List<(string Arq, bool Perfeito, int Casou, int Numero, bool Variante)>();
        foreach (var arq in arquivos)
        {
            var todas = Palavras(Path.GetFileNameWithoutExtension(arq));
            var exigidas = todas.Where(p => !Opcionais.Contains(p)).ToList();
            if (exigidas.Count == 0) continue;
            int casou = exigidas.Count(doNome.Contains);
            var faltam = exigidas.Where(p => !doNome.Contains(p)).ToList();
            bool perfeito = faltam.Count == 0;
            // só a geração faltando (o Windows mostra "AirPods Pro de Fulano" para qualquer geração)
            bool parcial = !perfeito && casou >= 1 && faltam.All(p => p.All(char.IsDigit)) && casou >= exigidas.Count - 1;
            if (!perfeito && !parcial) continue;
            bool temVariante = variante != null && todas.Contains(variante.ToLowerInvariant());
            candidatos.Add((arq, perfeito, casou, MaiorNumero(exigidas), temVariante));
        }
        return candidatos
            .OrderByDescending(c => c.Perfeito)
            .ThenByDescending(c => c.Casou)
            .ThenByDescending(c => c.Variante)
            .ThenByDescending(c => c.Numero)
            .ThenBy(c => c.Arq, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Arq)
            .FirstOrDefault();
    }

    /// <summary>Tira o " - Find My" que a Apple põe no nome (o Windows mostra "AirPods Pro 3 de Fulano - Find My").</summary>
    public static string NomeLimpo(string nome)
    {
        nome = nome.Trim();
        foreach (var fim in new[] { " - Find My", " – Find My", "- Find My" })
            if (nome.EndsWith(fim, StringComparison.OrdinalIgnoreCase)) return nome[..^fim.Length].TrimEnd();
        return nome;
    }
}

/// <summary>Modelos da Apple pelo id do anúncio BLE e do PnP (BTHENUM\..._VID&amp;0001004C_PID&amp;2027).</summary>
internal static class ModelosApple
{
    // ids conferidos no AirPodsDesktop (Core/AppleCP.cpp), mesmo número que o Windows mostra no PID
    private static readonly Dictionary<ushort, (string Nome, string? Foto)> Modelos = new()
    {
        [0x2002] = ("AirPods", null),
        [0x200F] = ("AirPods (2ª geração)", null),
        [0x2013] = ("AirPods (3ª geração)", null),
        [0x2019] = ("AirPods 4", "airpods-4.png"),
        [0x201B] = ("AirPods 4 com cancelamento de ruído", "airpods-4.png"),
        [0x2030] = ("AirPods 5", "airpods-5.png"),
        [0x2036] = ("AirPods 5", "airpods-5.png"),
        [0x200E] = ("AirPods Pro", null),
        [0x2014] = ("AirPods Pro 2", "airpods-pro-2.png"),
        [0x2024] = ("AirPods Pro 2 (USB-C)", "airpods-pro-2.png"),
        [0x2027] = ("AirPods Pro 3", "airpods-pro-3.png"),
        [0x200A] = ("AirPods Max", "airpods-max.png"),
        [0x201F] = ("AirPods Max (USB-C)", "airpods-max.png"),
        [0x2012] = ("Beats Fit Pro", null),
    };

    public static bool Conhecido(ushort id) => Modelos.ContainsKey(id);
    public static string? Nome(ushort id) => Modelos.TryGetValue(id, out var m) ? m.Nome : null;
    public static string? Foto(ushort id) => Modelos.TryGetValue(id, out var m) ? m.Foto : null;

    /// <summary>Fone que fala o protocolo da Apple (AirPods e Beats), pelo fabricante do PnP ou pelo nome.</summary>
    public static bool EhApple(ushort? fabricante, string nome) =>
        fabricante == 0x004C || nome.Contains("AirPods", StringComparison.OrdinalIgnoreCase) || nome.Contains("Beats", StringComparison.OrdinalIgnoreCase);
}
