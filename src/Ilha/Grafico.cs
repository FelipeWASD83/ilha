using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace Ilha;

/// <summary>
/// O compositor do sistema (Windows.UI.Composition, roda dentro do DWM) e o Direct2D que desenha texto e
/// ícones nas superfícies dele. A forma e as molas vivem no DWM; o app só desenha quando o conteúdo muda.
/// </summary>
internal sealed unsafe class Grafico : IDisposable
{
    private static readonly Guid IID_ICompositorDesktopInterop = new("29E691FA-4567-4DCA-B319-D0F207EB6807");
    private static readonly Guid IID_ICompositorInterop = new("25297D5C-3AD4-4C9C-B5CF-E36A38512330");
    private static readonly Guid IID_ICompositionDrawingSurfaceInterop = new("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE");
    private static readonly Guid IID_ICompositionGraphicsDeviceInterop = new("A116FF71-F8BF-4C8A-9C98-70779A32A9C8");
    private static readonly Guid IID_ID2D1DeviceContext = new("E8F7FE7A-191C-466D-AD95-975678BDA998");

    private static IntPtr _fila;

    public static bool UsarGpu;
    public Compositor Compositor { get; }
    public float Escala { get; set; } = 1f;
    public ID2D1Factory1 Fabrica { get; }
    public Vortice.DirectWrite.IDWriteFactory Escrita { get; }

    private ID3D11Device? _d3d;
    private ID2D1Device? _d2d;
    private readonly CompositionGraphicsDevice _dispositivo;
    private readonly List<WeakReference<Superficie>> _superficies = new();

    /// <summary>Precisa existir na thread da interface antes de qualquer Compositor.</summary>
    public static void CriarFila()
    {
        if (_fila != IntPtr.Zero) return;
        var o = new Nativo.DispatcherQueueOptions { dwSize = sizeof(Nativo.DispatcherQueueOptions), threadType = 2, apartmentType = 2 };
        int hr = Nativo.CreateDispatcherQueueController(o, out _fila);
        if (hr != 0) throw new COMException("CreateDispatcherQueueController", hr);
    }

    public Grafico()
    {
        CriarFila();
        Compositor = new Compositor();
        Fabrica = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded);
        Escrita = Vortice.DirectWrite.DWrite.DWriteCreateFactory<Vortice.DirectWrite.IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        CriarDispositivo();

