using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Forms;
using Ilha.Extras;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Prateleira;

/// <summary>
/// O cartão da prateleira e quem recebe os arquivos soltos na ilha. Nunca liga (não ocupa a pílula): abre pelo menu,
/// ou sozinho quando um arraste de arquivos entra na ilha, para mostrar onde soltar.
/// </summary>
internal sealed class AtividadePrateleira : Atividade, IReceptorDeArquivos
{
    public override string Tipo => "prateleira";
    public override string Nome => Lang.T("Prateleira", "Shelf");
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    private const float L = 360, CAB = 32, LINHA = 44, MINI = 34, VAZIA = 96, TEXTO_X = MINI + 10;
    private const int VISIVEIS = 5;
    private static readonly Color4 Fundo = new(1f, 1f, 1f, 0.09f);

    private readonly Servicos S;
    private readonly List<ItemDaPrateleira> _itens;
    private readonly Action _salvar;
    private readonly Func<ConfigPrateleira> _cfg;
    private readonly Miniaturas _mini = new();
    private readonly ArrasteParaFora _fora = new();
    private readonly HashSet<string> _verificando = new(LogicaDaPrateleira.Comparador);
    // PIDL de cada item, montado fora da thread da interface quando a conferência acha o original; o arraste só lê daqui
    private readonly Dictionary<string, byte[]> _pidls = new(LogicaDaPrateleira.Comparador);
    private readonly HashSet<string> _montandoPidl = new(LogicaDaPrateleira.Comparador);
    // a pasta já encurtada para a largura da linha (medir é criar um layout do DirectWrite)
    private readonly Dictionary<(string, float), string> _pastasCurtas = new();
    private readonly Timer _fecharDepois = new();
    private readonly Timer _desarmar = new() { Interval = 4000 };
    private bool _parado, _soltando, _abertoPeloArraste, _confirmarLimpar;
    private int _primeiro;
    private int _slotsMontados = -1;
    private bool _vaziaMontada;

    public AtividadePrateleira(Servicos s, List<ItemDaPrateleira> itens, Func<ConfigPrateleira> cfg, Action salvar)
    {
        S = s;
        _itens = itens;
        _cfg = cfg;
        _salvar = salvar;
        _fecharDepois.Tick += (_, _) =>
        {
            _fecharDepois.Stop();
            if (_parado || _soltando || !_abertoPeloArraste) return;
            if (S.Ilha.Expandida == this) S.Ilha.Recolher();
        };
        _desarmar.Tick += (_, _) =>
        {
            _desarmar.Stop();
            if (!_confirmarLimpar) return;
            _confirmarLimpar = false;
            if (TemCenaViva) Redesenhar();
        };
    }

    public void Abrir()
    {
        if (!_parado) S.Ilha.Expandir(this);
    }

    public void Parar()
    {
        _parado = true;
        _fecharDepois.Dispose();
        _desarmar.Dispose();
        _mini.Dispose();
        _fora.Dispose();
    }

    public override void ExpandidaMudou(bool aberta)
    {
        if (aberta)
        {
            // o original pode ter sumido (ou voltado) desde a última vez
            Verificar(_itens);
            return;
        }
        _soltando = false;
        _abertoPeloArraste = false;
        _confirmarLimpar = false;
        _fecharDepois.Stop();
    }

    // ------------------------------------------------------------------ receber (soltar na ilha)

    public bool Entrou(string[] caminhos)
    {
        if (_parado || _fora.Arrastando) return false;
        if (S.Config().Pausada || S.ContextoAtual().Competitivo) return false;
        // com um alerta na tela, abrir o cartão apagaria ele e a fila inteira (o Expandir limpa): só passar por cima
        // não pode custar um "Timer acabou"; o soltar fica recusado até o alerta sair
        if (S.Ilha.EstadoAtual == Estado.Alerta) return false;
        if (LogicaDaPrateleira.Aceitaveis(caminhos).Count == 0) return false;
        _fecharDepois.Stop();
        _soltando = true;
        if (S.Ilha.Expandida != this)
        {
            _abertoPeloArraste = true;
            S.Ilha.Expandir(this);
        }
        Redesenhar();
        return true;
    }

