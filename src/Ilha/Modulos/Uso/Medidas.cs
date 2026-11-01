using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ilha.Uso;

/// <summary>
/// Uma leitura do PC. GPU nula = sem placa NVIDIA (ou a leitura falhou). Rede nula = não lida (a checagem de fundo
/// só olha os limites) ou primeira leitura depois de um tempo sem ler.
/// </summary>
internal readonly record struct Amostra(
    double Cpu, ulong RamUsada, ulong RamTotal,
    double? Gpu, double? GpuTemp, ulong? VramUsada, ulong? VramTotal,
    double? Descida, double? Subida)
{
    public double Ram => RamTotal == 0 ? 0 : 100.0 * RamUsada / RamTotal;
}

/// <summary>Contadores brutos do processador (GetSystemTimes, em unidades de 100 ns).</summary>
internal readonly record struct TemposCpu(ulong Ocioso, ulong Kernel, ulong Usuario);

/// <summary>Bytes recebidos e enviados de uma interface de rede desde que ela subiu.</summary>
internal readonly record struct Octetos(ulong Recebidos, ulong Enviados);

internal static class Contas
{
    /// <summary>
    /// Uso do processador entre duas leituras, de 0 a 100. O tempo de kernel já inclui o ocioso, então o total é
    /// kernel + usuário. Null se nada andou (duas leituras no mesmo instante) ou se o contador voltou.
    /// </summary>
    public static double? Cpu(TemposCpu antes, TemposCpu agora)
    {
        if (agora.Ocioso < antes.Ocioso || agora.Kernel < antes.Kernel || agora.Usuario < antes.Usuario) return null;
        ulong ocioso = agora.Ocioso - antes.Ocioso;
        ulong total = (agora.Kernel - antes.Kernel) + (agora.Usuario - antes.Usuario);
        if (total == 0) return null;
        double uso = 1.0 - (double)ocioso / total;
        return Math.Clamp(uso, 0, 1) * 100.0;
    }

    /// <summary>
    /// Taxa de descida e subida (bytes por segundo) somando as interfaces presentes nas duas leituras. Interface que
    /// apareceu ou sumiu no meio não conta, e contador que voltou (placa reiniciada) também não.
    /// </summary>
    public static (double Descida, double Subida) Rede(IReadOnlyDictionary<ulong, Octetos> antes, IReadOnlyDictionary<ulong, Octetos> agora, double segundos)
    {
        if (segundos <= 0) return (0, 0);
        ulong desceu = 0, subiu = 0;
        foreach (var (luid, o) in agora)
        {
            if (!antes.TryGetValue(luid, out var a)) continue;
            if (o.Recebidos >= a.Recebidos) desceu += o.Recebidos - a.Recebidos;
            if (o.Enviados >= a.Enviados) subiu += o.Enviados - a.Enviados;
        }
        return (desceu / segundos, subiu / segundos);
    }
}

internal static class Formato
{
    private static readonly string[] Unidades = { "KB/s", "MB/s", "GB/s" };

    /// <summary>Número com uma casa abaixo de 10 e inteiro a partir dele ("0,5", "9,8", "150").</summary>
    private static string Num(double v) => Math.Round(v, 1) < 10 ? v.ToString("0.0", Lang.Culture) : Math.Round(v).ToString("0", Lang.Culture);

    /// <summary>Taxa de rede: "0,5 KB/s", "150 KB/s", "1,5 MB/s", "1,2 GB/s" (base 1024, troca de unidade em 1000).</summary>
    public static string Taxa(double bytesPorSegundo)
    {
        double v = Math.Max(0, bytesPorSegundo) / 1024.0;
        int u = 0;
        while (u < Unidades.Length - 1 && Math.Round(v) >= 1000) { v /= 1024.0; u++; }
        if (v < 0.05) return "0 " + Unidades[u];
        return Num(v) + " " + Unidades[u];
    }

    /// <summary>Gigabytes com uma casa ("14,2"), sem a unidade.</summary>
    public static string Gb(ulong bytes) => (bytes / 1073741824.0).ToString("0.0", Lang.Culture);

    /// <summary>Memória: "512 MB" abaixo de 1 GB, "14,2 GB" acima.</summary>
    public static string Bytes(ulong bytes) => bytes < 1073741824UL
        ? Math.Round(bytes / 1048576.0).ToString("0", Lang.Culture) + " MB"
        : Gb(bytes) + " GB";

    /// <summary>Usada sobre total na mesma unidade: "14,2 / 31,6 GB".</summary>
    public static string Par(ulong usada, ulong total) => $"{Gb(usada)} / {Gb(total)} GB";

    public static string Porcento(double v) => Math.Round(Math.Clamp(v, 0, 100)).ToString("0", Lang.Culture) + "%";

    public static string Graus(double v) => Math.Round(v).ToString("0", Lang.Culture) + " °C";
}

/// <summary>Os últimos N valores de uma medida, do mais velho para o mais novo.</summary>
internal sealed class Historico
{
    private readonly float[] _v;
    private int _inicio, _n;

    public Historico(int capacidade) => _v = new float[capacidade];

