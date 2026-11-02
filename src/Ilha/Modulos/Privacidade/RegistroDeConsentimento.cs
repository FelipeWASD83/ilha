using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Ilha.Privacidade;

// The ConsentStore side that the screen capture detector (Gravando) still reads. Camera and microphone moved to the
// Windows APIs on 02/11/2026 (UsoAoVivo); these pieces stayed as they were, moved here from Detector.cs and
// Consentimento.cs.

/// <summary>Uma subchave do ConsentStore: um app (pacote ou .exe) e quando ele começou e parou de usar a capacidade.</summary>
internal readonly record struct EntradaConsent(string Capacidade, string Subchave, bool NaoEmpacotado, long Inicio, long Fim);

internal static partial class Consentimento
{
    public const string Raiz = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    public const string Camera = "webcam", Microfone = "microphone", NaoEmpacotados = "NonPackaged";

    public static bool EmUso(long inicio, long fim) => inicio != 0 && fim == 0;

    /// <summary>Valor do registro (REG_QWORD vem como long; aceita também DWORD e binário de 8 bytes). Faltando: 0.</summary>
    public static long Qword(object? v) => v switch
    {
        long l => l,
        int i => (uint)i,
        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
        _ => 0,
    };

    /// <summary>"C:#Program Files#App#app.exe" -> "C:\Program Files\App\app.exe".</summary>
    public static string CaminhoDaSubchave(string subchave) => subchave.Replace('#', '\\');

    /// <summary>Nome do pacote tirado da família, quando o Windows não deu o nome de exibição: "Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "WindowsCamera".</summary>
    public static string NomeAmigavelPacote(string familia)
    {
        int sub = familia.LastIndexOf('_');
        string nome = sub > 0 ? familia[..sub] : familia;
        int ponto = nome.LastIndexOf('.');
        if (ponto >= 0 && ponto < nome.Length - 1) nome = nome[(ponto + 1)..];
        return nome.Length > 0 ? nome : familia;
    }

    /// <summary>A entrada é a da própria ilha (o .exe que está rodando agora)?</summary>
    public static bool EhProprio(EntradaConsent e, string? caminhoProprio) =>
        e.NaoEmpacotado && !string.IsNullOrEmpty(caminhoProprio)
        && string.Equals(CaminhoDaSubchave(e.Subchave), caminhoProprio, StringComparison.OrdinalIgnoreCase);

