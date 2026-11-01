using System;
using System.Runtime.InteropServices;

namespace Ilha;

/// <summary>Quem recebe arquivos soltos em cima da ilha (a prateleira). Só um por vez.</summary>
internal interface IReceptorDeArquivos
{
    /// <summary>Um arraste com estes caminhos entrou na ilha. true = aceita (e mostra onde soltar).</summary>
    bool Entrou(string[] caminhos);
    void Saiu();
    void Soltou(string[] caminhos);
}

[ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDropTarget
{
    [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] object dados, int teclas, Nativo.POINT pt, ref int efeito);
    [PreserveSig] int DragOver(int teclas, Nativo.POINT pt, ref int efeito);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] object dados, int teclas, Nativo.POINT pt, ref int efeito);
}

/// <summary>
/// Alvo de soltar (OLE) da janela de toque. Só aceita arquivos e pastas de verdade (CF_HDROP) e responde "atalho"
/// ou "cópia", nunca "mover": com "mover", o Explorer apagaria o original depois de soltar.
/// </summary>
[ComVisible(true)]
internal sealed class AlvoDeSoltar : IDropTarget
{
    private const int NADA = 0, COPIAR = 1, LINK = 4;
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget alvo);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(IntPtr hwnd);

    private readonly Func<IReceptorDeArquivos?> _receptor;
    private string[]? _caminhos;
    private bool _aceito;

    /// <summary>Um arraste de arquivos está em cima da ilha agora (o botão segurado não é "clique fora").</summary>
    public bool EmCima { get; private set; }
    /// <summary>Quando o último arraste saiu ou soltou (Environment.TickCount64).</summary>
    public long Fim { get; private set; }

    private AlvoDeSoltar(Func<IReceptorDeArquivos?> receptor) => _receptor = receptor;

    public static AlvoDeSoltar? Registrar(IntPtr janela, Func<IReceptorDeArquivos?> receptor)
    {
        try
        {
            System.Windows.Forms.Application.OleRequired();
            var a = new AlvoDeSoltar(receptor);
            int hr = RegisterDragDrop(janela, a);
            if (hr != 0) { Log.Escrever($"soltar arquivos: RegisterDragDrop 0x{hr:X8}"); return null; }
            return a;
        }
        catch (Exception e) { Log.Escrever("soltar arquivos: " + e.Message); return null; }
    }

    public static void Revogar(IntPtr janela)
    {
        try { RevokeDragDrop(janela); } catch { }
    }

    private static string[]? Arquivos(object dados)
    {
        try
        {
            var d = new System.Windows.Forms.DataObject(dados);
            return d.TryGetData<string[]>(System.Windows.Forms.DataFormats.FileDrop, out var c) && c is { Length: > 0 } ? c : null;
        }
        catch { return null; }
    }

    private int Efeito(int permitido) => !_aceito ? NADA : (permitido & LINK) != 0 ? LINK : (permitido & COPIAR) != 0 ? COPIAR : NADA;

    public int DragEnter(object dados, int teclas, Nativo.POINT pt, ref int efeito)
    {
        _caminhos = Arquivos(dados);
        _aceito = false;
        EmCima = true;
        if (_caminhos != null && _receptor() is { } r)
            try { _aceito = r.Entrou(_caminhos); } catch (Exception e) { Log.Escrever("soltar arquivos (entrou): " + e); }
        efeito = Efeito(efeito);
        return 0;
    }

    public int DragOver(int teclas, Nativo.POINT pt, ref int efeito)
    {
        efeito = Efeito(efeito);
        return 0;
    }

    public int DragLeave()
    {
        EmCima = false;
        Fim = Environment.TickCount64;
        if (_aceito) try { _receptor()?.Saiu(); } catch (Exception e) { Log.Escrever("soltar arquivos (saiu): " + e); }
        _aceito = false;
        _caminhos = null;
        return 0;
    }

    public int Drop(object dados, int teclas, Nativo.POINT pt, ref int efeito)
    {
        var caminhos = Arquivos(dados) ?? _caminhos;
        efeito = Efeito(efeito);
        EmCima = false;
        Fim = Environment.TickCount64;
        if (_aceito && caminhos != null)
            try { _receptor()?.Soltou(caminhos); } catch (Exception e) { Log.Escrever("soltar arquivos (soltou): " + e); }
        _aceito = false;
        _caminhos = null;
        return 0;
    }
}
