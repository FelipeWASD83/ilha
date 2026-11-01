using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Tempo;

/// <summary>
/// Timers, pomodoro e cronômetro na ilha. Compacta: anel e o tempo do que acaba primeiro (ou o cronômetro). Expandida:
/// o campo para criar ("25m chá", "pomodoro", "cron") e a lista com os botões. Fim de timer: alerta importante, que
/// passa pelo modo competitivo e pelo Não Incomodar.
/// </summary>
internal sealed class AtividadeTempo : Atividade
{
    public override string Tipo => "timer";
    public override string Nome => "Timers";
    public override bool PermitidaNoCompetitivo => true;
    public override bool EhFerramenta => true;
    // quem criou acabou de digitar: não precisa abrir o cartão de novo para anunciar
    public override bool AnunciaChegada => false;

    private const float L = 360f, CAMPO_A = 34f, TOPO = 44f, LINHA = 46f, VOLTA_A = 17f, MAX_A = 300f, DICA_A = 26f;
    private const float R_BOTAO = 15f, B1 = L - 26f, B2 = L - 62f, B3 = L - 98f;
    private const int VOLTAS_MOSTRADAS = 3;
    // pílula: anel na ponta esquerda (centro 12, raio 9, traço 3), tempo encostado na direita
    internal const float COMPACTA_L = 150f, FIM_DO_ANEL = 12 + 9 + 1.5f, FOLGA_MEIO = 6f, VAO_MAIS = 8f;

    private readonly Servicos _s;
    private readonly ConfigTempo _cfg;
    private readonly Relogios _r;
    private readonly Action _salvar;
    // redesenho enquanto há cena na tela e algo correndo; só redesenha quando o segundo mostrado muda
    private readonly System.Windows.Forms.Timer _tique = new() { Interval = 250 };
    // dispara no horário do próximo fim (timer ou fase do pomodoro), apareça a ilha ou não
    private readonly System.Windows.Forms.Timer _alarme = new();
    private int _cenas;
    private int _compactas;
    // o vão da hora foi medido com este texto; se o tempo crescer (cronômetro passando de 9:59 ou de 59:59), refaz
    private int _caracteresDoMeio;
    private long _assinatura;
    private bool _campo;
    private string? _erro;
    private int _rolagem;
    private bool _encerrada;

    private static long Agora => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public AtividadeTempo(Servicos s, ConfigTempo cfg, Action salvar)
    {
        _s = s;
        _cfg = cfg;
        _r = new Relogios(cfg.Estado);
        _salvar = salvar;
        _tique.Tick += (_, _) => Tique();
        _alarme.Tick += (_, _) => { _alarme.Stop(); Verificar(); };
    }

    /// <summary>
    /// O vão entre o anel e o tempo (com o "+N" dos outros), medido no texto que está na tela. A hora fica no meio
    /// desse vão, não no centro da pílula: o tempo à direita tem largura variável.
    /// </summary>
    public override (float De, float Ate)? MeioLivreCompacta
    {
        get
        {
            // o palco dos serviços (o núcleo pergunta antes da primeira cena; um palco guardado no Montar ainda seria null)
            if (_encerrada || _s.Palco is not { } p) return null;
            long agora = Agora;
            if (_r.Principal(agora) is not { } item) return null;
            string texto = Resumo(item, agora).Texto;
            int outros = _r.Quantos - 1;
            string mais = outros > 0 ? "+" + outros : "";
            _caracteresDoMeio = texto.Length + mais.Length;
            try
            {
                float lt = p.Texto.Medir(texto, 15, FontWeight.SemiBold, tabular: true).X;
                float lm = mais.Length > 0 ? p.Texto.Medir(mais, 12, FontWeight.Normal).X : 0;
                return MeioLivre(lt, lm);
            }
            catch (Exception e) { Log.Escrever("timer, medir o meio livre: " + e.Message); return null; }
        }
    }

    /// <summary>Lógica pura do vão: null quando o tempo encosta no anel.</summary>
    internal static (float De, float Ate)? MeioLivre(float larguraTempo, float larguraMais)
    {
        float reservado = larguraTempo + (larguraMais > 0 ? VAO_MAIS + larguraMais : 0);
        float de = FIM_DO_ANEL + FOLGA_MEIO, ate = COMPACTA_L - reservado - FOLGA_MEIO;
        return ate > de ? (de, ate) : null;
    }