    public void Saiu()
    {
        if (_parado) return;
        _soltando = false;
        if (TemCenaViva) Redesenhar();
        if (!_abertoPeloArraste) return;
        _fecharDepois.Interval = (int)Math.Clamp(_cfg().SegundosAposSair * 1000, 200, 30_000);
        _fecharDepois.Start();
    }

    public void Soltou(string[] caminhos)
    {
        if (_parado) return;
        _soltando = false;
        _abertoPeloArraste = false;
        _fecharDepois.Stop();
        var aceitos = LogicaDaPrateleira.Aceitaveis(caminhos);
        if (aceitos.Count == 0) { if (TemCenaViva) Redesenhar(); return; }
        LogicaDaPrateleira.Adicionar(_itens, aceitos, DateTime.Now);
        var chegaram = new List<ItemDaPrateleira>();
        foreach (var c in aceitos)
        {
            _mini.Esquecer(c);
            if (_itens.Find(x => LogicaDaPrateleira.Comparador.Equals(x.Caminho, c)) is { } item)
            {
                // veio de um arraste do Explorer agora: existe; a conferência só descobre se é pasta
                item.Disponivel = true;
                item.Lento = false;
                chegaram.Add(item);
            }
        }
        _primeiro = 0;
        Log.Escrever($"prateleira: {aceitos.Count} item(ns) solto(s), {_itens.Count} na prateleira");
        ListaMudou();
        Verificar(chegaram);
    }

    /// <summary>A config foi editada fora da ilha: adota a lista de lá.</summary>
    public void TrocarLista(List<ItemDaPrateleira> nova)
    {
        _itens.Clear();
        _itens.AddRange(nova);
        _mini.EsquecerTudo();
        _pidls.Clear();
        _pastasCurtas.Clear();
        _primeiro = 0;
        AtualizarLayout();
        S.Menu.Redesenhar();
        Verificar(_itens);
    }

    // ------------------------------------------------------------------ disco (sempre fora da thread da interface)

    /// <summary>
    /// Confere se cada original ainda existe. Drive de rede desligado pode prender o Exists por dezenas de segundos:
    /// roda numa tarefa, com 3 s para responder (depois disso aparece como "não responde" até a resposta chegar), e
    /// nunca duas conferências do mesmo caminho ao mesmo tempo.
    /// </summary>
    public void Verificar(IEnumerable<ItemDaPrateleira> quais)
    {
        if (_parado) return;
        foreach (var item in quais.ToList())
        {
            string caminho = item.Caminho;
            if (!_verificando.Add(caminho)) continue;
            _ = VerificarUm(caminho);
        }
    }

    private async Task VerificarUm(string caminho)
    {
        try
        {
            var t = Task.Run(() =>
            {
                bool pasta = Directory.Exists(caminho);
                return (Existe: pasta || File.Exists(caminho), Pasta: pasta);
            });
            if (await Task.WhenAny(t, Task.Delay(3000)).ConfigureAwait(false) != t)
                S.NaInterface(() => Marcado(caminho, false, null, lento: true));
            var (existe, pasta) = await t.ConfigureAwait(false);
            S.NaInterface(() =>
            {
                Marcado(caminho, existe, pasta, lento: false);
                if (existe) MontarPidl(caminho);
            });
        }
        catch (Exception e)
        {
            Log.Escrever("prateleira: conferir: " + e.Message);
            S.NaInterface(() => Marcado(caminho, false, null, lento: false));
        }
        finally
        {
            S.NaInterface(() => _verificando.Remove(caminho));
        }
    }

