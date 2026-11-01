using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Ilha.Volume;

/// <summary>
/// O ícone de cada app do mixer, tirado do .exe no disco (SHGetFileInfo numa thread STA própria: nada é lido do
/// processo) e reduzido uma vez para o tamanho exato em pixels. Cache por caminho e tamanho; a interface pede, recebe
/// null até ficar pronto e é avisada por `Chegou`. A thread só nasce no primeiro pedido e fica parada na fila.
/// </summary>
internal sealed class IconesDeApps : IDisposable
{
    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string caminho, uint atributos, ref SHFILEINFOW info, uint tamanho, uint marcas);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icone);

    private readonly Action<Action> _despachar;
    // só na thread da interface
    private readonly Dictionary<(string, int), byte[]?> _cache = new();
    private readonly HashSet<(string, int)> _pedidos = new();
    private BlockingCollection<(string Caminho, int Lado)>? _fila;

    /// <summary>Um ícone pedido ficou pronto (thread da interface).</summary>
    public event Action? Chegou;

    public IconesDeApps(Action<Action> despachar) => _despachar = despachar;

    /// <summary>Os pixels (BGRA pré-multiplicado, lado×lado) ou null enquanto não estão prontos ou se o arquivo não tem ícone.</summary>
    public byte[]? Pixels(string caminho, int lado)
    {
        var k = (caminho.ToLowerInvariant(), lado);
        if (_cache.TryGetValue(k, out var px)) return px;
        if (!_pedidos.Add(k)) return null;
        if (_fila == null)
        {
            _fila = new BlockingCollection<(string, int)>();
            var fila = _fila;
            var t = new Thread(() => Laco(fila)) { IsBackground = true, Name = "ilha-volume-icones" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
        try { _fila.Add((caminho, lado)); } catch { }
        return null;
    }

    private void Laco(BlockingCollection<(string Caminho, int Lado)> fila)
    {
        try
        {
            foreach (var (caminho, lado) in fila.GetConsumingEnumerable())
            {
                byte[]? px = null;
                try { px = Extrair(caminho, lado); }
                catch (Exception e) { Log.Escrever("mixer: ícone: " + e.Message); }
                var k = (caminho.ToLowerInvariant(), lado);
                _despachar(() =>
                {
                    _cache[k] = px;
                    _pedidos.Remove(k);
                    try { Chegou?.Invoke(); } catch (Exception e) { Log.Escrever("mixer: ícone pronto: " + e); }
                });
            }
        }
        catch { }
    }

    private static byte[]? Extrair(string caminho, int lado)
    {
        var info = new SHFILEINFOW();
        if (SHGetFileInfoW(caminho, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), SHGFI_ICON | SHGFI_LARGEICON) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            using var ico = System.Drawing.Icon.FromHandle(info.hIcon);
            using var original = ico.ToBitmap();
            using var reduzida = new System.Drawing.Bitmap(lado, lado, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = System.Drawing.Graphics.FromImage(reduzida))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.Clear(System.Drawing.Color.Transparent);
                g.DrawImage(original, 0, 0, lado, lado);
            }
            var bd = reduzida.LockBits(new System.Drawing.Rectangle(0, 0, lado, lado), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var dados = new byte[lado * lado * 4];
            try
            {
                for (int y = 0; y < lado; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, dados, y * lado * 4, lado * 4);
            }
            finally { reduzida.UnlockBits(bd); }
            return dados;
        }
        finally { DestroyIcon(info.hIcon); }
    }

    /// <summary>Desenha os pixels 1:1 (lado em pixels) com o canto em (x, y) DIP.</summary>
    public static unsafe void Desenhar(ID2D1DeviceContext dc, Palco p, byte[] px, int lado, float x, float y)
    {
        var props = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96f * p.Escala, 96f * p.Escala, BitmapOptions.None);
        float e = p.Escala;
        float x0 = MathF.Round(x * e) / e, y0 = MathF.Round(y * e) / e, l = lado / e;
        fixed (byte* d = px)
        {
            using var bmp = dc.CreateBitmap(new SizeI(lado, lado), (IntPtr)d, (uint)(lado * 4), props);
            dc.DrawBitmap(bmp, 1f, BitmapInterpolationMode.NearestNeighbor, new Rect(x0, y0, l, l));
        }
    }

    public void Dispose()
    {
        try { _fila?.CompleteAdding(); } catch { }
    }
}
