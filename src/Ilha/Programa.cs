using System;
using System.Threading;
using System.Windows.Forms;

namespace Ilha;

public static class Programa
{
    private static SynchronizationContext? _ui;

    /// <summary>Roda na thread da interface (callbacks do sistema chegam em outras threads).</summary>
    public static void NaInterface(Action a)
    {
        if (_ui == null) { a(); return; }
        _ui.Post(_ => { try { a(); } catch (Exception e) { Log.Escrever("erro: " + e); } }, null);
    }

    [STAThread]
    public static void Main(string[] args)
    {
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

        bool sair = Array.IndexOf(args, "--sair") >= 0;
        using var instancia = new Mutex(true, @"Local\Ilha.Instancia", out bool primeira);
        if (!primeira)
        {
            // já tem uma ilha aberta: --sair pede para ela fechar; sem argumento, não abre outra
            if (sair) { try { using var ev = EventWaitHandle.OpenExisting(@"Local\Ilha.Sair"); ev.Set(); } catch { } }
            return;
        }
        if (sair) return;

        using var pedidoDeSair = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Ilha.Sair");
        var nucleo = new Nucleo();
        ThreadPool.RegisterWaitForSingleObject(pedidoDeSair, (_, _) => NaInterface(nucleo.Sair), null, -1, true);
        if (Array.IndexOf(args, "--medir") >= 0) Medidor.Comecar(nucleo);
        else if (Array.IndexOf(args, "--roteiro") >= 0) nucleo.Demonstracao.Roteiro();
        Application.Run();
        nucleo.Dispose();
    }
}