    /// <summary>
    /// O PIDL do item, numa tarefa à parte (o SHParseDisplayName também pode prender num drive caído). Não segura a
    /// conferência: enquanto não chega, o arraste vai só com o CF_HDROP.
    /// </summary>
    private void MontarPidl(string caminho)
    {
        if (_parado || _pidls.ContainsKey(caminho) || !_montandoPidl.Add(caminho)) return;
        _ = Task.Run(() =>
        {
            byte[]? p = null;
            try { p = ArrasteParaFora.Pidl(caminho); }
            catch (Exception e) { Log.Escrever("prateleira: pidl: " + e.Message); }
            S.NaInterface(() =>
            {
                _montandoPidl.Remove(caminho);
                if (_parado || p == null) return;
                // só se o item ainda está na prateleira e não sumiu enquanto isso
                if (_itens.Find(x => LogicaDaPrateleira.Comparador.Equals(x.Caminho, caminho)) is { Disponivel: true })
                    _pidls[caminho] = p;
            });
        });
    }

    private void Marcado(string caminho, bool existe, bool? pasta, bool lento)
    {
        if (_parado) return;
        var item = _itens.Find(x => LogicaDaPrateleira.Comparador.Equals(x.Caminho, caminho));
        if (item == null) return;
        if (!existe) _pidls.Remove(caminho);
        bool voltou = existe && item.Disponivel == false;
        bool pastaAntes = item.Pasta == true;
        if (!LogicaDaPrateleira.Marcar(_itens, caminho, existe, pasta, lento)) return;
        if (voltou) _mini.Esquecer(caminho);
        // descobrir que é pasta muda o que vai para a config (o ícone de pasta continua quando ela some)
        if ((item.Pasta == true) != pastaAntes) _salvar();
        if (TemCenaViva) Redesenhar();
    }

    // ------------------------------------------------------------------ ações

    private void ListaMudou()
    {
        _primeiro = LogicaDaPrateleira.Rolar(_primeiro, 0, _itens.Count, VISIVEIS);
        _salvar();
        S.Menu.Redesenhar();
        AtualizarLayout();
    }

    /// <summary>A altura do cartão só muda quando muda o número de linhas na tela (ou ele fica vazio).</summary>
    private void AtualizarLayout()
    {
        int slots = Math.Min(_itens.Count, VISIVEIS);
        if (slots != _slotsMontados || (_itens.Count == 0) != _vaziaMontada) MudouTamanho();
        else if (TemCenaViva) Redesenhar();
    }

    private ItemDaPrateleira? NoSlot(int s)
    {
        int i = _primeiro + s;
        return i >= 0 && i < _itens.Count ? _itens[i] : null;
    }

    private void Tirar(int slot)
    {
        if (NoSlot(slot) is not { } item) return;
        LogicaDaPrateleira.Remover(_itens, item.Caminho);
        _mini.Esquecer(item.Caminho);
        _pidls.Remove(item.Caminho);
        ListaMudou();
    }

    private void Limpar()
    {
        if (_itens.Count == 0) return;
        if (!_confirmarLimpar)
        {
            _confirmarLimpar = true;
            _desarmar.Stop();
            _desarmar.Start();
            Redesenhar();
            return;
        }
        _confirmarLimpar = false;
        _desarmar.Stop();
        _itens.Clear();
        _mini.EsquecerTudo();
        _pidls.Clear();
        _pastasCurtas.Clear();
        _primeiro = 0;
        Log.Escrever("prateleira: limpa");
        ListaMudou();
    }

    private void Arrastar(IEnumerable<ItemDaPrateleira> itens)
    {
        if (_parado) return;
        // o que já se sabe que sumiu não vai (o app de destino só daria erro)
        var vao = itens.Where(x => x.Disponivel != false).ToList();
        if (vao.Count == 0) return;
        int px = (int)MathF.Round(MINI * S.Palco.Escala);
        var fantasma = vao[0].Disponivel == true ? _mini.Pegar(vao[0].Caminho, px, () => { }) : null;
        _confirmarLimpar = false;
        // nada de disco aqui: o item ainda conferindo ou com drive que caiu depois vai sem PIDL, e aí o pacote leva só o CF_HDROP
        var pidls = vao.Select(x => _pidls.GetValueOrDefault(x.Caminho)).ToList();
        // a janela de toque segurava o mouse: solta para o OLE pegar (o núcleo não recebe o soltar depois)
        S.Ilha.SoltarArraste();
        _fora.Arrastar(vao.Select(x => x.Caminho).ToList(), pidls, fantasma);
    }

