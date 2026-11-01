using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace Ilha.Mascote;

internal sealed partial class ModuloMascote
{
    /// <summary>Um PNG de 3x2 feito na hora (GDI+), com um pixel meio transparente.</summary>
    private static byte[] PngDeTeste()
    {
        using var bmp = new System.Drawing.Bitmap(3, 2, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        bmp.SetPixel(0, 0, System.Drawing.Color.FromArgb(255, 255, 0, 0));
        bmp.SetPixel(1, 0, System.Drawing.Color.FromArgb(128, 0, 0, 255));
        bmp.SetPixel(2, 1, System.Drawing.Color.FromArgb(255, 0, 255, 0));
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    public override string Autoteste()
    {
        var r = new StringBuilder();
        void Conferir(string nome, bool ok, string detalhe = "") =>
            r.AppendLine((ok ? "ok      " : "FALHOU  ") + nome + (detalhe.Length > 0 ? $" ({detalhe})" : ""));

        // ---- mensagens do app
        var ola = ProtocoloAcompanhante.Ler("""{"tipo":"ola","versao":1,"nome":"  Clawd  "}""");
        Conferir("ola: nome sem espaço e versão", ola is { Tipo: "ola", Nome: "Clawd", Versao: 1 }, $"{ola}");
        var longo = ProtocoloAcompanhante.Ler("{\"tipo\":\"ola\",\"nome\":\"" + new string('x', 100) + "\"}");
        Conferir("ola: nome comprido é cortado", longo?.Nome?.Length == ProtocoloAcompanhante.MAIOR_NOME);
        Conferir("ola: nome vazio vira nulo", ProtocoloAcompanhante.Ler("""{"tipo":"ola","nome":""}""")?.Nome == null);
        var png = PngDeTeste();
        string b64 = Convert.ToBase64String(png);
        var q = ProtocoloAcompanhante.Ler($$"""{"tipo":"quadro","largura":3,"altura":2,"png_base64":"{{b64}}","pixelado":false}""");
        Conferir("quadro: lê tamanho, PNG e pixelado", q is { Tipo: "quadro", Largura: 3, Altura: 2, Pixelado: false } && q.Png!.SequenceEqual(png));
        Conferir("quadro: pixelado é o padrão", ProtocoloAcompanhante.Ler($$"""{"tipo":"quadro","largura":3,"altura":2,"png_base64":"{{b64}}"}""")?.Pixelado == true);
        Conferir("quadro: tamanho fora do limite é recusado",
            ProtocoloAcompanhante.Ler($$"""{"tipo":"quadro","largura":0,"altura":2,"png_base64":"{{b64}}"}""") == null
            && ProtocoloAcompanhante.Ler($$"""{"tipo":"quadro","largura":3,"altura":9000,"png_base64":"{{b64}}"}""") == null);
        Conferir("quadro: base64 quebrado é recusado", ProtocoloAcompanhante.Ler("""{"tipo":"quadro","largura":3,"altura":2,"png_base64":"@@@"}""") == null);
        Conferir("entrar, sair", ProtocoloAcompanhante.Ler("""{"tipo":"entrar"}""")?.Tipo == "entrar" && ProtocoloAcompanhante.Ler("""{"tipo":"sair"}""")?.Tipo == "sair");
        Conferir("pairando dentro e fora", ProtocoloAcompanhante.Ler("""{"tipo":"pairando","dentro":true}""")?.Sim == true
            && ProtocoloAcompanhante.Ler("""{"tipo":"pairando","dentro":false}""")?.Sim == false
            && ProtocoloAcompanhante.Ler("""{"tipo":"pairando"}""")?.Sim == false);
        Conferir("visivel: sem campo vale visível", ProtocoloAcompanhante.Ler("""{"tipo":"visivel"}""")?.Sim == true
            && ProtocoloAcompanhante.Ler("""{"tipo":"visivel","sim":false}""")?.Sim == false);
        Conferir("recusou com motivo", ProtocoloAcompanhante.Ler("""{"tipo":"recusou","motivo":"agora não"}""")?.Motivo == "agora não");
        Conferir("tipo desconhecido, JSON quebrado e lista são ignorados",
            ProtocoloAcompanhante.Ler("""{"tipo":"apagar_tudo"}""") == null && ProtocoloAcompanhante.Ler("{ quebrado") == null
            && ProtocoloAcompanhante.Ler("[1,2]") == null && ProtocoloAcompanhante.Ler("""{"tipo":3}""") == null);

        // ---- mensagens da ilha
        var palco = JsonNode.Parse(ProtocoloAcompanhante.Palco(new System.Drawing.Rectangle(10, 20, 300, 45), false))!.AsObject();
        Conferir("palco: retângulo e pode_mostrar", (string?)palco["tipo"] == "palco" && (int?)palco["x"] == 10 && (int?)palco["y"] == 20
            && (int?)palco["largura"] == 300 && (int?)palco["altura"] == 45 && (bool?)palco["pode_mostrar"] == false);
        var ponto = JsonNode.Parse(ProtocoloAcompanhante.Ponto("arrasto_saida", -5, 1500))!.AsObject();
        Conferir("ponto: tipo e cursor (coordenada negativa de outro monitor)", (string?)ponto["tipo"] == "arrasto_saida" && (int?)ponto["x"] == -5 && (int?)ponto["y"] == 1500);
        Conferir("uma linha só por mensagem", !ProtocoloAcompanhante.Palco(new System.Drawing.Rectangle(0, 0, 1, 1), true).Contains('\n')
            && ProtocoloAcompanhante.Simples("pedir_sair") == """{"tipo":"pedir_sair"}""");

        // ---- PNG para os pixels da superfície
        var px = ProtocoloAcompanhante.Pixels(png, out int pl, out int pa);
        Conferir("PNG: tamanho", px != null && pl == 3 && pa == 2 && px.Length == 24, $"{pl}x{pa}");
        if (px != null)
        {
            Conferir("PNG: BGRA, vermelho opaco", px[0] == 0 && px[1] == 0 && px[2] == 255 && px[3] == 255, $"{px[0]},{px[1]},{px[2]},{px[3]}");
            Conferir("PNG: pré-multiplicado (azul a 50%)", px[4] >= 126 && px[4] <= 129 && px[7] == 128, $"{px[4]},{px[7]}");
            Conferir("PNG: transparente fica zerado", px[8] == 0 && px[11] == 0);
        }
        Conferir("PNG quebrado devolve nulo", ProtocoloAcompanhante.Pixels(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, out _, out _) == null);

        // ---- linhas do pipe
        var j = new JuntadorDeLinhas(32);
        var l1 = j.Receber(Encoding.UTF8.GetBytes("{\"a\":1}\n{\"b\""));
        var l2 = j.Receber(Encoding.UTF8.GetBytes(":2}\r\n\n{\"c\":3}\n"));
        Conferir("juntador: linha partida em dois pedaços, \\r e linha vazia", l1.Count == 1 && l1[0] == "{\"a\":1}" && l2.SequenceEqual(new[] { "{\"b\":2}", "{\"c\":3}" }),
            string.Join("|", l1.Concat(l2)));
        var acento = Encoding.UTF8.GetBytes("{\"n\":\"ção\"}\n");
        var ja = new JuntadorDeLinhas(64);
        var la = ja.Receber(acento.AsSpan(0, 9)).Concat(ja.Receber(acento.AsSpan(9))).ToList();
        Conferir("juntador: UTF-8 partido no meio de uma letra", la.Count == 1 && la[0] == "{\"n\":\"ção\"}", la.FirstOrDefault() ?? "");
        var jg = new JuntadorDeLinhas(10);
        jg.Receber(Encoding.UTF8.GetBytes("0123456789ABCDEF"));
        Conferir("juntador: linha grande demais fecha", jg.Estourou);

        // ---- encaixe do quadro
        var e1 = Encaixe.Calcular(11, 8, 80, 30, pixelado: true);
        Conferir("encaixe: pixel art cresce em múltiplo inteiro", e1 == new Encaixe(33, 24, true), e1.ToString());
        var e2 = Encaixe.Calcular(51, 69, 80, 30, pixelado: true);
        Conferir("encaixe: maior que a área encolhe suavizado", e2.A == 30 && e2.L == 22 && !e2.Vizinho, e2.ToString());
        var e3 = Encaixe.Calcular(100, 50, 80, 80, pixelado: false);
        Conferir("encaixe: sem pixel art, cabe na largura", e3 == new Encaixe(80, 40, false), e3.ToString());
        Conferir("encaixe: área vazia não quebra", Encaixe.Calcular(10, 10, 0, 0, true).L >= 1);

        // ---- arrastar para fora
        var a = new ArrasteParaFora();
        var passos = new List<ArrasteParaFora.Acao>
        {
            a.Passo(10, 10, false), a.Passo(12, 11, false), a.Passo(16, 10, false), a.Passo(30, 40, false), a.Passo(31, 41, true),
        };
        Conferir("arrastar: aperta, mexe pouco, passa de 4, move e solta", passos.SequenceEqual(new[]
            { ArrasteParaFora.Acao.Nada, ArrasteParaFora.Acao.Nada, ArrasteParaFora.Acao.Saiu, ArrasteParaFora.Acao.Moveu, ArrasteParaFora.Acao.Soltou }),
            string.Join(",", passos));
        var b = new ArrasteParaFora();
        Conferir("arrastar: apertar e soltar parado não sai (vale o clique)", b.Passo(5, 5, false) == ArrasteParaFora.Acao.Nada && b.Passo(6, 6, true) == ArrasteParaFora.Acao.Nada
            && b.Passo(5, 5, false) == ArrasteParaFora.Acao.Nada && b.Passo(20, 5, false) == ArrasteParaFora.Acao.Saiu);
        Conferir("arrastar: soltar sem ter apertado não faz nada", new ArrasteParaFora().Passo(1, 1, true) == ArrasteParaFora.Acao.Nada);

        // ---- palco com a ilha escondida e comando do app
        var esc = Palcos.Escondida(0, 0, 950, 1.25f);
        Conferir("palco escondido: no meio do topo da janela", esc == new System.Drawing.Rectangle((950 - 275) / 2, 0, 275, 70), esc.ToString());
        Conferir("comando com aspas", Comando.Dividir("\"C:\\Program Files\\Python\\pythonw.exe\" \"C:\\x y\\m.pyw\" --a") == ("C:\\Program Files\\Python\\pythonw.exe", "\"C:\\x y\\m.pyw\" --a"));
        Conferir("comando sem aspas e sem argumento", Comando.Dividir(" pythonw  m.pyw ") == ("pythonw", "m.pyw") && Comando.Dividir("app.exe") == ("app.exe", ""));
        Conferir("comando com %VARIAVEL% no programa e nos argumentos", Comando.Dividir("%SystemRoot%\\a.exe \"%SystemRoot%\\b c\"")
            == (Environment.GetEnvironmentVariable("SystemRoot") + "\\a.exe", "\"" + Environment.GetEnvironmentVariable("SystemRoot") + "\\b c\""));
        Conferir("comando vazio ou aspas abertas", Comando.Dividir("") == null && Comando.Dividir(null) == null && Comando.Dividir("\"sem fim") == null);
        Conferir("config: comando vazio e o argumento padrão", new ConfigMascote() is { ComandoAbrir: "", ArgumentoNaIlha: "--na-ilha" });

        // ---- o servidor de verdade, num nome só deste teste
        TestarServidor(Conferir, b64);
        return r.ToString().TrimEnd();
    }

    private static void TestarServidor(Action<string, bool, string> conferir, string b64)
    {
        string nome = "ilha-acompanhante-teste-" + Environment.ProcessId;
        var conectou = new ManualResetEventSlim();
        var caiu = new ManualResetEventSlim();
        var chegaram = new List<ProtocoloAcompanhante.Recebida>();
        var trava = new object();
        var chegouQuadro = new ManualResetEventSlim();
        using var srv = new ServidorAcompanhante(nome, () => conectou.Set(),
            m => { lock (trava) chegaram.Add(m); if (m.Tipo == "quadro") chegouQuadro.Set(); }, () => caiu.Set());
        try
        {
            using var cli = new NamedPipeClientStream(".", nome, PipeDirection.InOut);
            cli.Connect(3000);
            conferir("servidor: aceita o cliente do mesmo usuário", conectou.Wait(3000), "");
            srv.Enviar(ProtocoloAcompanhante.Simples("pedir_entrar"));
            var leitor = new StreamReader(cli, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            var lida = leitor.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            conferir("servidor: manda uma linha", lida == """{"tipo":"pedir_entrar"}""", lida ?? "nada");
            var saida = Encoding.UTF8.GetBytes("{\"tipo\":\"ola\",\"nome\":\"Teste\"}\n{\"tipo\":\"quadro\",\"largura\":3,\"altura\":2,\"png_base64\":\"" + b64 + "\"}\n");
            cli.Write(saida, 0, saida.Length);
            cli.Flush();
            bool veio = chegouQuadro.Wait(3000);
            lock (trava) conferir("servidor: recebe ola e quadro em ordem", veio && chegaram.Count == 2 && chegaram[0].Nome == "Teste" && chegaram[1].Tipo == "quadro", string.Join(",", chegaram.Select(x => x.Tipo)));
            cli.Dispose();
            conferir("servidor: avisa quando o cliente sai", caiu.Wait(3000), "");
            caiu.Reset();
            conectou.Reset();
            using var cli2 = new NamedPipeClientStream(".", nome, PipeDirection.InOut);
            cli2.Connect(3000);
            conferir("servidor: aceita o app de novo depois que ele saiu", conectou.Wait(3000), "");
        }
        catch (Exception e) { conferir("servidor: " + e.GetType().Name + ": " + e.Message, false, ""); }
    }
}
