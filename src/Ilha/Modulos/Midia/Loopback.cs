using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

// não é Ilha.Modulos.Midia: o núcleo tem a classe estática Ilha.Modulos e o namespace colidiria com ela
namespace Ilha.Midia;

/// <summary>
/// Escuta o que sai no dispositivo padrão (WASAPI loopback, modo compartilhado, por evento) numa thread própria e
/// publica as 4 bandas do visualizador. Só existe enquanto as barras estão na tela e tocando: Ligar cria a thread,
/// Desligar avisa e ela sai sozinha (a interface nunca espera). Dispositivo invalidado, troca da saída padrão e volta
/// da suspensão recriam a captura. Silêncio (pausa, conteúdo com DRM) vira bandas zeradas, sem erro.
/// </summary>
internal sealed class Loopback
{
    private sealed class Captura
    {
        public readonly ManualResetEvent Parar = new(false);
        public readonly AutoResetEvent Reiniciar = new(false);
        public string Formato = "";
    }

    private enum Fim { Parar, Reiniciar, Falhou }

    private readonly object _trava = new();
    private readonly float[] _bandas = new float[Espectro.Bandas];
    private Captura? _atual;

    public bool Ligado => _atual != null;
    /// <summary>O formato da captura que está rodando (para o log e para a sonda).</summary>
    public string Formato => _atual?.Formato ?? "";

    public void Ligar()
    {
        if (_atual != null) return;
        var c = new Captura();
        _atual = c;
        var t = new Thread(() => Laco(c)) { IsBackground = true, Name = "ilha-midia-loopback" };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
    }

    public void Desligar()
    {
        var c = _atual;
        if (c == null) return;
        _atual = null;
        c.Parar.Set();
        lock (_trava) Array.Clear(_bandas);
    }

    /// <summary>Recria a captura (volta da suspensão).</summary>
    public void Reiniciar() => _atual?.Reiniciar.Set();

    /// <summary>Copia as bandas mais recentes (0 a 1).</summary>
    public void Ler(Span<float> destino)
    {
        lock (_trava) _bandas.AsSpan().CopyTo(destino);
    }

    private void Publicar(Captura c, ReadOnlySpan<float> b)
    {
        lock (_trava)
            if (_atual == c) b.CopyTo(_bandas);
    }

    private void Laco(Captura c)
    {
        IMMDeviceEnumerator? en = null;
        AvisoDeDispositivo? aviso = null;
        try
        {
            en = (IMMDeviceEnumerator)ComDeAudio.NovoEnumerador();
            aviso = new AvisoDeDispositivo(() => c.Reiniciar.Set());
            en.RegisterEndpointNotificationCallback(aviso);
            int falhas = 0;
            while (!c.Parar.WaitOne(0))
            {
                Fim fim;
                try { fim = Sessao(c, en); }
                catch (Exception e)
                {
                    Log.Escrever("visualizador: " + e.Message);
                    fim = Fim.Falhou;
                }
                if (fim == Fim.Parar) break;
                if (fim == Fim.Reiniciar) { falhas = 0; continue; }
                // sem saída de áudio ou falha: tenta de novo mais tarde (ou já, se o padrão mudar)
                falhas++;
                int espera = Math.Min(10_000, 1000 * falhas);
                if (WaitHandle.WaitAny(new WaitHandle[] { c.Parar, c.Reiniciar }, espera) == 0) break;
            }
        }
        catch (Exception e) { Log.Escrever("visualizador não abriu: " + e.Message); }
        finally
        {
            if (en != null && aviso != null) { try { en.UnregisterEndpointNotificationCallback(aviso); } catch { } }
            // os eventos da Captura ficam para o coletor: o Desligar pode sinalizar depois de a thread ter saído
            CoreAudio.Soltar(en);
        }
    }