    private Action<float, float, bool> Arrastavel(Func<IEnumerable<ItemDaPrateleira>> quais)
    {
        var inicio = new InicioDeArraste();
        return (x, y, soltou) =>
        {
            if (!inicio.Mover(x, y, soltou, Control.MouseButtons.HasFlag(MouseButtons.Left))) return;
            var lista = quais().ToList();
            S.NaInterface(() =>
            {
                try { Arrastar(lista); }
                catch (Exception e) { Log.Escrever("prateleira: arrastar: " + e); }
                finally { inicio.Terminou(); }
            });
        };
    }

    public override bool Rolar(int delta)
    {
        if (S.Ilha.Expandida != this || _itens.Count <= VISIVEIS) return false;
        int dentes = delta / 120;
        if (dentes == 0) dentes = Math.Sign(delta);
        int novo = LogicaDaPrateleira.Rolar(_primeiro, dentes, _itens.Count, VISIVEIS);
        if (novo != _primeiro)
        {
            _primeiro = novo;
            Redesenhar();
        }
        return true;
    }

    // ------------------------------------------------------------------ cenas

    private static System.Drawing.RectangleF BotaoLimpar => new(L - 72, 2, 72, 26);
    private static System.Drawing.RectangleF BotaoTudo => new(L - 72 - 8 - 112, 2, 112, 26);

