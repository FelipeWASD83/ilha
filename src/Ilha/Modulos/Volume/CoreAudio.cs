using System;
using System.Runtime.InteropServices;

namespace Ilha.Volume;

// Core Audio (mmdeviceapi.h e endpointvolume.h) por COM interop escrito à mão. A ordem dos métodos em cada
// interface é a da vtable: não reordenar. Tudo [PreserveSig] para o HRESULT voltar como número, sem exceção.

internal enum FluxoAudio { Render = 0, Captura = 1, Todos = 2 }

internal enum PapelAudio { Console = 0, Multimidia = 1, Comunicacoes = 2 }

internal static class EstadoAparelho
{
    public const uint Ativo = 0x1, Desativado = 0x2, Ausente = 0x4, Desconectado = 0x8, Todos = 0xF;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ChaveDePropriedade
{
    public Guid Fmtid;
    public uint Pid;

    public ChaveDePropriedade(string fmtid, uint pid)
    {
        Fmtid = new Guid(fmtid);
        Pid = pid;
    }

    public bool Igual(ChaveDePropriedade o) => Fmtid == o.Fmtid && Pid == o.Pid;
}

/// <summary>PROPVARIANT (24 bytes no x64): só os tipos que a ilha lê (texto, número, GUID).</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct ValorDePropriedade
{
    [FieldOffset(0)] public ushort Tipo;
    [FieldOffset(8)] public IntPtr Ponteiro;
    [FieldOffset(8)] public uint Numero;

    public const ushort VT_UI4 = 19, VT_LPWSTR = 31, VT_CLSID = 72;
}

internal static class Propriedades
{
    // PKEY_Device_FriendlyName: "Fones de ouvido (AirPods ...)", o nome que o Windows mostra
    public static readonly ChaveDePropriedade NomeAmigavel = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    // PKEY_Device_DeviceDesc: "Fones de ouvido", "Alto-falantes"
    public static readonly ChaveDePropriedade Descricao = new("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
    // PKEY_Device_EnumeratorName: BTHENUM (Bluetooth estéreo), BTHHFENUM (Bluetooth Hands-Free), USB, HDAUDIO
    public static readonly ChaveDePropriedade Enumerador = new("a45c254e-df1c-4efd-8020-67d146a850e0", 24);
    // PKEY_DeviceInterface_FriendlyName: o nome do aparelho ("Logitech G733 Gaming Headset")
    public static readonly ChaveDePropriedade NomeDoAparelho = new("026e516e-b814-414b-83cd-856d6fef4822", 2);
    // PKEY_Device_ContainerId: os endpoints do mesmo aparelho físico dividem o mesmo
    public static readonly ChaveDePropriedade Conteiner = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
    // PKEY_AudioEndpoint_FormFactor: 1 alto-falantes, 3 fones, 5 headset, 9 saída digital de monitor
    public static readonly ChaveDePropriedade Formato = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 0);
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(FluxoAudio fluxo, uint mascaraDeEstado, out IMMDeviceCollection lista);
    [PreserveSig] int GetDefaultAudioEndpoint(FluxoAudio fluxo, PapelAudio papel, out IMMDevice aparelho);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice aparelho);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient cliente);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient cliente);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint n);
    [PreserveSig] int Item(uint i, out IMMDevice aparelho);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint contexto, IntPtr parametros, [MarshalAs(UnmanagedType.IUnknown)] out object interfaceAtivada);
    [PreserveSig] int OpenPropertyStore(uint acesso, out IPropertyStore loja);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint estado);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint n);
    [PreserveSig] int GetAt(uint i, out ChaveDePropriedade chave);
    [PreserveSig] int GetValue(ref ChaveDePropriedade chave, out ValorDePropriedade valor);
    [PreserveSig] int SetValue(ref ChaveDePropriedade chave, ref ValorDePropriedade valor);
    [PreserveSig] int Commit();
}

/// <summary>Avisos de aparelho (chegam numa thread do sistema: só anotar e despachar, nunca registrar aqui dentro).</summary>
[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint novoEstado);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDefaultDeviceChanged(FluxoAudio fluxo, PapelAudio papel, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, ChaveDePropriedade chave);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    [PreserveSig] int GetChannelCount(out uint n);
    [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid contexto);
    [PreserveSig] int SetMasterVolumeLevelScalar(float escala, ref Guid contexto);
    [PreserveSig] int GetMasterVolumeLevel(out float db);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float escala);
    [PreserveSig] int SetChannelVolumeLevel(uint canal, float db, ref Guid contexto);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint canal, float escala, ref Guid contexto);
    [PreserveSig] int GetChannelVolumeLevel(uint canal, out float db);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint canal, out float escala);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mudo, ref Guid contexto);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mudo);
    [PreserveSig] int GetVolumeStepInfo(out uint passo, out uint passos);
    [PreserveSig] int VolumeStepUp(ref Guid contexto);
    [PreserveSig] int VolumeStepDown(ref Guid contexto);
    [PreserveSig] int QueryHardwareSupport(out uint mascara);
    [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float passoDb);
}

/// <summary>Mudou o volume ou o mudo do aparelho (thread do sistema; o ponteiro só vale durante a chamada).</summary>
[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(IntPtr dados);
}

// Não documentado: é o que o painel de som do Windows usa para trocar o padrão. GUIDs válidos do Windows 10 RS1
// até hoje (EarTrumpet usa os mesmos); a ordem da vtable é a mesma desde então. Só SetDefaultEndpoint é chamado.
[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
internal class PolicyConfigClientCom { }

[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr formato);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int padrao, IntPtr formato);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr formato, IntPtr formatoMix);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int padrao, IntPtr periodo, IntPtr minimo);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr periodo);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr modo);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr modo);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int loja, ref ChaveDePropriedade chave, IntPtr valor);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int loja, ref ChaveDePropriedade chave, IntPtr valor);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, PapelAudio papel);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visivel);
}

internal static class Com
{
    public const uint CLSCTX_ALL = 0x17;
    public const uint STGM_READ = 0;
    public static readonly Guid IID_IAudioEndpointVolume = typeof(IAudioEndpointVolume).GUID;

    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref ValorDePropriedade v);

    public static void Soltar(object? o)
    {
        if (o != null && Marshal.IsComObject(o))
            try { Marshal.ReleaseComObject(o); } catch { }
    }

    public static string? LerTexto(IPropertyStore loja, ChaveDePropriedade chave)
    {
        if (loja.GetValue(ref chave, out var v) != 0) return null;
        try { return v.Tipo == ValorDePropriedade.VT_LPWSTR && v.Ponteiro != IntPtr.Zero ? Marshal.PtrToStringUni(v.Ponteiro) : null; }
        finally { PropVariantClear(ref v); }
    }

    public static uint? LerNumero(IPropertyStore loja, ChaveDePropriedade chave)
    {
        if (loja.GetValue(ref chave, out var v) != 0) return null;
        try { return v.Tipo == ValorDePropriedade.VT_UI4 ? v.Numero : null; }
        finally { PropVariantClear(ref v); }
    }

    public static Guid? LerGuid(IPropertyStore loja, ChaveDePropriedade chave)
    {
        if (loja.GetValue(ref chave, out var v) != 0) return null;
        try { return v.Tipo == ValorDePropriedade.VT_CLSID && v.Ponteiro != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(v.Ponteiro) : null; }
        finally { PropVariantClear(ref v); }
    }
}
