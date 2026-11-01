using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Windows.Media.Control;

namespace Ilha.Mascote;

internal sealed partial class ModuloMascote
{
    // folha de exemplo em Hash, com Split Layers, trim, durações próprias e três tags
    private const string JsonHash = """
    {
      "frames": {
        "boneco (corpo) 0.aseprite": { "frame": { "x": 0, "y": 0, "w": 2, "h": 2 }, "spriteSourceSize": { "x": 0, "y": 0, "w": 2, "h": 2 }, "sourceSize": { "w": 3, "h": 2 }, "duration": 120 },
        "boneco (olho) 0.aseprite":  { "frame": { "x": 2, "y": 0, "w": 1, "h": 1 }, "spriteSourceSize": { "x": 2, "y": 1, "w": 1, "h": 1 }, "sourceSize": { "w": 3, "h": 2 }, "duration": 120 },
        "boneco (corpo) 1.aseprite": { "frame": { "x": 3, "y": 0, "w": 2, "h": 2 }, "spriteSourceSize": { "x": 1, "y": 0, "w": 2, "h": 2 }, "sourceSize": { "w": 3, "h": 2 }, "duration": 80 },
        "boneco (corpo) 2.aseprite": { "frame": { "x": 0, "y": 0, "w": 2, "h": 2 }, "spriteSourceSize": { "x": 0, "y": 0, "w": 2, "h": 2 }, "sourceSize": { "w": 3, "h": 2 }, "duration": 100 },
        "boneco (corpo) 3.aseprite": { "frame": { "x": 3, "y": 0, "w": 2, "h": 2 }, "spriteSourceSize": { "x": 0, "y": 0, "w": 2, "h": 2 }, "sourceSize": { "w": 3, "h": 2 }, "duration": 100 }
      },
      "meta": {
        "app": "https://www.aseprite.org/", "image": "boneco.png", "format": "RGBA8888", "size": { "w": 5, "h": 2 }, "scale": "1",
        "frameTags": [
          { "name": "andando", "from": 0, "to": 2, "direction": "forward" },
          { "name": "Dançando", "from": 0, "to": 3, "direction": "pingpong" },
          { "name": "voltando", "from": 1, "to": 3, "direction": "reverse" }
        ],
        "layers": [ { "name": "corpo", "opacity": 255, "blendMode": "normal" }, { "name": "olho", "opacity": 128, "blendMode": "normal" } ],
        "slices": []
      }
    }
    """;

    // a mesma ideia em Array, com nomes "camada|quadro" e sem tag nenhuma
    private const string JsonArray = """
    {
      "frames": [
        { "filename": "base|0", "frame": { "x": 0, "y": 0, "w": 4, "h": 4 }, "sourceSize": { "w": 4, "h": 4 }, "duration": 100 },
        { "filename": "base|1", "frame": { "x": 4, "y": 0, "w": 4, "h": 4 }, "sourceSize": { "w": 4, "h": 4 }, "duration": 100 },
        { "filename": "chapeu|1", "frame": { "x": 8, "y": 0, "w": 2, "h": 1 }, "spriteSourceSize": { "x": 1, "y": 0, "w": 2, "h": 1 }, "sourceSize": { "w": 4, "h": 4 }, "duration": 100 }
      ],
      "meta": { "image": "pulando.png", "size": { "w": 10, "h": 4 } }
    }
    """;

    private const string JsonAjustes = """
    {
      "fps": 4,
      // comentário e vírgula sobrando também passam
      "animacoes": {
        "parado": { "ancora": [5.5, 8] },
        "trabalhando": { "quadros": 2, "ancora": [12.5, 7] },
        "andando": { "fps": 10 },
        "gerada": { "crescer": "parado", "quadros": 6, "fps": 15 },
      }
    }
    """;