    // ------------------------------------------------------------------ ciclo de vida

    /// <summary>Ao ligar: o que estava salvo volta; o que acabou com a ilha fechada vira alerta.</summary>
    public void Restaurar()
    {
        _r.Sanear();
        var ev = _r.Vencer(Agora, _cfg);
        var timers = ev.Where(e => e.Timer != null).ToList();
        if (timers.Count == 1) AlertarTimer(timers[0].Timer!);
        else if (timers.Count > 1) AlertarVarios(timers.Count);
        foreach (var e in ev.Where(e => e.NovaFase != null)) AlertarFase(e);
        Sincronizar(estrutura: true, salvar: ev.Count > 0);
    }

    public void ConfigMudou() => Redesenhar();

    public void Encerrar()
    {
        if (_encerrada) return;
        if (_campo && _s.Entrada.Aberta) _s.Entrada.Fechar();
        _encerrada = true;
        _tique.Stop();
        _alarme.Stop();
        Desligar();
    }

    public override void Dispose()
    {
        _tique.Dispose();
        _alarme.Dispose();
    }

    /// <summary>Estado mudou: salva (se pedido), liga/desliga, refaz a cena (estrutura) ou só redesenha.</summary>
    private void Sincronizar(bool estrutura, bool salvar)
    {
        if (_encerrada) return;
        if (salvar) _salvar();
        if (_r.Vazio) Desligar(); else Ligar();
        if (estrutura) { _rolagem = Math.Min(_rolagem, MaxRolagem()); MudouTamanho(); }
        else Redesenhar();
        _assinatura = _r.Assinatura(Agora);
        Reprogramar();
        AtualizarTique();
    }

    private void Reprogramar()
    {
        _alarme.Stop();
        if (_encerrada || _r.ProximoFim() is not long fim) return;
        // intervalo longo é revisto de hora em hora (o relógio do sistema pode ter mudado)
        _alarme.Interval = (int)Math.Clamp(fim - Agora + 5, 1, 3_600_000);
        _alarme.Start();
    }

    private void AtualizarTique()
    {
        bool precisa = !_encerrada && _cenas > 0 && _r.AlgoRodando;
        if (precisa && !_tique.Enabled) { _assinatura = _r.Assinatura(Agora); _tique.Start(); }
        else if (!precisa && _tique.Enabled) _tique.Stop();
    }

    private void Tique()
    {
        long agora = Agora;
        if (_r.ProximoFim() is long fim && fim <= agora) { Verificar(); return; }
        long a = _r.Assinatura(agora);
        if (a == _assinatura) return;
        _assinatura = a;
        // o tempo ganhou um dígito com a hora na pílula: refaz a cena para o vão ser medido de novo. Zera a base antes:
        // o núcleo só remede quando a hora pode aparecer, e sem isso cada segundo seguinte refaria a cena
        if (_compactas > 0 && _caracteresDoMeio > 0 && CaracteresAgora(agora) > _caracteresDoMeio) { _caracteresDoMeio = 0; MudouTamanho(); return; }
        Redesenhar();
    }

    private int CaracteresAgora(long agora)
    {
        if (_r.Principal(agora) is not { } item) return 0;
        int outros = _r.Quantos - 1;
        return Resumo(item, agora).Texto.Length + (outros > 0 ? 1 + outros.ToString(System.Globalization.CultureInfo.InvariantCulture).Length : 0);
    }

    private void Verificar()
    {
        if (_encerrada) return;
        var ev = _r.Vencer(Agora, _cfg);
        if (ev.Count == 0) { Reprogramar(); return; }
        foreach (var e in ev)
        {
            if (e.Timer != null) AlertarTimer(e.Timer);
            else AlertarFase(e);
        }
        Sincronizar(estrutura: true, salvar: true);
    }

    // ------------------------------------------------------------------ teclado

