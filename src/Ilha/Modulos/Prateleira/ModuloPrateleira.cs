using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Ilha.Extras;

namespace Ilha.Prateleira;

/// <summary>
/// Prateleira de arquivos: arrasta arquivos e pastas para a ilha e eles ficam listados ali; depois arrasta de volta para
/// qualquer lugar (Explorer, After Effects, Photoshop, navegador). Guarda só o caminho, nunca copia nem move; o que
/// sumiu fica na lista, marcado como indisponível. A lista mora na seção do módulo na config ("itens").
/// </summary>
[Modulo("prateleira", "Prateleira", English = "Shelf")]
internal sealed class ModuloPrateleira : Modulo
{
    private ConfigPrateleira _cfg = new();
    private AtividadePrateleira? _atv;
    private readonly List<ItemDaPrateleira> _itens = new();

    public override Type? TipoConfig => typeof(ConfigPrateleira);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigPrateleira>(cfg);
        _itens.Clear();
        _itens.AddRange(LogicaDaPrateleira.Limpar(_cfg.Itens));
        _cfg.Itens = _itens;
        _atv = new AtividadePrateleira(S, _itens, () => _cfg, () => Salvar(_cfg));
        // nunca liga (não fica na pílula), mas precisa estar na ilha para o Expandir
        S.Ilha.Adicionar(_atv);
        S.Ilha.Receptor = _atv;
        var atv = _atv;
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "prateleira",
            Nome = Lang.T("Prateleira", "Shelf"),
            Abrir = atv.Abrir,
            // empty shows in the icon itself (just the plank), so no "empty" line
            Detalhe = () => _itens.Count > 0 ? LogicaDaPrateleira.Contagem(_itens.Count) : null,
            Icone = (dc, palco, r) => AtividadePrateleira.DesenharIcone(dc, r, vazia: _itens.Count == 0),
            Ordem = 60,
        });
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        var novo = Ler<ConfigPrateleira>(cfg);
        var lista = LogicaDaPrateleira.Limpar(novo.Itens);
        _cfg.SegundosAposSair = novo.SegundosAposSair;
        // o próprio Salvar não volta para cá (o núcleo já guarda o texto salvo); chegou lista diferente, alguém editou à mão
        if (!LogicaDaPrateleira.MesmosCaminhos(lista, _itens)) _atv?.TrocarLista(lista);
    }

    public override void Parar()
    {
        if (_atv == null) return;
        if (S.Ilha.Receptor == _atv) S.Ilha.Receptor = null;
        _atv.Parar();
    }

    // ------------------------------------------------------------------ autoteste

    public override string Autoteste()
    {
        var r = new Relatorio();
        var N = (Func<string?, string?>)LogicaDaPrateleira.Normalizar;

        r.Conferir("caminho: barra do fim sai, raiz fica", N(@"C:\Projetos\Abertura\") == @"C:\Projetos\Abertura" && N(@"D:\") == @"D:\");
        r.Conferir("caminho: aspas e espaços em volta saem", N("  \"C:\\Meus Arquivos\\cena final.aep\" ") == @"C:\Meus Arquivos\cena final.aep");
        r.Conferir("caminho: .. é resolvido", N(@"C:\a\..\b\c.psd") == @"C:\b\c.psd");
        r.Conferir("caminho: relativo, vazio e sem barra depois do drive são recusados",
            N(@"pasta\a.txt") == null && N("") == null && N("   ") == null && N(null) == null && N("C:") == null && N(@"C:arquivo.txt") == null);
        r.Conferir("caminho: item virtual do shell é recusado", N("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}") == null);
        r.Conferir("caminho: rede (UNC) aceita, sem barra no fim", N(@"\\servidor\compartilhado\videos\") == @"\\servidor\compartilhado\videos");

        var t0 = new DateTime(2026, 11, 1, 15, 0, 0);
        var itens = new List<ItemDaPrateleira>();
        int novos = LogicaDaPrateleira.Adicionar(itens, new[] { @"C:\x\a.png", @"C:\x\b.mp4", @"c:\X\A.PNG", @"C:\x\a.png\", "relativo.txt" }, t0);
        r.Conferir("soltar: repetido no mesmo arraste conta uma vez, relativo fica de fora",
            novos == 2 && itens.Count == 2 && itens[0].Caminho == @"C:\x\a.png" && itens[1].Caminho == @"C:\x\b.mp4", $"novos={novos} itens={itens.Count}");
        novos = LogicaDaPrateleira.Adicionar(itens, new[] { @"C:\x\c.wav" }, t0.AddMinutes(1));
        r.Conferir("soltar: o novo entra no topo", novos == 1 && itens.Count == 3 && itens[0].Caminho == @"C:\x\c.wav");
        novos = LogicaDaPrateleira.Adicionar(itens, new[] { @"C:\X\B.MP4" }, t0.AddMinutes(2));
        r.Conferir("soltar: o que já estava não duplica, sobe para o topo e guarda a grafia de antes",
            novos == 0 && itens.Count == 3 && itens[0].Caminho == @"C:\x\b.mp4" && itens[0].Em == t0.AddMinutes(2));
        novos = LogicaDaPrateleira.Adicionar(itens, new[] { @"C:\x\d.psd", @"C:\x\e.psd" }, t0.AddMinutes(3));
        r.Conferir("soltar: vários juntos entram no topo na ordem em que vieram", novos == 2 && itens[0].Caminho == @"C:\x\d.psd" && itens[1].Caminho == @"C:\x\e.psd" && itens.Count == 5);

        bool m1 = LogicaDaPrateleira.Marcar(itens, @"c:\x\a.png", existe: false, pasta: null);
        var a = itens.Find(x => x.Caminho == @"C:\x\a.png");
        r.Conferir("sumiu: fica na lista, marcado como indisponível", m1 && itens.Count == 5 && a != null && a.Disponivel == false && !a.Lento);
        bool m2 = LogicaDaPrateleira.Marcar(itens, @"C:\x\a.png", existe: false, pasta: null);
        r.Conferir("sumiu: marcar de novo igual não conta como mudança", !m2);
        LogicaDaPrateleira.Marcar(itens, @"C:\x\a.png", existe: false, pasta: null, lento: true);
        r.Conferir("drive que não responde: marcado como lento", a!.Disponivel == false && a.Lento);
        bool m3 = LogicaDaPrateleira.Marcar(itens, @"C:\x\a.png", existe: true, pasta: false);
        r.Conferir("voltou: disponível de novo", m3 && a.Disponivel == true && !a.Lento && a.Pasta == false);
        LogicaDaPrateleira.Marcar(itens, @"C:\x\e.psd", existe: true, pasta: true);
        LogicaDaPrateleira.Marcar(itens, @"C:\x\e.psd", existe: false, pasta: false);
        r.Conferir("pasta que sumiu continua desenhada como pasta", itens.Find(x => x.Caminho == @"C:\x\e.psd")!.Pasta == true);
        r.Conferir("marcar caminho que não está na lista não faz nada", !LogicaDaPrateleira.Marcar(itens, @"C:\nada", true, false));

        r.Conferir("tirar um", LogicaDaPrateleira.Remover(itens, @"C:\X\C.WAV") && itens.Count == 4 && !itens.Any(x => x.Caminho == @"C:\x\c.wav"));
        r.Conferir("tirar o que não está não faz nada", !LogicaDaPrateleira.Remover(itens, @"C:\x\c.wav") && itens.Count == 4);

        // ida e volta pela config, com o mesmo serializador do núcleo
        var cfg = new ConfigPrateleira { SegundosAposSair = 2.5f, Itens = itens };
        itens.Add(new ItemDaPrateleira { Caminho = @"\\servidor\vídeos\ação ç ã.mov", Pasta = null, Em = t0, Disponivel = true });
        var json = JsonSerializer.Serialize(cfg, Config.Json);
        var volta = JsonSerializer.Deserialize<ConfigPrateleira>(json, Config.Json)!;
        var lida = LogicaDaPrateleira.Limpar(volta.Itens);
        r.Conferir("config: ida e volta mantém caminhos, ordem, pasta e data",
            LogicaDaPrateleira.MesmosCaminhos(lida, itens) && lida.Zip(itens).All(z => z.First.Pasta == z.Second.Pasta && z.First.Em == z.Second.Em)
            && volta.SegundosAposSair == 2.5f, $"{lida.Count} de {itens.Count}");
        r.Conferir("config: acento e barra invertida sobrevivem", lida[^1].Caminho == @"\\servidor\vídeos\ação ç ã.mov");
        r.Conferir("config: disponível e lento não vão para o arquivo", !json.Contains("disponivel", StringComparison.OrdinalIgnoreCase) && !json.Contains("lento", StringComparison.OrdinalIgnoreCase)
            && lida.All(x => x.Disponivel == null));
        var sujo = JsonSerializer.Deserialize<ConfigPrateleira>("""
            { "itens": [ { "caminho": "C:\\a\\b.txt" }, { "caminho": "c:\\A\\B.TXT\\" }, { "caminho": "relativo" }, { "caminho": "" }, null, { "caminho": "D:\\" } ] }
            """, Config.Json)!;
        var limpo = LogicaDaPrateleira.Limpar(sujo.Itens);
        r.Conferir("config editada à mão: repetido, relativo, vazio e nulo saem", limpo.Count == 2 && limpo[0].Caminho == @"C:\a\b.txt" && limpo[1].Caminho == @"D:\",
            string.Join(" | ", limpo.Select(x => x.Caminho)));
        r.Conferir("config sem a seção de itens vira lista vazia", LogicaDaPrateleira.Limpar(JsonSerializer.Deserialize<ConfigPrateleira>("{}", Config.Json)!.Itens).Count == 0
            && LogicaDaPrateleira.Limpar(null).Count == 0);

        r.Conferir("nome: arquivo, pasta e raiz", LogicaDaPrateleira.Nome(@"C:\a\cena.aep") == "cena.aep" && LogicaDaPrateleira.Nome(@"C:\a\Renders") == "Renders" && LogicaDaPrateleira.Nome(@"E:\") == @"E:\");
        r.Conferir("pasta curta: caminho curto fica inteiro", LogicaDaPrateleira.PastaCurta(@"C:\Videos\a.mp4", 40) == @"C:\Videos");
        string longa = LogicaDaPrateleira.PastaCurta(@"C:\Users\fulano\Documents\Trabalhos\2026\Clientes\Abertura\Renders\final.mov", 32);
        r.Conferir("pasta curta: corta pelo começo e mantém o fim", longa == @"C:\…\Clientes\Abertura\Renders" && longa.Length <= 32, longa);
        string uma = LogicaDaPrateleira.PastaCurta(@"C:\a\uma-pasta-com-um-nome-enorme-mesmo-de-verdade\x.png", 20);
        r.Conferir("pasta curta: só o último pedaço já não cabe", uma == @"…\uma-pasta-com-um-nome-enorme-mesmo-de-verdade", uma);
        r.Conferir("pasta curta: item na raiz não tem pasta", LogicaDaPrateleira.PastaCurta(@"D:\", 30) == "");
        string rede = LogicaDaPrateleira.PastaCurta(@"\\servidor\comp\um\dois\tres\quatro\a.txt", 32);
        r.Conferir("pasta curta: rede mantém o servidor", rede.StartsWith(@"\\servidor\comp\…\") && rede.EndsWith("quatro") && rede.Length <= 32, rede);

        Func<string, float> seis = s => s.Length * 6f;
        string medida = LogicaDaPrateleira.PastaQueCabe(@"C:\Users\fulano\Documents\Trabalhos\2026\Clientes\Abertura\Renders\final.mov", seis, 32 * 6);
        r.Conferir("pasta pela largura: com letras iguais dá o mesmo que contar", medida == @"C:\…\Clientes\Abertura\Renders", medida);
        r.Conferir("pasta pela largura: cabe inteira, fica inteira", LogicaDaPrateleira.PastaQueCabe(@"C:\Videos\a.mp4", seis, 1000) == @"C:\Videos");
        string nada = LogicaDaPrateleira.PastaQueCabe(@"C:\a\uma-pasta-com-um-nome-enorme-mesmo-de-verdade\x.png", seis, 30);
        r.Conferir("pasta pela largura: nem o último pedaço cabe, volta ele", nada == @"…\uma-pasta-com-um-nome-enorme-mesmo-de-verdade", nada);
        Func<string, float> largoW = s => s.Sum(ch => ch == 'W' ? 12f : 6f);
        string comW = LogicaDaPrateleira.PastaQueCabe(@"C:\WWWWWWWW\WWWWWWWW\abc\def\ghi\x.txt", largoW, 31 * 6);
        r.Conferir("pasta pela largura: letra larga encurta mais do que contar letras", comW == @"C:\…\abc\def\ghi" && largoW(comW) <= 31 * 6, comW);
        r.Conferir("pasta pela largura: item na raiz não tem pasta", LogicaDaPrateleira.PastaQueCabe(@"D:\", seis, 100) == "");

        string idioma = Lang.Choice;
        Lang.Set("pt");
        r.Conferir("contagem do menu", LogicaDaPrateleira.Contagem(0) == "vazia" && LogicaDaPrateleira.Contagem(1) == "1 item" && LogicaDaPrateleira.Contagem(7) == "7 itens");
        Lang.Set("en");
        r.Conferir("english: menu count", LogicaDaPrateleira.Contagem(0) == "empty" && LogicaDaPrateleira.Contagem(1) == "1 item" && LogicaDaPrateleira.Contagem(7) == "7 items");
        Lang.Set(idioma);
        r.Conferir("rolagem: desce, para no fim e no começo",
            LogicaDaPrateleira.Rolar(0, -1, 12, 5) == 1 && LogicaDaPrateleira.Rolar(6, -3, 12, 5) == 7 && LogicaDaPrateleira.Rolar(1, 4, 12, 5) == 0
            && LogicaDaPrateleira.Rolar(0, -1, 3, 5) == 0);
        r.Conferir("rolagem: lista encolheu, volta para dentro", LogicaDaPrateleira.Rolar(7, 0, 6, 5) == 1);

        var p1 = new byte[] { 4, 0, 9, 9, 0, 0 };
        var p2 = new byte[] { 3, 0, 7, 0, 0 };
        var cida = LogicaDaPrateleira.MontarCida(new[] { p1, p2 });
        uint U(int o) => BitConverter.ToUInt32(cida, o);
        r.Conferir("lista do shell: contagem, pai vazio e deslocamentos",
            U(0) == 2 && U(4) == 16 && cida[16] == 0 && cida[17] == 0 && U(8) == 18 && U(12) == 24 && cida.Length == 29
            && cida.AsSpan(18, 6).SequenceEqual(p1) && cida.AsSpan(24, 5).SequenceEqual(p2));

        var ar = new InicioDeArraste();
        bool a1 = ar.Mover(10, 10, false, true), a2 = ar.Mover(12, 11, false, true), a3 = ar.Mover(15, 10, false, true), a4 = ar.Mover(40, 10, false, true);
        r.Conferir("arraste começa depois de 4 DIP e só uma vez", !a1 && !a2 && a3 && !a4);
        ar.Terminou();
        r.Conferir("depois do arraste, movimento sem botão não arrasta de novo", !ar.Mover(12, 12, false, false) && !ar.Mover(30, 30, false, false));
        var ar2 = new InicioDeArraste();
        r.Conferir("clique sem mexer não arrasta", !ar2.Mover(5, 5, false, true) && !ar2.Mover(5, 5, true, false));

        var semAlfa = new byte[] { 10, 20, 30, 0, 40, 50, 60, 0 };
        Miniaturas.AjustarAlfa(semAlfa);
        r.Conferir("miniatura sem alfa nenhum fica opaca", semAlfa[3] == 255 && semAlfa[7] == 255 && semAlfa[0] == 10);
        var reto = new byte[] { 200, 100, 50, 128 };
        Miniaturas.AjustarAlfa(reto);
        r.Conferir("miniatura com alfa sem pré-multiplicar é pré-multiplicada", reto[0] == 100 && reto[1] == 50 && reto[2] == 25 && reto[3] == 128);
        var pre = new byte[] { 60, 30, 10, 128, 0, 0, 0, 0 };
        Miniaturas.AjustarAlfa(pre);
        r.Conferir("miniatura já pré-multiplicada fica igual", pre[0] == 60 && pre[1] == 30 && pre[2] == 10 && pre[3] == 128 && pre[7] == 0);

        // sondas com o shell de verdade (só leem; arquivos do próprio Windows)
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string explorer = Path.Combine(windows, "explorer.exe");
        r.Tentar("pidl", () =>
        {
            var pa = ArrasteParaFora.Pidl(explorer);
            var pb = ArrasteParaFora.Pidl(windows);
            r.Sonda($"PIDL do explorer.exe: {(pa == null ? "não saiu" : pa.Length + " bytes")}; da pasta do Windows: {(pb == null ? "não saiu" : pb.Length + " bytes")}");
            if (pa != null && pb != null)
            {
                var d = ArrasteParaFora.Montar(new[] { explorer, windows }, new[] { pa, pb });
                bool temHdrop = d.GetDataPresent(System.Windows.Forms.DataFormats.FileDrop);
                bool temIds = d.GetDataPresent("Shell IDList Array");
                r.Conferir("pacote do arraste tem CF_HDROP e a lista do shell", temHdrop && temIds);
            }
            // um item ainda sem PIDL (conferindo, drive caído): vai só o CF_HDROP, sem tocar no disco
            var meio = ArrasteParaFora.Montar(new[] { explorer, windows }, new[] { pa, null });
            r.Conferir("pacote do arraste sem todos os PIDL leva só o CF_HDROP",
                meio.GetDataPresent(System.Windows.Forms.DataFormats.FileDrop) && !meio.GetDataPresent("Shell IDList Array"));
        });
        r.Tentar("miniatura", () =>
        {
            Retrato? res = null;
            var t = new Thread(() => res = Miniaturas.DoShell(explorer, 43)) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            bool acabou = t.Join(10_000);
            r.Sonda(!acabou ? "miniatura do explorer.exe: o shell não respondeu em 10 s"
                : res == null ? "miniatura do explorer.exe: o shell não deu imagem"
                : $"miniatura do explorer.exe: {res.Largura}x{res.Altura} px (pedido 43)");
            if (res != null) r.Conferir("miniatura cabe no tamanho pedido", res.Largura <= 43 && res.Altura <= 43 && res.Dados.Length == res.Largura * res.Altura * 4);
        });
        return r.ToString();
    }
}
