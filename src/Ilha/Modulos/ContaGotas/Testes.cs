using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ilha.ContaGotas;

/// <summary>`Ilha.exe --testar contagotas`: formatos (hex, RGB, HSL), leitura de hex, histórico, posição da lupa e quando o clique termina. Sem janela.</summary>
internal static class TestesContaGotas
{
    private sealed class Relatorio
    {
        private readonly StringBuilder _sb = new();

        public void Anotar(string nome, bool ok, string? obs = null)
        {
            _sb.Append(ok ? "ok      " : "FALHOU  ").Append(nome);
            if (!ok && obs != null) _sb.Append("  (").Append(obs).Append(')');
            _sb.Append('\n');
        }

        public void Igual<T>(string nome, T obtido, T esperado) => Anotar(nome, EqualityComparer<T>.Default.Equals(obtido, esperado), $"deu {obtido}, esperado {esperado}");

        public override string ToString() => _sb.ToString().TrimEnd('\n');
    }

    public static string Rodar()
    {
        var r = new Relatorio();
        try { Formatos(r); } catch (Exception e) { r.Anotar("formatos sem exceção", false, e.Message); }
        try { Leitura(r); } catch (Exception e) { r.Anotar("leitura sem exceção", false, e.Message); }
        try { HistoricoDeCores(r); } catch (Exception e) { r.Anotar("histórico sem exceção", false, e.Message); }
        try { Lupa(r); } catch (Exception e) { r.Anotar("lupa sem exceção", false, e.Message); }
        try { SolturaDoBotao(r); } catch (Exception e) { r.Anotar("soltura sem exceção", false, e.Message); }
        return r.ToString();
    }

    private static void Formatos(Relatorio r)
    {
        var azul = new CorDaTela(30, 144, 255);
        r.Igual("hex maiúsculo", azul.Hex(), "#1E90FF");
        r.Igual("hex minúsculo", azul.Hex(true), "#1e90ff");
        r.Igual("rgb", azul.Rgb(), "rgb(30, 144, 255)");
        r.Igual("hsl do dodgerblue", azul.Hsl(), "hsl(210, 100%, 56%)");
        r.Igual("hsl do preto", new CorDaTela(0, 0, 0).Hsl(), "hsl(0, 0%, 0%)");
        r.Igual("hsl do branco", new CorDaTela(255, 255, 255).Hsl(), "hsl(0, 0%, 100%)");
        r.Igual("hsl do cinza 128", new CorDaTela(128, 128, 128).Hsl(), "hsl(0, 0%, 50%)");
        r.Igual("hsl do vermelho", new CorDaTela(255, 0, 0).Hsl(), "hsl(0, 100%, 50%)");
        r.Igual("hsl do verde", new CorDaTela(0, 255, 0).Hsl(), "hsl(120, 100%, 50%)");
        r.Igual("hsl do azul", new CorDaTela(0, 0, 255).Hsl(), "hsl(240, 100%, 50%)");
        r.Igual("hsl do magenta", new CorDaTela(255, 0, 255).Hsl(), "hsl(300, 100%, 50%)");
        // matiz perto de 360 (vermelho puxando para o magenta) não pode sair "360"
        r.Igual("matiz quase 360 vira 0", new CorDaTela(255, 0, 1).ParaHsl().H, 0);
        r.Igual("hsl do coral da ilha", new CorDaTela(0xD9, 0x77, 0x57).Hsl(), "hsl(15, 63%, 60%)");
        r.Igual("hsl do laranja escuro", new CorDaTela(128, 64, 0).Hsl(), "hsl(30, 100%, 25%)");
        r.Igual("COLORREF vira RGB", CorDaTela.DeColorRef(0x00FF9020), new CorDaTela(0x20, 0x90, 0xFF));
        r.Anotar("branco é claro", new CorDaTela(255, 255, 255).Clara);
        r.Anotar("preto não é claro", !new CorDaTela(0, 0, 0).Clara);
        r.Anotar("luminância do branco 1", Math.Abs(new CorDaTela(255, 255, 255).Luminancia() - 1) < 1e-9);
        // todas as cores de uma amostra ida e volta pelo hex
        bool idaEVolta = true;
        for (int i = 0; i < 4096; i++)
        {
            var c = new CorDaTela((byte)(i * 37), (byte)(i * 91 >> 2), (byte)(i * 13 + 7));
            if (!CorDaTela.TentarLer(c.Hex(), out var d) || d != c) { idaEVolta = false; break; }
            var (h, s, l) = c.ParaHsl();
            if (h is < 0 or > 359 || s is < 0 or > 100 || l is < 0 or > 100) { idaEVolta = false; break; }
        }
        r.Anotar("hex ida e volta e HSL dentro dos limites (4096 cores)", idaEVolta);
    }

