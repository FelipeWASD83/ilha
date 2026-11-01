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
    private readonly ToolStripMenuItem _pausar;
    private readonly ToolStripMenuItem _demo;

    public Bandeja(Nucleo n)
    {
        var menu = new ContextMenuStrip();
        var titulo = new ToolStripMenuItem($"Ilha {Atualizacao.Versao}") { Enabled = false };
        _pausar = new ToolStripMenuItem("Pausar a ilha");
        _pausar.Click += (_, _) => n.Pausar(!n.Config.Pausada);

        _demo = new ToolStripMenuItem("Demonstração (atividades de mentira)");
        foreach (var a in n.Demonstracao.Atividades)
        {
            var item = new ToolStripMenuItem(a.Nome) { CheckOnClick = false };
            item.Click += (_, _) => { n.Demonstracao.Alternar(a); };
            _demo.DropDownItems.Add(item);
        }
        _demo.DropDownItems.Add(new ToolStripSeparator());
        var alerta = new ToolStripMenuItem("Mostrar um alerta");
        alerta.Click += (_, _) => n.Demonstracao.AlertaDeTeste();
        var roteiro = new ToolStripMenuItem("Passear por todos os estados");
        roteiro.Click += (_, _) => n.Demonstracao.Roteiro();
        var limpar = new ToolStripMenuItem("Desligar todas");
        limpar.Click += (_, _) => n.Demonstracao.DesligarTodas();
        _demo.DropDownItems.AddRange(new ToolStripItem[] { alerta, roteiro, limpar });
        _demo.DropDownOpening += (_, _) =>
        {
            int i = 0;
            foreach (var a in n.Demonstracao.Atividades) ((ToolStripMenuItem)_demo.DropDownItems[i++]).Checked = a.Ativa;
        };

        var colada = new ToolStripMenuItem("Colada na borda (estilo notch)");
        colada.Click += (_, _) => n.AlternarColada();
        menu.Opening += (_, _) => colada.Checked = n.Config.Colada;
        var config = new ToolStripMenuItem("Abrir a configuração");
        config.Click += (_, _) => Abrir("notepad.exe", Config.Arquivo);
        var logs = new ToolStripMenuItem("Abrir a pasta da ilha (logs e medições)");
        logs.Click += (_, _) => Abrir("explorer.exe", Config.Pasta);
        var sair = new ToolStripMenuItem("Sair");
        sair.Click += (_, _) => n.Sair();
        menu.Items.AddRange(new ToolStripItem[] { titulo, new ToolStripSeparator(), _pausar, colada, _demo, new ToolStripSeparator(), config, logs, new ToolStripSeparator(), sair });

        _icone = new NotifyIcon
        {
            Icon = Desenhar(),
            ContextMenuStrip = menu,
            Visible = true,
            Text = "Ilha",
        };
        _icone.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) n.Ilha.Atalho(); };
        Atualizar(n.Config.Pausada);
    }

    private static void Abrir(string programa, string alvo)
    {
        try { Process.Start(new ProcessStartInfo(programa, $"\"{alvo}\"") { UseShellExecute = true }); }
        catch (Exception e) { Log.Escrever("não abriu " + alvo + ": " + e.Message); }
    }

    public void Atualizar(bool pausada)
    {
        _pausar.Checked = pausada;
        _icone.Text = pausada ? "Ilha (pausada)" : "Ilha";
    }

    public void Avisar(string texto) => _icone.ShowBalloonTip(6000, "Ilha", texto, ToolTipIcon.Info);

    /// <summary>O ícone é desenhado aqui: a pílula preta com borda clara, para aparecer na barra escura ou clara.</summary>
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
        if (a.Tecla == 0) return "nenhum";
        var partes = new List<string>();
        if ((a.Mods & Nativo.MOD_CONTROL) != 0) partes.Add("Ctrl");
        if ((a.Mods & Nativo.MOD_ALT) != 0) partes.Add("Alt");
        if ((a.Mods & Nativo.MOD_SHIFT) != 0) partes.Add("Shift");
        if ((a.Mods & Nativo.MOD_WIN) != 0) partes.Add("Win");
        partes.Add(a.Tecla == 0x20 ? "Espaço" : ((Keys)a.Tecla).ToString());
        return string.Join(" + ", partes);
    }

    public void Dispose() => Soltar();
}