    private Fim Sessao(Captura c, IMMDeviceEnumerator en)
    {
        IMMDevice? dev = null;
        IAudioClient? cliente = null;
        IAudioCaptureClient? cap = null;
        IntPtr pFormato = IntPtr.Zero;
        using var evento = new AutoResetEvent(false);
        bool iniciado = false;
        try
        {
            int hr = en.GetDefaultAudioEndpoint(FluxoAudio.Render, PapelAudio.Multimidia, out dev);
            if (hr != 0 || dev == null) return Fim.Falhou;
            var iid = CoreAudio.IID_IAudioClient;
            hr = dev.Activate(ref iid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out object o);
            if (hr != 0) return Fim.Falhou;
            cliente = (IAudioClient)o;
            hr = cliente.GetMixFormat(out pFormato);
            if (hr != 0) return Fim.Falhou;
            var wf = new byte[40];
            Marshal.Copy(pFormato, wf, 0, Marshal.ReadInt16(pFormato) == unchecked((short)0xFFFE) ? 40 : 18);
            var fmt = FormatoAudio.Ler(wf);
            if (fmt is not { Suportado: true } f)
            {
                Log.Escrever("visualizador: formato do mix não suportado");
                return Fim.Falhou;
            }
            c.Formato = f.ToString();
            hr = cliente.Initialize(CoreAudio.AUDCLNT_SHAREMODE_SHARED,
                CoreAudio.AUDCLNT_STREAMFLAGS_LOOPBACK | CoreAudio.AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                2_000_000, 0, pFormato, IntPtr.Zero);
            if (hr == CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED) return Fim.Reiniciar;
            if (hr != 0) { Log.Escrever($"visualizador: Initialize 0x{hr:X8}"); return Fim.Falhou; }
            hr = cliente.SetEventHandle(evento.SafeWaitHandle.DangerousGetHandle());
            if (hr != 0) return Fim.Falhou;
            var iidCap = CoreAudio.IID_IAudioCaptureClient;
            hr = cliente.GetService(ref iidCap, out object oc);
            if (hr != 0) return Fim.Falhou;
            cap = (IAudioCaptureClient)oc;
            hr = cliente.Start();
            if (hr != 0) return hr == CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED ? Fim.Reiniciar : Fim.Falhou;
            iniciado = true;
            return Ouvir(c, cap, f, evento);
        }
        finally
        {
            if (iniciado) { try { cliente!.Stop(); } catch { } }
            CoreAudio.Soltar(cap);
            CoreAudio.Soltar(cliente);
            CoreAudio.Soltar(dev);
            if (pFormato != IntPtr.Zero) Marshal.FreeCoTaskMem(pFormato);
        }
    }

    private Fim Ouvir(Captura c, IAudioCaptureClient cap, FormatoAudio f, AutoResetEvent evento)
    {
        var anel = new float[Espectro.N * 4];
        int escrita = 0;
        var janela = new float[Espectro.N];
        var mono = new float[8192];
        var bandas = new float[Espectro.Bandas];
        var espectro = new Espectro();
        var relogio = Stopwatch.StartNew();
        long ultimoDado = -10_000, ultimaAnalise = 0;
        var esperas = new WaitHandle[] { c.Parar, c.Reiniciar, evento };

        while (true)
        {
            // sem som nenhum o evento não chega: o tempo-limite mantém as barras descendo
            int w = WaitHandle.WaitAny(esperas, 40);
            if (w == 0) return Fim.Parar;
            if (w == 1) return Fim.Reiniciar;

            while (true)
            {
                int hr = cap.GetNextPacketSize(out uint pacote);
                if (hr == CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED) return Fim.Reiniciar;
                if (hr < 0) return Fim.Falhou;
                if (pacote == 0) break;
                hr = cap.GetBuffer(out IntPtr dados, out uint quadros, out uint flags, out _, out _);
                if (hr == CoreAudio.AUDCLNT_S_BUFFER_EMPTY) break;
                if (hr == CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED) return Fim.Reiniciar;
                if (hr < 0) return Fim.Falhou;
                int n = (int)Math.Min(quadros, (uint)mono.Length);
                if ((flags & CoreAudio.AUDCLNT_BUFFERFLAGS_SILENT) != 0) Array.Clear(mono, 0, n);
                else
                {
                    unsafe
                    {
                        var span = new ReadOnlySpan<byte>((void*)dados, n * f.Bloco);
                        n = f.ParaMono(span, n, mono);
                    }
                }
                cap.ReleaseBuffer(quadros);
                for (int i = 0; i < n; i++)
                {
                    anel[escrita] = mono[i];
                    escrita = (escrita + 1) % anel.Length;
                }
                if (n > 0) ultimoDado = relogio.ElapsedMilliseconds;
            }

            long agora = relogio.ElapsedMilliseconds;
            if (agora - ultimaAnalise < 33) continue;
            ultimaAnalise = agora;
            if (agora - ultimoDado > 150) Array.Clear(janela);
            else
                for (int i = 0; i < Espectro.N; i++)
                    janela[i] = anel[(escrita - Espectro.N + i + anel.Length) % anel.Length];
            espectro.Analisar(janela, f.Taxa, bandas);
            Publicar(c, bandas);
        }
    }

