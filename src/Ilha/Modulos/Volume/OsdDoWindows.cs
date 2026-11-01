using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace Ilha.Volume;

/// <summary>
/// Experimental (`esconderOsdDoWindows`): some com o pop-up de volume do Windows 11 para só a ilha aparecer.
/// Não existe jeito oficial. O pop-up é uma ilha XAML do explorer (classe XamlExplorerHostIslandWindow), a mesma
/// classe do Alt+Tab e de outros painéis, então a janela só é aceita se ACABOU de aparecer logo depois de uma
/// mudança de volume que não foi da ilha, é pequena e é do processo do shell. Esconder = estilo layered e
/// transparente ao mouse, posição fora da tela e minimizada (nada é injetado no explorer).
/// Efeito colateral conhecido: o Windows usa a mesma janela para os outros indicadores (brilho, modo avião, área de
/// trabalho virtual), que somem junto enquanto ela estiver escondida.
/// Restaura sempre: ao desligar a opção, ao sair, no ProcessExit, numa exceção fatal, no TaskbarCreated (explorer
/// reiniciado) e, se a ilha morrer sem conseguir, na próxima vez que abrir (marca em ~/.ilha/volume-osd.json).
/// </summary>
internal sealed class OsdDoWindows : IDisposable
{
    private const string CLASSE = "XamlExplorerHostIslandWindow";
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020;
    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_SHOWMINNOACTIVE = 7;
    private const int FORA = -32000;
    private static readonly int TaskbarCreated = (int)RegisterWindowMessageW("TaskbarCreated");

    private static string Marca => Path.Combine(Config.Pasta, "volume-osd.json");

    private IntPtr _janela;
    private int _estiloOriginal;
    private int _xOriginal, _yOriginal;
    private bool _escondida;
    private Dictionary<IntPtr, RetratoJanela> _ocioso = new();
    private readonly Timer _procura = new() { Interval = 40 };
    private readonly Timer _repouso = new() { Interval = 4000 };
    private int _tentativas;
    private Receptor? _receptor;
    private readonly EventHandler _aoSair;
    private readonly UnhandledExceptionEventHandler _aoQuebrar;

    public bool Ligado { get; private set; }

    public OsdDoWindows()
    {
        _procura.Tick += (_, _) => Procurar();
        // ~4 s sem mudança de volume: o pop-up nativo já sumiu, então o que está à vista agora é o "normal"
        _repouso.Tick += (_, _) => { _repouso.Stop(); _ocioso = Retratar(); };
        _aoSair = (_, _) => Restaurar();
        _aoQuebrar = (_, _) => Restaurar();
    }

    /// <summary>Se a ilha morreu com o pop-up escondido, devolve ele agora (vale com a opção ligada ou não).</summary>
    public static void RestaurarDaUltimaVez()
    {
        try
        {
            if (!File.Exists(Marca)) return;
            var m = JsonSerializer.Deserialize<MarcaOsd>(File.ReadAllText(Marca));
            File.Delete(Marca);
            if (m == null) return;
            var h = new IntPtr(m.Janela);
            // o número da janela pode ter sido reaproveitado: só mexe se for do mesmo explorer e ainda tiver as marcas da ilha
            if (!EhIlhaDoShell(h) || m.Shell != PidDoShell()) return;
            int estilo = GetWindowLongW(h, GWL_EXSTYLE);
            GetWindowRect(h, out var r);
            bool marcada = (estilo & (WS_EX_LAYERED | WS_EX_TRANSPARENT)) == (WS_EX_LAYERED | WS_EX_TRANSPARENT) && (IsIconic(h) || r.L <= FORA + 100);
            if (!marcada) return;
            Devolver(h, m.Estilo, m.X, m.Y);
            Log.Escrever("volume: pop-up nativo devolvido (a ilha anterior fechou com ele escondido)");
        }
        catch (Exception e) { Log.Escrever("volume: marca do pop-up: " + e.Message); }
    }

    public void Ligar()
    {
        if (Ligado) return;
        Ligado = true;
        _ocioso = Retratar();
        _receptor = new Receptor(this);
        AppDomain.CurrentDomain.ProcessExit += _aoSair;
        AppDomain.CurrentDomain.UnhandledException += _aoQuebrar;
        Log.Escrever("volume: esconder o pop-up nativo ligado (experimental)");
    }

    public void Desligar()
    {
        if (!Ligado) return;
        Ligado = false;
        _procura.Stop();
        _repouso.Stop();
        Restaurar();
        _janela = IntPtr.Zero;
        _receptor?.DestroyHandle();
        _receptor = null;
        AppDomain.CurrentDomain.ProcessExit -= _aoSair;
        AppDomain.CurrentDomain.UnhandledException -= _aoQuebrar;
    }

    /// <summary>
    /// Uma mudança de volume que não foi da ilha acabou de chegar. `esconder` = a ilha vai aparecer (fora do
    /// competitivo e sem pausa); senão o pop-up nativo volta, para ele não ficar sem nada na tela.
    /// </summary>
    public void MudouDeFora(bool esconder)
    {
        if (!Ligado) return;
        _repouso.Stop();
        _repouso.Start();
        if (!esconder) { Restaurar(); return; }
        if (_janela != IntPtr.Zero && EhIlhaDoShell(_janela))
        {
            // o explorer põe a janela no lugar de novo a cada aparição: reaplicar é barato
            Esconder(_janela);
            return;
        }
        _janela = IntPtr.Zero;
        _escondida = false;
        // o pop-up aparece alguns milissegundos depois da mudança: olha por até ~0,7 s
        _tentativas = 0;
        _procura.Start();
    }

