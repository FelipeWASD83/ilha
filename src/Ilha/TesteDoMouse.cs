using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace Ilha;

/// <summary>
/// `Ilha.exe --teste-mouse`: reproduz os casos de "a ilha fica em cima do mouse" mexendo no cursor de verdade
/// (SetCursorPos) e anota o estado em ~/.ilha/teste-mouse.txt. Mexe no mouse: só rodar com ninguém usando o PC.
/// Devolve o cursor onde estava e fecha sozinho.
/// </summary>
internal static class TesteDoMouse
{
    private sealed record Passo(int Ms, Action Acao);

    public static void Comecar(Nucleo n)
    {
        var ilha = n.Ilha;
        var log = new StringBuilder();
        Nativo.GetCursorPos(out var original);
        var timer = (DeMentira)n.Demonstracao.Atividades.Find(a => a.Tipo == "timer")!;
        var passos = new Queue<Passo>();
        int amostras = 0;
        string caso = "";
        var inicioCaso = DateTime.Now;

        void Anotar(string s) => log.AppendLine($"{(DateTime.Now - inicioCaso).TotalMilliseconds,6:0}ms [{caso}] {s}");
        void Cursor(int x, int y) { Nativo.SetCursorPos(x, y); Anotar($"cursor em {x},{y}"); }
        void Caso(string nome) { caso = nome; inicioCaso = DateTime.Now; Anotar("começou"); }
        System.Drawing.RectangleF R() => ilha.CorpoNaTela;
        int Cx() => (int)(R().Left + R().Width / 2);

        // alerta que passa pelo Não Incomodar (Importante), para o teste valer com ele ligado
        void Alerta() => ilha.Alertar(new Alerta
        {
            Tipo = "alerta", Importante = true, Som = "", Segundos = 4,
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(300, 52));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) => p.Texto.Desenhar(dc, "Alerta de teste do mouse", new Vector2(8, 14), 17, Cor.Branco));
                return c;
            },
        });

        passos.Enqueue(new(500, () => { Caso("prepara"); timer.Comecar(); }));
        // 1) alerta cresce embaixo de um cursor parado (fora da compacta, dentro do alerta): tem que sumir em ~4 s
        passos.Enqueue(new(4500, () =>
        {
            var r = R();
            Caso("1-alerta-sob-cursor-parado");
            Cursor(Cx(), (int)(r.Bottom + 24 * ilha.Escala));
            amostras = 90;
            Alerta();
        }));
        // 2) a região de toque cresce antes do desenho? cursor parado um pouco abaixo da compacta, cartão abre pelo
        // clique simulado; anota de 20 em 20 ms quando a janela embaixo do cursor vira a da ilha
        passos.Enqueue(new(10000, () =>
        {
            var r = R();
            Caso("2-regiao-durante-crescimento");
            Cursor(Cx(), (int)(r.Bottom + 30 * ilha.Escala));
            ilha.TesteClicarNoCorpo();
            amostras = -30;
        }));
        // 3) mouse sai de verdade do cartão aberto: tem que recolher depois do atraso
        passos.Enqueue(new(2500, () =>
        {
            var r = R();
            Caso("3-sai-do-cartao");
            Cursor(Cx(), (int)(r.Top + r.Height / 2));
        }));
        passos.Enqueue(new(800, () =>
        {
            var r = R();
            Cursor(Cx(), (int)(r.Bottom + 80 * ilha.Escala));
            amostras = 30;
        }));
        // 4) entra e sai da compacta
        passos.Enqueue(new(3500, () =>
        {
            var r = R();
            Caso("4-entra-e-sai");
            Cursor(Cx(), (int)(r.Top + r.Height / 2));
            amostras = 25;
        }));
        passos.Enqueue(new(1200, () => Cursor(original.X, original.Y + 300)));
        passos.Enqueue(new(2500, () =>
        {
            Caso("fim");
            Nativo.SetCursorPos(original.X, original.Y);
            timer.Parar();
            System.IO.File.WriteAllText(System.IO.Path.Combine(Config.Pasta, "teste-mouse.txt"), log.ToString());
            n.Sair();
        }));

        // amostras > 0: de 100 em 100 ms; < 0: de 20 em 20 ms (o caso da região)
        var amostra = new System.Windows.Forms.Timer { Interval = 20 };
        int tique = 0;
        amostra.Tick += (_, _) =>
        {
            tique++;
            if (amostras == 0) return;
            if (amostras > 0 && tique % 5 != 0) return;
            amostras += amostras > 0 ? -1 : 1;
            Nativo.GetCursorPos(out var c);
            Anotar(ilha.Diagnostico + $" sobAToque={(Nativo.WindowFromPoint(c) == ilha.HandleDoToque)}");
        };
        amostra.Start();

        var relogio = new System.Windows.Forms.Timer();
        void Proximo()
        {
            if (passos.Count == 0) return;
            relogio.Interval = Math.Max(1, passos.Peek().Ms);
            relogio.Start();
        }
        relogio.Tick += (_, _) =>
        {
            relogio.Stop();
            var p = passos.Dequeue();
            try { p.Acao(); } catch (Exception e) { Anotar("erro: " + e.Message); }
            Proximo();
        };
        Proximo();
    }
}