    /// <summary>O atalho dos timers: abre o cartão com o campo; de novo, fecha.</summary>
    public void Atalho()
    {
        if (_encerrada) return;
        if (_s.Ilha.Expandida == this)
        {
            if (_campo && _s.Entrada.Aberta) _s.Entrada.Fechar();
            else _s.Ilha.Recolher();
            return;
        }
        var ctx = _s.ContextoAtual();
        // no competitivo o cartão vazio não aparece (só o que está rodando): não adianta roubar o foco do jogo
        if (ctx.Competitivo && !Ativa) return;
        _s.Ilha.Expandir(this);
        // tela cheia exclusiva: pegar o teclado tiraria o jogo da tela; só mostra
        if (_s.Ilha.Expandida == this && !ctx.D3DExclusivo) AbrirCampo();
    }

    private void AbrirCampo()
    {
        if (_encerrada || (_campo && _s.Entrada.Aberta)) return;
        // aberto pelo atalho, o cartão fica até clicar fora (com o mouse ele fecharia ao sair de cima)
        _s.Ilha.Expandir(this);
        if (_s.Ilha.Expandida != this) return;
        _erro = null;
        _campo = true;
        _s.Entrada.Abrir("", _ => { _erro = null; Redesenhar(); }, Confirmar, fechou: () =>
        {
            _campo = false;
            _erro = null;
            if (_s.Ilha.Expandida == this) _s.Ilha.Recolher();
            else Redesenhar();
        });
        Redesenhar();
    }

    private void Confirmar(string texto)
    {
        var pedido = Interpretador.Ler(texto, out var erro);
        string? falhou = pedido == null ? erro : _r.Criar(pedido, Agora, _cfg);
        if (falhou != null) { _erro = falhou; Redesenhar(); return; }
        Sincronizar(estrutura: true, salvar: true);
        _s.Entrada.Fechar();
    }

    public override bool Rolar(int delta)
    {
        if (_s.Ilha.Expandida != this) return false;
        int max = MaxRolagem();
        if (max == 0) return false;
        int novo = Math.Clamp(_rolagem + (delta > 0 ? -1 : 1), 0, max);
        if (novo != _rolagem) { _rolagem = novo; MudouTamanho(); }
        return true;
    }

    // ------------------------------------------------------------------ ações dos botões

    private void AlternarTimer(int id)
    {
        if (_r.Timer(id) is not { } t) return;
        _r.Alternar(t, Agora);
        Sincronizar(estrutura: false, salvar: true);
    }

    private void CancelarTimer(int id)
    {
        if (_r.Cancelar(id)) Sincronizar(estrutura: true, salvar: true);
    }

    private void Faz(Action a, bool estrutura)
    {
        a();
        Sincronizar(estrutura, salvar: true);
    }

    // ------------------------------------------------------------------ alertas

    private void AlertarTimer(TimerSalvo t)
    {
        long? adiadoAte = null;
        string titulo = (t.Nome.Length > 0 ? Formato.Maiuscula(t.Nome) : "Timer") + " acabou";
        Alertar(new Alerta
        {
            Tipo = "timer",
            Som = "timer",
            Importante = true,
            PermitidoNoCompetitivo = true,
            Segundos = (float)_cfg.SegundosDoAlerta,
            Criar = p => CenaAlerta(p, titulo,
                () => adiadoAte is long a ? "mais 1 minuto, até " + Formato.Hora(a) : Formato.Duracao(t.DuracaoMs),
                Cor.Laranja, comMais: adiadoAte == null, mais: () =>
                {
                    if (adiadoAte != null || _encerrada) return;
                    adiadoAte = _r.MaisUmMinuto(t, Agora).FimMs;
                    // a versão nova refaz a cena do alerta (sem o botão, com a confirmação)
                    Sincronizar(estrutura: true, salvar: true);
                }),
        });
    }

    private void AlertarVarios(int n)
    {
        Alertar(new Alerta
        {
            Tipo = "timer",
            Som = "timer",
            Importante = true,
            PermitidoNoCompetitivo = true,
            Segundos = (float)_cfg.SegundosDoAlerta,
            Criar = p => CenaAlerta(p, $"{n} timers acabaram", () => "enquanto a ilha estava fechada", Cor.Laranja, false, null),
        });
    }

