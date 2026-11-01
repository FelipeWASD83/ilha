using System;
using System.Runtime.InteropServices;

namespace Ilha.Volume;

// Sessões de áudio por app (audiopolicy.h). Mesma regra do CoreAudio.cs: ordem da vtable, nada reordenado, tudo
// [PreserveSig]. Interface derivada repete os métodos da base (o interop do .NET não herda vtable de [ComImport]).

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    // IAudioSessionManager
    [PreserveSig] int GetAudioSessionControl(IntPtr grupo, uint marcas, out IntPtr controle);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr grupo, uint marcas, out IntPtr volume);
    // IAudioSessionManager2
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator lista);
    [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification aviso);
    [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification aviso);
    [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessao, IntPtr aviso);
    [PreserveSig] int UnregisterDuckNotification(IntPtr aviso);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int n);
    [PreserveSig] int GetSession(int i, [MarshalAs(UnmanagedType.IUnknown)] out object sessao);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // IAudioSessionControl
    [PreserveSig] int GetState(out int estado);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string? nome);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string nome, ref Guid contexto);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string? caminho);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string caminho, ref Guid contexto);
    [PreserveSig] int GetGroupingParam(out Guid grupo);
    [PreserveSig] int SetGroupingParam(ref Guid grupo, ref Guid contexto);
    [PreserveSig] int RegisterAudioSessionNotification(IAudioSessionEvents eventos);
    [PreserveSig] int UnregisterAudioSessionNotification(IAudioSessionEvents eventos);
    // IAudioSessionControl2
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string? id);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string? id);
    [PreserveSig] int GetProcessId(out uint pid);
    /// <summary>S_OK (0) = é a sessão dos sons do sistema; S_FALSE (1) = não é.</summary>
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool sair);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float escala, ref Guid contexto);
    [PreserveSig] int GetMasterVolume(out float escala);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mudo, ref Guid contexto);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mudo);
}

/// <summary>Sessão nova (thread do sistema; só chega a quem registrou numa thread MTA e já chamou GetCount).</summary>
[ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionNotification
{
    // o ponteiro só vale durante a chamada e não é guardado: a lista é relida na thread do mixer
    [PreserveSig] int OnSessionCreated(IntPtr sessao);
}

/// <summary>Eventos de uma sessão (thread do sistema: só anotar e despachar; nunca soltar o registro aqui dentro).</summary>
[ComImport, Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEvents
{
    [PreserveSig] int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string? nome, IntPtr contexto);
    [PreserveSig] int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string? caminho, IntPtr contexto);
    [PreserveSig] int OnSimpleVolumeChanged(float escala, [MarshalAs(UnmanagedType.Bool)] bool mudo, IntPtr contexto);
    [PreserveSig] int OnChannelVolumeChanged(uint canais, IntPtr volumes, uint canalMudado, IntPtr contexto);
    [PreserveSig] int OnGroupingParamChanged(IntPtr grupo, IntPtr contexto);
    [PreserveSig] int OnStateChanged(int estado);
    [PreserveSig] int OnSessionDisconnected(int motivo);
}
