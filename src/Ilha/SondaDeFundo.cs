using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ilha;

/// <summary>
/// Lê a luminância do que está em volta da ilha: um BitBlt só do retângulo que cobre as faixas de leitura (fora da
/// pílula e da sombra, porque atrás dela a própria ilha cobre). Nada de captura de janela nem de GPU. Mede quanto
/// cada leitura custa, para o log.
/// </summary>
internal sealed class SondaDeFundo : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bi, uint uso, out IntPtr bits, IntPtr secao, uint off);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int cores;
    }

    private const int SRCCOPY = 0x00CC0020;
    private IntPtr _mem, _bmp, _bits;
    private int _l, _a;
    private readonly Stopwatch _relogio = new();
    private int _leituras;
    private double _msTotal;

    /// <summary>Quantas leituras e quanto tempo médio cada uma levou desde a última pergunta (para o log).</summary>
    public (int leituras, double msMedio) Custo()
    {
        var r = (_leituras, _leituras == 0 ? 0 : _msTotal / _leituras);
        _leituras = 0;
        _msTotal = 0;
        return r;
    }

    /// <summary>Luminância média (0 a 1) das faixas dadas, em pixels de tela. null se não deu para ler.</summary>
    public double? Ler(System.Drawing.Rectangle[] faixas)
    {
        if (faixas.Length == 0) return null;
        var caixa = faixas[0];
        foreach (var f in faixas) caixa = System.Drawing.Rectangle.Union(caixa, f);
        if (caixa.Width <= 0 || caixa.Height <= 0 || caixa.Width > 4000 || caixa.Height > 2000) return null;
        _relogio.Restart();
        var tela = GetDC(IntPtr.Zero);
        if (tela == IntPtr.Zero) return null;
        try
        {
            if (!Garantir(tela, caixa.Width, caixa.Height)) return null;
            if (!BitBlt(_mem, 0, 0, caixa.Width, caixa.Height, tela, caixa.X, caixa.Y, SRCCOPY)) return null;
            double soma = 0;
            long n = 0;
            unsafe
            {
                byte* p = (byte*)_bits;
                foreach (var f in faixas)
                {
                    // de 2 em 2 pixels basta para uma média
                    for (int y = f.Top - caixa.Top; y < f.Bottom - caixa.Top; y += 2)
                        for (int x = f.Left - caixa.Left; x < f.Right - caixa.Left; x += 2)
                        {
                            byte* px = p + (y * _l + x) * 4;
                            soma += 0.0722 * px[0] + 0.7152 * px[1] + 0.2126 * px[2];
                            n++;
                        }
                }
            }
            return n == 0 ? null : soma / n / 255.0;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, tela);
            _relogio.Stop();
            _leituras++;
            _msTotal += _relogio.Elapsed.TotalMilliseconds;
        }
    }

    private bool Garantir(IntPtr tela, int l, int a)
    {
        if (_bmp != IntPtr.Zero && l <= _l && a <= _a) return true;
        Soltar();
        _l = Math.Max(l, 64);
        _a = Math.Max(a, 64);
        var bi = new BITMAPINFO { biSize = 40, biWidth = _l, biHeight = -_a, biPlanes = 1, biBitCount = 32 };
        _mem = CreateCompatibleDC(tela);
        _bmp = CreateDIBSection(tela, ref bi, 0, out _bits, IntPtr.Zero, 0);
        if (_mem == IntPtr.Zero || _bmp == IntPtr.Zero) { Soltar(); return false; }
        SelectObject(_mem, _bmp);
        return true;
    }

    private void Soltar()
    {
        if (_mem != IntPtr.Zero) DeleteDC(_mem);
        if (_bmp != IntPtr.Zero) DeleteObject(_bmp);
        _mem = _bmp = _bits = IntPtr.Zero;
        _l = _a = 0;
    }

    public void Dispose() => Soltar();
}
