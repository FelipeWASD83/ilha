using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Ilha.Extras;

namespace Ilha.Prateleira;

/// <summary>
/// Arrastar da prateleira para outro app. O pacote leva CF_HDROP (o que Explorer, Photoshop, navegador e Discord leem)
/// e o "Shell IDList Array" (o formato do shell que o Explorer também põe no arraste dele), para quem só confia no
/// arraste que parece vir do Explorer. Efeitos permitidos: cópia e atalho, nunca mover (o original fica onde está).
/// </summary>
internal sealed class ArrasteParaFora : IDisposable
{
    private Control? _origem;

    public bool Arrastando { get; private set; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string nome, IntPtr bc, out IntPtr pidl, uint pedir, out uint obtido);
    [DllImport("shell32.dll")] private static extern uint ILGetSize(IntPtr pidl);

    /// <summary>
    /// Os bytes do PIDL absoluto (null se o shell não reconheceu o caminho). Toca no disco (drive de rede caído prende
    /// por segundos): nunca na thread da interface. A atividade guarda o de cada item quando a conferência dele volta.
    /// </summary>
    public static byte[]? Pidl(string caminho)
    {
        if (SHParseDisplayName(caminho, IntPtr.Zero, out var p, 0, out _) != 0 || p == IntPtr.Zero) return null;
        try
        {
            int n = (int)ILGetSize(p);
            if (n < 2) return null;
            var b = new byte[n];
            Marshal.Copy(p, b, 0, n);
            return b;
        }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>
    /// O pacote do arraste, sem tocar no disco: `pidls` são os já prontos (null onde ainda não tem). A lista do shell só
    /// vai com todos: uma diferente do CF_HDROP confundiria quem lê as duas.
    /// </summary>
    public static DataObject Montar(IReadOnlyList<string> caminhos, IReadOnlyList<byte[]?> pidls)
    {
        var dados = new DataObject();
        var lista = new string[caminhos.Count];
        for (int i = 0; i < lista.Length; i++) lista[i] = caminhos[i];
        dados.SetData(DataFormats.FileDrop, lista);
        try
        {
            if (pidls.Count == caminhos.Count && pidls.All(p => p != null))
                dados.SetData("Shell IDList Array", false, new MemoryStream(LogicaDaPrateleira.MontarCida(pidls.Select(p => p!).ToList())));
        }
        catch (Exception e) { Log.Escrever("prateleira: lista do shell: " + e.Message); }
        return dados;
    }

    /// <summary>
    /// Roda o arraste (modal: volta quando a pessoa solta). Chamar fora do tratamento da mensagem do mouse
    /// (S.NaInterface) e depois de S.Ilha.SoltarArraste(), que solta a captura da janela de toque.
    /// </summary>
    public void Arrastar(IReadOnlyList<string> caminhos, IReadOnlyList<byte[]?> pidls, Retrato? fantasma)
    {
        if (caminhos.Count == 0 || Arrastando) return;
        _origem ??= new Control();
        _ = _origem.Handle;
        var dados = Montar(caminhos, pidls);
        using var imagem = fantasma == null ? null : Bitmap(fantasma);
        Arrastando = true;
        try
        {
            if (imagem != null)
                _origem.DoDragDrop(dados, DragDropEffects.Copy | DragDropEffects.Link, imagem, new System.Drawing.Point(imagem.Width / 2, imagem.Height / 2), false);
            else
                _origem.DoDragDrop(dados, DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch (Exception e) { Log.Escrever("prateleira: arraste: " + e.Message); }
        finally { Arrastando = false; }
    }

    private static System.Drawing.Bitmap? Bitmap(Retrato r)
    {
        try
        {
            var b = new System.Drawing.Bitmap(r.Largura, r.Altura, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var bd = b.LockBits(new System.Drawing.Rectangle(0, 0, r.Largura, r.Altura), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            for (int y = 0; y < r.Altura; y++) Marshal.Copy(r.Dados, y * r.Largura * 4, bd.Scan0 + y * bd.Stride, r.Largura * 4);
            b.UnlockBits(bd);
            return b;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _origem?.Dispose();
        _origem = null;
    }
}
