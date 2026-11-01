using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ilha.Volume;

/// <summary>Uma sessão de áudio como a thread do mixer leu (nada de COM aqui dentro).</summary>
internal sealed record SessaoDeSom(
    string Id, uint Pid, bool Sistema, string? Caminho, string? Exe, string? NomeExibicao, string? Descricao,
    int Estado, float Volume, bool Mudo, bool Vivo);

/// <summary>Uma linha do mixer: um app (todas as sessões dele juntas).</summary>
internal sealed record AppDeSom(string Chave, string Nome, string? Caminho, bool Sistema, float Volume, bool Mudo, bool Tocando, int Sessoes);

/// <summary>
/// Uma leitura do mixer: a lista e, desde a leitura anterior, os apps em que a ilha mexeu e que já foram relidos
/// depois do Set (`Confirmados`) e os que alguém de fora mexeu (`DeFora`, pelo contexto do aviso).
/// </summary>
internal sealed record LeituraDoMixer(IReadOnlyList<AppDeSom> Apps, bool Ok, IReadOnlySet<string> Confirmados, IReadOnlySet<string> DeFora)
{
    public static LeituraDoMixer Vazia(bool ok) => new(Array.Empty<AppDeSom>(), ok, new HashSet<string>(), new HashSet<string>());
}

/// <summary>Contas e regras do mixer por app, sem nada do sistema (o `--testar volume` confere).</summary>
internal static class LogicaDoMixer
{
    // AudioSessionState
    public const int Inativa = 0, Ativa = 1, Expirada = 2;
    public const string ChaveDoSistema = "sistema";
    public const string NomeDoSistema = "Sons do sistema";

