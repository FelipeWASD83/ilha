using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ilha.Privacidade;
using Microsoft.Win32;

namespace Ilha.Gravando;

/// <summary>Um app capturando a tela agora, com o nome que aparece e desde quando (UTC).</summary>
internal readonly record struct CapturaEmUso(string Nome, DateTime InicioUtc);

/// <summary>
/// Lógica pura da captura de tela pelo ConsentStore (graphicsCaptureProgrammatic e graphicsCaptureWithoutBorder,
/// o mesmo formato da câmera e do microfone). É heurística: qualquer uso do Windows.Graphics.Capture acende, então
/// também o compartilhamento de tela (Discord) e o print. O print dura menos de um segundo; por isso o uso só
/// aparece depois de um mínimo (2 s por padrão).
/// </summary>
internal static class Capturas
{
    public const string Programatica = "graphicsCaptureProgrammatic", SemBorda = "graphicsCaptureWithoutBorder";
    public static readonly string[] Capacidades = { Programatica, SemBorda };
    /// <summary>O OBS usa a captura do Windows nas fontes de janela o tempo todo, gravando ou não: ele tem o caminho próprio.</summary>
    public static readonly string[] ExesDoObs = { "obs64.exe", "obs32.exe", "obs.exe" };

    public static bool EhObs(string exe) => ExesDoObs.Any(o => o.Equals(exe, StringComparison.OrdinalIgnoreCase));

