using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Ilha.Volume;

/// <summary>
/// De quem é cada sessão: o caminho do .exe sem abrir o processo (vem no identificador da sessão, convertido pelo
/// QueryDosDevice), o nome pela lista Toolhelp e a descrição pelo próprio arquivo. Só quando o identificador não traz
/// o caminho ele pergunta ao processo, com PROCESS_QUERY_LIMITED_INFORMATION e nunca para jogo da lista competitiva.
/// Os caches não são protegidos: um objeto por thread (a do mixer, ou a sonda do autoteste).
/// </summary>
internal sealed class AppsDoSistema
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string nome, char[] alvo, uint tamanho);
    [DllImport("kernel32.dll")] private static extern uint GetLogicalDrives();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint acesso, bool herdar, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr processo, uint marcas, char[] caminho, ref uint tamanho);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string fonte, StringBuilder saida, uint tamanho, IntPtr reservado);

    private Dictionary<string, string>? _dos;
    private readonly Dictionary<string, string?> _descricoes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<uint, string> _nomes = new();

    /// <summary>pid -> nome do .exe de todos os processos (Toolhelp: nenhum handle aberto em processo nenhum).</summary>
    public Dictionary<uint, string> Processos() => _nomes = ListarProcessos();

    public string? ExeDoPid(uint pid) => _nomes.TryGetValue(pid, out var n) ? n : null;

    public static Dictionary<uint, string> ListarProcessos()
    {
        var r = new Dictionary<uint, string>();
        IntPtr snap = Nativo.CreateToolhelp32Snapshot(Nativo.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return r;
        try
        {
            var e = new Nativo.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Nativo.PROCESSENTRY32W>() };
            for (bool ok = Nativo.Process32FirstW(snap, ref e); ok; ok = Nativo.Process32NextW(snap, ref e))
                r[e.th32ProcessID] = e.szExeFile;
        }
        finally { Nativo.CloseHandle(snap); }
        return r;
    }

    /// <summary>"\Device\HarddiskVolume3" -> "C:" de cada letra montada.</summary>
    public static Dictionary<string, string> MapaDos()
    {
        var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        uint letras = GetLogicalDrives();
        var buf = new char[1024];
        for (int i = 0; i < 26; i++)
        {
            if ((letras & (1u << i)) == 0) continue;
            string letra = (char)('A' + i) + ":";
            uint n = QueryDosDeviceW(letra, buf, (uint)buf.Length);
            if (n == 0) continue;
            int fim = Array.IndexOf(buf, '\0');
            string alvo = new(buf, 0, fim < 0 ? (int)n : fim);
            if (alvo.Length > 0) r.TryAdd(alvo, letra);
        }
        return r;
    }

    /// <summary>
    /// O .exe da sessão. Primeiro pelo identificador (sem tocar no processo); se não der, pergunta ao processo, menos
    /// para os da lista competitiva.
    /// </summary>
    public string? Caminho(string? identificador, uint pid, string? exe, IEnumerable<string> competitivos)
    {
        var doId = LogicaDoMixer.CaminhoNoIdentificador(identificador);
        if (doId != null)
        {
            _dos ??= MapaDos();
            var c = LogicaDoMixer.ParaCaminhoDos(doId, _dos);
            if (c == null)
            {
                // disco montado depois de a ilha abrir
                _dos = MapaDos();
                c = LogicaDoMixer.ParaCaminhoDos(doId, _dos);
            }
            if (c != null) return c;
        }
        // sem o nome pela Toolhelp não dá para conferir a lista competitiva: não abre
        if (pid == 0 || exe == null || LogicaDoMixer.EhCompetitivo(exe, competitivos)) return null;
        return CaminhoPeloProcesso(pid);
    }

    private static string? CaminhoPeloProcesso(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buf = new char[1024];
            uint n = (uint)buf.Length;
            return QueryFullProcessImageNameW(h, 0, buf, ref n) && n > 0 ? new string(buf, 0, (int)n) : null;
        }
        finally { Nativo.CloseHandle(h); }
    }

    /// <summary>Nome de exibição da sessão, com "@arquivo.dll,-202" resolvido (null se não der).</summary>
    public static string? Exibicao(string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome)) return null;
        if (!LogicaDoMixer.Indireto(nome)) return nome.Trim();
        try
        {
            var sb = new StringBuilder(512);
            string fonte = Environment.ExpandEnvironmentVariables(nome.Trim());
            return SHLoadIndirectString(fonte, sb, (uint)sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }

    /// <summary>FileDescription do .exe (lido do arquivo no disco), em cache por caminho.</summary>
    public string? Descricao(string? caminho)
    {
        if (string.IsNullOrEmpty(caminho)) return null;
        if (_descricoes.TryGetValue(caminho, out var d)) return d;
        d = null;
        try
        {
            if (File.Exists(caminho))
            {
                var fi = FileVersionInfo.GetVersionInfo(caminho);
                d = string.IsNullOrWhiteSpace(fi.FileDescription) ? null : fi.FileDescription.Trim();
            }
        }
        catch { }
        _descricoes[caminho] = d;
        return d;
    }
}