    private static void Leitura(Relatorio r)
    {
        r.Anotar("lê #RRGGBB", CorDaTela.TentarLer("#1E90FF", out var a) && a == new CorDaTela(30, 144, 255));
        r.Anotar("lê sem # e minúsculo", CorDaTela.TentarLer("1e90ff", out var b) && b == new CorDaTela(30, 144, 255));
        r.Anotar("lê #RGB curto", CorDaTela.TentarLer("#F0A", out var c) && c == new CorDaTela(255, 0, 170));
        r.Anotar("lê com espaços", CorDaTela.TentarLer("  #000000 ", out var d) && d == new CorDaTela(0, 0, 0));
        r.Anotar("recusa vazio", !CorDaTela.TentarLer("", out _));
        r.Anotar("recusa nulo", !CorDaTela.TentarLer(null, out _));
        r.Anotar("recusa letra fora do hex", !CorDaTela.TentarLer("#GG0000", out _));
        r.Anotar("recusa tamanho errado", !CorDaTela.TentarLer("#12345", out _));
        r.Anotar("recusa com alfa", !CorDaTela.TentarLer("#11223344", out _));
        r.Anotar("recusa sinal", !CorDaTela.TentarLer("+12345", out _));
    }

    private static void HistoricoDeCores(Relatorio r)
    {
        var h = new List<string>();
        Historico.Lembrar(h, new CorDaTela(1, 2, 3), 12);
        Historico.Lembrar(h, new CorDaTela(4, 5, 6), 12);
        r.Igual("a mais nova fica em cima", string.Join(",", h), "#040506,#010203");
        Historico.Lembrar(h, new CorDaTela(1, 2, 3), 12);
        r.Igual("repetida sobe sem duplicar", string.Join(",", h), "#010203,#040506");
        for (int i = 0; i < 20; i++) Historico.Lembrar(h, new CorDaTela((byte)i, 100, 200), 12);
        r.Igual("limite de 12", h.Count, 12);
        r.Igual("a última pega é a primeira", h[0], "#1364C8");
        r.Igual("a mais velha que ficou", h[11], "#0864C8");
        var m = new List<string> { "#aabbcc" };
        Historico.Lembrar(m, new CorDaTela(0xAA, 0xBB, 0xCC), 12);
        r.Igual("minúscula guardada conta como a mesma", string.Join(",", m), "#AABBCC");
        var um = new List<string> { "#000000", "#111111" };
        Historico.Lembrar(um, new CorDaTela(9, 9, 9), 0);
        r.Igual("limite 0 vira 1", string.Join(",", um), "#090909");
        var grande = new List<string>();
        for (int i = 0; i < 40; i++) Historico.Lembrar(grande, new CorDaTela((byte)i, 0, 0), 500);
        r.Igual("limite acima do máximo vira 24", grande.Count, Historico.MAXIMO);

        var lida = Historico.Limpar(new[] { "#ff0000", "lixo", "", "FF0000", "#00ff00", "#0000FF", "#123" }, 3);
        r.Igual("limpar: valida, normaliza, tira repetida e corta", string.Join(",", lida), "#FF0000,#00FF00,#0000FF");
        r.Igual("limpar: nulo vira lista vazia", Historico.Limpar(null, 12).Count, 0);
        r.Igual("limpar: #RGB curto vira longo", string.Join(",", Historico.Limpar(new[] { "#123" }, 12)), "#112233");
    }

