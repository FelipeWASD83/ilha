using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace Ilha;

/// <summary>O que está acontecendo na tela agora, do ponto de vista da ilha.</summary>
internal sealed record Contexto
{
    public IntPtr Janela { get; init; }
    public string Processo { get; init; } = "";
    public bool Desktop { get; init; }
    public bool Competitivo { get; init; }
    public bool TelaCheia { get; init; }
    // o jogo pediu tela cheia exclusiva (pode ser exclusiva de verdade ou convertida pelo Windows)
    public bool D3DExclusivo { get; init; }
    public bool NaoPerturbe { get; init; }
    public bool? NaoPerturbeLido { get; init; }
    public bool Foco { get; init; }

    public override string ToString() =>
        $"frente={Processo} desktop={Desktop} competitivo={Competitivo} telaCheia={TelaCheia} d3dExclusivo={D3DExclusivo} " +
        $"naoPerturbe={(NaoPerturbeLido == null ? "?" : NaoPerturbeLido.ToString())} foco={Foco}";
}

/// <summary>
/// Vigia a janela da frente por evento do sistema (SetWinEventHook fora de contexto: nada é injetado em
/// processo nenhum) e descobre o nome do .exe pela lista de processos, sem abrir handle no jogo.
/// </summary>
internal sealed class Visibilidade : IDisposable
{
    private readonly Nativo.WinEventProc _proc;
    private readonly IntPtr _gancho, _ganchoMin;
    private readonly System.Windows.Forms.Timer _espera = new() { Interval = 120 };
    private readonly System.Windows.Forms.Timer _lento = new() { Interval = 10_000 };
    private readonly Dictionary<uint, string> _nomes = new();
    private uint _ultimoPid;
    private readonly uint _meuPid = (uint)Environment.ProcessId;
    private Windows.UI.Shell.FocusSessionManager? _foco;
    private Config _cfg;
    private HashSet<string> _competitivos = new(StringComparer.OrdinalIgnoreCase);

    public Contexto Atual { get; private set; } = new();
    public event Action<Contexto>? Mudou;

    public Visibilidade(Config cfg)
    {
        _cfg = cfg;
        TrocarConfig(cfg);
        _proc = AoEvento;
        uint f = Nativo.WINEVENT_OUTOFCONTEXT | Nativo.WINEVENT_SKIPOWNPROCESS;
        _gancho = Nativo.SetWinEventHook(Nativo.EVENT_SYSTEM_FOREGROUND, Nativo.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, f);
        // minimizar a última janela leva para a área de trabalho sem trocar a janela da frente na hora
        _ganchoMin = Nativo.SetWinEventHook(0x0016, Nativo.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _proc, 0, 0, f);
        _espera.Tick += (_, _) => { _espera.Stop(); Avaliar(); };
        // o Não Incomodar não tem evento público: lido a cada 10 s e a cada troca de janela
        _lento.Tick += (_, _) => Avaliar();
        _lento.Start();
        try
        {
            if (Windows.UI.Shell.FocusSessionManager.IsSupported)
            {
                _foco = Windows.UI.Shell.FocusSessionManager.GetDefault();
                _foco.IsFocusActiveChanged += (_, _) => Programa.NaInterface(Avaliar);
            }
        }
        catch (Exception e) { Log.Escrever("sessão de foco indisponível: " + e.Message); }
        Avaliar();
    }

