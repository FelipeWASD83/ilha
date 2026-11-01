using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Ilha.Privacidade;

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

/// <summary>
/// Quem está usando a câmera e o microfone, pelo ConsentStore. Relê quando o registro avisa (com uma espera curta
/// para juntar a rajada de escritas do Start e do Stop) e entrega o estado novo só quando ele muda. Enquanto algo
/// está em uso, confere a cada 30 s se o .exe ainda existe (o registro não avisa quando um app morre sem gravar o
/// fim); sem nada em uso, nenhum relógio roda.
/// </summary>
internal sealed class DetectorDePrivacidade : IDisposable
{
    private const int ESPERA_MS = 150, CONFERENCIA_MS = 30_000;
    private static readonly string[] Capacidades = { Consentimento.Camera, Consentimento.Microfone };
    private static readonly ConcurrentDictionary<string, string> _nomesDePacote = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _trava = new();
    private readonly List<VigiaDeChave> _vigias = new();
    private readonly Timer _atraso, _conferencia;
    private readonly Action<EstadoPrivacidade> _mudou;
    private readonly string? _proprio = Environment.ProcessPath;
    private EstadoPrivacidade _ultimo = EstadoPrivacidade.Vazio;
    private IReadOnlyCollection<string> _ignorar;
    private bool _vigiandoRaiz, _parado;

    public DetectorDePrivacidade(IReadOnlyCollection<string> ignorar, Action<EstadoPrivacidade> mudou)
    {
        _ignorar = ignorar;
        _mudou = mudou;
        _atraso = new Timer(_ => Ler(), null, Timeout.Infinite, Timeout.Infinite);
        _conferencia = new Timer(_ => Ler(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Iniciar() => _atraso.Change(0, Timeout.Infinite);

    public void Ignorar(IReadOnlyCollection<string> ignorar)
    {
        _ignorar = ignorar;
        Reler();
    }

    private void Reler()
    {
        try { _atraso.Change(ESPERA_MS, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void Ler()
    {
        EstadoPrivacidade est;
        lock (_trava)
        {
            if (_parado) return;
            try
            {
                RefazerVigiasSePreciso();
                est = Estado(_ignorar, _proprio);
            }
            catch (Exception e)
            {
                Log.Escrever($"privacidade: leitura falhou: {e.Message}");
                return;
            }
            _conferencia.Change(est.Algum ? CONFERENCIA_MS : Timeout.Infinite, Timeout.Infinite);
            if (est.Equals(_ultimo)) return;
            _ultimo = est;
        }
        _mudou(est);
    }

    /// <summary>Uma chave vigiada por capacidade; se alguma ainda não existe (nunca usada), vigia a raiz do ConsentStore até ela nascer.</summary>
    private void RefazerVigiasSePreciso()
    {
        bool faltaCapacidade = Capacidades.Any(c => !Existe(Consentimento.Raiz + "\\" + c));
        bool refazer = _vigias.Count == 0 || _vigias.Any(v => !v.Valida) || (_vigiandoRaiz && !faltaCapacidade);
        if (!refazer) return;
        foreach (var v in _vigias) v.Dispose();
        _vigias.Clear();
        _vigiandoRaiz = false;
        foreach (var c in Capacidades)
            if (VigiaDeChave.Abrir(Consentimento.Raiz + "\\" + c, Reler) is { } v) _vigias.Add(v);
        if (_vigias.Count < Capacidades.Length && VigiaDeChave.Abrir(Consentimento.Raiz, Reler) is { } r)
        {
            _vigias.Add(r);
            _vigiandoRaiz = true;
        }
        if (_vigias.Count == 0) Log.Escrever("privacidade: nenhuma chave do ConsentStore para vigiar");
    }

    private static bool Existe(string caminho)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(caminho, false); return k != null; }
        catch { return false; }
    }

    /// <summary>Lê o ConsentStore agora e decide (só leitura; também usado pela sonda do autoteste).</summary>
    public static EstadoPrivacidade Estado(IReadOnlyCollection<string> ignorar, string? proprio)
    {
        var entradas = LerEntradas();
        HashSet<string>? processos = null;
        // a lista de processos só é tirada se algum .exe não empacotado aparece como em uso
        bool Rodando(string exe)
        {
            processos ??= Processos();
            return processos.Contains(exe);
        }
        return Consentimento.Decidir(entradas, proprio, Rodando, ignorar, NomeDoPacote);
    }

    public static List<EntradaConsent> LerEntradas()
    {
        var l = new List<EntradaConsent>();
        foreach (var cap in Capacidades)
        {
            using var k = Registry.CurrentUser.OpenSubKey(Consentimento.Raiz + "\\" + cap, false);
            if (k == null) continue;
            foreach (var sub in k.GetSubKeyNames())
            {
                if (string.Equals(sub, Consentimento.NaoEmpacotados, StringComparison.OrdinalIgnoreCase))
                {
                    using var np = k.OpenSubKey(sub, false);
                    if (np == null) continue;
                    foreach (var exe in np.GetSubKeyNames()) Ler(np, cap, exe, true, l);
                }
                else Ler(k, cap, sub, false, l);
            }
        }
        return l;
    }

    private static void Ler(RegistryKey pai, string cap, string sub, bool naoEmpacotado, List<EntradaConsent> l)
    {
        try
        {
            using var s = pai.OpenSubKey(sub, false);
            if (s == null) return;
            l.Add(new EntradaConsent(cap, sub, naoEmpacotado,
                Consentimento.Qword(s.GetValue("LastUsedTimeStart")), Consentimento.Qword(s.GetValue("LastUsedTimeStop"))));
        }
        catch (Exception e) { Log.Escrever($"privacidade: subchave ilegível ({cap}): {e.Message}"); }
    }

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

    public void Dispose()
    {
        lock (_trava)
        {
            if (_parado) return;
            _parado = true;
            foreach (var v in _vigias) v.Dispose();
            _vigias.Clear();
        }
        _atraso.Dispose();
        _conferencia.Dispose();
    }
}
