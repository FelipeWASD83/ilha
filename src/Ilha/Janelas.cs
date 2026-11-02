using System;
using System.Windows.Forms;

namespace Ilha;

/// <summary>
/// A janela que mostra a ilha. Não recebe clique nenhum (camada transparente ao mouse), então a sombra e a
/// margem da animação nunca seguram clique de outro app. Também é ela que ouve os avisos de tela,
/// DPI, energia e o atalho global.
/// </summary>
internal sealed class JanelaVisual : NativeWindow
{
    public event Action? TelaMudou;
    public event Action? Acordou;
    public event Action<int>? Atalho;

    public JanelaVisual()
    {
        CreateHandle(new CreateParams
        {
            Caption = "Ilha",
            Style = Nativo.WS_POPUP,
            ExStyle = Nativo.WS_EX_NOREDIRECTIONBITMAP | Nativo.WS_EX_TOOLWINDOW | Nativo.WS_EX_TOPMOST
                      | Nativo.WS_EX_NOACTIVATE | Nativo.WS_EX_LAYERED | Nativo.WS_EX_TRANSPARENT,
            Width = 1,
            Height = 1,
        });
        // janela em camada só aparece depois disso; o conteúdo vem do compositor, não da superfície dela
        Nativo.SetLayeredWindowAttributes(Handle, 0, 255, 0x2);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Nativo.WM_DISPLAYCHANGE:
            case Nativo.WM_DPICHANGED:
                TelaMudou?.Invoke();
                break;
            case Nativo.WM_SETTINGCHANGE:
                // SPI_SETWORKAREA (barra de tarefas mudou de lugar ou de tamanho)
                if (m.WParam.ToInt32() == 0x2F) TelaMudou?.Invoke();
                break;
            case Nativo.WM_POWERBROADCAST:
                int ev = m.WParam.ToInt32();
                if (ev == Nativo.PBT_APMRESUMEAUTOMATIC || ev == Nativo.PBT_APMRESUMESUSPEND) Acordou?.Invoke();
                break;
            case Nativo.WM_HOTKEY:
                Atalho?.Invoke(m.WParam.ToInt32());
                break;
        }
        base.WndProc(ref m);
    }
}

/// <summary>
/// Janela sem conteúdo nenhum, do formato exato da pílula (SetWindowRgn): é ela que recebe o mouse. Fora da
/// região o clique passa para o que estiver atrás. Nunca vira janela ativa.
/// </summary>
internal sealed class JanelaToque : NativeWindow
{
    public event Action<int, int>? Moveu;
    public event Action? Saiu;
    public event Action<int, int>? Clicou;
    public event Action<int, int>? CliqueDireito;
    public event Action<int>? Rolou;
    public event Action<int, int>? Apertou;
    public event Action<int, int>? Arrastando;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
    private bool _apertado;
    // o botão desceu nesta janela (um botão solto que veio de um aperto em outro lugar não é clique)
    private bool _desceuAqui;

    /// <summary>Solta o mouse (o OLE do arrastar para outro app engole o botão solto).</summary>
    public void SoltarCaptura()
    {
        if (!_apertado) return;
        _apertado = false;
        ReleaseCapture();
    }

    private bool _rastreando;
    private IntPtr? _seta;

    /// <summary>Botão apertado em cima dela (o mouse está capturado para um arraste).</summary>
    public bool Apertado => _apertado;

    /// <summary>A ilha concluiu por conta própria que o mouse saiu: o próximo movimento volta a pedir o aviso de saída.</summary>
    public void EsquecerRastreio() => _rastreando = false;

    public JanelaToque()
    {
        CreateHandle(new CreateParams
        {
            Caption = "Ilha (toque)",
            Style = Nativo.WS_POPUP,
            ExStyle = Nativo.WS_EX_NOREDIRECTIONBITMAP | Nativo.WS_EX_TOOLWINDOW | Nativo.WS_EX_TOPMOST | Nativo.WS_EX_NOACTIVATE,
            Width = 1,
            Height = 1,
        });
    }

    private static (int x, int y) Ponto(IntPtr lp)
    {
        long v = lp.ToInt64();
        return ((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Nativo.WM_MOUSEACTIVATE:
                m.Result = (IntPtr)Nativo.MA_NOACTIVATE;
                return;
            case Nativo.WM_SETCURSOR:
                // the window class has no cursor (NativeWindow registers it with none): without this the pointer keeps
                // the shape of the window underneath, or vanishes when that one had hidden it
                if ((m.LParam.ToInt64() & 0xFFFF) == 1)
                {
                    _seta ??= Nativo.LoadCursorW(IntPtr.Zero, Nativo.IDC_ARROW);
                    Nativo.SetCursor(_seta.Value);
                    m.Result = (IntPtr)1;
                    return;
                }
                break;
            case Nativo.WM_MOUSEMOVE:
            {
                if (!_rastreando)
                {
                    var t = new Nativo.TRACKMOUSEEVENT { cbSize = 24, dwFlags = Nativo.TME_LEAVE, hwndTrack = Handle };
                    _rastreando = Nativo.TrackMouseEvent(ref t);
                }
                var (x, y) = Ponto(m.LParam);
                Moveu?.Invoke(x, y);
                if (_apertado) Arrastando?.Invoke(x, y);
                break;
            }
            case Nativo.WM_LBUTTONDOWN:
            {
                // captura o mouse para o arraste continuar mesmo saindo da pílula
                _apertado = true;
                _desceuAqui = true;
                SetCapture(Handle);
                var (x, y) = Ponto(m.LParam);
                Apertou?.Invoke(x, y);
                break;
            }
            case Nativo.WM_MOUSELEAVE:
                _rastreando = false;
                Saiu?.Invoke();
                break;
            case Nativo.WM_LBUTTONUP:
            {
                bool valeu = _desceuAqui;
                _desceuAqui = false;
                if (_apertado) { _apertado = false; ReleaseCapture(); }
                var (x, y) = Ponto(m.LParam);
                if (valeu) Clicou?.Invoke(x, y);
                break;
            }
            case Nativo.WM_RBUTTONUP:
            {
                var (x, y) = Ponto(m.LParam);
                CliqueDireito?.Invoke(x, y);
                break;
            }
            case 0x0215: // WM_CAPTURECHANGED: outra janela levou a captura (um arrastar para outro app)
                if (m.LParam != Handle) _apertado = false;
                break;
            case Nativo.WM_MOUSEWHEEL:
                Rolou?.Invoke((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
                break;
        }
        base.WndProc(ref m);
    }
}
