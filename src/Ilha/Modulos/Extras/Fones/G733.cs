using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Ilha.Extras;

/// <summary>
/// Tensão da bateria do G733 em porcentagem. O headset só informa a tensão (mV); a curva é a que o HeadsetControl
/// usa para a família G633/G733 (valores conservadores de Li-Ion, não medidos no G733), interpolada em linha reta
/// entre os pontos. É estimativa: pode não bater com o G HUB.
/// </summary>
internal static class CurvaG733
{
    private static readonly (int Mv, int Pct)[] Pontos =
    {
        (4100, 100), (3950, 80), (3850, 60), (3750, 40), (3650, 20), (3500, 10), (3300, 5), (3150, 0),
    };

    /// <summary>null abaixo do menor ponto (leitura que não faz sentido).</summary>
    public static int? Porcentagem(int mv)
    {
        if (mv >= Pontos[0].Mv) return 100;
        if (mv < Pontos[^1].Mv) return null;
        for (int i = 1; i < Pontos.Length; i++)
        {
            var (mvA, pA) = Pontos[i - 1];
            var (mvB, pB) = Pontos[i];
            if (mv < mvB) continue;
            float t = (mv - mvB) / (float)(mvA - mvB);
            return (int)MathF.Round(pB + t * (pA - pB));
        }
        return 0;
    }
}

internal enum TipoRespostaG733 { Leitura, Desligado }

internal readonly record struct RespostaG733(TipoRespostaG733 Tipo, int Milivolts, bool Carregando)
{
    public int? Porcentagem => Tipo == TipoRespostaG733.Leitura ? CurvaG733.Porcentagem(Milivolts) : null;
}

/// <summary>
/// HID++ 2.0 do G733 pelo receptor LIGHTSPEED (que expõe o próprio headset como VID 046D, PID 0AFE): relatório
/// longo 0x11, aparelho 0xFF, recurso 0x08, função 0 com o software id 0xA. A resposta repete 11 FF 08 e traz a
/// tensão em big endian nos bytes 4 e 5 e o estado no 6 (3 = carregando). Headset desligado: o receptor devolve o
/// erro 11 FF FF 08 0A (ou o curto 10 FF 8F 08). Relatório de entrada vai para todos que têm a interface aberta (o
/// G HUB também lê e pergunta), então tudo o que não for resposta da função 0 do recurso 8 é descartado; resposta
/// dada ao G HUB (outro software id) ou evento do recurso (software id 0) também serve, é a mesma leitura.
/// </summary>
internal static class HidPP
{
    public const ushort Logitech = 0x046D;
    public const ushort PaginaHidPP = 0xFF43, UsoHidPP = 0x0202;
    // as três versões do G733 que o HeadsetControl conhece
    public static readonly ushort[] ProdutosG733 = { 0x0AB5, 0x0AFE, 0x0B1F };

    public static byte[] Consulta(int tamanho = 20)
    {
        var b = new byte[Math.Max(tamanho, 20)];
        b[0] = 0x11;
        b[1] = 0xFF;
        b[2] = 0x08;
        b[3] = 0x0A;
        return b;
    }

    public static RespostaG733? Interpretar(ReadOnlySpan<byte> r)
    {
        if (r.Length >= 5 && r[0] == 0x10 && r[1] == 0xFF && r[2] == 0x8F && r[3] == 0x08) return new(TipoRespostaG733.Desligado, 0, false);
        if (r.Length < 7 || r[0] != 0x11 || r[1] != 0xFF) return null;
        if (r[2] == 0xFF && r[3] == 0x08 && (r[4] >> 4) == 0) return new(TipoRespostaG733.Desligado, 0, false);
        if (r[2] != 0x08 || (r[3] >> 4) != 0) return null;
        int mv = r[4] << 8 | r[5];
        if (mv == 0) return null;
        return new(TipoRespostaG733.Leitura, mv, r[6] == 0x03);
    }
}

internal enum MudancaG733 { Nada, Ligou, Desligou, Atualizou }

/// <summary>Ligado ou desligado pelo que a consulta responde. A primeira decisão pode ser silenciosa (a ilha abriu com ele já ligado).</summary>
internal sealed class EstadoG733
{
    public bool? Ligado { get; private set; }
    public RespostaG733? Ultima { get; private set; }
    private bool _silenciarPrimeira;

    public EstadoG733(bool silenciarPrimeira) => _silenciarPrimeira = silenciarPrimeira;

    public MudancaG733 Receber(RespostaG733 r)
    {
        bool agora = r.Tipo == TipoRespostaG733.Leitura;
        bool? antes = Ligado;
        Ligado = agora;
        if (agora) Ultima = r;
        if (antes == null && _silenciarPrimeira) { _silenciarPrimeira = false; return agora ? MudancaG733.Atualizou : MudancaG733.Nada; }
        _silenciarPrimeira = false;
        if (agora && antes != true) return MudancaG733.Ligou;
        if (!agora && antes == true) return MudancaG733.Desligou;
        return agora ? MudancaG733.Atualizou : MudancaG733.Nada;
    }