    public override string Autoteste()
    {
        var r = new StringBuilder();
        void Conferir(string nome, bool ok, string detalhe = "") =>
            r.AppendLine((ok ? "ok      " : "FALHOU  ") + nome + (detalhe.Length > 0 ? $" ({detalhe})" : ""));
        string L(IEnumerable<int> v) => string.Join(",", v);

        // ---- nomes dos pedaços
        Conferir("nome padrão do Aseprite", Folha.Nome("boneco (corpo) 12.aseprite", 0) == ("corpo", 12));
        Conferir("nome camada|quadro", Folha.Nome("chapéu azul|3", 0) == ("chapéu azul", 3));
        Conferir("nome só com quadro", Folha.Nome("idle 7.aseprite", 0) == ("base", 7));
        Conferir("nome sem número usa a posição", Folha.Nome("idle.aseprite", 5) == ("base", 5));

        // ---- folha em Hash
        Conferir("reconhece folha do Aseprite", Folha.EhFolha(JsonHash) && Folha.EhFolha(JsonArray) && !Folha.EhFolha(JsonAjustes) && !Folha.EhFolha("{ quebrado"));
        var fh = Folha.DoJson(JsonHash, "boneco.json");
        Conferir("hash: imagem, tamanho do quadro, quadros", fh.Imagem == "boneco.png" && fh.Largura == 3 && fh.Altura == 2 && fh.Quadros.Count == 4,
            $"{fh.Imagem} {fh.Largura}x{fh.Altura} {fh.Quadros.Count}");
        Conferir("hash: camadas em ordem e opacidade", string.Join(",", fh.Camadas) == "corpo,olho" && Math.Abs(fh.Opacidade["olho"] - 128 / 255f) < 0.001f);
        Conferir("hash: duas camadas no quadro 0, trim guardado", fh.Quadros[0].Count == 2 && fh.Quadros[0][1] == new Peca("olho", 2, 0, 1, 1, 2, 1));
        Conferir("hash: três tags", fh.Tags.Count == 3 && fh.Tags.ContainsKey("Dançando"));
        Conferir("forward", L(fh.QuadrosDaTag("andando")) == "0,1,2", L(fh.QuadrosDaTag("andando")));
        Conferir("pingpong não repete as pontas", L(fh.QuadrosDaTag("Dançando")) == "0,1,2,3,2,1", L(fh.QuadrosDaTag("Dançando")));
        Conferir("reverse", L(fh.QuadrosDaTag("voltando")) == "3,2,1", L(fh.QuadrosDaTag("voltando")));
        var duas = Folha.DoJson(JsonHash.Replace("\"to\": 3, \"direction\": \"pingpong\"", "\"to\": 1, \"direction\": \"pingpong\""), "x.json");
        Conferir("pingpong de dois quadros", L(duas.QuadrosDaTag("Dançando")) == "0,1");
        var pr = Folha.DoJson(JsonHash.Replace("\"direction\": \"reverse\"", "\"direction\": \"pingpong_reverse\""), "x.json");
        Conferir("pingpong_reverse", L(pr.QuadrosDaTag("voltando")) == "3,2,1,2", L(pr.QuadrosDaTag("voltando")));

        // ---- durações
        var seq = fh.QuadrosDaTag("andando");
        Conferir("duração de cada quadro do Aseprite", L(fh.Duracoes(seq, null, 4)) == "120,80,100", L(fh.Duracoes(seq, null, 4)));
        Conferir("fps próprio vale sempre", L(fh.Duracoes(seq, 10, 4)) == "100,100,100");
        var padrao = fh.QuadrosDaTag("voltando"); // 3,2,1: o 1 tem 80 ms, então não está "sem tempo"
        Conferir("fps geral só quando ninguém mexeu no tempo", L(fh.Duracoes(new[] { 2, 3 }, null, 4)) == "250,250" && L(fh.Duracoes(padrao, null, 4)) == "100,100,80");
        Conferir("sem fps nenhum, 100 ms", L(fh.Duracoes(new[] { 2, 3 }, null, null)) == "100,100");
        Conferir("fps inválido é ignorado", Folha.MsDoFps(0) == null && Folha.MsDoFps(-3) == null && Folha.MsDoFps(4) == 250);

        // ---- folha em Array, sem tag
        var fa = Folha.DoJson(JsonArray, "pulando.json");
        Conferir("array: tag com o nome do arquivo", fa.Tags.Count == 1 && L(fa.QuadrosDaTag("pulando")) == "0,1");
        Conferir("array: camada só no quadro 1", fa.Quadros[0].Count == 1 && fa.Quadros[1].Count == 2 && fa.Quadros[1][1].Dx == 1);

        // ---- PNG em tira
        var tira = Folha.DaTira("trabalhando.png", 32, 7, 2);
        Conferir("tira: dois quadros de 16x7", tira.Largura == 16 && tira.Altura == 7 && tira.Quadros[1][0].X == 16 && L(tira.QuadrosDaTag("trabalhando")) == "0,1");

        // ---- animacoes.json
        var aj = Ajustes.Ler(JsonAjustes);
        Conferir("ajustes: fps geral, quadros, âncora, fps próprio", aj.Fps == 4 && aj.De("trabalhando")?.Quadros == 2
            && aj.De("parado")?.Ancora == new System.Numerics.Vector2(5.5f, 8) && aj.De("ANDANDO")?.Fps == 10);
        Conferir("ajustes ilegível vira vazio", Ajustes.Ler("{ isso não é json").Animacoes.Count == 0 && Ajustes.Ler(null).Fps == null);

        // ---- montar quadros: camadas, trim, opacidade
        // imagem 5x2: (0,0)-(1,1) vermelho opaco; (2,0) branco; (3,0)-(4,1) azul
        var img = new byte[5 * 2 * 4];
        void Px(int x, int y, byte b, byte g, byte rr, byte a) { int i = (y * 5 + x) * 4; img[i] = b; img[i + 1] = g; img[i + 2] = rr; img[i + 3] = a; }
        for (int y = 0; y < 2; y++) { Px(0, y, 0, 0, 255, 255); Px(1, y, 0, 0, 255, 255); Px(3, y, 255, 0, 0, 255); Px(4, y, 255, 0, 0, 255); }
        Px(2, 0, 255, 255, 255, 255);
        var q0 = Montagem.Quadro(fh, 0, img, 5, 2);
        byte[] Em(byte[] q, int x, int y, int w) => q.Skip((y * w + x) * 4).Take(4).ToArray();
        Conferir("camada de baixo no lugar", Em(q0, 0, 0, 3).SequenceEqual(new byte[] { 0, 0, 255, 255 }));
        var olho = Em(q0, 2, 1, 3);
        Conferir("camada de cima com trim e opacidade 50%", olho[3] > 120 && olho[3] < 135 && olho[0] == 255, string.Join(",", olho));
        var q1 = Montagem.Quadro(fh, 1, img, 5, 2);
        Conferir("trim desloca o pedaço", Em(q1, 0, 0, 3)[3] == 0 && Em(q1, 1, 0, 3).SequenceEqual(new byte[] { 255, 0, 0, 255 }));
        var fMeio = Folha.DoJson(JsonHash.Replace("\"x\": 2, \"y\": 1, \"w\": 1, \"h\": 1 }", "\"x\": 0, \"y\": 0, \"w\": 1, \"h\": 1 }"), "x.json");
        var sobre = Em(Montagem.Quadro(fMeio, 0, img, 5, 2), 0, 0, 3);
        Conferir("over: branco a 50% sobre vermelho", sobre[3] == 255 && sobre[2] == 255 && sobre[0] > 120 && sobre[0] < 135, string.Join(",", sobre));

        var anims = Montagem.Animacoes(fh, img, 5, 2, aj);
        var andando = anims.First(a => a.Nome == "andando");
        Conferir("animação: quadros únicos e sequência", anims.Count == 3 && anims.First(a => a.Nome == "Dançando").Quadros.Count == 4
            && L(anims.First(a => a.Nome == "Dançando").Sequencia) == "0,1,2,3,2,1");
        Conferir("animação: fps próprio do animacoes.json", L(andando.Ms) == "100,100,100" && andando.Total == 300);
        Conferir("animação: âncora padrão é o pé no meio", andando.Ancora == new System.Numerics.Vector2(1.5f, 2));

        // ---- ampliar sem borrar
        var dois = new byte[] { 0, 0, 255, 255, 255, 0, 0, 128, 0, 255, 0, 255, 0, 0, 0, 0 }; // 2x2
        var x2 = Montagem.Tira(new[] { dois }, 2, 2, 4, 4);
        Conferir("vizinho mais próximo inteiro (2x)", Em(x2, 0, 0, 4).SequenceEqual(new byte[] { 0, 0, 255, 255 }) && Em(x2, 1, 1, 4).SequenceEqual(new byte[] { 0, 0, 255, 255 })
            && Em(x2, 3, 3, 4)[3] == 0 && Em(x2, 2, 0, 4).SequenceEqual(new byte[] { 128, 0, 0, 128 }), "pré-multiplicado: " + string.Join(",", Em(x2, 2, 0, 4)));
        var x15 = Montagem.Tira(new[] { dois }, 2, 2, 3, 3);
        bool soCoresDaOrigem = Enumerable.Range(0, 9).All(i => new[] { 255, 128, 0 }.Contains(x15[i * 4 + 3]));
        Conferir("vizinho mais próximo fracionário (1,5x) não inventa cor", soCoresDaOrigem);
        var duasNaTira = Montagem.Tira(new[] { dois, dois }, 2, 2, 2, 2);
        Conferir("tira com dois quadros lado a lado", duasNaTira.Length == 4 * 2 * 4 && Em(duasNaTira, 2, 0, 4).SequenceEqual(new byte[] { 0, 0, 255, 255 }));
        Conferir("escala inteira quando perde pouco", Montagem.Escala(11, 8, 30, 30) == 2 && Montagem.Escala(11, 8, 150, 30) == 3);
        Conferir("escala fracionária quando o inteiro encolhe demais", Math.Abs(Montagem.Escala(16, 7, 30, 30) - 1.875f) < 0.001f);

        // ---- escolha do estado e da tag
        var vinte = TimeSpan.FromMinutes(20);
        Conferir("estado: Claude ganha de música", Escolha.Estado(true, true, TimeSpan.FromHours(1), vinte) == EstadoMascote.Trabalhando);
        Conferir("estado: música dança", Escolha.Estado(false, true, TimeSpan.FromHours(1), vinte) == EstadoMascote.Dancando);
        Conferir("estado: 20 min parado dorme", Escolha.Estado(false, false, vinte, vinte) == EstadoMascote.Dormindo
            && Escolha.Estado(false, false, TimeSpan.FromMinutes(19.9), vinte) == EstadoMascote.Parado);
        Conferir("estado: dormir 0 = nunca", Escolha.Estado(false, false, TimeSpan.FromDays(1), TimeSpan.Zero) == EstadoMascote.Parado);
        Conferir("aparece: área de trabalho, música ou Claude; sem folha nunca",
            Escolha.Aparece(true, false, false, true) && Escolha.Aparece(true, false, true, false) && Escolha.Aparece(true, true, false, false)
            && !Escolha.Aparece(true, false, false, false) && !Escolha.Aparece(false, true, true, true));
        var tresTags = new[] { "idle", "pergunta", "trabalhando" };
        Conferir("tag: trabalhando", Escolha.Tag(tresTags,EstadoMascote.Trabalhando) == ("trabalhando", Efeito.Nenhum));
        Conferir("tag: sem dança, o parado dança no compositor", Escolha.Tag(tresTags,EstadoMascote.Dancando) == ("idle", Efeito.Danca));
        Conferir("tag: sem dormir, o parado dorme no compositor", Escolha.Tag(tresTags,EstadoMascote.Dormindo) == ("idle", Efeito.Sono));
        Conferir("tag: parado", Escolha.Tag(tresTags,EstadoMascote.Parado) == ("idle", Efeito.Nenhum));
        var completo = new[] { "Parado", "Dançando", "Dormindo", "Digitando" };
        Conferir("tag: com acento e maiúscula", Escolha.Tag(completo, EstadoMascote.Dancando) == ("Dançando", Efeito.Nenhum)
            && Escolha.Tag(completo, EstadoMascote.Dormindo) == ("Dormindo", Efeito.Nenhum) && Escolha.Tag(completo, EstadoMascote.Trabalhando) == ("Digitando", Efeito.Nenhum));
        Conferir("tag: sem trabalho cai no parado", Escolha.Tag(new[] { "idle" }, EstadoMascote.Trabalhando) == ("idle", Efeito.Nenhum));
        Conferir("tag: sem parado, a primeira em ordem", Escolha.Tag(new[] { "zumbi", "acenando" }, EstadoMascote.Parado) == ("acenando", Efeito.Nenhum));
        Conferir("tag: folha vazia", Escolha.Tag(Array.Empty<string>(), EstadoMascote.Parado).Tag == null);

        // ---- encaixe na cena (125%: pílula de 24 DIP = 30 px, bolha 30x30, largura máxima 150 px)
        foreach (var (w, h) in new[] { (11, 8), (16, 7), (16, 17), (64, 64), (1, 1) })
            foreach (var ef in new[] { Efeito.Nenhum, Efeito.Danca, Efeito.Sono })
                foreach (bool bol in new[] { false, true })
                {
                    int maxL = bol ? 30 : 150, maxA = 30;
                    var a = Escolha.Arranjar(w, h, maxL, maxA, ef, bol);
                    bool dentro = a.X >= 0 && a.Y >= 0 && a.X + a.L <= a.CenaL && a.Y + a.A <= a.CenaA && a.CenaL <= maxL && a.CenaA == maxA
                        && (!bol || a.CenaL == maxL) && a.Y - a.Pulo >= 0 && a.L > 0 && a.A > 0;
                    if (ef == Efeito.Sono) dentro &= 4 * a.ZEscala <= a.CenaL && 4 * a.ZEscala <= a.CenaA * 0.3f + 1;
                    if (!dentro) Conferir($"encaixe {w}x{h} {ef} {(bol ? "bolha" : "pílula")}", false, a.ToString());
                }
        Conferir("encaixe: tudo dentro da cena em todos os casos", !r.ToString().Contains("FALHOU  encaixe"));
        var pil = Escolha.Arranjar(11, 8, 150, 30, Efeito.Nenhum, false);
        Conferir("encaixe: parado na pílula em 3x", pil.Escala == 3 && pil.L == 33 && pil.A == 24 && pil.CenaL == 33, pil.ToString());
        var dan = Escolha.Arranjar(11, 8, 150, 30, Efeito.Danca, false);
        Conferir("encaixe: a dança tem espaço para pular e balançar", dan.Pulo >= 2 && dan.CenaL > dan.L, dan.ToString());

        // ---- o "z"
        var zp = AtividadeMascote.PixelsDoZ(2, new Vortice.Mathematics.Color4(1, 1, 1, 1));
        int acesos = Enumerable.Range(0, 64).Count(i => zp[i * 4 + 3] == 255);
        Conferir("z de pixel art 4x4 ampliado 2x", zp.Length == 8 * 8 * 4 && acesos == 10 * 4, acesos.ToString());

        // ---- Claude Code pela fila
        var cl = new SessoesDoClaude();
        cl.Receber("""{"t": 1000.5, "tipo": "ouvindo", "sessao": "a"}""");
        Conferir("claude: ouvindo = trabalhando", cl.Trabalhando(1001));
        cl.Receber("""{"t": 1000.5, "tipo": "terminou", "sessao": "a"}""");
        Conferir("claude: evento repetido (mesma hora) é pulado", cl.Trabalhando(1001));
        cl.Receber("""{"t": 1010, "tipo": "terminou", "sessao": "a"}""");
        Conferir("claude: terminou = parado", !cl.Trabalhando(1011));
        cl.Receber("""{"t": 1020, "tipo": "rodando", "sessao": "b", "detalhe": "dotnet build"}""");
        cl.Receber("isso não é json");
        cl.Receber("""{"t": 1030, "tipo": "lendo", "sessao": "a"}""");
        cl.Receber("""{"t": 1040, "tipo": "notificacao", "sessao": "a"}""");
        Conferir("claude: uma sessão trabalhando basta", cl.Trabalhando(1041));
        Conferir("claude: vence 15 min depois do último evento", cl.Vencimento(1041) == 1020 + SessoesDoClaude.ESQUECIDO
            && !cl.Trabalhando(1020 + SessoesDoClaude.ESQUECIDO + 1) && cl.Vencimento(1020 + SessoesDoClaude.ESQUECIDO + 1) == null);
        cl.Receber("""{"t": 1050, "tipo": "fala", "sessao": "b", "texto": "x"}""");
        Conferir("claude: outro evento da sessão renova o prazo", cl.Trabalhando(1020 + SessoesDoClaude.ESQUECIDO + 1));
        cl.Receber("""{"t": 1060, "tipo": "fim", "sessao": "b"}""");
        Conferir("claude: fim encerra a sessão", !cl.Trabalhando(1061) && cl.Sessoes == 1);
        cl.Receber("""{"t": 1070, "tipo": "digitando"}""");
        Conferir("claude: evento sem sessão também conta", cl.Trabalhando(1071));

        // ---- leitura incremental da fila (arquivo temporário próprio)
        string tmp = Path.Combine(Path.GetTempPath(), "ilha-mascote-teste-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var lt = new LeitorDeLinhas();
            Conferir("fila: arquivo que não existe", lt.Novas(tmp).Count == 0);
            File.WriteAllText(tmp, "{\"t\":1}\n{\"t\":2}\n{\"t\":3,\"meio");
            var a1 = lt.Novas(tmp);
            Conferir("fila: lê as linhas inteiras e guarda a pela metade", a1.Count == 2 && a1[1] == "{\"t\":2}", string.Join("|", a1));
            using (var fs = new FileStream(tmp, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) fs.Write(Encoding.UTF8.GetBytes("\"}\n{\"t\":4}\n"));
            var a2 = lt.Novas(tmp);
            Conferir("fila: completa a linha e lê só o novo", a2.Count == 2 && a2[0] == "{\"t\":3,\"meio\"}" && a2[1] == "{\"t\":4}", string.Join("|", a2));
            Conferir("fila: nada novo", lt.Novas(tmp).Count == 0);
            File.WriteAllText(tmp, "{\"t\":5}\n");
            var a3 = lt.Novas(tmp);
            Conferir("fila: arquivo que encolheu volta ao começo", a3.Count == 1 && a3[0] == "{\"t\":5}", string.Join("|", a3));
            using (var segurando = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                Conferir("fila: lê com o arquivo aberto por quem escreve", new LeitorDeLinhas().Novas(tmp).Count == 1);
            var grande = new StringBuilder();
            for (int i = 0; i < 6000; i++) grande.Append("{\"t\":").Append(i).Append(",\"tipo\":\"lendo\",\"sessao\":\"preenchimento-preenchimento\"}\n");
            File.WriteAllText(tmp, grande.ToString());
            var a4 = new LeitorDeLinhas().Novas(tmp);
            Conferir("fila grande: só o fim, sem linha cortada", a4.Count > 100 && a4.Count < 6000 && a4.All(x => x.StartsWith("{\"t\":") && x.EndsWith("}")) && a4[^1].Contains("5999"), a4.Count.ToString());
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }

        // ---- pasta de folhas de verdade (gerada aqui num temporário próprio)
        string pasta = Path.Combine(Path.GetTempPath(), "ilha-mascote-teste-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(pasta);
            SalvarPng(Path.Combine(pasta, "boneco.png"), img, 5, 2);
            File.WriteAllText(Path.Combine(pasta, "boneco.json"), JsonHash);
            var tiraPx = new byte[32 * 7 * 4];
            for (int i = 0; i < 16 * 7; i++) { int x = i % 16, y = i / 16; int d = (y * 32 + x) * 4; tiraPx[d + 2] = 200; tiraPx[d + 3] = 255; }
            SalvarPng(Path.Combine(pasta, "trabalhando.png"), tiraPx, 32, 7);
            SalvarPng(Path.Combine(pasta, "parado.png"), tiraPx, 11, 7);
            File.WriteAllText(Path.Combine(pasta, Biblioteca.AJUSTES), JsonAjustes);
            File.WriteAllText(Path.Combine(pasta, "outro.json"), "{\"nada\": 1}");
            Directory.CreateDirectory(Path.Combine(pasta, "subpasta"));
            SalvarPng(Path.Combine(pasta, "subpasta", "escondida.png"), tiraPx, 32, 7);
            var bib = Biblioteca.Carregar(pasta);
            var nomes = bib.Nomes.OrderBy(x => x, StringComparer.Ordinal).ToList();
            Conferir("pasta: tags do JSON e tiras soltas, sem o PNG do JSON nem subpasta", string.Join(",", nomes) == "Dançando,andando,parado,trabalhando,voltando", string.Join(",", nomes));
            var trab = bib["trabalhando"];
            Conferir("pasta: tira de 2 quadros a 4 fps com âncora", trab != null && trab.Quadros.Count == 2 && L(trab.Ms) == "250,250" && trab.Ancora == new System.Numerics.Vector2(12.5f, 7)
                && trab.Quadros[0][2] == 200 && trab.Quadros[1][3] == 0, trab == null ? "" : $"{trab.Quadros.Count} {L(trab.Ms)}");
            var dancaReal = bib["dançando"];
            Conferir("pasta: PNG decodificado e camadas juntas", dancaReal != null && dancaReal.Quadros[0].Take(4).SequenceEqual(new byte[] { 0, 0, 255, 255 }));
            Conferir("pasta inexistente ou vazia", Biblioteca.Carregar(Path.Combine(pasta, "nao-existe")).Quantas == 0 && Biblioteca.Carregar("").Quantas == 0);
        }
        catch (Exception e) { Conferir("pasta de folhas gerada", false, e.Message); }
        finally { try { Directory.Delete(pasta, true); } catch (IOException) { } }

        Sondas(r, Conferir);
        return r.ToString().TrimEnd();
    }

    private static void SalvarPng(string arquivo, byte[] bgra, int l, int a)
    {
        using var bmp = new System.Drawing.Bitmap(l, a, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, l, a), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        int origem = bgra.Length / 4 / a; // largura da linha de origem (pode ser maior que l: recorte da esquerda)
        for (int y = 0; y < a; y++) System.Runtime.InteropServices.Marshal.Copy(bgra, y * origem * 4, bd.Scan0 + y * bd.Stride, l * 4);
        bmp.UnlockBits(bd);
        bmp.Save(arquivo, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>Sondas só leitura: o ocioso do Windows, o SMTC e, se configuradas, a pasta das folhas e a fila do Claude.</summary>
    private static void Sondas(StringBuilder r, Action<string, bool, string> conferir)
    {
        var ocioso = Ocioso();
        conferir("sonda: GetLastInputInfo", ocioso >= TimeSpan.Zero && ocioso < TimeSpan.FromDays(50), $"{ocioso.TotalSeconds:0} s sem mouse e teclado");

        try
        {
            var pedido = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
            var g = pedido.Wait(5000) ? pedido.Result : null;
            if (g == null) conferir("sonda: SMTC respondeu", false, "sem resposta em 5 s");
            else
            {
                var ss = g.GetSessions();
                int tocando = ss.Count(s => s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
                conferir("sonda: SMTC respondeu", true, $"{ss.Count} sessões, {tocando} tocando");
            }
        }
        catch (Exception e) { conferir("sonda: SMTC respondeu", false, e.Message); }

        // config da ilha (só leitura); ILHA_TESTE_PASTA_FOLHAS e ILHA_TESTE_EVENTOS trocam o que a config diz
        string pasta = "", eventos = "";
        try
        {
            if (File.Exists(Config.Arquivo) && JsonNode.Parse(File.ReadAllText(Config.Arquivo),
                    documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })?["modulos"]?["mascote"] is JsonObject m)
            {
                pasta = m["pastaFolhas"]?.GetValue<string>() ?? "";
                eventos = m["arquivoEventos"]?.GetValue<string>() ?? "";
            }
        }
        catch (Exception e) { r.AppendLine("ok      sonda: config da ilha não lida (" + e.Message + ")"); }
        pasta = Normal(Environment.GetEnvironmentVariable("ILHA_TESTE_PASTA_FOLHAS") is { Length: > 0 } p ? p : pasta);
        eventos = Normal(Environment.GetEnvironmentVariable("ILHA_TESTE_EVENTOS") is { Length: > 0 } ev ? ev : eventos);

        if (pasta.Length == 0) r.AppendLine("ok      sonda: pastaFolhas não configurada (pulada)");
        else
        {
            var bib = Biblioteca.Carregar(pasta);
            var desc = bib.Nomes.OrderBy(x => x).Select(n => { var a = bib[n]!; return $"{n} {a.Largura}x{a.Altura} {a.Quadros.Count}q {string.Join("/", a.Ms)}ms"; });
            conferir("sonda: folhas da pastaFolhas", bib.Quantas > 0, bib.Quantas == 0 ? "nenhuma animação" : string.Join("; ", desc));
            foreach (EstadoMascote e in Enum.GetValues<EstadoMascote>())
            {
                var (tag, ef) = Escolha.Tag(bib.Nomes, e);
                r.AppendLine($"ok      sonda: {e} toca \"{tag}\"{(ef != Efeito.Nenhum ? $" + {ef} no compositor" : "")}");
            }
        }

        if (eventos.Length == 0) r.AppendLine("ok      sonda: arquivoEventos não configurado (pulado)");
        else if (!File.Exists(eventos)) r.AppendLine("ok      sonda: a fila de eventos ainda não existe (" + Path.GetFileName(eventos) + ")");
        else
        {
            var cl = new SessoesDoClaude();
            var linhas = new LeitorDeLinhas().Novas(eventos);
            foreach (var l in linhas) cl.Receber(l);
            double agora = SessoesDoClaude.Agora();
            conferir("sonda: fila de eventos do Claude", true, $"{linhas.Count} linhas, {cl.Sessoes} sessões, {(cl.Trabalhando(agora) ? "trabalhando" : "parado")}");
        }
    }
}