    protected override Cena Montar(Palco p, Forma f)
    {
        if (f != Forma.Expandida) return MontarCompacta(p);
        int n = _itens.Count;
        int slots = Math.Min(n, VISIVEIS);
        _slotsMontados = slots;
        _vaziaMontada = n == 0;
        float altura = n == 0 ? CAB + VAZIA : CAB + slots * LINHA;
        var c = p.NovaCena(new Vector2(L, altura));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) => Desenhar(dc, p, t));
        if (n > 0)
        {
            c.Botoes.Add(new Botao { Area = BotaoLimpar, Acao = Limpar });
            c.Botoes.Add(new Botao { Area = BotaoTudo, Arrastar = Arrastavel(() => _itens) });
        }
        for (int s = 0; s < slots; s++)
        {
            int slot = s;
            float y = CAB + s * LINHA;
            // o "x" antes da linha: o primeiro botão que contém o ponto ganha
            c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(L - 30, y + 7, 30, 30), Acao = () => Tirar(slot) });
            c.Botoes.Add(new Botao
            {
                Area = new System.Drawing.RectangleF(0, y, L - 32, LINHA),
                Arrastar = Arrastavel(() => NoSlot(slot) is { } it ? new[] { it } : Array.Empty<ItemDaPrateleira>()),
            });
        }
        return c;
    }

    private Cena MontarCompacta(Palco p)
    {
        var c = p.NovaCena(new Vector2(110, 24));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            DesenharIcone(dc, new System.Drawing.RectangleF(0, 0, 24, 24));
            p.Texto.Desenhar(dc, LogicaDaPrateleira.Contagem(_itens.Count), new Vector2(30, 3), 13, Cor.Branco, FontWeight.Medium, largura: 80);
        });
        return c;
    }

    private void Desenhar(ID2D1DeviceContext dc, Palco p, Vector2 t)
    {
        var f = p.G.Fabrica;
        int n = _itens.Count;
        string nome = Lang.T("Prateleira", "Shelf");
        p.Texto.Desenhar(dc, nome, new Vector2(0, 3), 15, Cor.Branco);
        float lt = p.Texto.Medir(nome, 15).X;
        if (n > 0) p.Texto.Desenhar(dc, LogicaDaPrateleira.Contagem(n), new Vector2(lt + 8, 6.5f), 12, Cor.Secundario, FontWeight.Normal);

        if (n == 0)
        {
            DesenharZonaVazia(dc, p, f, t);
            return;
        }

        var bl = BotaoLimpar;
        Icone.Retangulo(dc, bl.X, bl.Y, bl.Width, bl.Height, bl.Height / 2, _confirmarLimpar ? new Color4(1f, 0.27f, 0.23f, 0.22f) : Fundo);
        Centrado(dc, p, _confirmarLimpar ? Lang.T("Confirmar", "Confirm") : Lang.T("Limpar", "Clear"), bl, 12, _confirmarLimpar ? Cor.Vermelho : Cor.Branco);
        var bt = BotaoTudo;
        Icone.Retangulo(dc, bt.X, bt.Y, bt.Width, bt.Height, bt.Height / 2, Fundo);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 2; j++)
                Icone.Circulo(dc, new Vector2(bt.X + 13 + j * 4.5f, bt.Y + 8 + i * 5), 1.2f, Cor.Secundario);
        p.Texto.Desenhar(dc, Lang.T("Arrastar tudo", "Drag all"), new Vector2(bt.X + 28, bt.Y + 5), 12, Cor.Branco, FontWeight.Medium, largura: bt.Width - 32);

        int slots = Math.Min(n, VISIVEIS);
        int px = (int)MathF.Round(MINI * p.Escala);
        for (int s = 0; s < slots; s++)
        {
            if (NoSlot(s) is not { } item) break;
            float y = CAB + s * LINHA;
            if (s > 0) Icone.Retangulo(dc, TEXTO_X, y, L - TEXTO_X - 8, 1, 0, new Color4(1f, 1f, 1f, 0.06f));
            bool sumiu = item.Disponivel == false;
            Retrato? mini = item.Disponivel == true ? _mini.Pegar(item.Caminho, px, MiniaturaPronta) : null;
            if (mini != null)
            {
                var tam = mini.TamanhoDip(p.Escala);
                mini.Desenhar(dc, p.Escala, (MINI - tam.X) / 2, y + 5 + (MINI - tam.Y) / 2, 4);
            }
            else DesenharTipo(dc, f, new System.Drawing.RectangleF(0, y + 5, MINI, MINI), item.Pasta == true, sumiu ? 0.35f : 0.75f);

            float lnome = L - TEXTO_X - 36;
            p.Texto.Desenhar(dc, LogicaDaPrateleira.Nome(item.Caminho), new Vector2(TEXTO_X, y + 5), 13, sumiu ? Cor.Secundario : Cor.Branco, largura: lnome);
            if (sumiu)
            {
                string aviso = item.Lento ? Lang.T("Não responde", "Not responding") : Lang.T("Indisponível", "Unavailable");
                p.Texto.Desenhar(dc, aviso, new Vector2(TEXTO_X, y + 24), 11, Cor.Vermelho, FontWeight.Medium);
                float la = p.Texto.Medir(aviso, 11, FontWeight.Medium).X + 6;
                float lp = MathF.Max(10, lnome - la);
                p.Texto.Desenhar(dc, PastaQueCabe(p, item.Caminho, lp), new Vector2(TEXTO_X + la, y + 24), 11, Cor.Secundario, FontWeight.Normal, largura: lp);
            }
            else p.Texto.Desenhar(dc, PastaQueCabe(p, item.Caminho, lnome), new Vector2(TEXTO_X, y + 24), 11, Cor.Secundario, FontWeight.Normal, largura: lnome);

            var cx = new Vector2(L - 15, y + LINHA / 2);
            Icone.Linha(dc, cx + new Vector2(-4, -4), cx + new Vector2(4, 4), 1.6f, Cor.Secundario, f);
            Icone.Linha(dc, cx + new Vector2(-4, 4), cx + new Vector2(4, -4), 1.6f, Cor.Secundario, f);
        }

        if (n > VISIVEIS)
        {
            float trilho = slots * LINHA - 8, alto = MathF.Max(16, trilho * VISIVEIS / n);
            float y0 = CAB + 4 + (trilho - alto) * _primeiro / Math.Max(1, n - VISIVEIS);
            Icone.Retangulo(dc, L - 2, CAB + 4, 2, trilho, 1, new Color4(1f, 1f, 1f, 0.06f));
            Icone.Retangulo(dc, L - 2, y0, 2, alto, 1, Cor.Fraco);
        }

        if (_soltando)
        {
            var area = new System.Drawing.RectangleF(0, 0, t.X, t.Y);
            using (var fundo = dc.CreateSolidColorBrush(new Color4(0f, 0f, 0f, 0.78f)))
            {
                var rr = new RoundedRectangle(area, 14, 14);
                dc.FillRoundedRectangle(ref rr, fundo);
            }
            Moldura(dc, f, new System.Drawing.RectangleF(1, 1, t.X - 2, t.Y - 2), true);
            Centrado(dc, p, Lang.T("Solte para guardar na prateleira", "Drop to keep on the shelf"), area, 14, Cor.Branco);
        }
    }

    private void DesenharZonaVazia(ID2D1DeviceContext dc, Palco p, ID2D1Factory1 f, Vector2 t)
    {
        var zona = new System.Drawing.RectangleF(1, CAB + 1, t.X - 2, VAZIA - 2);
        Moldura(dc, f, zona, _soltando);
        var c = new System.Drawing.RectangleF(zona.X + zona.Width / 2 - 12, zona.Y + 14, 24, 24);
        DesenharSeta(dc, f, c, _soltando ? Cor.Azul : Cor.Secundario);
        string titulo = _soltando ? Lang.T("Solte para guardar", "Drop to keep") : Lang.T("Arraste arquivos ou pastas até a ilha", "Drag files or folders onto the island");
        Centrado(dc, p, titulo, new System.Drawing.RectangleF(zona.X, zona.Y + 44, zona.Width, 18), 13, Cor.Branco);
        Centrado(dc, p, Lang.T("Eles ficam aqui até você arrastar de volta para qualquer lugar", "They stay here until you drag them back anywhere"),
            new System.Drawing.RectangleF(zona.X, zona.Y + 64, zona.Width, 16), 11, Cor.Secundario, FontWeight.Normal);
    }

    private static void Moldura(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r, bool ativa)
    {
        var rr = new RoundedRectangle(r, 14, 14);
        if (ativa)
        {
            using var preenche = dc.CreateSolidColorBrush(new Color4(Cor.Azul.R, Cor.Azul.G, Cor.Azul.B, 0.14f));
            dc.FillRoundedRectangle(ref rr, preenche);
        }
        using var b = dc.CreateSolidColorBrush(ativa ? Cor.Azul : new Color4(1f, 1f, 1f, 0.22f));
        using var traco = f.CreateStrokeStyle(new StrokeStyleProperties { DashStyle = DashStyle.Dash, DashCap = CapStyle.Round });
        dc.DrawRoundedRectangle(rr, b, 1.4f, traco);
    }

    /// <summary>A pasta encurtada pelo começo até caber medida de verdade (11 px, peso normal, como é desenhada).</summary>
    private string PastaQueCabe(Palco p, string caminho, float largura)
    {
        var chave = (caminho.ToUpperInvariant(), MathF.Round(largura));
        if (_pastasCurtas.TryGetValue(chave, out var pronta)) return pronta;
        if (_pastasCurtas.Count > 256) _pastasCurtas.Clear();
        var r = LogicaDaPrateleira.PastaQueCabe(caminho, s => p.Texto.Medir(s, 11, FontWeight.Normal).X, largura);
        _pastasCurtas[chave] = r;
        return r;
    }

    private static void Centrado(ID2D1DeviceContext dc, Palco p, string texto, System.Drawing.RectangleF r, float tam, Color4 cor, FontWeight peso = FontWeight.Medium)
    {
        var m = p.Texto.Medir(texto, tam, peso);
        float l = MathF.Min(m.X, r.Width - 8);
        p.Texto.Desenhar(dc, texto, new Vector2(r.X + (r.Width - l) / 2, r.Y + (r.Height - m.Y) / 2), tam, cor, peso, largura: r.Width - 8);
    }

    private void MiniaturaPronta() => S.NaInterface(() =>
    {
        if (!_parado && TemCenaViva) Redesenhar();
    });

    // ------------------------------------------------------------------ ícones (só formas)

    /// <summary>Arquivo ou pasta sem miniatura (ainda conferindo, sumiu, ou o shell não deu).</summary>
    private static void DesenharTipo(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r, bool pasta, float forca)
    {
        var cor = new Color4(1f, 1f, 1f, forca);
        Icone.Retangulo(dc, r.X, r.Y, r.Width, r.Height, 8, new Color4(1f, 1f, 1f, 0.06f));
        float u = r.Width / 34f;
        if (pasta)
        {
            Icone.Retangulo(dc, r.X + 7 * u, r.Y + 9 * u, 9 * u, 5 * u, 1.5f * u, cor);
            Icone.Retangulo(dc, r.X + 7 * u, r.Y + 12 * u, 20 * u, 14 * u, 2.5f * u, cor);
            return;
        }
        Icone.Retangulo(dc, r.X + 10 * u, r.Y + 6 * u, 14 * u, 22 * u, 2.5f * u, cor);
        var linha = new Color4(0f, 0f, 0f, 0.45f);
        for (int i = 0; i < 3; i++) Icone.Retangulo(dc, r.X + 13 * u, r.Y + (13 + i * 4) * u, (i == 2 ? 5 : 8) * u, 1.4f * u, 0.7f * u, linha);
    }

    private static void DesenharSeta(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r, Color4 cor)
    {
        float cx = r.X + r.Width / 2;
        Icone.Linha(dc, new Vector2(cx, r.Y + 2), new Vector2(cx, r.Y + 15), 2f, cor, f);
        Icone.Linha(dc, new Vector2(cx - 5, r.Y + 10), new Vector2(cx, r.Y + 15), 2f, cor, f);
        Icone.Linha(dc, new Vector2(cx + 5, r.Y + 10), new Vector2(cx, r.Y + 15), 2f, cor, f);
        Icone.Retangulo(dc, r.X + 2, r.Y + 19, r.Width - 4, 2.4f, 1.2f, cor);
    }

    /// <summary>O ícone do menu: um arquivo e uma pasta em cima de uma tábua.</summary>
    public static void DesenharIcone(ID2D1DeviceContext dc, System.Drawing.RectangleF r)
    {
        float u = r.Width / 24f;
        Icone.Retangulo(dc, r.X + 4 * u, r.Y + 4 * u, 7 * u, 12 * u, 1.6f * u, Cor.Branco);
        Icone.Retangulo(dc, r.X + 12.5f * u, r.Y + 7 * u, 4 * u, 3 * u, 1 * u, Cor.Azul);
        Icone.Retangulo(dc, r.X + 12.5f * u, r.Y + 8.5f * u, 8 * u, 7.5f * u, 1.6f * u, Cor.Azul);
        Icone.Retangulo(dc, r.X + 1.5f * u, r.Y + 17.5f * u, 21 * u, 2.6f * u, 1.3f * u, Cor.Secundario);
        Icone.Retangulo(dc, r.X + 3.5f * u, r.Y + 19.5f * u, 2 * u, 2.5f * u, 0.8f * u, Cor.Secundario);
        Icone.Retangulo(dc, r.X + 18.5f * u, r.Y + 19.5f * u, 2 * u, 2.5f * u, 0.8f * u, Cor.Secundario);
    }
}