    private void AlertarFase(Evento e)
    {
        var fase = e.NovaFase ?? Fase.Foco;
        string titulo = fase switch { Fase.Foco => "Hora de focar", Fase.Pausa => "Hora da pausa", _ => "Pausa longa" };
        int n = _cfg.FocosAteLonga;
        int num = CicloPomodoro.NumeroDoFoco(fase, e.FocosFeitos, n);
        long dur = CicloPomodoro.DuracaoMs(fase, _cfg);
        string nome = _r.E.Pomodoro?.Nome is { Length: > 0 } nm ? Formato.Maiuscula(nm) + " · " : "";
        string sub = nome + Formato.Duracao(dur) + (fase == Fase.Foco ? $" · foco {num} de {n}" : $" · {num} de {n} focos feitos");
        if (e.Saltos > 1) sub += " (a ilha ficou fechada)";
        Alertar(new Alerta
        {
            Tipo = "timer",
            Som = "timer",
            Importante = true,
            PermitidoNoCompetitivo = true,
            Segundos = (float)_cfg.SegundosDoAlerta,
            Criar = p => CenaAlerta(p, titulo, () => sub, fase == Fase.Foco ? Cor.Laranja : Cor.Verde, false, null),
        });
    }

    /// <summary>
    /// Cartão do alerta. O "ok" é só desenho: clicar fora de um botão já fecha o alerta (é o que a ilha faz), então
    /// ele não precisa de área própria. O "+1 min" é botão.
    /// </summary>
    private Cena CenaAlerta(Palco p, string titulo, Func<string> sub, Color4 cor, bool comMais, Action? mais)
    {
        const float W = 340, H = 56;
        var c = p.NovaCena(new Vector2(W, H));
        var mais1 = new RectangleF(W - 140, 12, 78, 32);
        var ok = new RectangleF(W - 54, 12, 54, 32);
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            Icone.Anel(dc, p.G.Fabrica, new Vector2(28, 28), 19, 4, 1, cor);
            float larg = (comMais ? mais1.X : ok.X) - 8 - 60;
            p.Texto.Desenhar(dc, titulo, new Vector2(60, 6), 17, Cor.Branco, largura: larg);
            p.Texto.Desenhar(dc, sub(), new Vector2(60, 30), 13, Cor.Secundario, FontWeight.Normal, largura: larg);
            if (comMais) Pilula(dc, p, mais1, "+1 min", Tom(Cor.Laranja, 0.22f), Cor.Laranja);
            Pilula(dc, p, ok, "ok", Cor.Fraco, Cor.Branco);
        });
        if (comMais && mais != null) c.Botoes.Add(new Botao { Area = mais1, Acao = mais });
        return c;
    }

    // ------------------------------------------------------------------ cenas

    protected override Cena Montar(Palco p, Forma f)
    {
        var c = f switch
        {
            Forma.Bolha => Bolha(p),
            Forma.Compacta => Compacta(p),
            _ => Expandida(p),
        };
        _cenas++;
        if (f == Forma.Compacta) _compactas++;
        c.AoSoltar += () =>
        {
            _cenas--;
            if (f == Forma.Compacta) _compactas--;
            AtualizarTique();
            // a ilha recolheu (clique fora, atalho principal, outra atividade): o campo não fica pegando o teclado à toa
            if (f is Forma.Expandida or Forma.Alerta && _campo && _s.Entrada.Aberta && _s.Ilha.Expandida != this)
                _s.Entrada.Fechar(devolverFoco: true);
        };
        AtualizarTique();
        return c;
    }

    private (float Progresso, string Texto, Color4 Cor) Resumo(ItemRef item, long agora)
    {
        switch (item.Tipo)
        {
            case TipoItem.Pomodoro when _r.E.Pomodoro is { } po:
            {
                var cor = po.Pausado ? Cor.Secundario : po.Fase == Fase.Foco ? Cor.Laranja : Cor.Verde;
                return (Relogios.Progresso(po, agora), Formato.Contagem(Relogios.Restante(po, agora)), cor);
            }
            case TipoItem.Cronometro when _r.E.Cronometro is { } cr:
                return (Relogios.Progresso(cr, agora), Formato.Decorrido(Relogios.Decorrido(cr, agora)), cr.Rodando ? Cor.Laranja : Cor.Secundario);
            case TipoItem.Timer when _r.Timer(item.Id) is { } t:
                return (Relogios.Progresso(t, agora), Formato.Contagem(Relogios.Restante(t, agora)), t.Pausado ? Cor.Secundario : Cor.Laranja);
            default:
                return (0, "", Cor.Secundario);
        }
    }

    private Cena Bolha(Palco p)
    {
        var c = p.NovaCena(new Vector2(24, 24));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            if (_r.Principal(Agora) is not { } item) return;
            var (prog, _, cor) = Resumo(item, Agora);
            Icone.Anel(dc, p.G.Fabrica, new Vector2(12, 12), 9, 3, prog, cor);
        });
        return c;
    }

    private Cena Compacta(Palco p)
    {
        var c = p.NovaCena(new Vector2(COMPACTA_L, 24));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            long agora = Agora;
            if (_r.Principal(agora) is not { } item) return;
            var (prog, texto, cor) = Resumo(item, agora);
            Icone.Anel(dc, p.G.Fabrica, new Vector2(12, 12), 9, 3, prog, cor);
            var tam = p.Texto.Medir(texto, 15, FontWeight.SemiBold, tabular: true);
            float x = t.X - tam.X;
            p.Texto.Desenhar(dc, texto, new Vector2(x, (t.Y - tam.Y) / 2), 15, cor, FontWeight.SemiBold, tabular: true);
            int outros = _r.Quantos - 1;
            if (outros > 0)
            {
                string m = "+" + outros;
                var tm = p.Texto.Medir(m, 12, FontWeight.Normal);
                p.Texto.Desenhar(dc, m, new Vector2(x - 8 - tm.X, (t.Y - tm.Y) / 2), 12, Cor.Secundario, FontWeight.Normal);
            }
        });
        return c;
    }

    private sealed record Linha(ItemRef Item, float Y, float Altura);

    private float AlturaDe(ItemRef i) =>
        i.Tipo == TipoItem.Cronometro && _r.E.Cronometro is { Voltas.Count: > 0 } c
            ? LINHA + Math.Min(VOLTAS_MOSTRADAS, c.Voltas.Count) * VOLTA_A + 4
            : LINHA;

    /// <summary>Quantos itens do começo podem ficar escondidos (rolando) para o fim da lista caber.</summary>
    private int MaxRolagem()
    {
        var itens = _r.Itens();
        float soma = 0;
        int cabem = 0;
        for (int k = itens.Count - 1; k >= 0; k--)
        {
            float a = AlturaDe(itens[k]);
            if (soma + a > MAX_A - TOPO && cabem > 0) break;
            soma += a;
            cabem++;
        }
        return itens.Count - cabem;
    }

    private Cena Expandida(Palco p)
    {
        var itens = _r.Itens();
        _rolagem = Math.Clamp(_rolagem, 0, MaxRolagem());
        var linhas = new List<Linha>();
        float y = TOPO;
        for (int k = _rolagem; k < itens.Count; k++)
        {
            float a = AlturaDe(itens[k]);
            if (y + a > MAX_A && linhas.Count > 0) break;
            linhas.Add(new Linha(itens[k], y, a));
            y += a;
        }
        float altura = itens.Count == 0 ? TOPO + DICA_A : y;
        int escondidasDepois = itens.Count - _rolagem - linhas.Count;
        int rolagem = _rolagem, total = itens.Count;

        var c = p.NovaCena(new Vector2(L, altura));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            long agora = Agora;
            DesenharCampo(dc, p);
            if (total == 0)
            {
                p.Texto.Desenhar(dc, "Vários ao mesmo tempo. \"25m chá\" dá nome ao timer.", new Vector2(4, TOPO), 12, Cor.Secundario, FontWeight.Normal, largura: L - 8);
                return;
            }
            foreach (var ln in linhas) DesenharLinha(dc, p, ln, agora);
            if (rolagem > 0 || escondidasDepois > 0)
            {
                // barrinha de rolagem: onde a parte visível está na lista
                float trilho = altura - TOPO - 8;
                float ini = TOPO + 4 + trilho * rolagem / total, tam = MathF.Max(10, trilho * linhas.Count / total);
                Icone.Retangulo(dc, L - 3, TOPO + 4, 2, trilho, 1, Cor.Fraco);
                Icone.Retangulo(dc, L - 3, ini, 2, tam, 1, Cor.Secundario);
            }
        });

        c.Botoes.Add(new Botao { Area = new RectangleF(0, 0, L, CAMPO_A), Acao = AbrirCampo });
        foreach (var ln in linhas)
        {
            float cy = ln.Y + 22;
            RectangleF Area(float cx) => new(cx - R_BOTAO - 2, cy - R_BOTAO - 2, (R_BOTAO + 2) * 2, (R_BOTAO + 2) * 2);
            switch (ln.Item.Tipo)
            {
                case TipoItem.Timer:
                    int id = ln.Item.Id;
                    c.Botoes.Add(new Botao { Area = Area(B2), Acao = () => AlternarTimer(id) });
                    c.Botoes.Add(new Botao { Area = Area(B1), Acao = () => CancelarTimer(id) });
                    break;
                case TipoItem.Pomodoro:
                    c.Botoes.Add(new Botao { Area = Area(B3), Acao = () => Faz(() => _r.AlternarPomodoro(Agora), false) });
                    c.Botoes.Add(new Botao { Area = Area(B2), Acao = () => Faz(() => _r.PularFase(Agora, _cfg), false) });
                    c.Botoes.Add(new Botao { Area = Area(B1), Acao = () => Faz(_r.CancelarPomodoro, true) });
                    break;
                case TipoItem.Cronometro:
                    c.Botoes.Add(new Botao { Area = Area(B3), Acao = () =>
                    {
                        // a lista de voltas só cresce até 3 linhas; depois disso a altura não muda e basta redesenhar
                        if (_r.Volta(Agora)) Sincronizar(estrutura: _r.E.Cronometro!.Voltas.Count <= VOLTAS_MOSTRADAS, salvar: true);
                    } });
                    c.Botoes.Add(new Botao { Area = Area(B2), Acao = () => Faz(() => _r.AlternarCronometro(Agora), false) });
                    c.Botoes.Add(new Botao { Area = Area(B1), Acao = () => Faz(_r.ZerarCronometro, true) });
                    break;
            }
        }
        return c;
    }

    private string Previa(Pedido pd) => pd.Tipo switch
    {
        TipoPedido.Pomodoro => $"pomodoro {Formato.Duracao(CicloPomodoro.DuracaoMs(Fase.Foco, _cfg))} + {Formato.Duracao(CicloPomodoro.DuracaoMs(Fase.Pausa, _cfg))}",
        TipoPedido.Cronometro => "cronômetro",
        _ => Formato.Contagem(pd.DuracaoMs) + (pd.Nome.Length > 0 ? " · " + pd.Nome : ""),
    };

    private void DesenharCampo(ID2D1DeviceContext dc, Palco p)
    {
        Icone.Retangulo(dc, 0, 0, L, CAMPO_A, 10, new Color4(1f, 1f, 1f, 0.09f));
        if (!(_campo && _s.Entrada.Aberta))
        {
            var F = p.G.Fabrica;
            Icone.Linha(dc, new Vector2(12, CAMPO_A / 2), new Vector2(22, CAMPO_A / 2), 2, Cor.Laranja, F);
            Icone.Linha(dc, new Vector2(17, CAMPO_A / 2 - 5), new Vector2(17, CAMPO_A / 2 + 5), 2, Cor.Laranja, F);
            const string convite = "Novo: 25m, 1h30, 1:30, pomodoro, cron";
            var tc = p.Texto.Medir(convite, 14, FontWeight.Normal);
            p.Texto.Desenhar(dc, convite, new Vector2(32, (CAMPO_A - tc.Y) / 2), 14, Cor.Secundario, FontWeight.Normal, largura: L - 44);
            return;
        }
        string texto = _s.Entrada.Texto;
        string previa;
        Color4 cp;
        if (_erro != null) { previa = _erro; cp = Cor.Vermelho; }
        else if (texto.Trim().Length == 0) { previa = "Enter cria · Esc sai"; cp = Cor.Secundario; }
        else
        {
            var pd = Interpretador.Ler(texto, out _);
            previa = pd == null ? "?" : Previa(pd);
            cp = pd == null ? Cor.Secundario : Cor.Laranja;
        }
        var tp = p.Texto.Medir(previa, 12, FontWeight.Normal);
        float lp = MathF.Min(tp.X, 190);
        p.Texto.Desenhar(dc, previa, new Vector2(L - 12 - lp, (CAMPO_A - tp.Y) / 2), 12, cp, FontWeight.Normal, largura: 190);
        p.Texto.DesenharCampo(dc, texto, _s.Entrada.Cursor, false, new Vector2(12, 7), L - 36 - lp, 15, Cor.Branco, "25m chá, 1:30, pomodoro, cron");
    }

    private void DesenharLinha(ID2D1DeviceContext dc, Palco p, Linha ln, long agora)
    {
        var F = p.G.Fabrica;
        float y = ln.Y, cy = y + 22;
        string titulo, tempo;
        float prog;
        Color4 cor;
        float limite;
        switch (ln.Item.Tipo)
        {
            case TipoItem.Timer:
            {
                if (_r.Timer(ln.Item.Id) is not { } t) return;
                cor = t.Pausado ? Cor.Secundario : Cor.Laranja;
                prog = Relogios.Progresso(t, agora);
                tempo = Formato.Contagem(Relogios.Restante(t, agora));
                titulo = (t.Nome.Length > 0 ? Formato.Maiuscula(t.Nome) : "Timer") + " · " + Formato.Duracao(t.DuracaoMs)
                    + (t.Pausado ? " · pausado" : " · acaba às " + Formato.Hora(t.FimMs));
                BotaoPausa(dc, F, new Vector2(B2, cy), t.Pausado, Cor.Laranja);
                BotaoXis(dc, F, new Vector2(B1, cy));
                limite = B2;
                break;
            }
            case TipoItem.Pomodoro:
            {
                if (_r.E.Pomodoro is not { } po) return;
                var acento = po.Fase == Fase.Foco ? Cor.Laranja : Cor.Verde;
                cor = po.Pausado ? Cor.Secundario : acento;
                prog = Relogios.Progresso(po, agora);
                tempo = Formato.Contagem(Relogios.Restante(po, agora));
                int n = _cfg.FocosAteLonga;
                int num = CicloPomodoro.NumeroDoFoco(po.Fase, po.FocosFeitos, n);
                titulo = (po.Nome.Length > 0 ? Formato.Maiuscula(po.Nome) : "Pomodoro") + " · "
                    + (po.Fase == Fase.Foco ? $"foco {num} de {n}" : CicloPomodoro.NomeDaFase(po.Fase))
                    + (po.Pausado ? " · pausado" : "");
                BotaoPausa(dc, F, new Vector2(B3, cy), po.Pausado, acento);
                BotaoPular(dc, F, new Vector2(B2, cy));
                BotaoXis(dc, F, new Vector2(B1, cy));
                limite = B3;
                break;
            }
            default:
            {
                if (_r.E.Cronometro is not { } cr) return;
                cor = cr.Rodando ? Cor.Laranja : Cor.Secundario;
                prog = Relogios.Progresso(cr, agora);
                tempo = Formato.Decorrido(Relogios.Decorrido(cr, agora));
                titulo = "Cronômetro" + (cr.Voltas.Count > 0 ? $" · {cr.Voltas.Count} volta{(cr.Voltas.Count > 1 ? "s" : "")}" : "") + (cr.Rodando ? "" : " · pausado");
                BotaoVolta(dc, F, new Vector2(B3, cy), cr.Rodando);
                BotaoPausa(dc, F, new Vector2(B2, cy), !cr.Rodando, Cor.Laranja);
                BotaoXis(dc, F, new Vector2(B1, cy));
                limite = B3;
                // as últimas voltas, a mais nova em cima: número, duração da volta e o total naquela hora
                for (int k = 0; k < Math.Min(VOLTAS_MOSTRADAS, cr.Voltas.Count); k++)
                {
                    int i = cr.Voltas.Count - 1 - k;
                    float vy = y + LINHA + k * VOLTA_A;
                    p.Texto.Desenhar(dc, $"Volta {i + 1}", new Vector2(40, vy), 12, Cor.Secundario, FontWeight.Normal);
                    p.Texto.Desenhar(dc, Formato.Volta(Relogios.DuracaoDaVolta(cr, i)), new Vector2(120, vy), 12, Cor.Branco, FontWeight.Medium, tabular: true);
                    p.Texto.Desenhar(dc, Formato.Volta(cr.Voltas[i]), new Vector2(210, vy), 12, Cor.Secundario, FontWeight.Normal, tabular: true);
                }
                break;
            }
        }
        Icone.Anel(dc, F, new Vector2(16, cy), 12, 3, prog, cor);
        p.Texto.Desenhar(dc, titulo, new Vector2(40, y + 1), 12, Cor.Secundario, FontWeight.Normal, largura: limite - R_BOTAO - 8 - 40);
        p.Texto.Desenhar(dc, tempo, new Vector2(39, y + 15), 22, cor, FontWeight.Medium, tabular: true);
    }

    // ------------------------------------------------------------------ desenhos dos botões

    private static Color4 Tom(Color4 c, float a) => new(c.R, c.G, c.B, a);

    private static void BotaoPausa(ID2D1DeviceContext dc, ID2D1Factory1 F, Vector2 c, bool pausado, Color4 cor)
    {
        Icone.Circulo(dc, c, R_BOTAO, Tom(cor, 0.22f));
        if (pausado) Icone.Triangulo(dc, F, new Vector2(c.X - 4, c.Y - 7), new Vector2(c.X + 7, c.Y), new Vector2(c.X - 4, c.Y + 7), cor);
        else
        {
            Icone.Retangulo(dc, c.X - 5.5f, c.Y - 7, 4, 14, 1.2f, cor);
            Icone.Retangulo(dc, c.X + 1.5f, c.Y - 7, 4, 14, 1.2f, cor);
        }
    }

    private static void BotaoXis(ID2D1DeviceContext dc, ID2D1Factory1 F, Vector2 c)
    {
        Icone.Circulo(dc, c, R_BOTAO, Cor.Fraco);
        Icone.Linha(dc, new Vector2(c.X - 5, c.Y - 5), new Vector2(c.X + 5, c.Y + 5), 2.4f, Cor.Branco, F);
        Icone.Linha(dc, new Vector2(c.X + 5, c.Y - 5), new Vector2(c.X - 5, c.Y + 5), 2.4f, Cor.Branco, F);
    }

    private static void BotaoPular(ID2D1DeviceContext dc, ID2D1Factory1 F, Vector2 c)
    {
        Icone.Circulo(dc, c, R_BOTAO, Cor.Fraco);
        Icone.Triangulo(dc, F, new Vector2(c.X - 6, c.Y - 6.5f), new Vector2(c.X + 3, c.Y), new Vector2(c.X - 6, c.Y + 6.5f), Cor.Branco);
        Icone.Retangulo(dc, c.X + 3.5f, c.Y - 6.5f, 2.6f, 13, 1, Cor.Branco);
    }

    private static void BotaoVolta(ID2D1DeviceContext dc, ID2D1Factory1 F, Vector2 c, bool ativo)
    {
        var cor = ativo ? Cor.Branco : Tom(Cor.Branco, 0.3f);
        Icone.Circulo(dc, c, R_BOTAO, Cor.Fraco);
        Icone.Linha(dc, new Vector2(c.X - 4, c.Y - 7), new Vector2(c.X - 4, c.Y + 7), 2, cor, F);
        Icone.Triangulo(dc, F, new Vector2(c.X - 3, c.Y - 7), new Vector2(c.X + 7, c.Y - 3.5f), new Vector2(c.X - 3, c.Y), cor);
    }

    private static void Pilula(ID2D1DeviceContext dc, Palco p, RectangleF r, string texto, Color4 fundo, Color4 cor)
    {
        Icone.Retangulo(dc, r.X, r.Y, r.Width, r.Height, r.Height / 2, fundo);
        var t = p.Texto.Medir(texto, 14, FontWeight.SemiBold);
        p.Texto.Desenhar(dc, texto, new Vector2(r.X + (r.Width - t.X) / 2, r.Y + (r.Height - t.Y) / 2), 14, cor, FontWeight.SemiBold);
    }
}
