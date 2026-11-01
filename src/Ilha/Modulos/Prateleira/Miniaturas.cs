using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Ilha.Extras;

namespace Ilha.Prateleira;

/// <summary>
/// Miniaturas pelo próprio shell (a mesma do Explorer: foto, vídeo, PSD com o codec instalado, ícone da pasta ou do
/// tipo). Cada pedido roda numa thread STA própria, porque handler de miniatura pode ser lento ou exigir STA; a thread
/// sai sozinha depois de 30 s sem pedido. Guarda o resultado (inclusive o "não deu") até o caminho ser esquecido.
/// </summary>
internal sealed class Miniaturas : IDisposable
{
    private readonly Dictionary<(string, int), Retrato?> _cache = new();
    private readonly HashSet<(string, int)> _pedidos = new();
    private readonly BlockingCollection<(string Caminho, int Px, Action Pronto)> _fila = new();
    private readonly object _trava = new();
    private Thread? _thread;
    private bool _fechada;

    /// <summary>A miniatura já pronta, ou null e o pedido vai para a fila (`pronto` é chamado em outra thread).</summary>
    public Retrato? Pegar(string caminho, int px, Action pronto)
    {
        var chave = (caminho.ToUpperInvariant(), px);
        lock (_trava)
        {
            if (_cache.TryGetValue(chave, out var r)) return r;
            if (_fechada || !_pedidos.Add(chave)) return null;
            _fila.Add((caminho, px, pronto));
            if (_thread == null)
            {
                _thread = new Thread(Rodar) { IsBackground = true, Name = "prateleira: miniaturas" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }
        return null;
    }

    /// <summary>Esquece o caminho (o arquivo mudou, voltou a existir ou saiu da prateleira).</summary>
    public void Esquecer(string caminho)
    {
        var k = caminho.ToUpperInvariant();
        lock (_trava)
        {
            foreach (var c in new List<(string, int)>(_cache.Keys))
                if (c.Item1 == k) _cache.Remove(c);
        }
    }

    public void EsquecerTudo()
    {
        lock (_trava) _cache.Clear();
    }

    private void Rodar()
    {
        while (true)
        {
            (string Caminho, int Px, Action Pronto) p;
            try
            {
                if (!_fila.TryTake(out p, 30_000))
                {
                    lock (_trava)
                    {
                        if (_fila.Count > 0) continue;
                        _thread = null;
                        return;
                    }
                }
            }
            catch (ObjectDisposedException) { return; }
            catch (InvalidOperationException) { return; }
            Retrato? r = null;
            try { r = DoShell(p.Caminho, p.Px); }
            catch (Exception e) { Log.Escrever("prateleira: miniatura: " + e.Message); }
            var chave = (p.Caminho.ToUpperInvariant(), p.Px);
            lock (_trava)
            {
                _pedidos.Remove(chave);
                if (_fechada) return;
                _cache[chave] = r;
            }
            try { p.Pronto(); } catch (Exception e) { Log.Escrever("prateleira: miniatura pronta: " + e.Message); }
        }
    }

    public void Dispose()
    {
        lock (_trava)
        {
            _fechada = true;
            _cache.Clear();
            _pedidos.Clear();
        }
        // a thread pode estar presa num handler lento: não espera por ela (é de fundo e sai ao ver a fila fechada)
        try { _fila.CompleteAdding(); } catch { }
    }

    // ------------------------------------------------------------------ shell

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Tamanho tamanho, int flags, out IntPtr hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Tamanho { public int Cx, Cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x01;
    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string caminho, IntPtr bc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int tamanho, out BITMAP b);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint inicio, uint linhas, byte[] bits, ref BITMAPINFOHEADER info, uint uso);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    /// <summary>Miniatura do shell reduzida para caber em px x px (BGRA pré-multiplicado).</summary>
    public static Retrato? DoShell(string caminho, int px)
    {
        if (px < 4) return null;
        var iid = IID_IShellItemImageFactory;
        if (SHCreateItemFromParsingName(caminho, IntPtr.Zero, ref iid, out var fabrica) != 0 || fabrica == null) return null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            if (fabrica.GetImage(new Tamanho { Cx = px, Cy = px }, SIIGBF_BIGGERSIZEOK, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            var (dados, l, a) = Pixels(hbm);
            if (dados == null) return null;
            AjustarAlfa(dados);
            if (l <= px && a <= px) return new Retrato(dados, l, a);
            return Reduzir(dados, l, a, px);
        }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            try { Marshal.ReleaseComObject(fabrica); } catch { }
        }
    }

    private static (byte[]? Dados, int L, int A) Pixels(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var b) == 0 || b.bmWidth <= 0 || b.bmHeight == 0) return (null, 0, 0);
        int l = b.bmWidth, a = Math.Abs(b.bmHeight);
        if ((long)l * a > 4096L * 4096L) return (null, 0, 0);
        var info = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = l, biHeight = -a, biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        var dados = new byte[l * a * 4];
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbm, 0, (uint)a, dados, ref info, 0) == 0) return (null, 0, 0);
        }
        finally { DeleteDC(dc); }
        return (dados, l, a);
    }

    /// <summary>
    /// O shell devolve alfa pré-multiplicado nos ícones, mas miniatura de foto às vezes vem com o alfa todo zero (não
    /// tem alfa nenhum) e alguns handlers mandam alfa sem pré-multiplicar. Acerta os três casos.
    /// </summary>
    public static void AjustarAlfa(byte[] bgra)
    {
        bool algumAlfa = false, cruzou = false;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte a = bgra[i + 3];
            if (a != 0) algumAlfa = true;
            if (bgra[i] > a || bgra[i + 1] > a || bgra[i + 2] > a) cruzou = true;
        }
        if (!algumAlfa)
        {
            for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
            return;
        }
        if (!cruzou) return;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            for (int c = 0; c < 3; c++) bgra[i + c] = (byte)(bgra[i + c] * a / 255);
        }
    }

    private static unsafe Retrato? Reduzir(byte[] dados, int l, int a, int px)
    {
        fixed (byte* d = dados)
        {
            using var bmp = new System.Drawing.Bitmap(l, a, l * 4, System.Drawing.Imaging.PixelFormat.Format32bppPArgb, (IntPtr)d);
            return Retrato.DeBitmap(bmp, px, px, recortar: false);
        }
    }
}
