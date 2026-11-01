using System;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth.Advertisement;

namespace Ilha.Extras;

/// <summary>
/// BluetoothLEAdvertisementWatcher.ScanParameters e BluetoothLEAdvertisementScanParameters.CoexistenceOptimized()
/// existem no Windows (UniversalApiContract 19), mas a projeção C# do SDK 26100 que a ilha usa não traz o contrato 19.
/// Por isso a chamada vai direto pelas interfaces WinRT do sistema: os IIDs e a ordem dos métodos são os do
/// Windows.Devices.Bluetooth.winmd do próprio Windows (IBluetoothLEAdvertisementWatcher3: get/put UseUncoded1MPhy,
/// get/put UseCodedPhy, get/put ScanParameters...). Sem o contrato (Windows mais antigo) o QueryInterface falha e a
/// varredura segue com os parâmetros padrão.
/// </summary>
internal static unsafe class ParametrosBle
{
    private static readonly Guid IidWatcher3 = new("14d980be-4002-5dbe-8519-ffca6ca389f0");
    private static readonly Guid IidEstaticos = new("548e39cd-3c9e-5f8d-b5e1-adebed5c357c");
    private static readonly Guid IidParametros = new("94f91413-63d9-53bd-af4c-e6b1a6514595");
    private const string Classe = "Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementScanParameters";

    // IInspectable ocupa as 6 primeiras posições da tabela
    private const int CoexistenciaOtimizada = 6, PutScanParameters = 11, GetScanParameters = 10, GetScanWindow = 6, GetScanInterval = 7;

    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classe, ref Guid iid, out IntPtr fabrica);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string s, int n, out IntPtr h);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr h);

    private static IntPtr Metodo(IntPtr obj, int posicao) => (*(IntPtr**)obj)[posicao];

    private static IntPtr Vigia3(BluetoothLEAdvertisementWatcher w)
    {
        var p = ((WinRT.IWinRTObject)w).NativeObject.ThisPtr;
        var iid = IidWatcher3;
        return Marshal.QueryInterface(p, in iid, out var w3) == 0 ? w3 : IntPtr.Zero;
    }

    /// <summary>Liga o modo de coexistência (divide o rádio com o áudio A2DP). false = não deu (detalhe diz por quê).</summary>
    public static bool Coexistencia(BluetoothLEAdvertisementWatcher w, out string detalhe)
    {
        IntPtr fabrica = IntPtr.Zero, parametros = IntPtr.Zero, w3 = IntPtr.Zero, nome = IntPtr.Zero;
        try
        {
            if (WindowsCreateString(Classe, Classe.Length, out nome) != 0) { detalhe = "HSTRING"; return false; }
            var iid = IidEstaticos;
            int hr = RoGetActivationFactory(nome, ref iid, out fabrica);
            if (hr != 0) { detalhe = $"classe indisponível (0x{hr:X8})"; return false; }
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Metodo(fabrica, CoexistenciaOtimizada))(fabrica, &parametros);
            if (hr != 0 || parametros == IntPtr.Zero) { detalhe = $"CoexistenceOptimized falhou (0x{hr:X8})"; return false; }
            w3 = Vigia3(w);
            if (w3 == IntPtr.Zero) { detalhe = "o vigia não tem ScanParameters neste Windows"; return false; }
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Metodo(w3, PutScanParameters))(w3, parametros);
            detalhe = hr == 0 ? "ok" : $"put_ScanParameters falhou (0x{hr:X8})";
            return hr == 0;
        }
        catch (Exception e) { detalhe = e.Message; return false; }
        finally
        {
            if (w3 != IntPtr.Zero) Marshal.Release(w3);
            if (parametros != IntPtr.Zero) Marshal.Release(parametros);
            if (fabrica != IntPtr.Zero) Marshal.Release(fabrica);
            if (nome != IntPtr.Zero) WindowsDeleteString(nome);
        }
    }

    /// <summary>Janela e intervalo de varredura que o vigia está usando (para a sonda), ou null.</summary>
    public static (ushort Janela, ushort Intervalo)? Ler(BluetoothLEAdvertisementWatcher w)
    {
        IntPtr w3 = IntPtr.Zero, parametros = IntPtr.Zero, p2 = IntPtr.Zero;
        try
        {
            w3 = Vigia3(w);
            if (w3 == IntPtr.Zero) return null;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Metodo(w3, GetScanParameters))(w3, &parametros) != 0 || parametros == IntPtr.Zero) return null;
            var iid = IidParametros;
            if (Marshal.QueryInterface(parametros, in iid, out p2) != 0) return null;
            ushort janela = 0, intervalo = 0;
            ((delegate* unmanaged[Stdcall]<IntPtr, ushort*, int>)Metodo(p2, GetScanWindow))(p2, &janela);
            ((delegate* unmanaged[Stdcall]<IntPtr, ushort*, int>)Metodo(p2, GetScanInterval))(p2, &intervalo);
            return (janela, intervalo);
        }
        catch { return null; }
        finally
        {
            if (p2 != IntPtr.Zero) Marshal.Release(p2);
            if (parametros != IntPtr.Zero) Marshal.Release(parametros);
            if (w3 != IntPtr.Zero) Marshal.Release(w3);
        }
    }
}
