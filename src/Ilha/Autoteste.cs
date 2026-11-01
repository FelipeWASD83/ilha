using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Windows.Forms;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Windows.UI.Composition;

namespace Ilha;

/// <summary>
/// `Ilha.exe --autoteste`: abre uma pílula de teste no topo do monitor principal por um segundo e confere,
/// lendo a tela de verdade, que (1) a janela que desenha aparece, (2) o texto do Direct2D sai, (3) o clique
/// atravessa a janela que desenha e (4) a janela de toque só pega o clique dentro da região.
/// Resultado em ~/.ilha/autoteste.txt; código 0 = tudo certo.
/// </summary>
internal static class Autoteste
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Nativo.POINT p);

    public static int Rodar()
    {
        var saida = new List<string>();
        int falhas = 0;
        void Ok(bool cond, string o) { saida.Add((cond ? "ok    " : "FALHOU") + "  " + o); if (!cond) falhas++; }

        try
        {
            using var g = new Grafico();
            var vis = new JanelaVisual();
            var toque = new JanelaToque();
            var prim = Screen.PrimaryScreen!.Bounds;
            const int W = 360, H = 120;
            int x = prim.Left + (prim.Width - W) / 2, y = prim.Top;
            foreach (var h in new[] { vis.Handle, toque.Handle })
                Nativo.SetWindowPos(h, Nativo.HWND_TOPMOST, x, y, W, H, Nativo.SWP_NOACTIVATE);
            // região só na metade esquerda da janela de toque
            Nativo.SetWindowRgn(toque.Handle, Nativo.CreateRoundRectRgn(0, 0, W / 2, H, 0, 0), true);

            var alvo = g.Alvo(vis.Handle);
            var raiz = g.Compositor.CreateContainerVisual();
            raiz.Size = new Vector2(W, H);
            alvo.Root = raiz;
            var preto = g.Compositor.CreateSpriteVisual();
            preto.Size = new Vector2(W, H);
            preto.Brush = g.Compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
            raiz.Children.InsertAtTop(preto);

            g.Escala = 1f;
            var sup = g.NovaSuperficie(new Vector2(W, H), (dc, t) =>
            {
                using var branco = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
                dc.FillRectangle(new System.Drawing.RectangleF(20, 20, 120, 80), branco);
            });
            var txt = g.Compositor.CreateSpriteVisual();
            txt.Size = new Vector2(W, H);
            txt.Brush = sup.Pincel;
            raiz.Children.InsertAtTop(txt);

            Nativo.ShowWindow(vis.Handle, Nativo.SW_SHOWNOACTIVATE);
            Nativo.ShowWindow(toque.Handle, Nativo.SW_SHOWNOACTIVATE);
            var antes = Nativo.GetForegroundWindow();

            var relogio = new System.Windows.Forms.Timer { Interval = 700 };
            relogio.Tick += (_, _) =>
            {
                relogio.Stop();
                System.Drawing.Color Pixel(int px, int py)
                {
                    using var bmp = new Bitmap(1, 1);
                    using var gr = Graphics.FromImage(bmp);
                    gr.CopyFromScreen(px, py, 0, 0, new System.Drawing.Size(1, 1));
                    return bmp.GetPixel(0, 0);
                }
                var fundo = Pixel(x + W - 10, y + H - 10);
                var desenho = Pixel(x + 80, y + 60);
                Ok(fundo.R < 10 && fundo.G < 10 && fundo.B < 10, $"janela visual aparece (pixel do fundo preto: {fundo})");
                Ok(desenho.R > 240 && desenho.G > 240, $"Direct2D desenha na superfície (pixel branco: {desenho})");

                var dentro = WindowFromPoint(new Nativo.POINT { X = x + 40, Y = y + 60 });
                var fora = WindowFromPoint(new Nativo.POINT { X = x + W - 40, Y = y + 60 });
                Ok(dentro == toque.Handle, "janela de toque pega o ponto dentro da região");
                Ok(fora != toque.Handle && fora != vis.Handle, "fora da região o clique atravessa as duas janelas");
                Ok(Nativo.GetForegroundWindow() == antes, "nenhuma janela roubou o foco");
                Application.ExitThread();
            };
            relogio.Start();
            Application.Run();
            alvo.Dispose();
            vis.DestroyHandle();
            toque.DestroyHandle();
        }
        catch (Exception e)
        {
            saida.Add("EXCEÇÃO: " + e);
            falhas++;
        }

        Directory.CreateDirectory(Config.Pasta);
        File.WriteAllLines(Path.Combine(Config.Pasta, "autoteste.txt"), saida);
        foreach (var l in saida) Console.WriteLine(l);
        return falhas == 0 ? 0 : 1;
    }
}
