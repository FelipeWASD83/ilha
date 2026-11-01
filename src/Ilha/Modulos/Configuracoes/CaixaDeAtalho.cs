using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ilha.Configuracoes;

/// <summary>
/// Caixa que captura a combinação apertada (Ctrl, Alt, Shift e Win com uma tecla). Backspace ou Delete sozinhos
/// deixam sem atalho; Esc devolve o que estava. Tecla sem modificador só vale para F1 a F24.
/// Uma combinação que já é atalho global de algum programa (inclusive da própria ilha) não chega aqui: o Windows
/// entrega ela para quem registrou.
/// </summary>
internal sealed class CaixaDeAtalho : TextBox
{
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);

    private Atalho _valor = new() { Mods = 0, Tecla = 0 };
    private Atalho _antesDeEditar = new() { Mods = 0, Tecla = 0 };

    public event Action? Mudou;

    public CaixaDeAtalho()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        Cursor = Cursors.Hand;
        Width = 220;
        TextAlign = HorizontalAlignment.Center;
        BackColor = SystemColors.Window;
        Atualizar();
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Atalho Valor
    {
        get => new() { Mods = _valor.Mods, Tecla = _valor.Tecla };
        set { _valor = new Atalho { Mods = value.Mods, Tecla = value.Tecla }; Atualizar(); }
    }

    private void Atualizar() => Text = Focused ? "Aperte a combinação..." : Teclas.Nome(_valor);

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _antesDeEditar = Valor;
        Atualizar();
        BackColor = Color.FromArgb(232, 240, 254);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        BackColor = SystemColors.Window;
        Atualizar();
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Alt + tecla e Tab chegam como comando; aqui eles são parte do atalho
        if (Focused && (msg.Msg == 0x0100 || msg.Msg == 0x0104))
        {
            Capturar(keyData & Keys.KeyCode);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void Capturar(Keys tecla)
    {
        bool win = (GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0;
        var (r, atalho) = DeTecla(tecla, (ModifierKeys & Keys.Control) != 0, (ModifierKeys & Keys.Alt) != 0, (ModifierKeys & Keys.Shift) != 0, win);
        switch (r)
        {
            case Resultado.Ignorar:
                return;
            case Resultado.Cancelar:
                Valor = _antesDeEditar;
                Parent?.SelectNextControl(this, true, true, true, true);
                return;
            case Resultado.SemModificador:
                Text = "Use Ctrl, Alt, Shift ou Win junto";
                return;
        }
        _valor = atalho;
        Mudou?.Invoke();
        // tira o foco para mostrar o nome (e não capturar a próxima tecla sem querer)
        Parent?.SelectNextControl(this, true, true, true, true);
        Atualizar();
    }

    public enum Resultado { Ignorar, Cancelar, SemModificador, Atalho }

    /// <summary>Só a regra (o autoteste usa): o que fazer com a tecla e os modificadores apertados.</summary>
    public static (Resultado Resultado, Atalho Atalho) DeTecla(Keys tecla, bool ctrl, bool alt, bool shift, bool win)
    {
        var nenhum = new Atalho { Mods = 0, Tecla = 0 };
        if (tecla is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.LShiftKey or Keys.RShiftKey
            or Keys.LControlKey or Keys.RControlKey or Keys.LMenu or Keys.RMenu or Keys.None)
            return (Resultado.Ignorar, nenhum);
        bool algum = ctrl || alt || shift || win;
        if (!algum && tecla == Keys.Escape) return (Resultado.Cancelar, nenhum);
        if (!algum && tecla is Keys.Back or Keys.Delete) return (Resultado.Atalho, nenhum);
        bool funcao = tecla >= Keys.F1 && tecla <= Keys.F24;
        if (!algum && !funcao) return (Resultado.SemModificador, nenhum);
        int mods = (ctrl ? Nativo.MOD_CONTROL : 0) | (alt ? Nativo.MOD_ALT : 0) | (shift ? Nativo.MOD_SHIFT : 0) | (win ? Nativo.MOD_WIN : 0);
        return (Resultado.Atalho, new Atalho { Mods = mods, Tecla = (int)tecla });
    }
}
