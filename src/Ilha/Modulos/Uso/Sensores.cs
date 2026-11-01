using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Ilha.Uso;

/// <summary>
/// Processador, memória e rede pelas chamadas baratas do Windows (microssegundos cada): GetSystemTimes,
/// GlobalMemoryStatusEx e GetIfTable2. Nada de PDH nem WMI.
/// </summary>
internal static unsafe class Win
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out ulong ocioso, out ulong kernel, out ulong usuario);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("iphlpapi.dll")] private static extern uint GetIfTable2(out IntPtr tabela);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr p);

    public static TemposCpu? Cpu() => GetSystemTimes(out var o, out var k, out var u) ? new TemposCpu(o, k, u) : null;

    public static (ulong Usada, ulong Total)? Memoria()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        if (!GlobalMemoryStatusEx(ref m) || m.ullTotalPhys == 0) return null;
        return (m.ullTotalPhys - m.ullAvailPhys, m.ullTotalPhys);
    }

    // MIB_IF_ROW2 (netioapi.h): 1352 bytes; a tabela tem NumEntries (4 bytes) e as linhas a partir do byte 8
    public const int TAM_LINHA = 1352;
    private const int OFS_TIPO = 1128, OFS_FLAGS = 1152, OFS_ESTADO = 1156, OFS_IN = 1208, OFS_OUT = 1280, OFS_DESCRICAO = 542;
    private const uint IF_TYPE_SOFTWARE_LOOPBACK = 24, IF_OPER_STATUS_UP = 1;

    /// <summary>
    /// Bytes recebidos e enviados de cada interface física ligada, pelo LUID. Fica de fora o que é virtual (Hyper-V,
    /// VPN, loopback) e as interfaces de filtro (QoS, WFP), que repetem os contadores da placa embaixo delas.
    /// </summary>
    public static Dictionary<ulong, Octetos>? Rede(List<string>? nomes = null)
    {
        if (GetIfTable2(out var t) != 0 || t == IntPtr.Zero) return null;
        try
        {
            var r = new Dictionary<ulong, Octetos>();
            uint n = *(uint*)t;
            byte* linhas = (byte*)t + 8;
            for (uint i = 0; i < n; i++)
            {
                byte* l = linhas + i * TAM_LINHA;
                byte flags = l[OFS_FLAGS];
                bool hardware = (flags & 0x01) != 0, filtro = (flags & 0x02) != 0;
                uint tipo = *(uint*)(l + OFS_TIPO), estado = *(uint*)(l + OFS_ESTADO);
                if (!hardware || filtro || tipo == IF_TYPE_SOFTWARE_LOOPBACK || estado != IF_OPER_STATUS_UP) continue;
                r[*(ulong*)l] = new Octetos(*(ulong*)(l + OFS_IN), *(ulong*)(l + OFS_OUT));
                nomes?.Add(new string((char*)(l + OFS_DESCRICAO)));
            }
            return r;
        }
        finally { FreeMibTable(t); }
    }
}

/// <summary>
/// RTX (e qualquer NVIDIA) pela NVML do driver, a nvml.dll da System32 (assinada, passa no Smart App Control).
/// O nvmlInit custa ~70 ms: uma vez só, fora da thread da interface. Depois cada leitura custa microssegundos.
/// Sem a DLL (sem NVIDIA) tudo fica nulo, sem erro.
/// </summary>
internal sealed unsafe class Nvml : IDisposable
{
    private const int SUCESSO = 0, NAO_SUPORTADO = 3, NAO_INICIADA = 1, GPU_PERDIDA = 15;