    /// <summary>FILETIME do registro em UTC; inválido ou zero: null.</summary>
    public static DateTime? Instante(long filetime)
    {
        if (filetime <= 0) return null;
        try { return DateTime.FromFileTimeUtc(filetime); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>
    /// Quem está capturando há pelo menos `minimo`. `rever` = daqui a quanto tempo um uso que ainda é curto passa
    /// do mínimo (quem chama relê nessa hora; null = nada esperando). O mesmo app nas duas capacidades conta uma vez,
    /// com o início mais antigo.
    /// </summary>
    public static List<CapturaEmUso> Decidir(IEnumerable<EntradaConsent> entradas, DateTime agoraUtc, TimeSpan minimo,
        string? proprio, Func<string, bool>? rodando, IReadOnlyCollection<string> ignorar, Func<string, string> nomeDoPacote,
        out TimeSpan? rever)
    {
        rever = null;
        var porNome = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entradas)
        {
            if (!Consentimento.EmUso(e.Inicio, e.Fim)) continue;
            if (Instante(e.Inicio) is not DateTime inicio) continue;
            if (Consentimento.EhProprio(e, proprio)) continue;
            if (e.NaoEmpacotado)
            {
                string exe = Consentimento.NomeDoExe(e.Subchave);
                if (EhObs(exe)) continue;
                if (rodando != null && !rodando(exe)) continue;
            }
            string nome = e.NaoEmpacotado ? Consentimento.NomeAmigavelExe(e.Subchave) : nomeDoPacote(e.Subchave);
            if (Consentimento.Ignorado(e, nome, ignorar)) continue;
            if (inicio > agoraUtc) inicio = agoraUtc;
            if (!porNome.TryGetValue(nome, out var antes) || inicio < antes) porNome[nome] = inicio;
        }
        var l = new List<CapturaEmUso>();
        foreach (var (nome, inicio) in porNome)
        {
            var falta = inicio + minimo - agoraUtc;
            if (falta > TimeSpan.Zero) { if (rever == null || falta < rever) rever = falta; continue; }
            l.Add(new CapturaEmUso(nome, inicio));
        }
        return l.OrderBy(c => c.InicioUtc).ThenBy(c => c.Nome, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool Iguais(IReadOnlyList<CapturaEmUso> a, IReadOnlyList<CapturaEmUso> b) => a.SequenceEqual(b);
}

/// <summary>
/// Vigia as duas chaves de captura do ConsentStore por RegNotifyChangeKeyValue (o VigiaDeChave da privacidade, sem
/// polling). Relê com uma espera curta para juntar a rajada de escritas, relê de novo quando um uso curto passa do
/// mínimo e, enquanto algo está em uso, confere a cada 30 s se o .exe ainda existe (app que morreu sem gravar o fim).
/// Sem nada em uso e nada esperando, nenhum relógio roda.
/// </summary>
internal sealed class DetectorDeCaptura : IDisposable
{
    private const int ESPERA_MS = 150, CONFERENCIA_MS = 30_000;

    private readonly object _trava = new();
    private readonly List<VigiaDeChave> _vigias = new();
    private readonly Timer _atraso, _conferencia;
    private readonly Action<List<CapturaEmUso>> _mudou;
    private readonly string? _proprio = Environment.ProcessPath;
    private List<CapturaEmUso> _ultimo = new();
    private IReadOnlyCollection<string> _ignorar;
    private TimeSpan _minimo;
    private bool _vigiandoRaiz, _parado;

    public DetectorDeCaptura(IReadOnlyCollection<string> ignorar, TimeSpan minimo, Action<List<CapturaEmUso>> mudou)
    {
        _ignorar = ignorar;
        _minimo = minimo;
        _mudou = mudou;
        _atraso = new Timer(_ => Ler(), null, Timeout.Infinite, Timeout.Infinite);
        _conferencia = new Timer(_ => Ler(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Iniciar() => Agendar(0);

    public void Ajustar(IReadOnlyCollection<string> ignorar, TimeSpan minimo)
    {
        _ignorar = ignorar;
        _minimo = minimo;
        Agendar(ESPERA_MS);
    }

    private void Agendar(int ms)
    {
        try { _atraso.Change(ms, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void Ler()
    {
        List<CapturaEmUso> agora;
        lock (_trava)
        {
            if (_parado) return;
            TimeSpan? rever;
            try
            {
                RefazerVigiasSePreciso();
                agora = Estado(_ignorar, _minimo, _proprio, out rever);
            }
            catch (Exception e)
            {
                Log.Escrever($"gravando: leitura da captura falhou: {e.Message}");
                return;
            }
            // uso curto esperando o mínimo: relê quando ele passar (o registro não avisa nada nessa hora)
            if (rever is TimeSpan r) Agendar((int)Math.Clamp(r.TotalMilliseconds + 50, 50, int.MaxValue));
            bool emUso = agora.Count > 0 || rever != null;
            try { _conferencia.Change(emUso ? CONFERENCIA_MS : Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
            if (Capturas.Iguais(agora, _ultimo)) return;
            _ultimo = agora;
        }
        _mudou(agora);
    }

    private void RefazerVigiasSePreciso()
    {
        bool falta = Capturas.Capacidades.Any(c => !Existe(Consentimento.Raiz + "\\" + c));
        bool refazer = _vigias.Count == 0 || _vigias.Any(v => !v.Valida) || (_vigiandoRaiz && !falta);
        if (!refazer) return;
        foreach (var v in _vigias) v.Dispose();
        _vigias.Clear();
        _vigiandoRaiz = false;
        foreach (var c in Capturas.Capacidades)
            if (VigiaDeChave.Abrir(Consentimento.Raiz + "\\" + c, () => Agendar(ESPERA_MS)) is { } v) _vigias.Add(v);
        if (_vigias.Count < Capturas.Capacidades.Length && VigiaDeChave.Abrir(Consentimento.Raiz, () => Agendar(ESPERA_MS)) is { } raiz)
        {
            _vigias.Add(raiz);
            _vigiandoRaiz = true;
        }
        if (_vigias.Count == 0) Log.Escrever("gravando: nenhuma chave de captura do ConsentStore para vigiar");
    }

    private static bool Existe(string caminho)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(caminho, false); return k != null; }
        catch { return false; }
    }

    /// <summary>Lê agora e decide (só leitura; também usado pela sonda do autoteste).</summary>
    public static List<CapturaEmUso> Estado(IReadOnlyCollection<string> ignorar, TimeSpan minimo, string? proprio, out TimeSpan? rever)
    {
        var entradas = LerEntradas();
        HashSet<string>? processos = null;
        bool Rodando(string exe) => (processos ??= ProcessosDoConsent.Processos()).Contains(exe);
        return Capturas.Decidir(entradas, DateTime.UtcNow, minimo, proprio, Rodando, ignorar, ProcessosDoConsent.NomeDoPacote, out rever);
    }

    public static List<EntradaConsent> LerEntradas()
    {
        var l = new List<EntradaConsent>();
        foreach (var cap in Capturas.Capacidades)
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
        catch (Exception e) { Log.Escrever($"gravando: subchave ilegível ({cap}): {e.Message}"); }
    }

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