    private void Procurar()
    {
        if (++_tentativas > 18) { _procura.Stop(); return; }
        uint shell = PidDoShell();
        foreach (var r in Retratar().Values)
        {
            var mon = TamanhoDoMonitor(r.Janela);
            _ocioso.TryGetValue(r.Janela, out var antes);
            bool tinha = _ocioso.ContainsKey(r.Janela);
            if (!Logica.CandidataAoOsd(tinha ? antes : null, r, mon.l, mon.a, shell)) continue;
            _procura.Stop();
            _janela = r.Janela;
            _estiloOriginal = GetWindowLongW(r.Janela, GWL_EXSTYLE);
            _xOriginal = r.X;
            _yOriginal = r.Y;
            Esconder(r.Janela);
            Log.Escrever($"volume: pop-up nativo achado ({r.Largura}x{r.Altura} em {r.X},{r.Y}) e escondido");
            return;
        }
    }

    private void Esconder(IntPtr h)
    {
        SetWindowLongW(h, GWL_EXSTYLE, _estiloOriginal | WS_EX_LAYERED | WS_EX_TRANSPARENT);
        SetWindowPos(h, IntPtr.Zero, FORA, FORA, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        ShowWindow(h, SW_SHOWMINNOACTIVE);
        if (!_escondida)
        {
            _escondida = true;
            try
            {
                Directory.CreateDirectory(Config.Pasta);
                File.WriteAllText(Marca, JsonSerializer.Serialize(new MarcaOsd { Janela = h.ToInt64(), Shell = PidDoShell(), Estilo = _estiloOriginal, X = _xOriginal, Y = _yOriginal }));
            }
            catch { }
        }
    }

    /// <summary>Devolve o pop-up como estava (pode ser chamado de qualquer thread, inclusive no ProcessExit).</summary>
    public void Restaurar()
    {
        if (!_escondida) return;
        _escondida = false;
        var h = _janela;
        if (h != IntPtr.Zero && EhIlhaDoShell(h)) Devolver(h, _estiloOriginal, _xOriginal, _yOriginal);
        try { File.Delete(Marca); } catch { }
    }

    private static void Devolver(IntPtr h, int estilo, int x, int y)
    {
        SetWindowLongW(h, GWL_EXSTYLE, estilo);
        SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        // tira do minimizado e deixa escondida, que é como o explorer a deixa entre uma aparição e outra
        if (IsIconic(h)) ShowWindow(h, SW_SHOWNOACTIVATE);
        ShowWindow(h, SW_HIDE);
    }

    private void ShellReiniciou()
    {
        // explorer reiniciado (ou barra de tarefas recriada): devolve se a janela ainda existe e procura de novo depois
        Restaurar();
        _janela = IntPtr.Zero;
        _ocioso = Retratar();
    }

    public void Dispose()
    {
        Desligar();
        _procura.Dispose();
        _repouso.Dispose();
    }

    // ---------------------------------------------------------------- janelas

    /// <summary>As ilhas XAML de primeiro nível do explorer, agora.</summary>
    public static Dictionary<IntPtr, RetratoJanela> Retratar()
    {
        var d = new Dictionary<IntPtr, RetratoJanela>();
        IntPtr h = IntPtr.Zero;
        int limite = 0;
        while ((h = FindWindowExW(IntPtr.Zero, h, CLASSE, null)) != IntPtr.Zero && limite++ < 64)
        {
            GetWindowThreadProcessId(h, out uint pid);
            GetWindowRect(h, out var r);
            DwmGetWindowAttribute(h, 14, out int encoberta, 4);
            d[h] = new RetratoJanela(h, pid, IsWindowVisible(h), encoberta != 0, IsIconic(h), r.L, r.T, r.R - r.L, r.B - r.T);
        }
        return d;
    }

    public static uint PidDoShell()
    {
        var s = GetShellWindow();
        if (s == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(s, out uint pid);
        return pid;
    }

    private static bool EhIlhaDoShell(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindow(h)) return false;
        var b = new StringBuilder(64);
        GetClassNameW(h, b, b.Capacity);
        if (b.ToString() != CLASSE) return false;
        GetWindowThreadProcessId(h, out uint pid);
        return pid != 0 && pid == PidDoShell();
    }

    private static (int l, int a) TamanhoDoMonitor(IntPtr h)
    {
        var m = MonitorFromWindow(h, 2);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (m == IntPtr.Zero || !GetMonitorInfoW(m, ref mi)) return (0, 0);
        return (mi.rcMonitor.R - mi.rcMonitor.L, mi.rcMonitor.B - mi.rcMonitor.T);
    }

    /// <summary>Janela invisível de primeiro nível só para ouvir o TaskbarCreated (janela só de mensagens não recebe broadcast).</summary>
    private sealed class Receptor : NativeWindow
    {
        private readonly OsdDoWindows _dono;

        public Receptor(OsdDoWindows dono)
        {
            _dono = dono;
            CreateHandle(new CreateParams { Caption = "Ilha.Volume.Osd", ExStyle = 0x80 /* WS_EX_TOOLWINDOW */ });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == TaskbarCreated && TaskbarCreated != 0) _dono.ShellReiniciou();
            base.WndProc(ref m);
        }
    }

    private sealed class MarcaOsd
    {
        public long Janela { get; set; }
        public uint Shell { get; set; }
        public int Estilo { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr pai, IntPtr depois, string classe, string? titulo);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder b, int n);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr depois, int x, int y, int l, int a, uint f);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string nome);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int s);
}