    /// <summary>A config manda ignorar este app? Casa com o nome do .exe (com ou sem .exe), o nome amigável ou a família do pacote.</summary>
    public static bool Ignorado(EntradaConsent e, string nomeAmigavel, IReadOnlyCollection<string> ignorar)
    {
        if (ignorar.Count == 0) return false;
        foreach (var i in ignorar)
        {
            var x = i.Trim();
            if (x.Length == 0) continue;
            if (string.Equals(x, nomeAmigavel, StringComparison.OrdinalIgnoreCase)) return true;
            if (e.NaoEmpacotado)
            {
                string exe = NomeDoExe(e.Subchave);
                if (string.Equals(x, exe, StringComparison.OrdinalIgnoreCase)) return true;
                if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && string.Equals(x, exe[..^4], StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(x, e.Subchave, StringComparison.OrdinalIgnoreCase)
                     || e.Subchave.StartsWith(x + "_", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

/// <summary>
/// Avisa quando uma chave do HKCU (ou qualquer coisa abaixo dela) muda. RegNotifyChangeKeyValue é de um tiro só:
/// rearma a cada sinal. Com REG_NOTIFY_THREAD_AGNOSTIC a notificação não depende da thread que chamou continuar
/// viva, então pode ser armada e rearmada de thread do pool; o evento é esperado por RegisterWaitForSingleObject
/// (nenhuma thread parada esperando, nenhum polling).
/// </summary>
internal sealed class VigiaDeChave : IDisposable
{
    private const uint REG_NOTIFY_CHANGE_NAME = 0x1, REG_NOTIFY_CHANGE_LAST_SET = 0x4, REG_NOTIFY_THREAD_AGNOSTIC = 0x10000000;

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, [MarshalAs(UnmanagedType.Bool)] bool bWatchSubtree,
        uint dwNotifyFilter, SafeWaitHandle hEvent, [MarshalAs(UnmanagedType.Bool)] bool fAsynchronous);

    private readonly RegistryKey _chave;
    private readonly AutoResetEvent _evento = new(false);
    private readonly RegisteredWaitHandle _espera;
    private readonly Action _mudou;
    private readonly object _trava = new();
    private bool _parado;

    public string Caminho { get; }
    /// <summary>false quando o rearme falhou (chave apagada): quem usa refaz o vigia.</summary>
    public bool Valida { get; private set; }
    public int UltimoErro { get; private set; }

    private VigiaDeChave(string caminho, RegistryKey chave, Action mudou)
    {
        Caminho = caminho;
        _chave = chave;
        _mudou = mudou;
        _espera = ThreadPool.RegisterWaitForSingleObject(_evento, (_, _) => Sinal(), null, Timeout.Infinite, false);
    }

    /// <summary>Abre a chave (só leitura, que inclui KEY_NOTIFY) e arma. null se a chave não existe ou não deu para armar.</summary>
    public static VigiaDeChave? Abrir(string caminho, Action mudou)
    {
        RegistryKey? k;
        try { k = Registry.CurrentUser.OpenSubKey(caminho, false); }
        catch (Exception e) { Log.Escrever($"privacidade: não abriu {caminho}: {e.Message}"); return null; }
        if (k == null) return null;
        var v = new VigiaDeChave(caminho, k, mudou);
        bool ok;
        lock (v._trava) ok = v.Armar();
        if (ok) return v;
        Log.Escrever($"privacidade: RegNotifyChangeKeyValue falhou em {caminho} (erro {v.UltimoErro})");
        v.Dispose();
        return null;
    }

    private bool Armar()
    {
        UltimoErro = RegNotifyChangeKeyValue(_chave.Handle, true,
            REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_THREAD_AGNOSTIC, _evento.SafeWaitHandle, true);
        Valida = UltimoErro == 0;
        return Valida;
    }

    private void Sinal()
    {
        lock (_trava)
        {
            if (_parado) return;
            // rearma antes de reler: o que mudar durante a leitura gera outro sinal
            Armar();
        }
        _mudou();
    }

    public void Dispose()
    {
        lock (_trava)
        {
            if (_parado) return;
            _parado = true;
        }
        using (var feito = new ManualResetEvent(false))
            if (_espera.Unregister(feito)) feito.WaitOne(1000);
        _chave.Dispose();
        _evento.Dispose();
    }
}

/// <summary>Process names and package names for the ConsentStore readers.</summary>
internal static class ProcessosDoConsent
{
    private static readonly ConcurrentDictionary<string, string> _nomesDePacote = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Nomes dos .exe rodando, pela lista Toolhelp (nenhum handle aberto em processo nenhum).</summary>
    public static HashSet<string> Processos()
    {
        var h = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IntPtr snap = Nativo.CreateToolhelp32Snapshot(Nativo.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return h;
        try
        {
            var e = new Nativo.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Nativo.PROCESSENTRY32W>() };
            for (bool ok = Nativo.Process32FirstW(snap, ref e); ok; ok = Nativo.Process32NextW(snap, ref e))
                h.Add(e.szExeFile);
        }
        finally { Nativo.CloseHandle(snap); }
        return h;
    }

    /// <summary>Nome de exibição do pacote ("Câmera do Windows", "WhatsApp") pelo PackageManager, guardado; sem ele, o nome tirado da família.</summary>
    public static string NomeDoPacote(string familia) => _nomesDePacote.GetOrAdd(familia, f =>
    {
        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            foreach (var p in pm.FindPackagesForUser("", f))
            {
                string n = p.DisplayName;
                if (!string.IsNullOrWhiteSpace(n) && !n.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return n.Trim();
            }
        }
        catch (Exception e) { Log.Escrever($"privacidade: nome do pacote {f}: {e.Message}"); }
        return Consentimento.NomeAmigavelPacote(f);
    });
}