    [StructLayout(LayoutKind.Sequential)] private struct Utilizacao { public uint Gpu, Memoria; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoriaV1 { public ulong Total, Livre, Usada; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoriaV2 { public uint Versao; public ulong Total, Reservada, Livre, Usada; }

    private IntPtr _dll, _placa;
    private delegate* unmanaged<int> _init, _desligar;
    private delegate* unmanaged<uint, IntPtr*, int> _porIndice;
    private delegate* unmanaged<IntPtr, Utilizacao*, int> _uso;
    private delegate* unmanaged<IntPtr, int, uint*, int> _temperatura;
    private delegate* unmanaged<IntPtr, MemoriaV1*, int> _memV1;
    private delegate* unmanaged<IntPtr, MemoriaV2*, int> _memV2;
    private delegate* unmanaged<IntPtr, byte*, uint, int> _nome;
    private int _falhasSeguidas;

    public bool Pronta => _placa != IntPtr.Zero;
    /// <summary>A nvml.dll existe e carregou (sem ela não adianta tentar de novo).</summary>
    public bool TemDll => _dll != IntPtr.Zero;
    public string Nome { get; private set; } = "";
    /// <summary>Por que não ficou pronta (para o log e o autoteste).</summary>
    public string Motivo { get; private set; } = "";
    /// <summary>true se a VRAM vem da v2 (sem a parte que o driver reserva, igual ao nvidia-smi).</summary>
    public bool MemoriaV2Disponivel => _memV2 != null;

    /// <summary>Carrega a DLL e abre a placa 0. Lento (~70 ms): chamar fora da thread da interface.</summary>
    public bool Iniciar()
    {
        if (Pronta) return true;
        if (_dll == IntPtr.Zero && !Carregar()) return false;
        int r = _init();
        if (r != SUCESSO) { Motivo = $"nvmlInit_v2 devolveu {r}"; return false; }
        IntPtr p;
        r = _porIndice(0, &p);
        if (r != SUCESSO || p == IntPtr.Zero) { Motivo = $"nenhuma placa NVIDIA ({r})"; _desligar(); return false; }
        _placa = p;
        _falhasSeguidas = 0;
        if (_nome != null)
        {
            byte* b = stackalloc byte[96];
            if (_nome(p, b, 96) == SUCESSO) Nome = Encoding.UTF8.GetString(b, new ReadOnlySpan<byte>(b, 96).IndexOf((byte)0) is int z and >= 0 ? z : 96);
        }
        return true;
    }

    private bool Carregar()
    {
        // só de pasta do sistema (nunca da pasta da ilha); a pasta antiga do NVSMI cobre driver velho
        IntPtr h = IntPtr.Zero;
        string sistema = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll");
        string nvsmi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
        foreach (var c in new[] { sistema, nvsmi })
            if (File.Exists(c) && NativeLibrary.TryLoad(c, out h)) break;
        if (h == IntPtr.Zero) { Motivo = "sem nvml.dll (sem placa NVIDIA)"; return false; }
        IntPtr F(string nome) => NativeLibrary.TryGetExport(h, nome, out var f) ? f : IntPtr.Zero;
        var init = F("nvmlInit_v2");
        var desligar = F("nvmlShutdown");
        var porIndice = F("nvmlDeviceGetHandleByIndex_v2");
        var uso = F("nvmlDeviceGetUtilizationRates");
        var temp = F("nvmlDeviceGetTemperature");
        var memV1 = F("nvmlDeviceGetMemoryInfo");
        if (init == IntPtr.Zero || desligar == IntPtr.Zero || porIndice == IntPtr.Zero || uso == IntPtr.Zero || temp == IntPtr.Zero || memV1 == IntPtr.Zero)
        {
            Motivo = "nvml.dll sem as funções esperadas";
            NativeLibrary.Free(h);
            return false;
        }
        _dll = h;
        _init = (delegate* unmanaged<int>)init;
        _desligar = (delegate* unmanaged<int>)desligar;
        _porIndice = (delegate* unmanaged<uint, IntPtr*, int>)porIndice;
        _uso = (delegate* unmanaged<IntPtr, Utilizacao*, int>)uso;
        _temperatura = (delegate* unmanaged<IntPtr, int, uint*, int>)temp;
        _memV1 = (delegate* unmanaged<IntPtr, MemoriaV1*, int>)memV1;
        var memV2 = F("nvmlDeviceGetMemoryInfo_v2");
        _memV2 = memV2 == IntPtr.Zero ? null : (delegate* unmanaged<IntPtr, MemoriaV2*, int>)memV2;
        var nome = F("nvmlDeviceGetName");
        _nome = nome == IntPtr.Zero ? null : (delegate* unmanaged<IntPtr, byte*, uint, int>)nome;
        return true;
    }

    public readonly record struct Leitura(double? Uso, double? Temperatura, ulong? VramUsada, ulong? VramTotal);

    /// <summary>Uso, temperatura e VRAM. O que falhar volta nulo; três falhas seguidas soltam a placa (driver reiniciou).</summary>
    public Leitura Ler()
    {
        if (!Pronta) return default;
        double? uso = null, temp = null;
        ulong? usada = null, total = null;
        bool perdida = false;

        Utilizacao u;
        int r = _uso(_placa, &u);
        if (r == SUCESSO) uso = u.Gpu; else perdida |= r is GPU_PERDIDA or NAO_INICIADA;

        uint t;
        r = _temperatura(_placa, 0, &t);
        if (r == SUCESSO) temp = t; else perdida |= r is GPU_PERDIDA or NAO_INICIADA;

        if (_memV2 != null)
        {
            var m = new MemoriaV2 { Versao = (uint)sizeof(MemoriaV2) | (2u << 24) };
            r = _memV2(_placa, &m);
            if (r == SUCESSO) { usada = m.Usada; total = m.Total; }
            else if (r == NAO_SUPORTADO) _memV2 = null;
            else perdida |= r is GPU_PERDIDA or NAO_INICIADA;
        }
        if (usada == null)
        {
            MemoriaV1 m;
            r = _memV1(_placa, &m);
            if (r == SUCESSO) { usada = m.Usada; total = m.Total; } else perdida |= r is GPU_PERDIDA or NAO_INICIADA;
        }

        if (uso == null && temp == null && usada == null)
        {
            if (perdida || ++_falhasSeguidas >= 3) Soltar("a placa parou de responder");
        }
        else _falhasSeguidas = 0;
        return new Leitura(uso, temp, usada, total);
    }

    private void Soltar(string motivo)
    {
        if (_placa == IntPtr.Zero) return;
        _placa = IntPtr.Zero;
        Motivo = motivo;
        try { _desligar(); } catch { }
    }

    public void Dispose()
    {
        Soltar("desligada");
        // a DLL fica carregada: soltar e carregar de novo a nvml.dll no mesmo processo não ganha nada
    }
}

/// <summary>Junta tudo numa amostra, guardando a leitura anterior para as contas de delta.</summary>
internal sealed class Sensores
{
    private TemposCpu? _cpuAntes;
    private Dictionary<ulong, Octetos>? _redeAntes;
    private long _redeQuando;
    private double _ultimaCpu;

    /// <summary>A placa NVIDIA, quando a inicialização (em outra thread) terminar. Null = sem GPU.</summary>
    public Nvml? Gpu { get; set; }

    /// <summary>Lê as bases do delta sem produzir amostra (antes de uma leitura curta depois de muito tempo parado).</summary>
    public void Base()
    {
        _cpuAntes = Win.Cpu();
        _redeAntes = Win.Rede();
        _redeQuando = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Uma amostra. A rede (o GetIfTable2 custa ~0,5 ms, o resto junto alguns microssegundos) só com `comRede`;
    /// base de rede com mais de 3 s vira só base nova, sem taxa (seria a média de um tempo em que ninguém olhava).
    /// </summary>
    public Amostra Ler(bool comRede = true)
    {
        var cpu = Win.Cpu();
        if (cpu is TemposCpu c && _cpuAntes is TemposCpu a && Contas.Cpu(a, c) is double pct) _ultimaCpu = pct;
        _cpuAntes = cpu ?? _cpuAntes;

        var mem = Win.Memoria() ?? (0, 0);

        double? desce = null, sobe = null;
        if (comRede && Win.Rede() is { } rede)
        {
            long agora = System.Diagnostics.Stopwatch.GetTimestamp();
            double dt = (agora - _redeQuando) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (_redeAntes != null && dt <= 3)
            {
                var (d, s) = Contas.Rede(_redeAntes, rede, dt);
                desce = d;
                sobe = s;
            }
            _redeAntes = rede;
            _redeQuando = agora;
        }

        var g = Gpu is { Pronta: true } n ? n.Ler() : default;
        return new Amostra(_ultimaCpu, mem.Usada, mem.Total, g.Uso, g.Temperatura, g.VramUsada, g.VramTotal, desce, sobe);
    }
}
