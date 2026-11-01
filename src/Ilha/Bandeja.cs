using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Ilha;

/// <summary>Ícone perto do relógio: pausar, demonstração, configuração, logs e sair.</summary>
internal sealed class Bandeja : IDisposable
{
    private readonly NotifyIcon _icone;
    private readonly Nucleo _n;
    private ToolStripMenuItem _pausar = null!;
    private ToolStripMenuItem _demo = null!;

    public Bandeja(Nucleo n)
    {
        _n = n;
        _icone = new NotifyIcon
        {
            Icon = Carregar(),
            ContextMenuStrip = MontarMenu(n),
            Visible = true,
            Text = "Ilha",
        };
        _icone.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) n.Ilha.Atalho(); };
        Atualizar(n.Config.Pausada);
    }

    /// <summary>The interface language changed: the menu is built again with the new texts.</summary>
    public void Recriar()
    {
        var velho = _icone.ContextMenuStrip;
        _icone.ContextMenuStrip = MontarMenu(_n);
        velho?.Dispose();
        Atualizar(_n.Config.Pausada);
    }

    private ContextMenuStrip MontarMenu(Nucleo n)
    {
        var menu = new ContextMenuStrip();
        var titulo = new ToolStripMenuItem($"Ilha {Atualizacao.Versao}") { Enabled = false };
        _pausar = new ToolStripMenuItem(Lang.T("Pausar a ilha", "Pause Ilha"));
        _pausar.Click += (_, _) => n.Pausar(!n.Config.Pausada);

        _demo = new ToolStripMenuItem(Lang.T("Demonstração (atividades de mentira)", "Demo (fake activities)"));
        foreach (var a in n.Demonstracao.Atividades)
        {
            var item = new ToolStripMenuItem(a.Nome) { CheckOnClick = false };
            item.Click += (_, _) => { n.Demonstracao.Alternar(a); };
            _demo.DropDownItems.Add(item);
        }
        _demo.DropDownItems.Add(new ToolStripSeparator());
        var alerta = new ToolStripMenuItem(Lang.T("Mostrar um alerta", "Show an alert"));
        alerta.Click += (_, _) => n.Demonstracao.AlertaDeTeste();
        var roteiro = new ToolStripMenuItem(Lang.T("Passear por todos os estados", "Tour all the states"));
        roteiro.Click += (_, _) => n.Demonstracao.Roteiro();
        var limpar = new ToolStripMenuItem(Lang.T("Desligar todas", "Turn all off"));
        limpar.Click += (_, _) => n.Demonstracao.DesligarTodas();
        _demo.DropDownItems.AddRange(new ToolStripItem[] { alerta, roteiro, limpar });
        _demo.DropDownOpening += (_, _) =>
        {
            int i = 0;
            foreach (var a in n.Demonstracao.Atividades) ((ToolStripMenuItem)_demo.DropDownItems[i++]).Checked = a.Ativa;
        };

        var inicio = new ToolStripMenuItem(Lang.T("Iniciar com o Windows", "Start with Windows"));
        inicio.Click += (_, _) => n.AlternarInicio();
        menu.Opening += (_, _) => inicio.Checked = n.Config.IniciarComWindows;
        var colada = new ToolStripMenuItem(Lang.T("Colada na borda (estilo notch)", "Attached to the edge (notch style)"));
        colada.Click += (_, _) => n.AlternarColada();
        menu.Opening += (_, _) => colada.Checked = n.Config.Colada;
        var config = new ToolStripMenuItem(Lang.T("Configurações...", "Settings...")) { Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) };
        config.Click += (_, _) => n.AbrirConfiguracoes();
        var json = new ToolStripMenuItem(Lang.T("Abrir o config.json", "Open config.json"));
        json.Click += (_, _) => Abrir("notepad.exe", Config.Arquivo);
        var atualizar = new ToolStripMenuItem(Lang.T("Procurar atualização", "Check for updates"));
        atualizar.Click += (_, _) =>
        {
            if (!global::Ilha.Atualizacoes.ModuloAtualizador.ProcurarAgora())
                Avisar(Lang.T("O atualizador está desligado nas configurações (módulos).", "The updater is turned off in the settings (modules)."));
        };
        var logs = new ToolStripMenuItem(Lang.T("Abrir a pasta da ilha (logs e medições)", "Open the Ilha folder (logs and measurements)"));
        logs.Click += (_, _) => Abrir("explorer.exe", Config.Pasta);
        var sair = new ToolStripMenuItem(Lang.T("Sair", "Quit"));
        sair.Click += (_, _) => n.Sair();
        menu.Items.AddRange(new ToolStripItem[] { titulo, new ToolStripSeparator(), _pausar, colada, inicio, _demo, new ToolStripSeparator(), config, json, logs, atualizar, new ToolStripSeparator(), sair });
        return menu;
    }

    private static void Abrir(string programa, string alvo)
    {
        try { Process.Start(new ProcessStartInfo(programa, $"\"{alvo}\"") { UseShellExecute = true }); }
        catch (Exception e) { Log.Escrever("não abriu " + alvo + ": " + e.Message); }
    }

    public void Atualizar(bool pausada)
    {
        _pausar.Checked = pausada;
        _icone.Text = pausada ? Lang.T("Ilha (pausada)", "Ilha (paused)") : "Ilha";
    }

    public void Avisar(string texto) => _icone.ShowBalloonTip(6000, "Ilha", texto, ToolTipIcon.Info);

    /// <summary>O ícone do app (icone.ico, o desenho do Menu Iniciar); sem ele, uma pílula desenhada aqui.</summary>
    private static Icon Carregar()
    {
        string arq = System.IO.Path.Combine(AppContext.BaseDirectory, "icone.ico");
        try { if (System.IO.File.Exists(arq)) return new Icon(arq, 32, 32); } catch { }
        return Desenhar();
    }

    private static Icon Desenhar()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var p = new GraphicsPath();
            var r = new RectangleF(2, 9, 28, 14);
            p.AddArc(r.Left, r.Top, r.Height, r.Height, 90, 180);
            p.AddArc(r.Right - r.Height, r.Top, r.Height, r.Height, 270, 180);
            p.CloseFigure();
            g.FillPath(Brushes.Black, p);
            using var borda = new Pen(Color.FromArgb(200, 255, 255, 255), 1.5f);
            g.DrawPath(borda, p);
            using var verde = new SolidBrush(Color.FromArgb(255, 48, 209, 88));
            g.FillEllipse(verde, 21, 13, 6, 6);
        }
        // FromHandle não fica dono do HICON: copia e devolve o handle ao Windows
        IntPtr h = bmp.GetHicon();
        var icone = (Icon)Icon.FromHandle(h).Clone();
        Nativo.DestroyIcon(h);
        return icone;
    }

    public void Dispose()
    {
        _icone.Visible = false;
        _icone.Dispose();
    }
}