    /// <summary>
    /// O caminho do .exe que vem dentro do identificador da sessão, no formato que o Windows usa hoje (não documentado):
    /// "{0.0.0.00000000}.{guid}|\Device\HarddiskVolume3\...\app.exe%b{guid}". "#" no lugar do caminho = sem processo
    /// (os sons do sistema). null quando o formato não bate.
    /// </summary>
    public static string? CaminhoNoIdentificador(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        int barra = id.IndexOf('|');
        if (barra < 0) return null;
        int fim = id.IndexOf("%b", barra + 1, StringComparison.Ordinal);
        string c = (fim < 0 ? id[(barra + 1)..] : id[(barra + 1)..fim]).Trim();
        if (c.Length == 0 || c.StartsWith('#')) return null;
        if (!c.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return c;
    }

    /// <summary>"\Device\HarddiskVolume3\Programas\x.exe" -> "C:\Programas\x.exe" pelo mapa de QueryDosDevice (aparelho -> "C:").</summary>
    public static string? ParaCaminhoDos(string caminho, IReadOnlyDictionary<string, string> mapa)
    {
        if (caminho.Length >= 3 && char.IsLetter(caminho[0]) && caminho[1] == ':' && caminho[2] == '\\') return caminho;
        foreach (var (aparelho, letra) in mapa)
        {
            if (aparelho.Length == 0) continue;
            if (caminho.Length > aparelho.Length && caminho[aparelho.Length] == '\\'
                && caminho.StartsWith(aparelho, StringComparison.OrdinalIgnoreCase))
                return letra.TrimEnd('\\') + caminho[aparelho.Length..];
        }
        return null;
    }

    /// <summary>Nome de exibição indireto ("@%SystemRoot%\System32\x.dll,-202") que precisa do SHLoadIndirectString.</summary>
    public static bool Indireto(string? nome) => nome != null && nome.TrimStart().StartsWith('@');

    private static bool Util(string? s) => !string.IsNullOrWhiteSpace(s) && !Indireto(s);

    public static string NormalizarExe(string n)
    {
        n = n.Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe";
    }

    /// <summary>O .exe está na lista de jogos competitivos (com ou sem ".exe", sem diferença de maiúscula)?</summary>
    public static bool EhCompetitivo(string? exe, IEnumerable<string> lista)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;
        string n = NormalizarExe(Path.GetFileName(exe));
        foreach (var x in lista)
            if (!string.IsNullOrWhiteSpace(x) && string.Equals(NormalizarExe(x), n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Um app por linha: os sons do sistema numa só; o resto pelo .exe (o navegador toca de mais de um processo), sem caminho pelo processo.</summary>
    public static string Chave(SessaoDeSom s)
    {
        if (s.Sistema) return ChaveDoSistema;
        if (!string.IsNullOrEmpty(s.Caminho)) return "exe:" + s.Caminho.ToLowerInvariant();
        return "pid:" + s.Pid;
    }

    /// <summary>Nome de exibição da sessão; senão a descrição do arquivo; senão o nome do .exe.</summary>
    public static string Nome(IEnumerable<SessaoDeSom> grupo)
    {
        var l = grupo as IList<SessaoDeSom> ?? grupo.ToList();
        if (l.Any(s => s.Sistema)) return NomeDoSistema;
        var exib = l.FirstOrDefault(s => Util(s.NomeExibicao))?.NomeExibicao;
        if (exib != null) return exib.Trim();
        var desc = l.FirstOrDefault(s => Util(s.Descricao))?.Descricao;
        if (desc != null) return desc.Trim();
        var cam = l.FirstOrDefault(s => !string.IsNullOrEmpty(s.Caminho))?.Caminho;
        if (cam != null) return Path.GetFileNameWithoutExtension(cam);
        var exe = l.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Exe))?.Exe;
        if (exe != null) return Path.GetFileNameWithoutExtension(exe.Trim());
        return "App";
    }

    /// <summary>
    /// Junta as sessões em apps. Fica de fora sessão expirada e de processo que já fechou. O volume da linha é o da
    /// primeira sessão tocando (senão o da primeira); mudo só se todas estão mudas. Ordem alfabética, sistema por último:
    /// a lista não pula quando um app começa ou para de tocar.
    /// </summary>
    public static List<AppDeSom> Agrupar(IEnumerable<SessaoDeSom> sessoes)
    {
        var validas = sessoes.Where(s => s.Estado != Expirada && (s.Sistema || s.Vivo)).ToList();
        var r = new List<AppDeSom>();
        foreach (var g in validas.GroupBy(Chave))
        {
            var l = g.ToList();
            var refe = l.FirstOrDefault(s => s.Estado == Ativa) ?? l[0];
            r.Add(new AppDeSom(
                g.Key, Nome(l), l.FirstOrDefault(s => !string.IsNullOrEmpty(s.Caminho))?.Caminho, l.Any(s => s.Sistema),
                float.IsFinite(refe.Volume) ? Math.Clamp(refe.Volume, 0f, 1f) : 0f, l.All(s => s.Mudo), l.Any(s => s.Estado == Ativa), l.Count));
        }
        return r.OrderBy(a => a.Sistema ? 1 : 0)
            .ThenBy(a => a.Nome, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Chave, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// As linhas que aparecem: as tocando; as paradas também se `paradas`; e, com o cartão aberto, as que já
    /// apareceram nesta abertura (uma linha não some debaixo do dedo porque o app pausou).
    /// </summary>
    public static List<AppDeSom> Visiveis(IEnumerable<AppDeSom> apps, bool paradas, ICollection<string> jaMostradas) =>
        apps.Where(a => a.Tocando || paradas || jaMostradas.Contains(a.Chave)).ToList();

    /// <summary>Rolagem da lista (em linhas), presa para nunca sobrar linha vazia no fim.</summary>
    public static int Rolagem(int atual, int dentes, int total, int linhas) =>
        Math.Clamp(atual - dentes, 0, Math.Max(0, total - linhas));

    /// <summary>
    /// Solta os valores que a ilha mandou e que valiam no lugar dos do Windows: os confirmados, os que alguém de fora
    /// mexeu e os de app que sumiu. Leitura antiga (sem a confirmação) não solta nada, então a barra não volta um
    /// quadro. O volume do app sendo arrastado fica com o do dedo. Devolve se soltou algum.
    /// </summary>
    public static bool Reconciliar(Dictionary<string, float> volumes, Dictionary<string, bool> mudos, IEnumerable<AppDeSom> apps,
        IReadOnlySet<string> confirmados, IReadOnlySet<string> deFora, string? arrastando)
    {
        var presentes = new HashSet<string>(apps.Select(a => a.Chave), StringComparer.Ordinal);
        bool Solta(string k) => !presentes.Contains(k) || confirmados.Contains(k) || deFora.Contains(k);
        int antes = volumes.Count + mudos.Count;
        foreach (var k in volumes.Keys.Where(k => k != arrastando && Solta(k)).ToList()) volumes.Remove(k);
        foreach (var k in mudos.Keys.Where(Solta).ToList()) mudos.Remove(k);
        return volumes.Count + mudos.Count != antes;
    }

    // AudioSessionDisconnectReason.DisconnectReasonServerShutdown
    public const int MotivoServicoDesligou = 1;

    /// <summary>
    /// Espera (ms) antes da tentativa `n` (0 = a primeira) de reabrir o gerente de sessões depois que o serviço de
    /// áudio caiu: 1, 2, 4, 8 e 15 s, depois três de 30 s (2 min no total); -1 = desiste até a saída padrão mudar.
    /// </summary>
    public static int EsperaParaReabrir(int n) => n switch
    {
        < 0 => 1000,
        < 4 => 1000 << n,
        4 => 15_000,
        < 8 => 30_000,
        _ => -1,
    };
}
