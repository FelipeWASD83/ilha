using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace Ilha.Brilho;

/// <summary>
/// The brightness of the main monitor (the one with the island) as a tall slider in the menu, next to the sound one,
/// like the iPhone's. It talks to the monitor over DDC/CI (VCP code 0x10, the same thing its own menu changes): each
/// call takes tens of milliseconds, so everything goes through one thread and a drag only sends the newest value. A
/// monitor without DDC/CI (or with it turned off in its menu) gets no tile.
/// </summary>
[Modulo("brilho", "Brilho do monitor", English = "Monitor brightness")]
internal sealed class ModuloBrilho : Modulo
{
    private Ddc? _ddc;
    private readonly MenuSlider _slider = new();
    private float _nivel = -1;
    private int _roda;
    private bool _noMenu, _parado;

    private static ModuloBrilho? _atual;

    /// <summary>The main monitor's brightness (0 to 1) as last read, null when the module is off or has no reading.</summary>
    internal static float? NivelAtual => _atual is { _parado: false, _nivel: >= 0 } m ? m._nivel : null;

    /// <summary>Sets the brightness from another module (the focus modes), on the interface thread.</summary>
    internal static bool DefinirDeFora(float nivel)
    {
        if (_atual is not { _parado: false } m || m._nivel < 0) return false;
        m.Definir(nivel);
        return true;
    }

    protected override void Iniciar(JsonObject cfg)
    {
        _atual = this;
        _ddc = new Ddc(nivel => S.NaInterface(() => Leu(nivel)));
        _ddc.Iniciar();
    }

    private void Leu(float? nivel)
    {
        if (_parado) return;
        if (nivel is not float n)
        {
            if (_noMenu) { S.Menu.Remover("brilho"); _noMenu = false; }
            return;
        }
        // a value the island itself is still sending wins over an older reading
        if (!_slider.Ativo) _nivel = n;
        if (_noMenu) { S.Menu.Redesenhar(); return; }
        _noMenu = true;
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "brilho", Nome = Lang.T("Brilho", "Brightness"), Ordem = 72, Rows = 2,
            // a tap reads the monitor again (its own buttons may have changed it)
            Abrir = () => _ddc?.Ler(),
            Draw = (dc, p, r) => MenuSlider.Draw(dc, p, r, MathF.Max(0, _nivel), false,
                (d, pos, cor) => Sol(d, p, pos, 26, cor), _nivel < 0 ? "–" : $"{MathF.Round(_nivel * 100):0}%"),
            Drag = (pos, tamanho, soltou) =>
            {
                if (_nivel < 0) return;
                if (_slider.Drag(pos, tamanho.Height, soltou, _nivel) is float novo) Definir(novo);
            },
            Wheel = Rolar,
        });
    }

    private void Definir(float nivel)
    {
        _nivel = Math.Clamp(nivel, 0, 1);
        _ddc?.Definir(_nivel);
        S.Menu.Redesenhar();
    }

    /// <summary>5% per notch, on a grid of 5 (like the sound's wheel).</summary>
    private bool Rolar(int delta)
    {
        if (_nivel < 0) return false;
        _roda += delta;
        int dentes = _roda / 120;
        if (dentes == 0) return true;
        _roda -= dentes * 120;
        Definir(Volume.Logica.Rolar(_nivel, dentes * 120, 5));
        return true;
    }

    /// <summary>A sun: a disc and eight short rays, in a square of `lado` from `pos`.</summary>
    internal static void Sol(Vortice.Direct2D1.ID2D1DeviceContext dc, Palco p, Vector2 pos, float lado, Vortice.Mathematics.Color4 cor)
    {
        var c = pos + new Vector2(lado / 2, lado / 2);
        float u = lado / 24f;
        Icone.Circulo(dc, c, 4.6f * u, cor);
        for (int i = 0; i < 8; i++)
        {
            float a = i * MathF.PI / 4;
            var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
            Icone.Linha(dc, c + d * 7.6f * u, c + d * 10.4f * u, 2f * u, cor, p.G.Fabrica);
        }
    }

    public override void Parar()
    {
        _parado = true;
        _ddc?.Dispose();
        _ddc = null;
        if (_noMenu) S.Menu.Remover("brilho");
        _noMenu = false;
    }

    public override string Autoteste()
    {
        var r = new StringBuilder();
        void Conferir(string nome, bool ok, string detalhe = "") =>
            r.AppendLine((ok ? "ok      " : "FALHOU  ") + nome + (detalhe.Length > 0 ? $" ({detalhe})" : ""));
        Conferir("nível pela resposta do monitor (atual e máximo)", Ddc.Nivel(50, 100) == 0.5f && Ddc.Nivel(0, 100) == 0 && Ddc.Nivel(30, 0) == null && Ddc.Nivel(120, 100) == 1f);
        Conferir("valor para o monitor (arredonda e prende no máximo)", Ddc.Valor(0.5f, 100) == 50 && Ddc.Valor(0.333f, 100) == 33 && Ddc.Valor(1.4f, 100) == 100 && Ddc.Valor(-1, 100) == 0 && Ddc.Valor(0.5f, 255) == 128);
        var s = new MenuSlider();
        Conferir("barra: um toque não muda nada", s.Drag(new Vector2(40, 100), 180, false, 0.5f) == null && s.Drag(new Vector2(40, 101), 180, true, 0.5f) == null);
        var subiu = s.Drag(new Vector2(40, 100), 180, false, 0.5f) ?? s.Drag(new Vector2(40, 64), 180, false, 0.5f);
        s.Drag(new Vector2(40, 64), 180, true, 0.5f);
        Conferir("barra: arrastar 36 de 180 sobe 20%", subiu is float v && MathF.Abs(v - 0.7f) < 0.001f, subiu?.ToString() ?? "null");
        // probe: only reads the monitor (never writes in the test)
        var lido = Ddc.LerAgora(out string descricao);
        r.AppendLine(lido is float l ? $"ok      sonda: monitor principal por DDC/CI ({descricao}): {MathF.Round(l * 100):0}%"
            : $"info    sonda: monitor principal sem resposta por DDC/CI ({descricao}): o menu fica sem o brilho");
        return r.ToString().TrimEnd();
    }
}

