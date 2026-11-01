using System;
using System.Runtime.InteropServices;

// não é Ilha.Modulos.Midia: o núcleo tem a classe estática Ilha.Modulos e o namespace colidiria com ela
namespace Ilha.Midia;

// Core Audio (mmdeviceapi.h, endpointvolume.h, audioclient.h) declarado à mão: COM do sistema, sem pacote.
// Tudo com PreserveSig para os HRESULT voltarem como número (AUDCLNT_E_DEVICE_INVALIDATED não pode virar exceção solta).

internal enum FluxoAudio { Render = 0, Captura = 1, Todos = 2 }
internal enum PapelAudio { Console = 0, Multimidia = 1, Comunicacao = 2 }

[StructLayout(LayoutKind.Sequential)]
internal struct ChavePropriedade
{
    public Guid Formato;
    public int Id;
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class EnumeradorDeDispositivos { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(FluxoAudio fluxo, int estados, out IntPtr colecao);
    [PreserveSig] int GetDefaultAudioEndpoint(FluxoAudio fluxo, PapelAudio papel, out IMMDevice dispositivo);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice dispositivo);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient cliente);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient cliente);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int contexto, IntPtr parametros, [MarshalAs(UnmanagedType.IUnknown)] out object interfaceAtivada);
    [PreserveSig] int OpenPropertyStore(int acesso, out IntPtr loja);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int estado);
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int estado);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDefaultDeviceChanged(FluxoAudio fluxo, PapelAudio papel, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, ChavePropriedade chave);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notificacao);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notificacao);
    [PreserveSig] int GetChannelCount(out uint canais);
    [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid contexto);
    [PreserveSig] int SetMasterVolumeLevelScalar(float nivel, ref Guid contexto);
    [PreserveSig] int GetMasterVolumeLevel(out float db);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float nivel);
    [PreserveSig] int SetChannelVolumeLevel(uint canal, float db, ref Guid contexto);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint canal, float nivel, ref Guid contexto);
    [PreserveSig] int GetChannelVolumeLevel(uint canal, out float db);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint canal, out float nivel);
    [PreserveSig] int SetMute(int mudo, ref Guid contexto);
    [PreserveSig] int GetMute(out int mudo);
    [PreserveSig] int GetVolumeStepInfo(out uint passo, out uint passos);
    [PreserveSig] int VolumeStepUp(ref Guid contexto);
    [PreserveSig] int VolumeStepDown(ref Guid contexto);
    [PreserveSig] int QueryHardwareSupport(out uint suporte);
    [PreserveSig] int GetVolumeRange(out float minimo, out float maximo, out float incremento);
}

[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int modo, uint flags, long duracaoBuffer, long periodo, IntPtr formato, IntPtr sessao);
    [PreserveSig] int GetBufferSize(out uint quadros);
    [PreserveSig] int GetStreamLatency(out long latencia);
    [PreserveSig] int GetCurrentPadding(out uint quadros);
    [PreserveSig] int IsFormatSupported(int modo, IntPtr formato, out IntPtr parecido);
    [PreserveSig] int GetMixFormat(out IntPtr formato);
    [PreserveSig] int GetDevicePeriod(out long padrao, out long minimo);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr evento);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object servico);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr dados, out uint quadros, out uint flags, out ulong posicaoDispositivo, out ulong posicaoQpc);
    [PreserveSig] int ReleaseBuffer(uint quadros);
    [PreserveSig] int GetNextPacketSize(out uint quadros);
}

internal static class CoreAudio
{
    public static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    public const int CLSCTX_ALL = 0x17;
    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    public const int AUDCLNT_S_BUFFER_EMPTY = 0x08890001;
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int E_NOTFOUND = unchecked((int)0x80070490);

    public static void Soltar(object? o)
    {
        if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }
}

/// <summary>Aviso do sistema de que a saída padrão mudou (chega numa thread do sistema: só sinaliza, nunca bloqueia).</summary>
[ComVisible(true)]
internal sealed class AvisoDeDispositivo : IMMNotificationClient
{
    private readonly Action _mudou;

    public AvisoDeDispositivo(Action mudou) => _mudou = mudou;

    public int OnDeviceStateChanged(string id, int estado) => 0;
    public int OnDeviceAdded(string id) => 0;
    public int OnDeviceRemoved(string id) => 0;

    public int OnDefaultDeviceChanged(FluxoAudio fluxo, PapelAudio papel, string? id)
    {
        // chega uma vez por papel; só o de multimídia da saída interessa
        if (fluxo == FluxoAudio.Render && papel == PapelAudio.Multimidia) _mudou();
        return 0;
    }

    public int OnPropertyValueChanged(string id, ChavePropriedade chave) => 0;
}

/// <summary>Volume do dispositivo de saída padrão (IAudioEndpointVolume). Sempre pega o padrão da hora: nunca fica preso num aparelho velho.</summary>
internal static class VolumeDoSistema
{
    /// <summary>
    /// Contexto que a mídia passa ao mudar o volume: quem acompanha o volume por evento (OnNotify) reconhece que a
    /// mudança veio da rodinha em cima da ilha.
    /// </summary>
    public static readonly Guid Contexto = new("6F1D7C42-58A9-4B3E-9E07-2C4B8A61D3F5");

    private static T? ComVolume<T>(Func<IAudioEndpointVolume, T?> acao)
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        object? o = null;
        try
        {
            en = (IMMDeviceEnumerator)new EnumeradorDeDispositivos();
            if (en.GetDefaultAudioEndpoint(FluxoAudio.Render, PapelAudio.Multimidia, out dev) != 0 || dev == null) return default;
            var iid = CoreAudio.IID_IAudioEndpointVolume;
            if (dev.Activate(ref iid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out o) != 0) return default;
            return acao((IAudioEndpointVolume)o);
        }
        finally
        {
            CoreAudio.Soltar(o);
            CoreAudio.Soltar(dev);
            CoreAudio.Soltar(en);
        }
    }

    /// <summary>Nível atual (0 a 1) e se está mudo; null sem saída de áudio.</summary>
    public static (float Nivel, bool Mudo)? Ler() => ComVolume<(float, bool)?>(v =>
    {
        if (v.GetMasterVolumeLevelScalar(out float n) != 0) return null;
        v.GetMute(out int m);
        return (n, m != 0);
    });

    /// <summary>Soma `delta` (fração, 0,02 = 2%) ao volume, arredondando para o por cento inteiro. Subir tira do mudo.</summary>
    public static float? Somar(float delta) => ComVolume<float?>(v =>
    {
        if (v.GetMasterVolumeLevelScalar(out float n) != 0) return null;
        float novo = Novo(n, delta);
        var ctx = Contexto;
        if (v.SetMasterVolumeLevelScalar(novo, ref ctx) != 0) return null;
        if (delta > 0 && v.GetMute(out int m) == 0 && m != 0) v.SetMute(0, ref ctx);
        return novo;
    });

    /// <summary>A conta do volume novo (pura, entra no autoteste).</summary>
    public static float Novo(float atual, float delta) =>
        Math.Clamp(MathF.Round((atual + delta) * 100f, MidpointRounding.AwayFromZero) / 100f, 0f, 1f);
}
