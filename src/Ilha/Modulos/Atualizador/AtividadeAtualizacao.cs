using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Atualizacoes;

/// <summary>"Versão X disponível" com os botões Atualizar e Depois; depois o download, a troca ou o erro.</summary>
internal sealed class AtividadeAtualizacao : Atividade
{
    public enum Fase { Disponivel, Baixando, Trocando, Erro }

    public override string Tipo => "atualizacao";
    public override string Nome => Lang.T("Atualização", "Update");

    public Fase Estado { get; private set; }
    private string _versao = "";
    private string _atual = "";
    private string _resumo = "";
    private string _erro = "";
    private double _progresso;

    public Action? AoAtualizar;
    public Action? AoDispensar;

    private const float LARGURA = 340, ALTURA = 112;

    public void Disponivel(string versao, string atual, string resumo)
    {
        _versao = versao;
        _atual = atual;
        _resumo = resumo;
        Trocar(Fase.Disponivel);
        Ligar();
    }

    public void Baixando(double progresso)
    {
        bool mudouFase = Estado != Fase.Baixando;
        _progresso = progresso;
        if (mudouFase) Trocar(Fase.Baixando); else Redesenhar();
    }

    public void TrocandoArquivos() => Trocar(Fase.Trocando);

    public void Erro(string mensagem)
    {
        _erro = mensagem;
        Trocar(Fase.Erro);
        Ligar();
    }

    public void Fechar() => Desligar();

    private void Trocar(Fase f)
    {
        Estado = f;
        // os botões mudam com a fase: a cena precisa ser refeita, não só redesenhada
        MudouTamanho();
    }

    private static Color4 AzulFraco => new(Cor.Azul.R, Cor.Azul.G, Cor.Azul.B, 0.25f);

    private void Seta(ID2D1DeviceContext dc, Palco p, Vector2 c, float r)
    {
        Icone.Circulo(dc, c, r, Estado == Fase.Erro ? Cor.Vermelho : Cor.Azul);
        float s = r * 0.5f;
        Icone.Linha(dc, new Vector2(c.X, c.Y + s), new Vector2(c.X, c.Y - s), r * 0.22f, Cor.Branco, p.G.Fabrica);
        Icone.Linha(dc, new Vector2(c.X - s * 0.8f, c.Y - s * 0.15f), new Vector2(c.X, c.Y - s), r * 0.22f, Cor.Branco, p.G.Fabrica);
        Icone.Linha(dc, new Vector2(c.X + s * 0.8f, c.Y - s * 0.15f), new Vector2(c.X, c.Y - s), r * 0.22f, Cor.Branco, p.G.Fabrica);
    }

    private static void Botao(ID2D1DeviceContext dc, Palco p, System.Drawing.RectangleF r, string texto, Color4 fundo, Color4 cor)
    {
        Icone.Retangulo(dc, r.X, r.Y, r.Width, r.Height, r.Height / 2, fundo);
        var t = p.Texto.Medir(texto, 13, FontWeight.SemiBold);
        p.Texto.Desenhar(dc, texto, new Vector2(r.X + (r.Width - t.X) / 2, r.Y + (r.Height - t.Y) / 2), 13, cor);
    }

    /// <summary>Quebra o texto em até `linhas` linhas que cabem na largura (a última corta com reticências).</summary>
    private static List<string> Quebrar(Palco p, string texto, float tamanho, float largura, int linhas)
    {
        var todas = new List<string>();
        var atual = "";
        foreach (var palavra in texto.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var teste = atual.Length == 0 ? palavra : atual + " " + palavra;
            if (atual.Length == 0 || p.Texto.Medir(teste, tamanho, FontWeight.Normal).X <= largura) { atual = teste; continue; }
            todas.Add(atual);
            atual = palavra;
        }
        if (atual.Length > 0) todas.Add(atual);
        if (todas.Count <= linhas) return todas;
        // o que sobra vai todo na última linha; o Desenhar com largura corta com reticências
        var saida = todas.GetRange(0, linhas - 1);
        saida.Add(string.Join(" ", todas.GetRange(linhas - 1, todas.Count - linhas + 1)));
        return saida;
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        if (f == Forma.Bolha)
        {
            var b = p.NovaCena(new Vector2(24, 24));
            p.Desenho(b, Vector2.Zero, b.Tamanho, (dc, t) => Seta(dc, p, new Vector2(12, 12), 10));
            return b;
        }
        if (f == Forma.Compacta)
        {
            var c = p.NovaCena(new Vector2(170, 24));
            p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
            {
                Seta(dc, p, new Vector2(12, 12), 10);
                string texto = Estado switch
                {
                    Fase.Baixando => $"{(int)(_progresso * 100)}%",
                    Fase.Trocando => Lang.T("Instalando", "Installing"),
                    Fase.Erro => Lang.T("Falhou", "Failed"),
                    _ => _versao,
                };
                var tam = p.Texto.Medir(texto, 15, FontWeight.SemiBold, tabular: true);
                p.Texto.Desenhar(dc, texto, new Vector2(t.X - tam.X, (t.Y - tam.Y) / 2), 15, Estado == Fase.Erro ? Cor.Vermelho : Cor.Azul, FontWeight.SemiBold, tabular: true);
            });
            return c;
        }