/// <summary>DDC/CI with the main monitor, on its own thread. `leu` gets the level (0 to 1) or null when it does not answer.</summary>
internal sealed class Ddc : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct PONTO { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Descricao;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(PONTO pt, uint flags);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint n);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint n, [Out] PHYSICAL_MONITOR[] lista);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool DestroyPhysicalMonitors(uint n, PHYSICAL_MONITOR[] lista);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte codigo, IntPtr tipo, out uint atual, out uint maximo);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool SetVCPFeature(IntPtr monitor, byte codigo, uint valor);

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const byte VCP_BRILHO = 0x10;

    private readonly Action<float?> _leu;
    private readonly AutoResetEvent _acorda = new(false);
    private int _pedido = -1, _ler;
    private volatile bool _parado;

    public Ddc(Action<float?> leu) => _leu = leu;

    public void Iniciar()
    {
        _ler = 1;
        new Thread(Laco) { IsBackground = true, Name = "ilha-brilho" }.Start();
        _acorda.Set();
    }

    public void Ler() { Volatile.Write(ref _ler, 1); _acorda.Set(); }

    /// <summary>Only the newest value goes to the monitor (a drag sends many).</summary>
    public void Definir(float nivel)
    {
        Volatile.Write(ref _pedido, (int)MathF.Round(Math.Clamp(nivel, 0, 1) * 10000));
        _acorda.Set();
    }

    internal static float? Nivel(uint atual, uint maximo) => maximo == 0 ? null : Math.Clamp((float)atual / maximo, 0, 1);

    internal static uint Valor(float nivel, uint maximo) => (uint)MathF.Round(Math.Clamp(nivel, 0, 1) * maximo, MidpointRounding.AwayFromZero);

    private void Laco()
    {
        PHYSICAL_MONITOR[]? monitores = null;
        uint maximo = 0;
        try
        {
            while (!_parado)
            {
                _acorda.WaitOne();
                if (_parado) break;
                if (monitores == null && !Abrir(out monitores)) { _leu(null); continue; }
                var h = monitores![0].Handle;
                int pedido = Interlocked.Exchange(ref _pedido, -1);
                if (pedido >= 0 && maximo > 0)
                {
                    if (!SetVCPFeature(h, VCP_BRILHO, Valor(pedido / 10000f, maximo)))
                    {
                        Log.Escrever($"brilho: o monitor não aceitou o valor (erro {Marshal.GetLastWin32Error()})");
                        Fechar(ref monitores);
                    }
                    // a drag goes on (the next value is already waiting), or a read came in meanwhile
                    if (Volatile.Read(ref _pedido) >= 0 || Volatile.Read(ref _ler) == 1) _acorda.Set();
                    continue;
                }
                if (Interlocked.Exchange(ref _ler, 0) == 1)
                {
                    if (GetVCPFeatureAndVCPFeatureReply(h, VCP_BRILHO, IntPtr.Zero, out uint atual, out maximo) && Nivel(atual, maximo) is float n) _leu(n);
                    else
                    {
                        Log.Escrever($"brilho: o monitor não respondeu por DDC/CI (erro {Marshal.GetLastWin32Error()})");
                        maximo = 0;
                        Fechar(ref monitores);
                        _leu(null);
                    }
                }
            }
        }
        catch (Exception e) { Log.Escrever("brilho: " + e.Message); }
        finally { Fechar(ref monitores); _acorda.Dispose(); }
    }

    private static bool Abrir(out PHYSICAL_MONITOR[]? lista)
    {
        lista = null;
        var mon = MonitorFromPoint(new PONTO(), MONITOR_DEFAULTTOPRIMARY);
        if (mon == IntPtr.Zero || !GetNumberOfPhysicalMonitorsFromHMONITOR(mon, out uint n) || n == 0) return false;
        var l = new PHYSICAL_MONITOR[n];
        if (!GetPhysicalMonitorsFromHMONITOR(mon, n, l)) return false;
        lista = l;
        return true;
    }

    private static void Fechar(ref PHYSICAL_MONITOR[]? lista)
    {
        if (lista != null) try { DestroyPhysicalMonitors((uint)lista.Length, lista); } catch { }
        lista = null;
    }

    /// <summary>One read on the calling thread (the self test's probe); never writes.</summary>
    internal static float? LerAgora(out string descricao)
    {
        descricao = "?";
        if (!Abrir(out var l)) return null;
        try
        {
            descricao = l![0].Descricao;
            return GetVCPFeatureAndVCPFeatureReply(l[0].Handle, VCP_BRILHO, IntPtr.Zero, out uint atual, out uint maximo) ? Nivel(atual, maximo) : null;
        }
        finally { Fechar(ref l); }
    }

    public void Dispose()
    {
        _parado = true;
        try { _acorda.Set(); } catch { }
    }
}
