using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ilha.Privacidade;

/// <summary>
/// Quem está usando a câmera e o microfone: os processos vêm do <see cref="UsoAoVivo"/> (WASAPI e o monitor de
/// câmera do Media Foundation, avisados pelo sistema), os nomes da lista de processos (Toolhelp: nenhum handle
/// aberto em processo nenhum, por causa do anti-cheat). Entrega o estado novo só quando ele muda. Enquanto algo está
/// em uso, confere de novo a cada 30 s (um processo que morreu sem fechar a sessão some da lista de processos); sem
/// nada em uso, nenhum relógio roda.
/// </summary>
internal sealed class DetectorDePrivacidade : IDisposable
{
    private const int CONFERENCIA_MS = 30_000;

    private readonly object _trava = new();
    private readonly UsoAoVivo _uso;
    private readonly Timer _conferencia;
    private readonly Action<EstadoPrivacidade> _mudou;
    private IReadOnlyCollection<string> _ignorar;
    private uint[] _mic = Array.Empty<uint>(), _camera = Array.Empty<uint>();
    // a ordem em que cada processo foi visto em uso (o primeiro a começar aparece primeiro)
    private readonly Dictionary<uint, long> _desde = new();
    private long _ordem;
    private EstadoPrivacidade _ultimo = EstadoPrivacidade.Vazio;
    private bool _parado;

    public DetectorDePrivacidade(IReadOnlyCollection<string> ignorar, Action<EstadoPrivacidade> mudou)
    {
        _ignorar = ignorar;
        _mudou = mudou;
        _uso = new UsoAoVivo(Chegou);
        _conferencia = new Timer(_ => _uso.Refresh(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Iniciar() => _uso.Start();

    public void Ignorar(IReadOnlyCollection<string> ignorar)
    {
        _ignorar = ignorar;
        Decidir();
    }

    private void Chegou(IReadOnlyCollection<uint> mic, IReadOnlyCollection<uint> camera)
    {
        lock (_trava)
        {
            _mic = mic.ToArray();
            _camera = camera.ToArray();
        }
        Decidir();
    }

    private void Decidir()
    {
        EstadoPrivacidade est;
        lock (_trava)
        {
            if (_parado) return;
            var emUso = _mic.Concat(_camera).ToHashSet();
            foreach (var pid in _desde.Keys.Where(p => !emUso.Contains(p)).ToList()) _desde.Remove(pid);
            foreach (var pid in emUso) if (!_desde.ContainsKey(pid)) _desde[pid] = ++_ordem;
            Dictionary<uint, string>? nomes = null;
            string? Exe(uint pid) => (nomes ??= Processos()).TryGetValue(pid, out var n) ? n : null;
            try
            {
                est = Consentimento.Decidir(_camera.Select(p => (p, _desde[p])), _mic.Select(p => (p, _desde[p])), Exe, _ignorar);
            }
            catch (Exception e)
            {
                Log.Escrever($"privacidade: decisão falhou: {e.Message}");
                return;
            }
            try { _conferencia.Change(emUso.Count > 0 ? CONFERENCIA_MS : Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
            if (est.Equals(_ultimo)) return;
            _ultimo = est;
        }
        Log.Escrever($"privacidade: câmera [{est.AppsDaCamera}] microfone [{est.AppsDoMicrofone}]");
        _mudou(est);
    }

    /// <summary>O .exe de cada processo rodando, pela lista Toolhelp (nenhum handle aberto em processo nenhum).</summary>
    public static Dictionary<uint, string> Processos()
    {
        var d = new Dictionary<uint, string>();
        IntPtr snap = Nativo.CreateToolhelp32Snapshot(Nativo.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return d;
        try
        {
            var e = new Nativo.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Nativo.PROCESSENTRY32W>() };
            for (bool ok = Nativo.Process32FirstW(snap, ref e); ok; ok = Nativo.Process32NextW(snap, ref e))
                d[e.th32ProcessID] = e.szExeFile;
        }
        finally { Nativo.CloseHandle(snap); }
        return d;
    }

    public void Dispose()
    {
        lock (_trava)
        {
            if (_parado) return;
            _parado = true;
        }
        _uso.Dispose();
        _conferencia.Dispose();
    }
}