    /// <summary>Sonda do autoteste: abre a captura por ~300 ms e diz o formato e quantos quadros chegaram (só lê).</summary>
    public static string Sondar()
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        IAudioClient? cliente = null;
        IAudioCaptureClient? cap = null;
        IntPtr pFormato = IntPtr.Zero;
        try
        {
            en = (IMMDeviceEnumerator)ComDeAudio.NovoEnumerador();
            int hr = en.GetDefaultAudioEndpoint(FluxoAudio.Render, PapelAudio.Multimidia, out dev);
            if (hr == CoreAudio.E_NOTFOUND) return "aviso   loopback: nenhuma saída de áudio";
            if (hr != 0) return $"FALHOU  loopback: GetDefaultAudioEndpoint 0x{hr:X8}";
            var iid = CoreAudio.IID_IAudioClient;
            hr = dev.Activate(ref iid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out object o);
            if (hr != 0) return $"FALHOU  loopback: Activate 0x{hr:X8}";
            cliente = (IAudioClient)o;
            hr = cliente.GetMixFormat(out pFormato);
            if (hr != 0) return $"FALHOU  loopback: GetMixFormat 0x{hr:X8}";
            var wf = new byte[40];
            Marshal.Copy(pFormato, wf, 0, Marshal.ReadInt16(pFormato) == unchecked((short)0xFFFE) ? 40 : 18);
            var fmt = FormatoAudio.Ler(wf);
            if (fmt is not { Suportado: true } f) return "FALHOU  loopback: formato do mix não suportado";
            using var evento = new AutoResetEvent(false);
            hr = cliente.Initialize(CoreAudio.AUDCLNT_SHAREMODE_SHARED,
                CoreAudio.AUDCLNT_STREAMFLAGS_LOOPBACK | CoreAudio.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 2_000_000, 0, pFormato, IntPtr.Zero);
            if (hr != 0) return $"FALHOU  loopback: Initialize 0x{hr:X8}";
            if ((hr = cliente.SetEventHandle(evento.SafeWaitHandle.DangerousGetHandle())) != 0) return $"FALHOU  loopback: SetEventHandle 0x{hr:X8}";
            var iidCap = CoreAudio.IID_IAudioCaptureClient;
            if ((hr = cliente.GetService(ref iidCap, out object oc)) != 0) return $"FALHOU  loopback: GetService 0x{hr:X8}";
            cap = (IAudioCaptureClient)oc;
            if ((hr = cliente.Start()) != 0) return $"FALHOU  loopback: Start 0x{hr:X8}";
            long total = 0;
            int pacotes = 0;
            var relogio = Stopwatch.StartNew();
            while (relogio.ElapsedMilliseconds < 300)
            {
                evento.WaitOne(40);
                while (cap.GetNextPacketSize(out uint p) == 0 && p > 0)
                {
                    if (cap.GetBuffer(out _, out uint q, out _, out _, out _) != 0) break;
                    cap.ReleaseBuffer(q);
                    total += q;
                    pacotes++;
                }
            }
            cliente.Stop();
            string som = pacotes == 0 ? " (nada tocando: sem pacotes, o que é normal no silêncio)" : "";
            return $"ok      loopback abriu ({f}): {pacotes} pacotes, {total} quadros em 300 ms{som}";
        }
        catch (Exception e) { return "FALHOU  loopback: " + e.Message; }
        finally
        {
            CoreAudio.Soltar(cap);
            CoreAudio.Soltar(cliente);
            CoreAudio.Soltar(dev);
            CoreAudio.Soltar(en);
            if (pFormato != IntPtr.Zero) Marshal.FreeCoTaskMem(pFormato);
        }
    }
}
