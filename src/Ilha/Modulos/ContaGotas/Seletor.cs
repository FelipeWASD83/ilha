using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ilha.ContaGotas;

/// <summary>
/// O conta-gotas na tela, sem gancho de mouse nem de teclado: uma janela de captura por monitor (em camada com alfa 1,
/// que recebe o mouse e mostra a mira) e uma lupa separada, transparente ao mouse. O Esc vem de um RegisterHotKey
/// que só existe enquanto ele está aberto (e do WM_KEYDOWN da janela de captura). No clique, as janelas são
/// encobertas pelo DWM antes de ler o pixel, para o valor sair exato (a de alfa 1 ainda escureceria um nível).
/// Nada fica aberto depois: no fim tudo é destruído e o foco volta para a janela de antes.
/// </summary>
internal sealed class Seletor
{
    // ------------------------------------------------------------------ Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Nativo.RECT r, IntPtr dados);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr dados);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dcDestino, ref Nativo.POINT pos, ref SIZE tam, IntPtr dcOrigem,
        ref Nativo.POINT posOrigem, uint chave, ref BLENDFUNCTION mistura, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr inst, IntPtr nome);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr c);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out Nativo.RECT r);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref Nativo.RECT r, IntPtr pincel);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int i);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint uso, out IntPtr bits, IntPtr secao, uint deslocamento);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr destino, int x, int y, int l, int a, IntPtr origem, int ox, int oy, uint op);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000, ULW_ALPHA = 0x2, LWA_ALPHA = 0x2;
    private const int WM_SETCURSOR = 0x0020, WM_ERASEBKGND = 0x0014, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104,
        WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONDBLCLK = 0x0206, WM_ACTIVATEAPP = 0x001C,
        WM_CAPTURECHANGED = 0x0215, VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_ESCAPE = 0x1B, IDC_CROSS = 32515,
        BLACK_BRUSH = 4, ID_ESC = 0x4347;

    /// <summary>Quanto o fim espera o botão ser solto antes de terminar assim mesmo (botão travado, mensagem perdida).</summary>
    internal const long TETO_SOLTURA_MS = 10_000;

    /// <summary>
    /// Se o clique já pode terminar: só com os botões soltos de verdade (senão o cartão que abre em seguida recolhe
    /// pelo "clique fora" e o botão solto cai órfão no programa de baixo), ou depois do teto.
    /// </summary>
    internal static bool PodeTerminar(bool algumBotaoApertado, long esperouMs) => !algumBotaoApertado || esperouMs >= TETO_SOLTURA_MS;

    /// <summary>Um bitmap de memória (BGRA, de cima para baixo) com o DC dele já pronto.</summary>
    private sealed class Dib : IDisposable
    {
        public readonly IntPtr Dc, Bits;
        public readonly int Largura, Altura;
        private readonly IntPtr _bmp, _antigo;

        public Dib(int largura, int altura)
        {
            Largura = largura;
            Altura = altura;
            Dc = CreateCompatibleDC(IntPtr.Zero);
            var bmi = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = largura, biHeight = -altura, biPlanes = 1, biBitCount = 32,
            };
            _bmp = CreateDIBSection(Dc, ref bmi, 0, out Bits, IntPtr.Zero, 0);
            if (_bmp == IntPtr.Zero || Bits == IntPtr.Zero) { DeleteDC(Dc); throw new InvalidOperationException("CreateDIBSection falhou"); }
            _antigo = SelectObject(Dc, _bmp);
        }

        public void Dispose()
        {
            SelectObject(Dc, _antigo);
            Nativo.DeleteObject(_bmp);
            DeleteDC(Dc);
        }
    }

    private readonly record struct MonitorDaTela(IntPtr Handle, Nativo.RECT Area);

    // ------------------------------------------------------------------ janelas

    /// <summary>Cobre um monitor inteiro com alfa 1: invisível, mas recebe o mouse (com alfa 0 o clique passaria).</summary>
    private sealed class JanelaDeCaptura : NativeWindow
    {
        private readonly Seletor _dono;

        public JanelaDeCaptura(Seletor dono, Nativo.RECT r)
        {
            _dono = dono;
            CreateHandle(new CreateParams
            {
                Caption = Lang.T("Conta-gotas", "Color picker"),
                Style = Nativo.WS_POPUP,
                ExStyle = Nativo.WS_EX_LAYERED | Nativo.WS_EX_TOPMOST | Nativo.WS_EX_TOOLWINDOW,
                X = r.Left, Y = r.Top, Width = r.Right - r.Left, Height = r.Bottom - r.Top,
            });
            Nativo.SetLayeredWindowAttributes(Handle, 0, 1, LWA_ALPHA);
        }

        protected override void WndProc(ref Message m)
        {
            try
            {
                switch (m.Msg)
                {
                    case WM_SETCURSOR:
                        SetCursor(_dono._mira);
                        m.Result = (IntPtr)1;
                        return;
                    case WM_ERASEBKGND:
                        if (GetClientRect(m.HWnd, out var r)) FillRect(m.WParam, ref r, GetStockObject(BLACK_BRUSH));
                        m.Result = (IntPtr)1;
                        return;
                    case Nativo.WM_DPICHANGED:
                        // o retângulo sugerido encolheria a janela: ela cobre o monitor em pixel físico e fica assim
                        m.Result = IntPtr.Zero;
                        return;
                    case Nativo.WM_MOUSEMOVE:
                        _dono.Moveu();
                        break;
                    case Nativo.WM_LBUTTONDOWN:
                    case WM_LBUTTONDBLCLK:
                        _dono.Apertou(this);
                        return;
                    // o que fecha vai pela fila: a janela não é destruída dentro da própria mensagem
                    case Nativo.WM_LBUTTONUP:
                        _dono._postar(_dono.SoltouEsquerdo);
                        return;
                    // o botão direito cancela no solto, com a captura: solto no programa de baixo abriria o menu de contexto dele
                    case WM_RBUTTONDOWN:
                    case WM_RBUTTONDBLCLK:
                        _dono.ApertouDireito(this);
                        return;
                    case Nativo.WM_RBUTTONUP:
                        _dono._postar(_dono.SoltouDireito);
                        return;
                    case WM_KEYDOWN:
                    case WM_SYSKEYDOWN:
                        if (m.WParam.ToInt32() == VK_ESCAPE) { _dono._postar(_dono.Cancelar); return; }
                        break;
                    case Nativo.WM_HOTKEY:
                        if (m.WParam.ToInt32() == ID_ESC) { _dono._postar(_dono.Cancelar); return; }
                        break;
                    case WM_ACTIVATEAPP:
                        // outro programa tomou a frente (Alt+Tab, Win): o conta-gotas não fica cobrindo a tela
                        if (m.WParam == IntPtr.Zero) _dono.PerdeuFoco();
                        break;
                    case WM_CAPTURECHANGED:
                        _dono.PerdeuCaptura();
                        break;
                    case Nativo.WM_DISPLAYCHANGE:
                        _dono._postar(_dono.Cancelar);
                        break;
                }
            }
            catch (Exception e) { Log.Escrever("conta-gotas (janela): " + e); }
            base.WndProc(ref m);
        }
    }

    /// <summary>A lupa: em camada com alfa por pixel, transparente ao mouse, nunca ativa.</summary>
    private sealed class JanelaDaLupa : NativeWindow
    {
        public JanelaDaLupa()
        {
            CreateHandle(new CreateParams
            {
                Caption = Lang.T("Conta-gotas (lupa)", "Color picker (magnifier)"),
                Style = Nativo.WS_POPUP,
                ExStyle = Nativo.WS_EX_LAYERED | Nativo.WS_EX_TRANSPARENT | Nativo.WS_EX_TOPMOST | Nativo.WS_EX_TOOLWINDOW | Nativo.WS_EX_NOACTIVATE,
                Width = 1, Height = 1,
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Nativo.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
            if (m.Msg == Nativo.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Nativo.MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }
    }

    // ------------------------------------------------------------------ estado

    public static Seletor? Atual { get; private set; }

    private readonly Action<Action> _postar;
    private readonly Action<CorDaTela> _pegou;
    private readonly Action _cancelou;
    private readonly List<MonitorDaTela> _monitores = new();
    private readonly List<JanelaDeCaptura> _capturas = new();
    private readonly IntPtr _mira;
    private JanelaDaLupa? _lupa;
    private IntPtr _tela, _antes, _janelaEsc;
    private Dib? _amostra, _quadro;
    private System.Drawing.Bitmap? _bitmap;
    private Geometria.Medidas _medidas;
    private uint _dpi;
    private bool _lupaVisivel, _escRegistrado, _terminando, _cancelarAoSoltar, _fechado;
    private long _apertouEm;
    private int _ultX = int.MinValue, _ultY = int.MinValue;
    private CorDaTela? _cor;
    private readonly Timer _refresco = new() { Interval = 40 };
    private readonly Timer _limite = new() { Interval = 120_000 };
    private readonly Timer _soltura = new() { Interval = 50 };
    private System.Drawing.Font? _fonte;
    private float _fonteTam;

    private Seletor(Action<Action> postar, Action<CorDaTela> pegou, Action cancelou)
    {
        _postar = postar;
        _pegou = pegou;
        _cancelou = cancelou;
        _mira = LoadCursorW(IntPtr.Zero, (IntPtr)IDC_CROSS);
        // só enquanto está aberto: a tela pode mudar embaixo de um cursor parado (vídeo, animação)
        _refresco.Tick += (_, _) => { try { AtualizarLupa(forcar: true); } catch (Exception e) { Log.Escrever("conta-gotas (lupa): " + e.Message); } };
        _limite.Tick += (_, _) => Cancelar();
        // só entre o apertar e o soltar: a mensagem do botão solto pode não chegar (captura tomada por outro programa)
        _soltura.Tick += (_, _) => { try { TentarTerminar(); } catch (Exception e) { Log.Escrever("conta-gotas (soltura): " + e); } };
    }

    /// <summary>Abre o conta-gotas. false = já estava aberto ou não deu para cobrir a tela.</summary>
    public static bool Comecar(Action<Action> postar, Action<CorDaTela> pegou, Action cancelou)
    {
        if (Atual != null) return false;
        var s = new Seletor(postar, pegou, cancelou);
        try
        {
            if (!s.Abrir()) { s.Destruir(); return false; }
        }
        catch (Exception e)
        {
            Log.Escrever("conta-gotas: não abriu: " + e);
            s.Destruir();
            return false;
        }
        Atual = s;
        return true;
    }

    private bool Abrir()
    {
        _antes = Nativo.GetForegroundWindow();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr _, ref Nativo.RECT r, IntPtr _) =>
        {
            _monitores.Add(new MonitorDaTela(mon, r));
            return true;
        }, IntPtr.Zero);
        if (_monitores.Count == 0) { Log.Escrever("conta-gotas: nenhum monitor"); return false; }
        _tela = GetDC(IntPtr.Zero);
        if (_tela == IntPtr.Zero) return false;

        foreach (var m in _monitores)
        {
            var j = new JanelaDeCaptura(this, m.Area);
            _capturas.Add(j);
            Nativo.SetWindowPos(j.Handle, Nativo.HWND_TOPMOST, m.Area.Left, m.Area.Top, m.Area.Right - m.Area.Left, m.Area.Bottom - m.Area.Top,
                Nativo.SWP_SHOWWINDOW | Nativo.SWP_NOACTIVATE);
        }
        _lupa = new JanelaDaLupa();

        // o atalho acabou de chegar: a ilha tem o direito de trazer uma janela para a frente (o teclado vem junto)
        Nativo.GetCursorPos(out var c);
        var frente = _capturas[Math.Max(0, IndiceDoMonitor(c.X, c.Y))];
        if (Nativo.SetForegroundWindow(frente.Handle)) SetFocus(frente.Handle);
        else Log.Escrever("conta-gotas: não pegou a frente (o Esc ainda vem pelo atalho temporário)");
        _janelaEsc = frente.Handle;
        _escRegistrado = Nativo.RegisterHotKey(frente.Handle, ID_ESC, Nativo.MOD_NOREPEAT, VK_ESCAPE);
        if (!_escRegistrado) Log.Escrever("conta-gotas: o Esc já é de outro programa; vale o Esc da janela e o botão direito");
        SetCursor(_mira);
        AtualizarLupa(forcar: true);
        _refresco.Start();
        _limite.Start();
        Log.Escrever($"conta-gotas: aberto em {_monitores.Count} monitor(es)");
        return true;
    }

    private int IndiceDoMonitor(int x, int y)
    {
        for (int i = 0; i < _monitores.Count; i++)
        {
            var a = _monitores[i].Area;
            if (x >= a.Left && x < a.Right && y >= a.Top && y < a.Bottom) return i;
        }
        return -1;
    }

    private bool NaTela(int x, int y) => IndiceDoMonitor(x, y) >= 0;

    // ------------------------------------------------------------------ mouse

    private void Moveu()
    {
        if (_terminando || _fechado) return;
        AtualizarLupa(forcar: false);
    }

    private void Apertou(JanelaDeCaptura janela)
    {
        if (_terminando || _cancelarAoSoltar || _fechado) return;
        _terminando = true;
        _refresco.Stop();
        Nativo.GetCursorPos(out var p);
        // o botão solto vem para cá (senão cairia no programa de baixo sem o apertar dele)
        SetCapture(janela.Handle);
        int sim = 1;
        foreach (var j in _capturas) Nativo.DwmSetWindowAttribute(j.Handle, Nativo.DWMWA_CLOAK, ref sim, 4);
        if (_lupa != null) Nativo.DwmSetWindowAttribute(_lupa.Handle, Nativo.DWMWA_CLOAK, ref sim, 4);
        // dois quadros compostos já sem as janelas encobertas
        DwmFlush();
        DwmFlush();
        _cor = LerPixel(p.X, p.Y);
        if (_cor == null) Log.Escrever($"conta-gotas: não leu o pixel em {p.X},{p.Y}");
        EsperarSoltura();
    }

    private void ApertouDireito(JanelaDeCaptura janela)
    {
        if (_terminando || _cancelarAoSoltar || _fechado) return;
        _cancelarAoSoltar = true;
        SetCapture(janela.Handle);
        EsperarSoltura();
    }

    private void SoltouEsquerdo()
    {
        if (_terminando) TentarTerminar();
    }

    private void SoltouDireito()
    {
        if (_cancelarAoSoltar) TentarTerminar();
    }

    private void EsperarSoltura()
    {
        _apertouEm = Environment.TickCount64;
        _soltura.Start();
    }

    private static bool AlgumBotaoApertado() =>
        (Nativo.GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (Nativo.GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

    /// <summary>Fecha quando os botões estão soltos de verdade: entrega a cor (clique) ou cancela (botão direito).</summary>
    private void TentarTerminar()
    {
        if (_fechado || (!_terminando && !_cancelarAoSoltar)) return;
        bool apertado = AlgumBotaoApertado();
        if (!PodeTerminar(apertado, Environment.TickCount64 - _apertouEm)) return; // o _soltura continua olhando
        if (apertado) Log.Escrever("conta-gotas: o botão não soltou; termina assim mesmo");
        if (_terminando) Entregar();
        else
        {
            Fechar();
            _postar(_cancelou);
        }
    }

    private void Entregar()
    {
        if (_fechado) return;
        var cor = _cor;
        Fechar();
        if (cor is { } c) _postar(() => _pegou(c));
        else _postar(_cancelou);
    }

    private void PerdeuCaptura()
    {
        // ReleaseCapture do próprio Fechar também chega aqui (já fechado); sem a captura o botão solto vai para
        // outro programa, então quem decide é o estado real dos botões
        if (!_fechado && (_terminando || _cancelarAoSoltar)) _postar(TentarTerminar);
    }

    private void PerdeuFoco()
    {
        if (_terminando || _fechado) return;
        _postar(Cancelar);
    }

    public void Cancelar()
    {
        if (_fechado) return;
        // Esc, competitivo, módulo parando: não espera o botão; a cor já lida ainda vale
        if (_terminando && _cor != null) { Entregar(); return; }
        Fechar();
        _postar(_cancelou);
    }

    private CorDaTela? LerPixel(int x, int y)
    {
        if (!NaTela(x, y)) return null;
        using var d = new Dib(1, 1);
        // CAPTUREBLT: janelas em camada de outros programas (dica, menu) também são o que a pessoa vê
        if (!BitBlt(d.Dc, 0, 0, 1, 1, _tela, x, y, SRCCOPY | CAPTUREBLT)) return null;
        uint bgra = (uint)Marshal.ReadInt32(d.Bits);
        return new CorDaTela((byte)(bgra >> 16), (byte)(bgra >> 8), (byte)bgra);
    }

    // ------------------------------------------------------------------ lupa

    private unsafe void AtualizarLupa(bool forcar)
    {
        if (_lupa == null || _fechado || _terminando) return;
        Nativo.GetCursorPos(out var c);
        if (!forcar && c.X == _ultX && c.Y == _ultY) return;
        _ultX = c.X;
        _ultY = c.Y;

        int im = IndiceDoMonitor(c.X, c.Y);
        var mon = im >= 0 ? _monitores[im] : _monitores[0];
        uint dpi = 96;
        try { if (Nativo.GetDpiForMonitor(mon.Handle, 0, out uint dx, out _) == 0 && dx > 0) dpi = dx; } catch { }
        if (dpi != _dpi || _quadro == null)
        {
            _dpi = dpi;
            _medidas = Geometria.Medir(dpi);
            _bitmap?.Dispose();
            _quadro?.Dispose();
            _quadro = new Dib(_medidas.Largura, _medidas.Altura);
            _bitmap = new System.Drawing.Bitmap(_medidas.Largura, _medidas.Altura, _medidas.Largura * 4,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb, _quadro.Bits);
            _fonte?.Dispose();
            _fonte = null;
        }
        _amostra ??= new Dib(Geometria.AMOSTRA, Geometria.AMOSTRA);

        const int N = Geometria.AMOSTRA, MEIO = N / 2;
        // sem CAPTUREBLT aqui: com ele o cursor pisca a cada leitura; o valor final é lido com ele
        BitBlt(_amostra.Dc, 0, 0, N, N, _tela, c.X - MEIO, c.Y - MEIO, SRCCOPY);
        var px = (uint*)_amostra.Bits;
        for (int k = 0; k < N * N; k++)
        {
            uint v = px[k];
            _pixels[k] = new CorDaTela((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }

        Desenhar(c.X, c.Y, _pixels[MEIO * N + MEIO]);

        var (lx, ly) = Geometria.Posicionar(c.X, c.Y, _medidas.Largura, _medidas.Altura, _medidas.Deslocamento,
            mon.Area.Left, mon.Area.Top, mon.Area.Right, mon.Area.Bottom);
        var pos = new Nativo.POINT { X = lx, Y = ly };
        var tam = new SIZE { cx = _medidas.Largura, cy = _medidas.Altura };
        var zero = new Nativo.POINT();
        var mistura = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        UpdateLayeredWindow(_lupa.Handle, IntPtr.Zero, ref pos, ref tam, _quadro!.Dc, ref zero, 0, ref mistura, ULW_ALPHA);
        if (!_lupaVisivel)
        {
            _lupaVisivel = true;
            Nativo.SetWindowPos(_lupa.Handle, Nativo.HWND_TOPMOST, 0, 0, 0, 0,
                Nativo.SWP_NOMOVE | Nativo.SWP_NOSIZE | Nativo.SWP_NOACTIVATE | Nativo.SWP_SHOWWINDOW);
        }
    }

    private readonly CorDaTela[] _pixels = new CorDaTela[Geometria.AMOSTRA * Geometria.AMOSTRA];

    private void Desenhar(int cx, int cy, CorDaTela centro)
    {
        const int N = Geometria.AMOSTRA, MEIO = N / 2;
        var m = _medidas;
        float e = _dpi / 96f;
        int lado = m.Largura;
        using var g = System.Drawing.Graphics.FromImage(_bitmap!);
        g.Clear(System.Drawing.Color.Transparent);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

        float raio = 7 * e;
        using (var moldura = Arredondado(0, 0, lado, lado, raio))
        using (var fundo = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 24, 24, 26)))
            g.FillPath(fundo, moldura);

        // as células sem suavização (encostadas, sem fresta), recortadas pelo canto arredondado de dentro
        using (var dentro = Arredondado(m.Borda, m.Borda, lado - 2 * m.Borda, lado - 2 * m.Borda, MathF.Max(1, raio - m.Borda)))
        {
            g.SetClip(dentro);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            using var pincel = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                {
                    int sx = cx - MEIO + i, sy = cy - MEIO + j;
                    if (NaTela(sx, sy))
                    {
                        var k = _pixels[j * N + i];
                        pincel.Color = System.Drawing.Color.FromArgb(255, k.R, k.G, k.B);
                    }
                    else
                    {
                        // fora de qualquer monitor (borda da tela, vão entre monitores de tamanhos diferentes): xadrez
                        int t = ((sx + sy) & 1) == 0 ? 44 : 34;
                        pincel.Color = System.Drawing.Color.FromArgb(255, t, t, t);
                    }
                    g.FillRectangle(pincel, m.Borda + i * m.Celula, m.Borda + j * m.Celula, m.Celula, m.Celula);
                }
            if (m.Celula >= 7)
            {
                using var linha = new System.Drawing.Pen(System.Drawing.Color.FromArgb(46, 0, 0, 0), 1);
                for (int k = 1; k < N; k++)
                {
                    int p = m.Borda + k * m.Celula;
                    g.DrawLine(linha, p, m.Borda, p, lado - m.Borda);
                    g.DrawLine(linha, m.Borda, p, lado - m.Borda, p);
                }
            }
            g.ResetClip();
        }

        // o pixel do cursor: anel preto por fora e branco por dentro, aparece em qualquer cor
        int esp = Math.Max(1, (int)MathF.Round(1.5f * e));
        int x0 = m.Borda + MEIO * m.Celula, y0 = x0;
        using (var preto = new System.Drawing.Pen(System.Drawing.Color.Black, esp))
            g.DrawRectangle(preto, x0 - esp / 2f - esp, y0 - esp / 2f - esp, m.Celula + esp * 2, m.Celula + esp * 2);
        using (var branco = new System.Drawing.Pen(System.Drawing.Color.White, esp))
            g.DrawRectangle(branco, x0 - esp / 2f, y0 - esp / 2f, m.Celula + esp, m.Celula + esp);

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var aro = new System.Drawing.Pen(System.Drawing.Color.FromArgb(70, 255, 255, 255), 1))
        using (var moldura = Arredondado(0.5f, 0.5f, lado - 1, lado - 1, raio))
            g.DrawPath(aro, moldura);

        // a etiqueta embaixo: quadradinho da cor e o hex (pode diferir 1 nível do final, que é lido sem as janelas)
        float ey = lado + m.Vao;
        using (var etq = Arredondado(0, ey, lado, m.Etiqueta, m.Etiqueta / 2f))
        using (var fundo = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(240, 24, 24, 26)))
            g.FillPath(fundo, etq);
        float q = m.Etiqueta * 0.5f, qx = m.Etiqueta * 0.3f, qy = ey + (m.Etiqueta - q) / 2;
        using (var amostraP = Arredondado(qx, qy, q, q, q * 0.28f))
        using (var cor = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, centro.R, centro.G, centro.B)))
        using (var aro = new System.Drawing.Pen(System.Drawing.Color.FromArgb(90, 255, 255, 255), 1))
        {
            g.FillPath(cor, amostraP);
            g.DrawPath(aro, amostraP);
        }
        float tamFonte = 12.5f * e;
        if (_fonte == null || _fonteTam != tamFonte)
        {
            _fonte?.Dispose();
            _fonte = new System.Drawing.Font("Segoe UI Semibold", tamFonte, System.Drawing.GraphicsUnit.Pixel);
            _fonteTam = tamFonte;
        }
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        string hex = centro.Hex();
        var medida = g.MeasureString(hex, _fonte);
        using var texto = new System.Drawing.SolidBrush(System.Drawing.Color.White);
        g.DrawString(hex, _fonte, texto, qx + q + 6 * e, ey + (m.Etiqueta - medida.Height) / 2);
    }

    private static System.Drawing.Drawing2D.GraphicsPath Arredondado(float x, float y, float l, float a, float r)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        r = MathF.Max(0.5f, MathF.Min(r, MathF.Min(l, a) / 2));
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + l - d, y, d, d, 270, 90);
        p.AddArc(x + l - d, y + a - d, d, d, 0, 90);
        p.AddArc(x, y + a - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ------------------------------------------------------------------ fim

    private void Fechar()
    {
        if (_fechado) return;
        Destruir();
        Atual = null;
        Log.Escrever("conta-gotas: fechado");
    }

    private bool EhNossa(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        if (_lupa != null && _lupa.Handle == h) return true;
        foreach (var j in _capturas) if (j.Handle == h) return true;
        return false;
    }

    /// <summary>Também no caminho de falha do Abrir: a frente pode já ter vindo para uma janela de captura.</summary>
    private void Destruir()
    {
        _fechado = true;
        _refresco.Dispose();
        _limite.Dispose();
        _soltura.Dispose();
        if (_escRegistrado) Nativo.UnregisterHotKey(_janelaEsc, ID_ESC);
        _escRegistrado = false;
        if (EhNossa(GetCapture())) ReleaseCapture();
        // ainda com a frente: devolve para quem estava antes de fechar as janelas (senão o Windows escolhe outra);
        // se outro programa já tomou a frente (Alt+Tab), ele fica com ela
        try
        {
            if (EhNossa(Nativo.GetForegroundWindow()) && _antes != IntPtr.Zero && !EhNossa(_antes) && IsWindow(_antes))
                Nativo.SetForegroundWindow(_antes);
        }
        catch (Exception e) { Log.Escrever("conta-gotas (frente): " + e.Message); }
        try { _lupa?.DestroyHandle(); } catch (Exception e) { Log.Escrever("conta-gotas (lupa): " + e.Message); }
        _lupa = null;
        foreach (var j in _capturas)
            try { j.DestroyHandle(); } catch (Exception e) { Log.Escrever("conta-gotas (captura): " + e.Message); }
        _capturas.Clear();
        _bitmap?.Dispose();
        _bitmap = null;
        _quadro?.Dispose();
        _quadro = null;
        _amostra?.Dispose();
        _amostra = null;
        _fonte?.Dispose();
        _fonte = null;
        if (_tela != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _tela);
        _tela = IntPtr.Zero;
    }
}