    public int Capacidade => _v.Length;
    public int Contagem => _n;

    /// <summary>Acrescenta o valor `vezes` vezes (a checagem de 5 s preenche 5 casas de 1 s com o mesmo valor).</summary>
    public void Adicionar(float valor, int vezes = 1)
    {
        for (int k = 0; k < Math.Min(vezes, _v.Length); k++)
        {
            if (_n < _v.Length) { _v[(_inicio + _n) % _v.Length] = valor; _n++; }
            else { _v[_inicio] = valor; _inicio = (_inicio + 1) % _v.Length; }
        }
    }

    public void Limpar() { _inicio = 0; _n = 0; }

    /// <summary>i = 0 é o mais velho guardado.</summary>
    public float this[int i] => _v[(_inicio + i) % _v.Length];

    /// <summary>Os últimos `quantos` (ou menos, se ainda não tem).</summary>
    public float[] Ultimos(int quantos)
    {
        int q = Math.Min(quantos, _n);
        var r = new float[q];
        for (int i = 0; i < q; i++) r[i] = this[_n - q + i];
        return r;
    }

    public float Maximo(int quantos)
    {
        float m = 0;
        foreach (var x in Ultimos(quantos)) m = MathF.Max(m, x);
        return m;
    }
}

internal enum Medida { GpuTemp, Cpu, Gpu, Ram }

/// <summary>
/// Quando a atividade de uso aparece sozinha. Cada medida tem um limite e quanto tempo precisa ficar acima dele
/// para contar (a CPU e a GPU passam de 90% por um instante o tempo todo). Depois de ligada, só desliga quando
/// todas ficam abaixo do limite por `SegundosParaSumir` seguidos (um pico acima recomeça a conta): é a histerese
/// que impede piscar.
/// </summary>
internal sealed class Limites
{
    public sealed record Regra(Medida Medida, double Limite, double Segundos);

    private readonly Dictionary<Medida, Regra> _regras = new();
    private readonly Dictionary<Medida, double> _acimaDesde = new();
    private readonly HashSet<Medida> _estouradas = new();
    private double? _abaixoDesde;
    private double? _ultima;

    public double SegundosParaSumir { get; set; } = 30;
    /// <summary>Intervalo maior que este entre duas avaliações (PC suspenso, ilha pausada) zera as contagens.</summary>
    public double Lacuna { get; set; } = 20;
    public bool Ligada { get; private set; }
    /// <summary>A medida que fez a atividade aparecer (a mais grave das estouradas; fica até ela sumir).</summary>
    public Medida? Motivo { get; private set; }
    public IReadOnlyCollection<Medida> Estouradas => _estouradas;

    public void Definir(IEnumerable<Regra> regras)
    {
        _regras.Clear();
        foreach (var r in regras) if (r.Limite > 0) _regras[r.Medida] = r;
        foreach (var m in new List<Medida>(_estouradas)) if (!_regras.ContainsKey(m)) _estouradas.Remove(m);
        foreach (var m in new List<Medida>(_acimaDesde.Keys)) if (!_regras.ContainsKey(m)) _acimaDesde.Remove(m);
    }

    public double? LimiteDe(Medida m) => _regras.TryGetValue(m, out var r) ? r.Limite : null;

    public void Zerar()
    {
        _acimaDesde.Clear();
        _estouradas.Clear();
        _abaixoDesde = null;
        _ultima = null;
        Ligada = false;
        Motivo = null;
    }

    /// <summary>Avalia uma leitura no instante `agora` (segundos). Devolve true se Ligada mudou.</summary>
    public bool Avaliar(double agora, Func<Medida, double?> valor)
    {
        if (_ultima is double u && agora - u > Lacuna) _acimaDesde.Clear();
        _ultima = agora;
        bool antes = Ligada, algumAcima = false;
        foreach (var r in _regras.Values)
        {
            var v = valor(r.Medida);
            if (v is double x && x > r.Limite)
            {
                algumAcima = true;
                if (!_acimaDesde.TryGetValue(r.Medida, out var desde)) _acimaDesde[r.Medida] = desde = agora;
                if (agora - desde >= r.Segundos) _estouradas.Add(r.Medida);
            }
            else
            {
                _acimaDesde.Remove(r.Medida);
                _estouradas.Remove(r.Medida);
            }
        }
        if (_estouradas.Count > 0)
        {
            Ligada = true;
            _abaixoDesde = null;
            // a ordem do enum é a da gravidade: temperatura primeiro
            Medida mais = Medida.Ram;
            foreach (var m in _estouradas) if (m < mais) mais = m;
            if (Motivo == null || !_estouradas.Contains(Motivo.Value) || mais < Motivo.Value) Motivo = mais;
        }
        else if (Ligada && algumAcima)
        {
            // passou do limite por um instante (sem durar o bastante para estourar): ainda não está "abaixo"
            _abaixoDesde = null;
        }
        else if (Ligada)
        {
            _abaixoDesde ??= agora;
            if (agora - _abaixoDesde.Value >= SegundosParaSumir)
            {
                Ligada = false;
                Motivo = null;
                _abaixoDesde = null;
            }
        }
        return Ligada != antes;
    }
}