    public void TrocarConfig(Config cfg)
    {
        _cfg = cfg;
        _competitivos = new HashSet<string>(cfg.JogosCompetitivos.Select(Normalizar), StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalizar(string n)
    {
        n = n.Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe";
    }

    private void AoEvento(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        if (obj != 0) return;
        _espera.Stop();
        _espera.Start();
    }

    public void Avaliar()
    {
        var antes = Atual;
        var h = Nativo.GetForegroundWindow();
        string classe = h == IntPtr.Zero ? "" : Nativo.Classe(h);
        Nativo.GetWindowThreadProcessId(h, out uint pid);

        bool transitoria = h == IntPtr.Zero || pid == _meuPid || EhDoShellTransitorio(classe, pid);
        bool desktop = transitoria ? antes.Desktop : EhAreaDeTrabalho(h, classe, pid);
        string proc = transitoria ? antes.Processo : NomeDoProcesso(pid);
        IntPtr janela = transitoria ? antes.Janela : h;

        Nativo.SHQueryUserNotificationState(out int quns);
        bool? np = LerNaoPerturbe();
        bool foco = false;
        try { foco = _foco?.IsFocusActive ?? false; } catch { }

        var novo = new Contexto
        {
            Janela = janela,
            Processo = proc,
            Desktop = desktop,
            Competitivo = !desktop && _competitivos.Contains(proc),
            // janela maximizada passa uns pixels da borda do monitor: não é tela cheia
            TelaCheia = quns is 2 or 3 or 4 || (!desktop && !Nativo.IsZoomed(janela) && CobreOMonitor(janela)),
            D3DExclusivo = quns == 3,
            NaoPerturbe = np == true || foco,
            NaoPerturbeLido = np,
            Foco = foco,
        };
        if (novo == antes) return;
        Atual = novo;
        Log.Escrever("contexto: " + novo);
        Mudou?.Invoke(novo);
    }

    // Barra de tarefas, Iniciar, central de notificações, Alt+Tab: passam pela frente sem tirar a pessoa de onde estava
    private static readonly string[] ClassesTransitorias =
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland", "ForegroundStaging", "MultitaskingViewFrame", "TaskSwitcherWnd",
    };

    private bool EhDoShellTransitorio(string classe, uint pid)
    {
        if (Array.IndexOf(ClassesTransitorias, classe) >= 0) return true;
        // Iniciar, Pesquisa, Configurações rápidas, notificações: qualquer janela desses processos do shell
        string n = NomeDoProcesso(pid);
        return n is "StartMenuExperienceHost.exe" or "SearchHost.exe" or "ShellExperienceHost.exe" or "ShellHost.exe";
    }

    private static bool EhAreaDeTrabalho(IntPtr h, string classe, uint pid)
    {
        if (classe != "Progman" && classe != "WorkerW") return false;
        Nativo.GetWindowThreadProcessId(Nativo.GetShellWindow(), out uint shell);
        return pid == shell;
    }

    private static bool CobreOMonitor(IntPtr h)
    {
        if (h == IntPtr.Zero || !Nativo.GetWindowRect(h, out var r)) return false;
        var m = Nativo.MonitorFromWindow(h, Nativo.MONITOR_DEFAULTTONEAREST);
        var mi = new Nativo.MONITORINFO { cbSize = Marshal.SizeOf<Nativo.MONITORINFO>() };
        if (!Nativo.GetMonitorInfoW(m, ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    /// <summary>Nome do .exe pela lista de processos (Toolhelp): nenhum handle é aberto no processo.</summary>
    private string NomeDoProcesso(uint pid)
    {
        if (pid == 0) return "";
        // o Windows reaproveita PID: o cache só vale para o mesmo PID seguido; PID novo tira lista nova (~1 ms)
        if (pid == _ultimoPid && _nomes.TryGetValue(pid, out var n)) return n;
        _ultimoPid = pid;
        _nomes.Clear();
        IntPtr snap = Nativo.CreateToolhelp32Snapshot(Nativo.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return "";
        try
        {
            var e = new Nativo.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Nativo.PROCESSENTRY32W>() };
            for (bool ok = Nativo.Process32FirstW(snap, ref e); ok; ok = Nativo.Process32NextW(snap, ref e))
                _nomes[e.th32ProcessID] = e.szExeFile;
        }
        finally { Nativo.CloseHandle(snap); }
        return _nomes.TryGetValue(pid, out n) ? n : "";
    }

    /// <summary>
    /// Não Incomodar do Windows 11 pela WNF (não documentada): 0 desligado, 1 só prioridade, 2 só alarmes.
    /// Falhou ou veio valor estranho: null, e a ilha não muda de comportamento por causa disso.
    /// </summary>
    private static unsafe bool? LerNaoPerturbe()
    {
        try
        {
            ulong nome = 0x0D83063EA3BF1C75;
            uint valor = 0, tam = 4;
            int st = Nativo.NtQueryWnfStateData(ref nome, IntPtr.Zero, IntPtr.Zero, out _, &valor, ref tam);
            if (st != 0 || tam < 4 || valor > 2) return null;
            return valor != 0;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        Nativo.UnhookWinEvent(_gancho);
        Nativo.UnhookWinEvent(_ganchoMin);
        _espera.Dispose();
        _lento.Dispose();
    }
}
