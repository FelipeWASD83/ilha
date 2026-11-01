using System;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ilha.IA;

/// <summary>
/// O texto selecionado no app da frente, para o tradutor. Primeiro por UI Automation (TextPattern da janela com
/// foco, sem mexer na área de transferência), num thread próprio e com timeout curto do IUIAutomation2 (um app
/// travado não segura a ilha). Se não der (app sem TextPattern: muitos Electron, Qt, terminais), Ctrl+C por
/// SendInput depois de esperar soltar as teclas do atalho, esperando o número de sequência da área de
/// transferência mudar, e devolvendo o que estava lá antes. Quem chama garante que não há jogo competitivo na frente.
/// </summary>
internal static class Selecao
{
    public const int MaxCaracteres = 20000;

    // ── UI Automation (COM, sem pacote: vtable lida do UIAutomationClient.h do SDK 10.0.26100) ──
    private static readonly Guid CLSID_CUIAutomation8 = new("e22ad333-b25f-460c-83d0-0581107395c9");
    private static readonly Guid IID_IUIAutomation2 = new("34723aff-0c9d-49d0-9896-7ab52df8cd8a");
    private static readonly Guid IID_IUIAutomationTextPattern = new("32eba289-3583-42c9-9c59-3b6d9a1e9b6a");
    private const int UIA_TextPatternId = 10014;
    // posições na vtable (0, 1 e 2 são do IUnknown)
    private const int IUIAutomation_GetFocusedElement = 8;
    private const int IUIAutomation2_get_ConnectionTimeout = 60, IUIAutomation2_put_ConnectionTimeout = 61;
    private const int IUIAutomation2_get_TransactionTimeout = 62, IUIAutomation2_put_TransactionTimeout = 63;
    private const int IUIAutomationElement_GetCurrentPatternAs = 14;
    private const int IUIAutomationTextPattern_GetSelection = 5;
    private const int IUIAutomationTextRangeArray_get_Length = 3, IUIAutomationTextRangeArray_GetElement = 4;
    private const int IUIAutomationTextRange_GetText = 12;

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reservado, uint modo);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid, IntPtr fora, uint ctx, in Guid iid, out IntPtr ppv);
    private const uint COINIT_MULTITHREADED = 0x0, CLSCTX_INPROC_SERVER = 0x1;

    private static unsafe IntPtr* Vt(IntPtr p) => *(IntPtr**)p;

    private static void Soltar(IntPtr p) { if (p != IntPtr.Zero) Marshal.Release(p); }

    /// <summary>Cria o IUIAutomation2 com os timeouts; o chamador solta.</summary>
    private static unsafe IntPtr NovoUia(uint timeoutMs)
    {
        if (CoCreateInstance(CLSID_CUIAutomation8, IntPtr.Zero, CLSCTX_INPROC_SERVER, IID_IUIAutomation2, out var uia) != 0) return IntPtr.Zero;
        var vt = Vt(uia);
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)vt[IUIAutomation2_put_ConnectionTimeout])(uia, timeoutMs);
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)vt[IUIAutomation2_put_TransactionTimeout])(uia, timeoutMs);
        return uia;
    }

    private static unsafe string? LerPorUia(uint timeoutMs)
    {
        IntPtr uia = IntPtr.Zero, el = IntPtr.Zero, pad = IntPtr.Zero, lista = IntPtr.Zero;
        try
        {
            uia = NovoUia(timeoutMs);
            if (uia == IntPtr.Zero) return null;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vt(uia)[IUIAutomation_GetFocusedElement])(uia, &el) != 0 || el == IntPtr.Zero) return null;
            Guid iid = IID_IUIAutomationTextPattern;
            if (((delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, IntPtr*, int>)Vt(el)[IUIAutomationElement_GetCurrentPatternAs])(el, UIA_TextPatternId, &iid, &pad) != 0 || pad == IntPtr.Zero) return null;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vt(pad)[IUIAutomationTextPattern_GetSelection])(pad, &lista) != 0 || lista == IntPtr.Zero) return null;
            int n = 0;
            if (((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vt(lista)[IUIAutomationTextRangeArray_get_Length])(lista, &n) != 0 || n <= 0) return null;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n && sb.Length < MaxCaracteres; i++)
            {
                IntPtr faixa = IntPtr.Zero, bstr = IntPtr.Zero;
                try
                {
                    if (((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Vt(lista)[IUIAutomationTextRangeArray_GetElement])(lista, i, &faixa) != 0 || faixa == IntPtr.Zero) continue;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Vt(faixa)[IUIAutomationTextRange_GetText])(faixa, MaxCaracteres, &bstr) != 0 || bstr == IntPtr.Zero) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(Marshal.PtrToStringBSTR(bstr));
                }
                finally
                {
                    if (bstr != IntPtr.Zero) Marshal.FreeBSTR(bstr);
                    Soltar(faixa);
                }
            }
            string t = sb.ToString();
            return t.Trim().Length == 0 ? null : t;
        }
        catch { return null; }
        finally { Soltar(lista); Soltar(pad); Soltar(el); Soltar(uia); }
    }

    /// <summary>UIA num thread MTA próprio; passou do prazo, desiste e deixa o thread terminar sozinho.</summary>
    public static Task<string?> PorUiAutomation(int prazoMs = 600)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
            try { tcs.TrySetResult(LerPorUia((uint)Math.Max(100, prazoMs - 150))); }
            catch { tcs.TrySetResult(null); }
            finally { if (hr >= 0) CoUninitialize(); }
        }) { IsBackground = true, Name = "ilha-uia" };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        return Task.WhenAny(tcs.Task, Task.Delay(prazoMs)).ContinueWith(x => x.Result == tcs.Task ? tcs.Task.Result : null, TaskScheduler.Default);
    }

    /// <summary>Sonda do autoteste: cria o IUIAutomation2, grava e lê de volta os dois timeouts (confere a vtable).</summary>
    public static unsafe string SondaUia()
    {
        string r = "";
        var th = new Thread(() =>
        {
            int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
            IntPtr uia = IntPtr.Zero;
            try
            {
                uia = NovoUia(321);
                if (uia == IntPtr.Zero) { r = "sem CUIAutomation8"; return; }
                uint con = 0, tra = 0;
                ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Vt(uia)[IUIAutomation2_get_ConnectionTimeout])(uia, &con);
                ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Vt(uia)[IUIAutomation2_get_TransactionTimeout])(uia, &tra);
                r = con == 321 && tra == 321 ? "ok" : $"timeouts lidos {con}/{tra}";
            }
            catch (Exception e) { r = "exceção " + e.GetType().Name; }
            finally { Soltar(uia); if (hr >= 0) CoUninitialize(); }
        }) { IsBackground = true };
        th.SetApartmentState(ApartmentState.MTA);
        th.Start();
        return th.Join(3000) ? r : "travou";
    }

    // ── Ctrl+C ──

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] entradas, int tamanho);

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    // o union do INPUT tem o tamanho do MOUSEINPUT (32 bytes no x64): total 40
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2;
    private const ushort VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_C = 0x43;

    public static uint Sequencia() => GetClipboardSequenceNumber();

    // Num terminal, Ctrl+C sem seleção interrompe o programa que está rodando: lá só vale a UI Automation
    private static readonly string[] ClassesDeTerminal =
    {
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "mintty", "PuTTY", "KiTTY", "VirtualConsoleClass",
        "org.wezfurlong.wezterm", "Alacritty", "TMobaXtermForm",
    };

    public static bool EhTerminal(IntPtr janela) => Array.IndexOf(ClassesDeTerminal, Nativo.Classe(janela)) >= 0;

    private static bool Apertada(int vk) => (Nativo.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Espera soltar Ctrl, Alt, Shift, Win e a tecla do atalho (senão o Ctrl+C sai como Ctrl+Alt+C).</summary>
    public static async Task<bool> EsperarSoltar(int teclaDoAtalho, int prazoMs = 1500)
    {
        long fim = Environment.TickCount64 + prazoMs;
        while (Environment.TickCount64 < fim)
        {
            if (!Apertada(VK_CONTROL) && !Apertada(VK_MENU) && !Apertada(VK_SHIFT) && !Apertada(VK_LWIN) && !Apertada(VK_RWIN)
                && (teclaDoAtalho == 0 || !Apertada(teclaDoAtalho)))
                return true;
            await Task.Delay(15);
        }
        return false;
    }

    private static INPUT Tecla(ushort vk, bool solta) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = solta ? KEYEVENTF_KEYUP : 0 },
    };

    /// <summary>O que estava na área de transferência, para devolver depois do Ctrl+C (texto, imagem ou arquivos).</summary>
    private sealed record Guardado(string? Texto, System.Drawing.Image? Imagem, StringCollection? Arquivos);

    private static T? Tentar<T>(Func<T> f)
    {
        for (int i = 0; i < 5; i++)
        {
            try { return f(); }
            catch (ExternalException) { Thread.Sleep(20); }
        }
        return default;
    }

    private static Guardado Guardar() => new(
        Tentar(() => Clipboard.ContainsText() ? Clipboard.GetText() : null),
        Tentar(() => Clipboard.ContainsImage() ? Clipboard.GetImage() : null),
        Tentar(() => Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList() : null));

    private static void Devolver(Guardado g)
    {
        Tentar(() =>
        {
            var d = new DataObject();
            bool algo = false;
            if (g.Texto != null) { d.SetText(g.Texto); algo = true; }
            if (g.Imagem != null) { d.SetImage(g.Imagem); algo = true; }
            if (g.Arquivos is { Count: > 0 }) { d.SetFileDropList(g.Arquivos); algo = true; }
            if (algo) Clipboard.SetDataObject(d, true, 5, 20);
            else Clipboard.Clear();
            return true;
        });
    }

    /// <summary>
    /// Manda Ctrl+C para a janela da frente e lê o texto copiado; devolve a área de transferência como estava.
    /// Roda na thread da interface (STA, exigida pela área de transferência do Windows Forms). Null se nada foi copiado.
    /// </summary>
    public static async Task<string?> PorCtrlC(int esperaMs = 300)
    {
        var antes = Guardar();
        uint seq = GetClipboardSequenceNumber();
        var teclas = new[] { Tecla(VK_CONTROL, false), Tecla(VK_C, false), Tecla(VK_C, true), Tecla(VK_CONTROL, true) };
        if (SendInput((uint)teclas.Length, teclas, Marshal.SizeOf<INPUT>()) != teclas.Length) return null;
        long fim = Environment.TickCount64 + esperaMs;
        while (GetClipboardSequenceNumber() == seq && Environment.TickCount64 < fim) await Task.Delay(10);
        if (GetClipboardSequenceNumber() == seq) return null;
        // o app às vezes escreve a área de transferência em mais de um passo: um respiro antes de ler
        await Task.Delay(30);
        string? texto = Tentar(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        Devolver(antes);
        if (string.IsNullOrWhiteSpace(texto)) return null;
        return texto.Length > MaxCaracteres ? texto[..MaxCaracteres] : texto;
    }
}