    private static void SolturaDoBotao(Relatorio r)
    {
        r.Anotar("termina com o botão solto", Seletor.PodeTerminar(false, 0));
        r.Anotar("clique segurado 800 ms ainda espera", !Seletor.PodeTerminar(true, 800));
        r.Anotar("segurado logo antes do teto espera", !Seletor.PodeTerminar(true, Seletor.TETO_SOLTURA_MS - 1));
        r.Anotar("segurado no teto termina", Seletor.PodeTerminar(true, Seletor.TETO_SOLTURA_MS));
        r.Anotar("teto acima de 1 s", Seletor.TETO_SOLTURA_MS > 1000);
    }

    private static void Lupa(Relatorio r)
    {
        var m100 = Geometria.Medir(96);
        var m125 = Geometria.Medir(120);
        var m200 = Geometria.Medir(192);
        r.Igual("célula a 100%", m100.Celula, 9);
        r.Igual("célula a 125% (ultrawide)", m125.Celula, 11);
        r.Igual("célula a 200% (TV 4K)", m200.Celula, 18);
        r.Anotar("lupa cresce com o DPI", m200.Largura > m125.Largura && m125.Largura > m100.Largura);
        r.Igual("lado = amostra x célula + bordas", m125.Largura, Geometria.AMOSTRA * m125.Celula + 2 * m125.Borda);
        r.Anotar("altura tem a etiqueta embaixo", m125.Altura == m125.Largura + m125.Vao + m125.Etiqueta);
        r.Igual("DPI 0 não quebra", Geometria.Medir(0).Celula, 4);

        // monitor principal 3440x1440 em (0,0); o em pé à esquerda, em coordenada negativa
        const int L = 200, A = 240, D = 28;
        r.Igual("no meio: embaixo e à direita", Geometria.Posicionar(1000, 500, L, A, D, 0, 0, 3440, 1440), (1028, 528));
        r.Igual("perto da borda direita: vira para a esquerda", Geometria.Posicionar(3400, 500, L, A, D, 0, 0, 3440, 1440), (3400 - D - L, 528));
        r.Igual("perto de baixo: vira para cima", Geometria.Posicionar(1000, 1430, L, A, D, 0, 0, 3440, 1440), (1028, 1430 - D - A));
        r.Igual("canto de baixo à direita: vira os dois", Geometria.Posicionar(3439, 1439, L, A, D, 0, 0, 3440, 1440), (3439 - D - L, 1439 - D - A));
        r.Igual("monitor em pé à esquerda (x negativo)", Geometria.Posicionar(-1070, 100, L, A, D, -1080, -240, 0, 1680), (-1070 + D, 100 + D));
        r.Igual("borda direita do monitor da esquerda", Geometria.Posicionar(-5, 100, L, A, D, -1080, -240, 0, 1680), (-5 - D - L, 100 + D));
        r.Igual("topo negativo do monitor em pé", Geometria.Posicionar(-500, -239, L, A, D, -1080, -240, 0, 1680), (-500 + D, -239 + D));
        r.Igual("monitor menor que a lupa fica preso no canto", Geometria.Posicionar(50, 50, L, A, D, 0, 0, 100, 100), (0, 0));
        bool dentro = true;
        var rnd = new Random(7);
        for (int i = 0; i < 2000 && dentro; i++)
        {
            int cx = rnd.Next(-1080, 3440), cy = rnd.Next(-240, 1440);
            var (e, t, d, b) = cx < 0 ? (-1080, -240, 0, 1680) : (0, 0, 3440, 1440);
            var (x, y) = Geometria.Posicionar(cx, cy, L, A, D, e, t, d, b);
            // nunca sai do monitor e nunca cobre o pixel do cursor
            bool cobre = cx >= x && cx < x + L && cy >= y && cy < y + A;
            if (x < e || y < t || x + L > d || y + A > b || cobre) dentro = false;
        }
        r.Anotar("2000 posições: lupa sempre no monitor e fora do cursor", dentro);
    }
}
