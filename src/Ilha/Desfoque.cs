using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Graphics.Effects;

namespace Ilha;

/// <summary>
/// Descrição do desfoque gaussiano do Direct2D para o compositor, escrita à mão (o Win2D atual é só para
/// WinUI 3). O compositor pede IGraphicsEffectD2D1Interop por QueryInterface e lê o efeito por essa vtable;
/// "BlurAmount" vira propriedade animável dentro do DWM.
/// </summary>
internal sealed unsafe class Desfoque : IGraphicsEffect, IGraphicsEffectSource, ICustomQueryInterface
{
    public string Name { get; set; } = "Blur";
    public float Quantidade = 0f;
    public IGraphicsEffectSource Fonte;

    public Desfoque(IGraphicsEffectSource fonte) => Fonte = fonte;

    private static readonly Guid IID_D2D1Interop = new("2FC57384-A068-44D7-A331-30982FCF7177");
    private static readonly Guid CLSID_GaussianBlur = new("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5");
    private static readonly Guid IID_IPropertyValue = new("4BD682DD-7554-40E9-9A9B-82654EDE7E62");
    private static readonly Guid IID_IGraphicsEffectSource = new("2D8F9DDC-4339-4EB9-9216-F9DEB75658A2");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    private struct Obj { public IntPtr* vtbl; public GCHandle self; public int refs; }
    private static readonly IntPtr* Vtbl;

    static Desfoque()
    {
        Vtbl = (IntPtr*)NativeMemory.AllocZeroed((nuint)(sizeof(IntPtr) * 9));
        Vtbl[0] = (IntPtr)(delegate* unmanaged[Stdcall]<Obj*, Guid*, IntPtr*, int>)&QI;
        Vtbl[1] = (IntPtr)(delegate* unmanaged[Stdcall]<Obj*, uint>)&AddRef;
        Vtbl[2] = (IntPtr)(delegate* unmanaged[Stdcall]<Obj*, uint>)&Release;
        Vtbl[3] = (IntPtr)(delegate* unmanaged[Stdcall]<Obj*, Guid*, int>)&GetEffectId;
        Vtbl[4] = (IntPtr)(delegate* unmanaged[Stdcall]<Obj*, char*, uint*, int*, int>)&GetNamedPropertyMapping;
        Vtbl[5] = (IntPtr)(delegate* unmanaged[Stdcall]<Obj*, uint*, int>)&GetPropertyCount;
        Vtbl[6] = (IntPtr)(delegate* unmanaged[Stdcall]<Obj*, uint, IntPtr*, int>)&GetProperty;
        Vtbl[7] = (IntPtr)(delegate* unmanaged[Stdcall]<Obj*, uint, IntPtr*, int>)&GetSource;
        Vtbl[8] = (IntPtr)(delegate* unmanaged[Stdcall]<Obj*, uint*, int>)&GetSourceCount;
    }

    private static Desfoque Self(Obj* o) => (Desfoque)o->self.Target!;

    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv)
    {
        if (iid == IID_D2D1Interop)
        {
            var o = (Obj*)NativeMemory.AllocZeroed((nuint)sizeof(Obj));
            o->vtbl = Vtbl;
            o->self = GCHandle.Alloc(this);
            o->refs = 1;
            ppv = (IntPtr)o;
            return CustomQueryInterfaceResult.Handled;
        }
        ppv = IntPtr.Zero;
        return CustomQueryInterfaceResult.NotHandled;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QI(Obj* o, Guid* iid, IntPtr* ppv)
    {
        if (*iid == IID_D2D1Interop || *iid == IID_IUnknown) { o->refs++; *ppv = (IntPtr)o; return 0; }
        *ppv = IntPtr.Zero;
        return unchecked((int)0x80004002);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint AddRef(Obj* o) => (uint)++o->refs;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint Release(Obj* o)
    {
        int r = --o->refs;
        if (r == 0) { o->self.Free(); NativeMemory.Free(o); }
        return (uint)r;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetEffectId(Obj* o, Guid* id) { *id = CLSID_GaussianBlur; return 0; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetNamedPropertyMapping(Obj* o, char* name, uint* index, int* mapping)
    {
        // BlurAmount = D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION, mapeamento direto
        if (new string(name) == "BlurAmount") { *index = 0; *mapping = 1; return 0; }
        return unchecked((int)0x80070057);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetPropertyCount(Obj* o, uint* c) { *c = 3; return 0; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetProperty(Obj* o, uint index, IntPtr* value)
    {
        try
        {
            object caixa = index switch
            {
                0 => Windows.Foundation.PropertyValue.CreateSingle(Self(o).Quantidade),
                1 => Windows.Foundation.PropertyValue.CreateUInt32(1), // otimização equilibrada
                _ => Windows.Foundation.PropertyValue.CreateUInt32(0), // borda suave
            };
            IntPtr insp = WinRT.MarshalInspectable<object>.FromManaged(caixa);
            Guid iid = IID_IPropertyValue;
            int hr = Marshal.QueryInterface(insp, in iid, out *value);
            Marshal.Release(insp);
            return hr;
        }
        catch { return unchecked((int)0x80004005); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetSource(Obj* o, uint index, IntPtr* src)
    {
        try
        {
            IntPtr p = ((WinRT.IWinRTObject)Self(o).Fonte).NativeObject.ThisPtr;
            Guid iid = IID_IGraphicsEffectSource;
            return Marshal.QueryInterface(p, in iid, out *src);
        }
        catch { return unchecked((int)0x80004005); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetSourceCount(Obj* o, uint* c) { *c = 1; return 0; }
}