/// <summary>Atalho global numa janela que já existe (a visual), sem gancho de teclado.</summary>
internal sealed class Teclas : IDisposable
{
    private const int ID = 0x494C;
    private readonly IntPtr _janela;
    private bool _registrado;

    public Teclas(IntPtr janela) => _janela = janela;
    public static bool Eh(int id) => id == ID;

    /// <summary>Tenta o atalho da config; se outro programa já usa, tenta as reservas e devolve o que ficou.</summary>
    public Atalho? Registrar(Atalho preferido)
    {
        Soltar();
        if (preferido.Tecla == 0) return preferido;
        var tentativas = new List<Atalho>
        {
            preferido,
            new() { Mods = Nativo.MOD_CONTROL | Nativo.MOD_SHIFT, Tecla = 0x20 },
            new() { Mods = Nativo.MOD_ALT | Nativo.MOD_SHIFT, Tecla = 0x20 },
            new() { Mods = Nativo.MOD_CONTROL | Nativo.MOD_ALT, Tecla = 0x49 }, // I
        };
        foreach (var a in tentativas)
        {
            if (Nativo.RegisterHotKey(_janela, ID, a.Mods | Nativo.MOD_NOREPEAT, a.Tecla))
            {
                _registrado = true;
                return a;
            }
        }
        return null;
    }

    private void Soltar()
    {
        if (_registrado) Nativo.UnregisterHotKey(_janela, ID);
        _registrado = false;
    }

    public static string Nome(Atalho a)
    {
        if (a.Tecla == 0) return Lang.T("nenhum", "none");
        var partes = new List<string>();
        if ((a.Mods & Nativo.MOD_CONTROL) != 0) partes.Add("Ctrl");
        if ((a.Mods & Nativo.MOD_ALT) != 0) partes.Add("Alt");
        if ((a.Mods & Nativo.MOD_SHIFT) != 0) partes.Add("Shift");
        if ((a.Mods & Nativo.MOD_WIN) != 0) partes.Add("Win");
        partes.Add(a.Tecla == 0x20 ? Lang.T("Espaço", "Space") : ((Keys)a.Tecla).ToString());
        return string.Join(" + ", partes);
    }

    public void Dispose() => Soltar();
}