        IntPtr comp = ((WinRT.IWinRTObject)Compositor).NativeObject.ThisPtr;
        Marshal.QueryInterface(comp, in IID_ICompositorInterop, out IntPtr interop);
        try
        {
            IntPtr gd;
            var criar = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)(*(IntPtr**)interop)[5];
            int hr = criar(interop, _d2d!.NativePointer, &gd);
            if (hr != 0) throw new COMException("CreateGraphicsDevice", hr);
            _dispositivo = CompositionGraphicsDevice.FromAbi(gd);
            Marshal.Release(gd);
        }
        finally { Marshal.Release(interop); }
        _dispositivo.RenderingDeviceReplaced += (_, _) => Programa.NaInterface(RedesenharTudo);
    }

    private void CriarDispositivo()
    {
        _d2d?.Dispose();
        _d3d?.Dispose();
        var niveis = new[] { Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1, Vortice.Direct3D.FeatureLevel.Level_10_0 };
        ID3D11Device? d3d = null;
        // padrão: Direct3D por software (WARP). O desenho é só texto e ícone, raro e pequeno; as molas rodam no DWM.
        // Com a GPU, o driver da NVIDIA sozinho custava ~110 MB de RAM privada a este processo.
        var ordem = UsarGpu ? new[] { DriverType.Hardware, DriverType.Warp } : new[] { DriverType.Warp, DriverType.Hardware };
        foreach (var tipo in ordem)
        {
            try { d3d = D3D11.D3D11CreateDevice(tipo, DeviceCreationFlags.BgraSupport, niveis); break; }
            catch (Exception e) { Log.Escrever($"Direct3D ({tipo}) falhou: {e.Message}"); }
        }
        _d3d = d3d ?? throw new InvalidOperationException("sem Direct3D 11");
        using var dxgi = _d3d.QueryInterface<IDXGIDevice>();
        _d2d = Fabrica.CreateDevice(dxgi);
    }

    /// <summary>Recria o Direct3D depois de reset do driver; o compositor avisa e tudo é redesenhado.</summary>
    private void RecriarDispositivo()
    {
        Log.Escrever("dispositivo gráfico perdido: recriando");
        CriarDispositivo();
        IntPtr gd = ((WinRT.IWinRTObject)_dispositivo).NativeObject.ThisPtr;
        Marshal.QueryInterface(gd, in IID_ICompositionGraphicsDeviceInterop, out IntPtr interop);
        try
        {
            var trocar = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(IntPtr**)interop)[4];
            trocar(interop, _d2d!.NativePointer);
        }
        finally { Marshal.Release(interop); }
    }

    public DesktopWindowTarget Alvo(IntPtr hwnd)
    {
        IntPtr comp = ((WinRT.IWinRTObject)Compositor).NativeObject.ThisPtr;
        Marshal.QueryInterface(comp, in IID_ICompositorDesktopInterop, out IntPtr interop);
        try
        {
            IntPtr alvo;
            var criar = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)(*(IntPtr**)interop)[3];
            int hr = criar(interop, hwnd, 1, &alvo);
            if (hr != 0) throw new COMException("CreateDesktopWindowTarget", hr);
            var t = DesktopWindowTarget.FromAbi(alvo);
            Marshal.Release(alvo);
            return t;
        }
        finally { Marshal.Release(interop); }
    }

    /// <summary>Superfície desenhada pelo Direct2D, medida em DIP. Redesenha sozinha se o dispositivo trocar.</summary>
    public Superficie NovaSuperficie(Vector2 tamanhoDip, Action<ID2D1DeviceContext, Vector2> desenho, bool emPixels = false)
    {
        var s = new Superficie(this, tamanhoDip, desenho, emPixels);
        _superficies.RemoveAll(w => !w.TryGetTarget(out _));
        _superficies.Add(new WeakReference<Superficie>(s));
        s.Redesenhar();
        return s;
    }

    internal CompositionDrawingSurface CriarSuperficie(Vector2 px) =>
        _dispositivo.CreateDrawingSurface(new Windows.Foundation.Size(Math.Max(1, px.X), Math.Max(1, px.Y)),
            DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);

    internal bool Desenhar(CompositionDrawingSurface s, Vector2 tamanhoDip, Action<ID2D1DeviceContext, Vector2> desenho, float escala)
    {
        IntPtr ps = ((WinRT.IWinRTObject)s).NativeObject.ThisPtr;
        Marshal.QueryInterface(ps, in IID_ICompositionDrawingSurfaceInterop, out IntPtr interop);
        try
        {
            var vt = *(IntPtr**)interop;
            var comecar = (delegate* unmanaged[Stdcall]<IntPtr, Nativo.RECT*, Guid*, IntPtr*, Nativo.POINT*, int>)vt[3];
            var terminar = (delegate* unmanaged[Stdcall]<IntPtr, int>)vt[4];
            Guid iid = IID_ID2D1DeviceContext;
            IntPtr pdc;
            Nativo.POINT off;
            int hr = comecar(interop, null, &iid, &pdc, &off);
            if (hr == unchecked((int)0x887A0005) || hr == unchecked((int)0x887A0007))
            {
                RecriarDispositivo();
                return false;
            }
            if (hr != 0) { Log.Escrever($"BeginDraw falhou 0x{hr:X8}"); return false; }
            using (var dc = new ID2D1DeviceContext(pdc))
            {
                dc.SetDpi(96f * escala, 96f * escala);
                dc.Transform = Matrix3x2.CreateTranslation(off.X / escala, off.Y / escala);
                dc.Clear(new Color4(0, 0, 0, 0));
                try { desenho(dc, tamanhoDip); }
                catch (Exception e) { Log.Escrever("desenho falhou: " + e); }
            }
            terminar(interop);
            return true;
        }
        finally { Marshal.Release(interop); }
    }

    private void RedesenharTudo()
    {
        foreach (var w in _superficies.ToArray())
            if (w.TryGetTarget(out var s)) s.Redesenhar();
    }

    public void Dispose()
    {
        _dispositivo.Dispose();
        Compositor.Dispose();
        _d2d?.Dispose();
        _d3d?.Dispose();
        Escrita.Dispose();
        Fabrica.Dispose();
    }
}

internal sealed class Superficie
{
    private readonly Grafico _g;
    private Action<ID2D1DeviceContext, Vector2> _desenho;
    public CompositionDrawingSurface Superf { get; private set; }
    public Vector2 TamanhoDip { get; private set; }
    public CompositionSurfaceBrush Pincel { get; }

    /// <summary>Em pixels: tamanho e desenho sem a escala do monitor (as faixas de quadros já vêm no tamanho exato).</summary>
    public bool EmPixels { get; }

    public Superficie(Grafico g, Vector2 tamanhoDip, Action<ID2D1DeviceContext, Vector2> desenho, bool emPixels = false)
    {
        _g = g;
        _desenho = desenho;
        EmPixels = emPixels;
        TamanhoDip = tamanhoDip;
        Superf = g.CriarSuperficie(Px(tamanhoDip));
        Pincel = g.Compositor.CreateSurfaceBrush(Superf);
        Pincel.Stretch = CompositionStretch.Fill;
    }

    private float E => EmPixels ? 1f : _g.Escala;
    private Vector2 Px(Vector2 dip) => new(MathF.Ceiling(dip.X * E), MathF.Ceiling(dip.Y * E));
    public Vector2 TamanhoPx => Px(TamanhoDip);

    public void Redesenhar() => _g.Desenhar(Superf, TamanhoDip, _desenho, E);

    public void Trocar(Action<ID2D1DeviceContext, Vector2> desenho)
    {
        _desenho = desenho;
        Redesenhar();
    }

    public void Dispose()
    {
        Pincel.Dispose();
        Superf.Dispose();
    }

    public void Redimensionar(Vector2 tamanhoDip)
    {
        if (tamanhoDip == TamanhoDip) return;
        TamanhoDip = tamanhoDip;
        Superf.Resize(new Windows.Graphics.SizeInt32 { Width = (int)Px(tamanhoDip).X, Height = (int)Px(tamanhoDip).Y });
        Redesenhar();
    }
}
