using System;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth.Advertisement;

namespace Ilha.Extras;

/// <summary>Bateria dos AirPods num anúncio: de 10 em 10% (null = sem leitura, ex.: o fone está no estojo fechado).</summary>
internal sealed record BateriaApple(ushort Modelo, int? Esquerdo, int? Direito, int? Estojo,
    bool CarregandoEsquerdo, bool CarregandoDireito, bool CarregandoEstojo, short Rssi)
{
    /// <summary>A menor das duas peças de ouvido (o que acaba primeiro).</summary>
    public int? MenorFone => Esquerdo is int e && Direito is int d ? Math.Min(e, d) : Esquerdo ?? Direito;
    public bool AlgumaLeitura => Esquerdo != null || Direito != null || Estojo != null;
}

/// <summary>
/// Mensagem "proximity pairing" (tipo 0x07) que AirPods e Beats anunciam por BLE com o fabricante 0x004C. Os
/// dados (sem o id do fabricante) têm 27 bytes: tipo, tamanho restante (25), prefixo, modelo (2 bytes, little
/// endian), estado, bateria das duas peças (um nibble cada), estojo e carregando, tampa, cor e 16 bytes cifrados
/// (que não são necessários). O bit 0x20 do estado diz se quem anuncia é o esquerdo: aí o nibble baixo é o
/// esquerdo; senão o baixo é o direito. Mesma leitura do AirPodsDesktop e do OpenPods.
/// </summary>
internal static class AnuncioApple
{
    public const ushort Fabricante = 0x004C;
    public const byte TipoProximityPairing = 0x07;

    public static BateriaApple? Decodificar(byte[] d, short rssi = 0)
    {
        if (d.Length != 27 || d[0] != TipoProximityPairing || d[1] != 25) return null;
        ushort modelo = (ushort)(d[3] | d[4] << 8);
        bool daEsquerda = (d[5] & 0x20) != 0;
        int atual = d[6] & 0x0F, outro = d[6] >> 4;
        int estojo = d[7] & 0x0F;
        bool cAtual = (d[7] & 0x10) != 0, cOutro = (d[7] & 0x20) != 0, cEstojo = (d[7] & 0x40) != 0;
        static int? Pct(int n) => n <= 10 ? n * 10 : null;
        return daEsquerda
            ? new BateriaApple(modelo, Pct(atual), Pct(outro), Pct(estojo), cAtual, cOutro, cEstojo, rssi)
            : new BateriaApple(modelo, Pct(outro), Pct(atual), Pct(estojo), cOutro, cAtual, cEstojo, rssi);
    }
}

/// <summary>
/// Varredura BLE passiva em rajadas curtas (nada de varredura contínua: dividir o rádio com o A2DP pode engasgar o
/// áudio), com os parâmetros de coexistência. Fica com o anúncio mais forte do modelo esperado durante a rajada.
/// O endereço do anúncio é aleatório, então não dá para casar pelo MAC: casa pelo modelo e pelo sinal.
/// </summary>
internal sealed class VarreduraApple : IDisposable
{
    private BluetoothLEAdvertisementWatcher? _vigia;
    private readonly System.Windows.Forms.Timer _fim = new();
    private ushort? _modelo;
    private short _rssiMinimo;
    private BateriaApple? _melhor;
    private readonly Action<BateriaApple> _achou;

    public bool Varrendo => _vigia != null;

    /// <param name="achou">chamado na thread da interface a cada leitura melhor que a anterior da mesma rajada</param>
    public VarreduraApple(Action<BateriaApple> achou)
    {
        _achou = achou;
        _fim.Tick += (_, _) => Parar();
    }

    /// <summary>Uma rajada de `segundos`. `modelo` null aceita qualquer AirPods ou Beats conhecido.</summary>
    public void Rajada(int segundos, ushort? modelo, short rssiMinimo)
    {
        _modelo = modelo;
        _rssiMinimo = rssiMinimo;
        _fim.Stop();
        _fim.Interval = Math.Max(1000, segundos * 1000);
        _fim.Start();
        if (_vigia != null) return;
        _melhor = null;
        try
        {
            var v = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
            v.AdvertisementFilter.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData { CompanyId = AnuncioApple.Fabricante });
            if (!ParametrosBle.Coexistencia(v, out string det)) Log.Escrever("fones: parâmetros de coexistência indisponíveis: " + det);
            v.Received += Recebeu;
            v.Stopped += (_, a) =>
            {
                if (a.Error != Windows.Devices.Bluetooth.BluetoothError.Success) Log.Escrever("fones: varredura BLE parou: " + a.Error);
            };
            _vigia = v;
            v.Start();
        }
        catch (Exception e)
        {
            Log.Escrever("fones: varredura BLE não começou: " + e.Message);
            _vigia = null;
        }
    }

    private void Recebeu(BluetoothLEAdvertisementWatcher s, BluetoothLEAdvertisementReceivedEventArgs a)
    {
        foreach (var m in a.Advertisement.ManufacturerData)
        {
            if (m.CompanyId != AnuncioApple.Fabricante || m.Data.Length != 27) continue;
            var b = AnuncioApple.Decodificar(m.Data.ToArray(), a.RawSignalStrengthInDBm);
            if (b == null) continue;
            // o evento chega numa thread do sistema
            Programa.NaInterface(() => Considerar(b));
        }
    }

    private void Considerar(BateriaApple b)
    {
        if (_vigia == null) return;
        if (!Aceita(b, _modelo, _rssiMinimo)) return;
        // o mesmo fone anuncia várias vezes por segundo: só avisa quando muda o número, e prefere o sinal mais forte
        if (_melhor != null && b.Rssi < _melhor.Rssi - 6) return;
        bool mudou = _melhor == null || _melhor with { Rssi = 0 } != b with { Rssi = 0 };
        _melhor = b;
        if (mudou) _achou(b);
    }

    /// <summary>O anúncio serve: do modelo esperado (ou de um modelo conhecido), com sinal forte e alguma bateria.</summary>
    public static bool Aceita(BateriaApple b, ushort? modelo, short rssiMinimo)
    {
        if (!b.AlgumaLeitura) return false;
        if (b.Rssi != 0 && b.Rssi < rssiMinimo) return false;
        return modelo is ushort m ? b.Modelo == m : ModelosApple.Conhecido(b.Modelo);
    }

    public void Parar()
    {
        _fim.Stop();
        var v = _vigia;
        _vigia = null;
        if (v == null) return;
        v.Received -= Recebeu;
        try { v.Stop(); } catch { }
    }

    public void Dispose()
    {
        Parar();
        _fim.Dispose();
    }
}
