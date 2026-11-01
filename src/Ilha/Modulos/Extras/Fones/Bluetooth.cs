using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Ilha.Extras;

internal enum TipoAparelho { Fone, Controle, Outro }

/// <summary>O que se sabe de um aparelho Bluetooth pareado.</summary>
internal sealed record AparelhoBluetooth(string Id, string Nome, ulong Endereco, TipoAparelho Tipo, ushort? Fabricante, ushort? Produto);

/// <summary>
/// Dados do PnP (cfgmgr32) dos nós BTHENUM do aparelho, achados pelo endereço que vai no id da instância
/// ("BTHENUM\{...}_VID&amp;0001004C_PID&amp;2027\8&amp;...&amp;F8D3F0971F09_C00000000"): fabricante e produto do perfil de
/// identificação e a bateria que o Windows mostra em Configurações (só aparece quando o aparelho a informa).
/// </summary>
internal static class NosPnp
{
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_ID_List_SizeW(out int tamanho, string? filtro, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_ID_ListW(string? filtro, char[] buf, int tamanho, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Locate_DevNodeW(out uint no, string id, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_DevNode_PropertyW(uint no, ref DEVPROPKEY chave, out uint tipo, byte[]? buf, ref int tamanho, int flags);

    [StructLayout(LayoutKind.Sequential)] private struct DEVPROPKEY { public Guid fmtid; public uint pid; }
    private const int CM_GETIDLIST_FILTER_ENUMERATOR = 0x1, CM_GETIDLIST_FILTER_PRESENT = 0x100, CR_SUCCESS = 0;
    private const uint DEVPROP_TYPE_BYTE = 0x3;
    // a bateria que Configurações > Bluetooth mostra
    private static readonly Guid ChaveBateria = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");

    private static readonly Regex VidPid = new(@"_VID&(?<vid>[0-9A-F]{8})_PID&(?<pid>[0-9A-F]{4})", RegexOptions.IgnoreCase);

    public static string EnderecoHex(ulong endereco) => endereco.ToString("X12");

    /// <summary>Fabricante (só os 16 bits de baixo: 0001004C vira 004C) e produto, de um id de instância.</summary>
    public static (ushort Fabricante, ushort Produto)? LerVidPid(string id)
    {
        var m = VidPid.Match(id);
        if (!m.Success) return null;
        uint vid = Convert.ToUInt32(m.Groups["vid"].Value, 16);
        return ((ushort)(vid & 0xFFFF), Convert.ToUInt16(m.Groups["pid"].Value, 16));
    }

    public static List<string> IdsDoEndereco(IEnumerable<string> ids, ulong endereco)
    {
        string hex = EnderecoHex(endereco);
        return ids.Where(i => i.Contains(hex, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static List<string> IdsBthenum()
    {
        var r = new List<string>();
        int flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
        if (CM_Get_Device_ID_List_SizeW(out int tam, "BTHENUM", flags) != CR_SUCCESS || tam <= 1) return r;
        var buf = new char[tam];
        if (CM_Get_Device_ID_ListW("BTHENUM", buf, tam, flags) != CR_SUCCESS) return r;
        foreach (var s in new string(buf).Split('\0')) if (s.Length > 0) r.Add(s);
        return r;
    }

    public static (ushort Fabricante, ushort Produto)? VidPidDoAparelho(ulong endereco)
    {
        try
        {
            foreach (var id in IdsDoEndereco(IdsBthenum(), endereco))
                if (LerVidPid(id) is { } v) return v;
        }
        catch (Exception e) { Log.Escrever("fones: PnP: " + e.Message); }
        return null;
    }

    /// <summary>A bateria (0 a 100) que o Windows guardou em algum nó do aparelho, se guardou.</summary>
    public static int? BateriaDoAparelho(ulong endereco)
    {
        try
        {
            foreach (var id in IdsDoEndereco(IdsBthenum(), endereco))
            {
                if (CM_Locate_DevNodeW(out uint no, id, 0) != CR_SUCCESS) continue;
                var chave = new DEVPROPKEY { fmtid = ChaveBateria, pid = 2 };
                int tam = 1;
                var buf = new byte[1];
                if (CM_Get_DevNode_PropertyW(no, ref chave, out uint tipo, buf, ref tam, 0) == CR_SUCCESS && tipo == DEVPROP_TYPE_BYTE && buf[0] <= 100)
                    return buf[0];
            }
        }
        catch (Exception e) { Log.Escrever("fones: bateria do PnP: " + e.Message); }
        return null;
    }
}

/// <summary>
/// Conectou e desconectou de aparelho Bluetooth, por evento: um DeviceWatcher dos pareados (cada um vira um
/// BluetoothDevice vivo, com ConnectionStatusChanged) e outro do seletor de conectados, que só serve de gatilho
/// para reler o estado (o seletor da Microsoft tem um OR que pode trazer pareados desconectados). Os objetos ficam
/// vivos enquanto o módulo está ligado, senão o evento para de chegar.
/// </summary>
internal sealed class VigiaBluetooth : IDisposable
{
    private readonly Action<AparelhoBluetooth, bool, bool> _mudou;
    private readonly Dictionary<string, BluetoothDevice> _aparelhos = new();
    private readonly Dictionary<string, bool> _estado = new();
    private DeviceWatcher? _pareados, _conectados;
    // aparelhos que vieram no levantamento inicial: o primeiro estado deles não é "acabou de conectar"
    private readonly HashSet<string> _doLevantamento = new();
    private volatile bool _enumerou;
    private bool _parado;

    /// <param name="mudou">aparelho, conectado, e se é do levantamento inicial (a ilha abrindo); na thread da interface</param>
    public VigiaBluetooth(Action<AparelhoBluetooth, bool, bool> mudou) => _mudou = mudou;

    public void Iniciar()
    {
        try
        {
            _pareados = DeviceInformation.CreateWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(true), null, DeviceInformationKind.AssociationEndpoint);
            // os eventos de um vigia chegam em ordem: o que vem antes do EnumerationCompleted é do levantamento
            _pareados.Added += (w, d) => { bool lev = !_enumerou; _ = Acompanhar(d.Id, lev); };
            _pareados.Removed += (_, d) => Programa.NaInterface(() => Esquecer(d.Id));
            _pareados.EnumerationCompleted += (_, _) => _enumerou = true;
            _pareados.Start();

            _conectados = DeviceInformation.CreateWatcher(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected), null, DeviceInformationKind.AssociationEndpoint);
            _conectados.Added += (_, d) => Programa.NaInterface(() => Reler(d.Id));
            _conectados.Updated += (_, d) => Programa.NaInterface(() => Reler(d.Id));
            _conectados.Removed += (_, d) => Programa.NaInterface(() => Reler(d.Id));
            _conectados.Start();
        }
        catch (Exception e) { Log.Escrever("fones: vigia do Bluetooth não começou: " + e.Message); }
    }

    private async Task Acompanhar(string id, bool levantamento)
    {
        try
        {
            var dev = await BluetoothDevice.FromIdAsync(id);
            if (dev == null) return;
            Programa.NaInterface(() =>
            {
                if (_parado) { dev.Dispose(); return; }
                if (_aparelhos.TryGetValue(id, out var velho)) velho.Dispose();
                _aparelhos[id] = dev;
                if (levantamento) _doLevantamento.Add(id);
                dev.ConnectionStatusChanged += (s, _) => Programa.NaInterface(() => Reler(id));
                Reler(id);
            });
        }
        catch (Exception e) { Log.Escrever("fones: aparelho não abriu: " + e.Message); }
    }

    private void Reler(string id)
    {
        if (_parado || !_aparelhos.TryGetValue(id, out var dev)) return;
        bool conectado;
        try { conectado = dev.ConnectionStatus == BluetoothConnectionStatus.Connected; }
        catch { return; }
        bool antes = _estado.TryGetValue(id, out var a) && a;
        if (_estado.ContainsKey(id) && antes == conectado) return;
        bool primeiro = !_estado.ContainsKey(id);
        _estado[id] = conectado;
        bool inicial = primeiro && _doLevantamento.Remove(id);
        // primeira vez que se vê um aparelho desconectado: não é mudança nenhuma
        if (primeiro && !conectado) return;
        _mudou(Descrever(id, dev), conectado, inicial);
    }

    private static AparelhoBluetooth Descrever(string id, BluetoothDevice dev)
    {
        string nome = "";
        try { nome = dev.Name; } catch { }
        var tipo = TipoAparelho.Outro;
        try
        {
            var c = dev.ClassOfDevice;
            if (c.MajorClass == BluetoothMajorClass.AudioVideo) tipo = TipoAparelho.Fone;
            else if (c.MajorClass == BluetoothMajorClass.Peripheral && ((c.RawValue >> 2) & 0xF) is 1 or 2) tipo = TipoAparelho.Controle;
        }
        catch { }
        var vp = NosPnp.VidPidDoAparelho(dev.BluetoothAddress);
        if (vp?.Fabricante == AnuncioApple.Fabricante) tipo = TipoAparelho.Fone;
        return new AparelhoBluetooth(id, nome, dev.BluetoothAddress, tipo, vp?.Fabricante, vp?.Produto);
    }

    private void Esquecer(string id)
    {
        if (_aparelhos.Remove(id, out var dev))
        {
            if (_estado.TryGetValue(id, out var c) && c) { _estado[id] = false; _mudou(Descrever(id, dev), false, false); }
            dev.Dispose();
        }
        _estado.Remove(id);
    }

    public IEnumerable<AparelhoBluetooth> Conectados() =>
        _aparelhos.Where(kv => _estado.TryGetValue(kv.Key, out var c) && c).Select(kv => Descrever(kv.Key, kv.Value)).ToList();

    public void Dispose()
    {
        _parado = true;
        foreach (var w in new[] { _pareados, _conectados })
        {
            if (w == null) continue;
            try { if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop(); } catch { }
        }
        _pareados = _conectados = null;
        foreach (var d in _aparelhos.Values) d.Dispose();
        _aparelhos.Clear();
        _estado.Clear();
    }
}