    /// <summary>A consulta não teve resposta nenhuma no prazo: conta como desligado.</summary>
    public MudancaG733 SemResposta() => Receber(new RespostaG733(TipoRespostaG733.Desligado, 0, false));
}

/// <summary>
/// Uma thread por interface HID++ aberta, parada num ReadFile sobreposto (custo zero esperando). Consulta a tensão
/// ao abrir, a cada `intervalo` e pouco depois de qualquer relatório que não seja a resposta (o receptor manda
/// alguma coisa quando o headset liga ou desliga). As respostas saem por `chegou` (na thread da leitura).
/// </summary>
internal sealed class LeitorG733 : IDisposable
{
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int ERROR_IO_PENDING = 997;
    private const uint WAIT_TIMEOUT = 258;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string nome, uint acesso, uint compartilhar, IntPtr seg, uint criar, uint flags, IntPtr modelo);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern unsafe bool ReadFile(SafeFileHandle h, byte* buf, int n, IntPtr lidos, NativeOverlapped* ov);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern unsafe bool WriteFile(SafeFileHandle h, byte* buf, int n, IntPtr escritos, NativeOverlapped* ov);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern unsafe bool GetOverlappedResult(SafeFileHandle h, NativeOverlapped* ov, out int n, bool esperar);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern unsafe bool CancelIoEx(SafeFileHandle h, NativeOverlapped* ov);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForMultipleObjects(uint n, IntPtr[] handles, bool todos, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr dados);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr dados);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr dados, out HIDP_CAPS caps);
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    public string Caminho { get; }
    private readonly TimeSpan _intervalo;
    private readonly Action<RespostaG733?> _chegou;
    private readonly ManualResetEvent _parar = new(false);
    private readonly AutoResetEvent _consultar = new(false);
    private Thread? _thread;

    /// <param name="chegou">resposta da consulta, ou null quando a consulta não teve resposta no prazo</param>
    public LeitorG733(string caminho, TimeSpan intervalo, Action<RespostaG733?> chegou)
    {
        Caminho = caminho;
        _intervalo = intervalo;
        _chegou = chegou;
    }

    public void Iniciar()
    {
        _thread = new Thread(Rodar) { IsBackground = true, Name = "ilha-g733", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void ConsultarAgora() => _consultar.Set();

    /// <summary>Uma consulta só, sem thread (sonda do autoteste). null = não abriu ou não respondeu.</summary>
    public static RespostaG733? ConsultarUmaVez(string caminho, int prazoMs, out string detalhe)
    {
        RespostaG733? achada = null;
        using var fim = new ManualResetEvent(false);
        using (var l = new LeitorG733(caminho, TimeSpan.FromHours(1), r => { achada = r; fim.Set(); }))
        {
            l.Iniciar();
            bool veio = fim.WaitOne(prazoMs + 1500);
            detalhe = achada != null ? "respondeu" : veio ? "sem resposta no prazo" : "a interface não abriu ou travou";
        }
        return achada;
    }

    private unsafe void Rodar()
    {
        SafeFileHandle h;
        try
        {
            h = CreateFileW(Caminho, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) { Log.Escrever($"g733: interface não abriu (erro {Marshal.GetLastWin32Error()})"); return; }
        }
        catch (Exception e) { Log.Escrever("g733: " + e.Message); return; }

        int tamEntrada = 20, tamSaida = 20;
        if (HidD_GetPreparsedData(h, out var pd))
        {
            try
            {
                if (HidP_GetCaps(pd, out var caps) == HIDP_STATUS_SUCCESS)
                {
                    if (caps.UsagePage != HidPP.PaginaHidPP || caps.Usage != HidPP.UsoHidPP)
                    {
                        Log.Escrever($"g733: interface errada (página {caps.UsagePage:X4}, uso {caps.Usage:X4})");
                        h.Dispose();
                        return;
                    }
                    if (caps.InputReportByteLength > 0) tamEntrada = caps.InputReportByteLength;
                    if (caps.OutputReportByteLength > 0) tamSaida = caps.OutputReportByteLength;
                }
            }
            finally { HidD_FreePreparsedData(pd); }
        }

        var evLer = new ManualResetEvent(false);
        var evEscrever = new ManualResetEvent(false);
        var ovLer = (NativeOverlapped*)Marshal.AllocHGlobal(sizeof(NativeOverlapped));
        var ovEscrever = (NativeOverlapped*)Marshal.AllocHGlobal(sizeof(NativeOverlapped));
        var bufLer = (byte*)Marshal.AllocHGlobal(tamEntrada);
        var bufEscrever = (byte*)Marshal.AllocHGlobal(tamSaida);
        bool lendo = false;
        try
        {
            var esperar = new[] { evLer.SafeWaitHandle.DangerousGetHandle(), _parar.SafeWaitHandle.DangerousGetHandle(), _consultar.SafeWaitHandle.DangerousGetHandle() };
            DateTime proxima = DateTime.UtcNow, prazo = DateTime.MaxValue, ultimaConsulta = DateTime.MinValue;
            var resposta = new byte[tamEntrada];
            while (true)
            {
                if (!lendo)
                {
                    *ovLer = default;
                    evLer.Reset();
                    ovLer->EventHandle = esperar[0];
                    if (ReadFile(h, bufLer, tamEntrada, IntPtr.Zero, ovLer)) evLer.Set();
                    else if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) { Log.Escrever($"g733: leitura falhou (erro {Marshal.GetLastWin32Error()}), parando"); break; }
                    lendo = true;
                }
                var agora = DateTime.UtcNow;
                var alvo = proxima < prazo ? proxima : prazo;
                uint ms = alvo <= agora ? 0 : (uint)Math.Min((alvo - agora).TotalMilliseconds, int.MaxValue);
                uint r = WaitForMultipleObjects(3, esperar, false, ms);
                if (r == 1) break;
                if (r == 0)
                {
                    lendo = false;
                    if (!GetOverlappedResult(h, ovLer, out int n, false)) { Log.Escrever("g733: dispositivo sumiu"); break; }
                    new Span<byte>(bufLer, Math.Min(n, tamEntrada)).CopyTo(resposta);
                    var resp = HidPP.Interpretar(resposta.AsSpan(0, Math.Min(n, tamEntrada)));
                    if (resp != null)
                    {
                        prazo = DateTime.MaxValue;
                        _chegou(resp);
                    }
                    else if (DateTime.UtcNow - ultimaConsulta > TimeSpan.FromSeconds(10))
                        // o receptor falou sem ser perguntado: o headset pode ter ligado ou desligado
                        proxima = DateTime.UtcNow.AddSeconds(1.5);
                    continue;
                }
                if (r == 2 || r == WAIT_TIMEOUT)
                {
                    if (r == WAIT_TIMEOUT && DateTime.UtcNow >= prazo)
                    {
                        prazo = DateTime.MaxValue;
                        _chegou(null);
                        if (DateTime.UtcNow < proxima) continue;
                    }
                    if (r == 2 || DateTime.UtcNow >= proxima)
                    {
                        var c = HidPP.Consulta(tamSaida);
                        for (int i = 0; i < tamSaida; i++) bufEscrever[i] = i < c.Length ? c[i] : (byte)0;
                        *ovEscrever = default;
                        evEscrever.Reset();
                        ovEscrever->EventHandle = evEscrever.SafeWaitHandle.DangerousGetHandle();
                        bool ok = WriteFile(h, bufEscrever, tamSaida, IntPtr.Zero, ovEscrever);
                        if (!ok && Marshal.GetLastWin32Error() == ERROR_IO_PENDING)
                        {
                            if (WaitForSingleObject(ovEscrever->EventHandle, 1000) == WAIT_TIMEOUT) { CancelIoEx(h, ovEscrever); GetOverlappedResult(h, ovEscrever, out _, true); }
                            else ok = GetOverlappedResult(h, ovEscrever, out _, false);
                        }
                        if (!ok) Log.Escrever($"g733: consulta não foi (erro {Marshal.GetLastWin32Error()})");
                        ultimaConsulta = DateTime.UtcNow;
                        prazo = DateTime.UtcNow.AddMilliseconds(1500);
                        proxima = DateTime.UtcNow + _intervalo;
                    }
                }
                else if (r != WAIT_TIMEOUT) { Log.Escrever($"g733: espera falhou ({r})"); break; }
            }
        }
        catch (Exception e) { Log.Escrever("g733: " + e); }
        finally
        {
            if (lendo) { CancelIoEx(h, ovLer); GetOverlappedResult(h, ovLer, out _, true); }
            h.Dispose();
            Marshal.FreeHGlobal((IntPtr)ovLer);
            Marshal.FreeHGlobal((IntPtr)ovEscrever);
            Marshal.FreeHGlobal((IntPtr)bufLer);
            Marshal.FreeHGlobal((IntPtr)bufEscrever);
            evLer.Dispose();
            evEscrever.Dispose();
        }
    }

    public void Dispose()
    {
        _parar.Set();
        if (_thread != null && _thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(2000);
        // thread ainda presa (driver travado): deixa os eventos vivos para ela não usar handle fechado
        if (_thread == null || !_thread.IsAlive)
        {
            _parar.Dispose();
            _consultar.Dispose();
        }
    }
}
