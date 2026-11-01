using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Ilha;

/// <summary>
/// `Ilha.exe --medir`: passeia por todos os estados e mede, com Windows.Graphics.Capture da própria janela,
/// quantos quadros o compositor entrega durante cada transição; depois mede a CPU com a ilha parada.
/// Resultado em ~/.ilha/medicao.txt. Fecha sozinho no fim.
/// </summary>
internal static unsafe class Medidor
{
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr sw, uint flags, IntPtr levels, uint n, uint sdk, out IntPtr dev, out int lvl, out IntPtr ctx);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi, out IntPtr insp);
    [DllImport("combase.dll")] private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string s, int len, out IntPtr h);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr cls, in Guid iid, out IntPtr f);

    private static readonly List<long> _quadros = new();
    private static readonly List<(long t, string nome)> _passos = new();
    private static readonly StringBuilder _saida = new();
    private static GraphicsCaptureSession? _sessao;
    private static Direct3D11CaptureFramePool? _pool;

    private static long Agora => (long)(Stopwatch.GetTimestamp() * (1e7 / Stopwatch.Frequency));

    public static void Comecar(Nucleo n)
    {
        _saida.AppendLine($"Medição da ilha {Atualizacao.Versao} em {DateTime.Now:dd/MM/yyyy HH:mm}");
        bool capturando = false;
        n.Ilha.EstadoMudou += e =>
        {
            if (capturando || e == Estado.Oculta) return;
            capturando = true;
            var espera = new System.Windows.Forms.Timer { Interval = 60 };
            espera.Tick += (_, _) => { espera.Stop(); IniciarCaptura(n.Ilha.JanelaVisual); };
            espera.Start();
        };
        n.Demonstracao.Passo += nome => { lock (_passos) _passos.Add((Agora, nome)); };
        // primeiro uma animação linear de 1 s (sem o fim lento da mola), depois o roteiro de estados
        var t0 = (TimerDeMentira)n.Demonstracao.Atividades[0];
        t0.Comecar();
        var passo = 0;
        var r = new System.Windows.Forms.Timer { Interval = 2500 };
        r.Tick += (_, _) =>
        {
            if (passo == 0) { lock (_passos) _passos.Add((Agora, "linear 1 s (a borda muda em todo quadro)")); n.Ilha.TesteLinear(); r.Interval = 1500; }
            else { r.Stop(); t0.Parar(); n.Demonstracao.Roteiro(() => MedirParada(n)); }
            passo++;
        };
        r.Start();
    }

    private static void IniciarCaptura(IntPtr hwnd)
    {
        try
        {
            int hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out IntPtr dev, out _, out IntPtr ctx);
            Guid iidDxgi = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.QueryInterface(dev, in iidDxgi, out IntPtr dxgi);
            CreateDirect3D11DeviceFromDXGIDevice(dxgi, out IntPtr insp);
            var d3d = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(insp);
            WindowsCreateString("Windows.Graphics.Capture.GraphicsCaptureItem", 44, out IntPtr hs);
            Guid iidInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            RoGetActivationFactory(hs, in iidInterop, out IntPtr fab);
            Guid iidItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            IntPtr pItem;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)fab)[3])(fab, hwnd, &iidItem, &pItem);
            if (hr != 0) { _saida.AppendLine($"captura não abriu (0x{hr:X8})"); return; }
            var item = GraphicsCaptureItem.FromAbi(pItem);
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(d3d, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, item.Size);
            _pool.FrameArrived += (p, _) =>
            {
                using var f = p.TryGetNextFrame();
                if (f != null) lock (_quadros) _quadros.Add(f.SystemRelativeTime.Ticks);
            };
            _sessao = _pool.CreateCaptureSession(item);
            _sessao.IsCursorCaptureEnabled = false;
            try { _sessao.IsBorderRequired = false; } catch { }
            try { _sessao.MinUpdateInterval = TimeSpan.FromTicks(1); } catch { }
            _sessao.StartCapture();
            _saida.AppendLine($"captura da janela {item.Size.Width}x{item.Size.Height} ligada");
        }
        catch (Exception e) { _saida.AppendLine("captura falhou: " + e.Message); }
    }

    private static void MedirParada(Nucleo n)
    {
        _sessao?.Dispose();
        _pool?.Dispose();
        Relatorio();

        // CPU com o timer na compacta (redesenha 1 vez por segundo) e depois com a ilha escondida
        var proc = Process.GetCurrentProcess();
        var t = (TimerDeMentira)n.Demonstracao.Atividades[0];
        t.Comecar();
        var fase = 0;
        TimeSpan cpu0 = TimeSpan.Zero;
        var sw = Stopwatch.StartNew();
        var relogio = new System.Windows.Forms.Timer { Interval = 100 };
        relogio.Tick += (_, _) =>
        {
            double s = sw.Elapsed.TotalSeconds;
            if (fase == 0 && s > 6) { proc.Refresh(); cpu0 = proc.TotalProcessorTime; sw.Restart(); fase = 1; }
            else if (fase == 1 && s > 10)
            {
                proc.Refresh();
                var gasto = proc.TotalProcessorTime - cpu0;
                _saida.AppendLine($"CPU parada na compacta (timer redesenhando 1x/s): {gasto.TotalMilliseconds:F0} ms em 10 s = {gasto.TotalMilliseconds / 100 / Environment.ProcessorCount:F3}% do processador");
                t.Parar();
                sw.Restart();
                fase = 2;
            }
            else if (fase == 2 && s > 4) { proc.Refresh(); cpu0 = proc.TotalProcessorTime; sw.Restart(); fase = 3; }
            else if (fase == 3 && s > 10)
            {
                proc.Refresh();
                var gasto = proc.TotalProcessorTime - cpu0;
                _saida.AppendLine($"CPU com a ilha escondida: {gasto.TotalMilliseconds:F0} ms em 10 s = {gasto.TotalMilliseconds / 100 / Environment.ProcessorCount:F3}% do processador");
                proc.Refresh();
                _saida.AppendLine($"memória: {proc.WorkingSet64 / 1048576.0:F0} MB (working set), {proc.PrivateMemorySize64 / 1048576.0:F0} MB privados");
                relogio.Stop();
                Directory.CreateDirectory(Config.Pasta);
                File.WriteAllText(Path.Combine(Config.Pasta, "medicao.txt"), _saida.ToString());
                n.Sair();
            }
        };
        relogio.Start();
    }

    private static void Relatorio()
    {
        long[] q;
        lock (_quadros) q = _quadros.OrderBy(x => x).ToArray();
        (long t, string nome)[] p;
        lock (_passos) p = _passos.ToArray();
        _saida.AppendLine($"quadros capturados no roteiro: {q.Length}");
        var todos = new List<double>();
        for (int i = 0; i < p.Length; i++)
        {
            long ini = p[i].t, fim = Math.Min(ini + 9_000_000, i + 1 < p.Length ? p[i + 1].t : long.MaxValue); // 0,9 s depois do passo
            var dentro = q.Where(x => x >= ini && x < fim).ToArray();
            if (dentro.Length < 3) { _saida.AppendLine($"  {p[i].nome}: {dentro.Length} quadros (nada animando)"); continue; }
            var d = dentro.Zip(dentro.Skip(1), (a, b) => (b - a) / 10000.0).OrderBy(x => x).ToArray();
            todos.AddRange(d);
            double span = (dentro[^1] - dentro[0]) / 1e7;
            _saida.AppendLine($"  {p[i].nome}: {dentro.Length} quadros em {span:F2} s = {(dentro.Length - 1) / span:F0} fps; intervalo mediano {d[d.Length / 2]:F2} ms, p95 {d[(int)(d.Length * 0.95)]:F2} ms, pior {d[^1]:F2} ms");
            // onde caem os buracos (ms desde o passo): no fim = mola assentando abaixo de 1 pixel; no meio = quadro perdido
            var buracos = new List<string>();
            for (int k = 1; k < dentro.Length; k++)
                if ((dentro[k] - dentro[k - 1]) / 10000.0 > 6) buracos.Add($"{(dentro[k - 1] - ini) / 10000.0:F0}+{(dentro[k] - dentro[k - 1]) / 10000.0:F0}");
            if (buracos.Count > 0) _saida.AppendLine($"      buracos (início+duração, ms): {string.Join(" ", buracos.Take(14))}");
        }
        if (todos.Count > 0)
        {
            var o = todos.OrderBy(x => x).ToArray();
            double med = o[o.Length / 2];
            _saida.AppendLine($"todas as transições: intervalo mediano {med:F2} ms = {1000 / med:F0} fps; p95 {o[(int)(o.Length * 0.95)]:F2} ms");
        }
    }
}
