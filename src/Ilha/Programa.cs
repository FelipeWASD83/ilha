using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Ilha;

public static class Programa
{
    private static SynchronizationContext? _ui;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterApplicationRestart(string? args, int flags);
    private const int RESTART_NO_PATCH = 4, RESTART_NO_REBOOT = 8;

    /// <summary>Roda na thread da interface (callbacks do sistema chegam em outras threads).</summary>
    public static void NaInterface(Action a)
    {
        if (_ui == null) { a(); return; }
        _ui.Post(_ => { try { a(); } catch (Exception e) { Log.Escrever("erro: " + e); } }, null);
    }

    [STAThread]
    public static void Main(string[] args)
    {
        // o executor de uma atualização (troca os arquivos e volta atrás se a versão nova não abrir)
        if (Atualizador.TratarArgumentos(args)) return;
        // o compilar.ps1 usa isto para saber se o Smart App Control deixou o Ilha.dll carregar
        if (Array.IndexOf(args, "--ping") >= 0) { Environment.Exit(0); return; }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Escrever("erro: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Escrever("erro fatal: " + e.ExceptionObject);
        WindowsFormsSynchronizationContext.AutoInstall = true;
        _ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);

        if (Array.IndexOf(args, "--autoteste") >= 0)
        {
            Environment.Exit(Autoteste.Rodar());
            return;
        }
        int iTeste = Array.IndexOf(args, "--testar");
        if (iTeste >= 0)
        {
            // autoteste dos módulos, sem janela: o relatório vai para o console e para ~/.ilha/testes.txt
            var saida = new System.IO.StringWriter();
            var antes = Console.Out;
            Console.SetOut(saida);
            int r = CatalogoDeModulos.Testar(iTeste + 1 < args.Length ? args[iTeste + 1] : null);
            Console.SetOut(antes);
            System.IO.Directory.CreateDirectory(Config.Pasta);
            System.IO.File.WriteAllText(System.IO.Path.Combine(Config.Pasta, "testes.txt"), saida.ToString());
            Console.Write(saida.ToString());
            Environment.Exit(r);
            return;
        }

        int iPonte = Array.IndexOf(args, "--ponte-claude");
        if (iPonte >= 0)
        {
            // the bridge to the Claude app with no window (BridgeProbe): reads, or changes one session's effort
            Environment.Exit(global::Ilha.Claude.BridgeProbe.Run(args.Skip(iPonte + 1).ToArray()));
            return;
        }

        // --sair, and --quit as the same word the other apps of the setup use (Fundo Vivo, Relógio Mond)
        bool sair = Array.IndexOf(args, "--sair") >= 0 || Array.IndexOf(args, "--quit") >= 0;
        using var instancia = new Mutex(true, @"Local\Ilha.Instancia", out bool primeira);
        if (!primeira)
        {
            // já tem uma ilha aberta: --sair pede para ela fechar; sem argumento, não abre outra
            if (sair) { try { using var ev = EventWaitHandle.OpenExisting(@"Local\Ilha.Sair"); ev.Set(); } catch { } }
            return;
        }
        if (sair) return;
        // a crash or a hang (a native one in a Windows DLL included, which no catch stops) brings the island back: Windows
        // reopens it after it ran for at least 60 s, so a crash right at the start does not loop (3.5.0, after the media
        // crash of 03/11/2026)
        try
        {
            int hr = RegisterApplicationRestart("--reiniciada", RESTART_NO_PATCH | RESTART_NO_REBOOT);
            if (hr != 0) Log.Escrever($"reinício automático não registrou (0x{hr:X8})");
        }
        catch (Exception e) { Log.Escrever("reinício automático: " + e.Message); }
        if (Array.IndexOf(args, "--reiniciada") >= 0) Log.Escrever("a ilha fechou sozinha e o Windows abriu de novo");

        using var pedidoDeSair = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Ilha.Sair");
        var nucleo = new Nucleo();
        // a versão nova abriu bem: o atualizador não precisa voltar para a anterior
        NaInterface(Atualizador.AbriuBem);
        ThreadPool.RegisterWaitForSingleObject(pedidoDeSair, (_, _) => NaInterface(nucleo.Sair), null, -1, true);
        if (Array.IndexOf(args, "--medir") >= 0) Medidor.Comecar(nucleo);
        else if (Array.IndexOf(args, "--roteiro") >= 0) nucleo.Demonstracao.Roteiro();
        else if (Array.IndexOf(args, "--teste-mouse") >= 0) TesteDoMouse.Comecar(nucleo);
        if (Array.IndexOf(args, "--menu") >= 0)
        {
            // opens the main menu after startup (for screenshots and tests)
            var tm = new System.Windows.Forms.Timer { Interval = 3500 };
            tm.Tick += (_, _) => { tm.Stop(); nucleo.Ilha.AbrirMenu(); };
            tm.Start();
        }
        int ab = Array.IndexOf(args, "--abrir");
        if (ab >= 0 && ab + 1 < args.Length)
        {
            // opens one menu entry's card after startup (for screenshots and tests), without taking the focus
            string id = args[ab + 1];
            var ta = new System.Windows.Forms.Timer { Interval = 4500 };
            ta.Tick += (_, _) => { ta.Stop(); nucleo.AbrirEntrada(id); };
            ta.Start();
        }
        int d = Array.IndexOf(args, "--demo");
        if (d >= 0 && d + 1 < args.Length)
        {
            // liga uma atividade de mentira pelo tipo (timer, midia, claude, uso, ia, galeria); --expandir abre o cartão.
            // Several separated by commas run together (the pill with its two bubbles, 3.7.0); the first is the main one
            var tipos = args[d + 1].Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var tipo in tipos.Skip(1).Reverse()) nucleo.Demonstracao.Atividades.Find(x => x.Tipo == tipo)?.Comecar();
            var a = nucleo.Demonstracao.Atividades.Find(x => x.Tipo == tipos[0]);
            a?.Comecar();
            if (a != null && Array.IndexOf(args, "--expandir") >= 0)
            {
                var t = new System.Windows.Forms.Timer { Interval = 3500 };
                t.Tick += (_, _) => { t.Stop(); nucleo.Ilha.Atalho(); };
                t.Start();
            }
        }
        Application.Run();
        nucleo.Dispose();
    }
}