        var cena = p.NovaCena(new Vector2(LARGURA, ALTURA));
        var atualizar = new System.Drawing.RectangleF(LARGURA - 196, ALTURA - 36, 104, 32);
        var depois = new System.Drawing.RectangleF(LARGURA - 84, ALTURA - 36, 84, 32);
        p.Desenho(cena, Vector2.Zero, cena.Tamanho, (dc, t) =>
        {
            Seta(dc, p, new Vector2(20, 22), 18);
            const float x = 50, larguraTexto = LARGURA - 50;
            switch (Estado)
            {
                case Fase.Disponivel:
                    p.Texto.Desenhar(dc, Lang.T("Atualização disponível", "Update available"), new Vector2(x, 0), 13, Cor.Secundario, largura: larguraTexto);
                    p.Texto.Desenhar(dc, $"Ilha {_versao}", new Vector2(x, 17), 20, Cor.Branco, largura: larguraTexto);
                    p.Texto.Desenhar(dc, _resumo.Length > 0 ? _resumo : Lang.T($"Você está na {_atual}", $"You're on {_atual}"), new Vector2(x, 46), 12, Cor.Secundario, FontWeight.Normal, largura: larguraTexto);
                    Botao(dc, p, atualizar, Lang.T("Atualizar", "Update"), Cor.Azul, Cor.Branco);
                    Botao(dc, p, depois, Lang.T("Depois", "Later"), Cor.Fraco, Cor.Branco);
                    break;
                case Fase.Baixando:
                    p.Texto.Desenhar(dc, Lang.T($"Baixando a {_versao}", $"Downloading {_versao}"), new Vector2(x, 0), 13, Cor.Secundario, largura: larguraTexto);
                    p.Texto.Desenhar(dc, $"{(int)(_progresso * 100)}%", new Vector2(x, 17), 20, Cor.Branco, tabular: true);
                    Icone.Retangulo(dc, x, 62, LARGURA - x, 6, 3, AzulFraco);
                    Icone.Retangulo(dc, x, 62, MathF.Max(6, (float)(LARGURA - x) * (float)_progresso), 6, 3, Cor.Azul);
                    p.Texto.Desenhar(dc, Lang.T("O SHA-256 é conferido antes de trocar qualquer arquivo", "The SHA-256 is checked before any file is replaced"), new Vector2(x, 80), 12, Cor.Secundario, FontWeight.Normal, largura: larguraTexto);
                    break;
                case Fase.Trocando:
                    p.Texto.Desenhar(dc, Lang.T($"Instalando a {_versao}", $"Installing {_versao}"), new Vector2(x, 0), 13, Cor.Secundario, largura: larguraTexto);
                    p.Texto.Desenhar(dc, Lang.T("A ilha vai fechar e abrir de novo", "The island will close and open again"), new Vector2(x, 17), 18, Cor.Branco, largura: larguraTexto);
                    p.Texto.Desenhar(dc, Lang.T($"Se a nova não abrir, ela volta sozinha para a {_atual}", $"If the new one doesn't open, it goes back to {_atual} on its own"), new Vector2(x, 46), 12, Cor.Secundario, FontWeight.Normal, largura: larguraTexto);
                    break;
                case Fase.Erro:
                    p.Texto.Desenhar(dc, Lang.T("Não deu para atualizar", "Couldn't update"), new Vector2(x, 0), 13, Cor.Secundario, largura: larguraTexto);
                    float y = 18;
                    foreach (var l in Quebrar(p, _erro, 13, larguraTexto, 3))
                    {
                        p.Texto.Desenhar(dc, l, new Vector2(x, y), 13, Cor.Branco, FontWeight.Normal, largura: larguraTexto);
                        y += 18;
                    }
                    Botao(dc, p, depois, Lang.T("Fechar", "Close"), Cor.Fraco, Cor.Branco);
                    break;
            }
        });
        if (Estado == Fase.Disponivel)
        {
            cena.Botoes.Add(new Botao { Area = atualizar, Acao = () => AoAtualizar?.Invoke() });
            cena.Botoes.Add(new Botao { Area = depois, Acao = () => AoDispensar?.Invoke() });
        }
        else if (Estado == Fase.Erro)
            cena.Botoes.Add(new Botao { Area = depois, Acao = Fechar });
        return cena;
    }
}
