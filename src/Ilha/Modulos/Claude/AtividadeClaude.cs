using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Claude;

/// <summary>
/// O Claude Code na ilha: a sessão mais recente em andamento (ou a que pede permissão), com a logo animada como no
/// ClaudeDeMentira (entrada e loop trabalhando, bola e bolinhas pensando, saída e pulso esperando ou ao terminar).
/// O cartão mostra o projeto, o que ele está fazendo, os botões de modelo, esforço e mensagem (pela ponte com o app),
/// os botões de permitir e negar quando tem um pedido, e as outras sessões em andamento.
/// </summary>
internal sealed class AtividadeClaude : Atividade
{
    public override string Tipo => "claude";
    public override string Nome => "Claude Code";
    // aparece e some a cada pedido: abrir o cartão toda vez seria barulho (permissão e fim já têm alerta próprio)
    public override bool AnunciaChegada => false;

    private enum Modo { Normal, Modelos, Esforcos, Mensagem }
    private enum Logo { Nada, Loop, Bola, Pulso }

    private readonly Servicos S;
    private readonly Sessoes _m;
    private readonly PonteClaudeApp _ponte;
    private readonly Func<double> _agora;
    private readonly Func<ConfigClaude> _cfg;

    private Modo _modo;
    private string? _escolhida;
    private string _rascunho = "";
    private string? _aviso;
    private bool _avisoErro;
    private readonly System.Windows.Forms.Timer _tirarAviso = new() { Interval = 5000 };
    private bool _lendo;
    // o que a ponte leu de cada sessão (pelo nome dela no app)
    private readonly Dictionary<string, (PainelLido Painel, double Em)> _lidos = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _opcoesDoApp = new();
    private string _assinatura = "";

    // logo: todas as cenas vivas tocam juntas; cena nova entra no loop atual
    private readonly List<LogoAnimada> _logos = new();
    private string[] _seq = Array.Empty<string>();
    private Logo _logo;
    private string? _bolinha;
    private int _vezes;
    private static readonly string[] Bolinhas = { "orbita", "cometa", "onda", "divide", "anel" };

    public AtividadeClaude(Servicos s, Sessoes m, PonteClaudeApp ponte, Func<double> agora, Func<ConfigClaude> cfg)
    {
        S = s;
        _m = m;
        _ponte = ponte;
        _agora = agora;
        _cfg = cfg;
        _tirarAviso.Tick += (_, _) => { _tirarAviso.Stop(); _aviso = null; Atualizar(); };
        S.Entrada.Mudou += EntradaMudou;
    }

    public override void Dispose()
    {
        S.Entrada.Mudou -= EntradaMudou;
        if (_modo == Modo.Mensagem && S.Entrada.Aberta) S.Entrada.Fechar(devolverFoco: false);
        _tirarAviso.Dispose();
    }

    private void EntradaMudou()
    {
        if (_modo == Modo.Mensagem) Redesenhar();
    }

    // ------------------------------------------------------------------ estado

    private Sessao? Mostrada
    {
        get
        {
            if (_escolhida != null && _m.Pegar(_escolhida) is { } e && Sessoes.Visivel(e)) return e;
            _escolhida = null;
            return _m.Principal();
        }
    }

    private PedidoPermissao? PedidoDe(Sessao? s) => s?.Pedidos.FirstOrDefault();

    // what the pill shows: step, project and color; a change animates instead of just being redrawn
    private readonly record struct CompactLine(string Status, string Project, Color4 Color);
    private readonly List<(Palco P, Cena Cena, Superficie Surface)> _compactLayers = new();
    private CompactLine? _compactShown;

    private CompactLine? CompactNow()
    {
        var s = Mostrada;
        if (s == null) return null;
        var cor = PedidoDe(s) != null ? Cor.Coral : s.Fase is Fase.Terminou or Fase.Parada ? Cor.Secundario : Cor.Branco;
        return new CompactLine(Status(s), Projeto(s), cor);
    }

    private static void DrawCompact(Palco p, ID2D1DeviceContext dc, Vector2 t, CompactLine line)
    {
        var tam = p.Texto.Medir(line.Status, 13);
        float l = MathF.Min(tam.X, 160);
        p.Texto.Desenhar(dc, line.Status, new Vector2(t.X - l, (t.Y - tam.Y) / 2), 13, line.Color, largura: 160);
        float sobra = t.X - l - 8 - 34;
        if (sobra > 24)
        {
            var tp = p.Texto.Medir(line.Project, 13, FontWeight.Normal);
            float lp = MathF.Min(tp.X, sobra);
            p.Texto.Desenhar(dc, line.Project, new Vector2(t.X - l - 8 - lp, (t.Y - tp.Y) / 2), 13, Cor.Secundario, FontWeight.Normal, largura: sobra);
        }
    }

    /// <summary>The step text on the pill changed: the old one goes up and fades, the new one comes from below.</summary>
    private void AnimateCompactChange()
    {
        var now = CompactNow();
        var old = _compactShown;
        _compactShown = now;
        if (old is not { } o || now is not { } n || o == n || o.Status == n.Status && o.Project == n.Project) return;
        foreach (var (p, cena, surface) in _compactLayers.ToArray())
            ContentSwap.Play(p, cena, surface, (dc, t) => DrawCompact(p, dc, t, o), new Vector2(0, -1), 14, 300);
    }

    /// <summary>Chamado pelo módulo a cada mudança: liga, desliga, troca a logo e redesenha só o que mudou.</summary>
    public void Atualizar()
    {
        var s = Mostrada;
        if (s == null)
        {
            if (_modo == Modo.Mensagem && S.Entrada.Aberta) S.Entrada.Fechar();
            _modo = Modo.Normal;
            AjustarLogo(null);
            Desligar();
            return;
        }
        AjustarLogo(s);
        if (!Ativa) { _assinatura = Assinatura(); Ligar(); return; }
        var a = Assinatura();
        if (a != _assinatura) { _assinatura = a; _compactShown = CompactNow(); MudouTamanho(); }
        else { AnimateCompactChange(); Redesenhar(); }
    }

    private string Assinatura()
    {
        var s = Mostrada;
        int outras = Outras(s).Count;
        // qual pedido, não só se tem: o segundo da mesma sessão assumindo refaz os botões ligados a ele
        return $"{_modo}|{s?.Id}|{PedidoDe(s)?.Numero ?? 0}|{outras}|{_aviso != null}|{_opcoesDoApp.Count}";
    }

    private List<Sessao> Outras(Sessao? s) => _m.Ordenadas().Where(x => x != s).Take(3).ToList();

    private string Status(Sessao s)
    {
        var p = PedidoDe(s);
        if (p != null) return Lang.T("Permissão: ", "Permission: ") + p.Ferramenta;
        return s.Texto.Length > 0 ? s.Texto : Lang.T("Pensando", "Thinking");
    }

    private static string Projeto(Sessao s) => s.Projeto.Length > 0 ? s.Projeto : "Claude Code";

    // ------------------------------------------------------------------ logo

    private LogoAnimada NovaLogo(Palco p, Cena c, Vector2 pos, float lado)
    {
        var l = p.Logo(c, pos, lado);
        _logos.Add(l);
        c.AoSoltar += () => _logos.Remove(l);
        if (_seq.Length > 0) l.Tocar(_seq[^1]);
        return l;
    }

    private void Animar(params string[] seq)
    {
        _seq = seq;
        foreach (var l in _logos.ToArray()) l.Tocar(seq);
    }

    private void AjustarLogo(Sessao? s)
    {
        Logo alvo = s == null ? Logo.Nada
            : PedidoDe(s) != null ? Logo.Pulso
            : s.Fase == Fase.Pensando ? Logo.Bola
            : s.Fase == Fase.Trabalhando ? Logo.Loop
            : Logo.Pulso;
        if (alvo == _logo) return;
        var antes = _logo;
        _logo = alvo;
        switch (alvo)
        {
            case Logo.Nada:
                _seq = Array.Empty<string>();
                _bolinha = null;
                break;
            case Logo.Loop:
                if (antes == Logo.Bola && _bolinha != null) Animar(_bolinha + "-saida", "claude-bola-encolhe", "claude-surge", "claude-loop");
                else if (antes == Logo.Pulso) Animar("claude-surge", "claude-loop");
                else Animar("claude-entrada", "claude-loop");
                _bolinha = null;
                break;
            case Logo.Bola:
                // uma animação de bolinhas diferente a cada vez que ele pensa
                _bolinha = "claude-bola-" + Bolinhas[_vezes++ % Bolinhas.Length];
                if (antes == Logo.Loop) Animar("claude-bola", "claude-bola-cresce", _bolinha + "-entrada", _bolinha);
                else if (antes == Logo.Pulso) Animar("claude-bola-cresce", _bolinha + "-entrada", _bolinha);
                else Animar("claude-entrada", "claude-bola", "claude-bola-cresce", _bolinha + "-entrada", _bolinha);
                break;
            case Logo.Pulso:
                if (antes == Logo.Bola && _bolinha != null) Animar(_bolinha + "-saida", "claude-bola-encolhe", "claude-pulso");
                else if (antes == Logo.Loop) Animar("claude-saida", "claude-pulso");
                else Animar("claude-pulso");
                _bolinha = null;
                break;
        }
    }

    // ------------------------------------------------------------------ cenas

    // largura fixa: o texto muda a cada ferramenta, e mudar a largura refaria a cena (com transição) toda hora
    private const float LARGURA_COMPACTA = 240;

    protected override Cena Montar(Palco p, Forma f)
    {
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(24, 24));
                NovaLogo(p, c, Vector2.Zero, 24);
                return c;
            }
            case Forma.Compacta:
            {
                var c = p.NovaCena(new Vector2(LARGURA_COMPACTA, 24));
                NovaLogo(p, c, Vector2.Zero, 24);
                var texto = p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    if (CompactNow() is { } line) DrawCompact(p, dc, t, line);
                });
                var item = (p, c, texto);
                _compactLayers.Add(item);
                c.AoSoltar += () => _compactLayers.Remove(item);
                _compactShown = CompactNow();
                return c;
            }
            default:
                return Cartao(p);
        }
    }

    private const float L = 340;

    private Cena Cartao(Palco p)
    {
        var s = Mostrada;
        var pedido = PedidoDe(s);
        var outras = Outras(s);
        float y = 28;
        float altura;
        var botoes = new List<Botao>();

        switch (_modo)
        {
            case Modo.Modelos:
            {
                int linhas = (Modelos().Count + 1 + 1) / 2;
                altura = y + 22 + linhas * 34;
                break;
            }
            case Modo.Esforcos:
                // 5 levels and Back in the grid, then the Ultracode switch on its own row
                altura = YUltracode(y) + 28;
                break;
            case Modo.Mensagem:
                altura = y + 64;
                break;
            default:
                altura = y + (pedido != null ? 82 : 46) + 34 + (_aviso != null ? 20 : 0) + (outras.Count > 0 ? 8 + outras.Count * 22 : 0);
                break;
        }

        var c = p.NovaCena(new Vector2(L, altura));
        NovaLogo(p, c, new Vector2(-4, -5), 30);
        // o cartão fechou com o campo aberto (clique fora, atalho): fecha o campo e volta ao normal
        c.AoSoltar += () => S.NaInterface(() =>
        {
            if (S.Ilha.Expandida == this) return;
            if (_modo == Modo.Mensagem && S.Entrada.Aberta) S.Entrada.Fechar(devolverFoco: false);
            if (_modo != Modo.Normal) { _modo = Modo.Normal; _escolhida = null; Atualizar(); }
        });

        // abriu o cartão de verdade (não a chegada): lê modelo e esforço no app, se faz tempo
        if (s != null && _modo == Modo.Normal && S.Ilha.Expandida == this) LerDoAppSePreciso(s);

        var lido = s != null && s.Nome.Length > 0 && _lidos.TryGetValue(s.Nome, out var li) ? li.Painel : null;

        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var F = p.G.Fabrica;
            var sm = Mostrada;
            string cab = sm == null ? "Claude Code" : "Claude Code · " + Projeto(sm);
            p.Texto.Desenhar(dc, cab, new Vector2(32, 1), 13, Cor.Secundario, largura: L - 32 - 70);
            int n = _m.Ordenadas().Count;
            if (n > 1)
            {
                string q = Lang.T($"{n} sessões", $"{n} sessions");
                var tq = p.Texto.Medir(q, 12, FontWeight.Normal);
                p.Texto.Desenhar(dc, q, new Vector2(L - tq.X, 2), 12, Cor.Secundario, FontWeight.Normal);
            }
            if (sm == null) return;

            switch (_modo)
            {
                case Modo.Modelos:
                {
                    p.Texto.Desenhar(dc, Lang.T("Escolha o modelo", "Choose the model"), new Vector2(0, y), 13, Cor.Secundario, FontWeight.Normal);
                    var ops = Modelos();
                    string? atual = lido?.Modelo;
                    for (int i = 0; i <= ops.Count; i++)
                    {
                        var r = Grade(i, y + 22);
                        bool voltar = i == ops.Count;
                        string tx = voltar ? Lang.T("Voltar", "Back") : ops[i];
                        bool marcado = !voltar && atual != null && string.Equals(tx, atual, StringComparison.OrdinalIgnoreCase);
                        Chip(p, dc, r, tx, marcado ? Marcado : Cor.Fraco, voltar ? Cor.Secundario : Cor.Branco, false);
                    }
                    break;
                }
                case Modo.Esforcos:
                {
                    p.Texto.Desenhar(dc, Lang.T("Escolha o esforço", "Choose the effort"), new Vector2(0, y), 13, Cor.Secundario, FontWeight.Normal);
                    var ops = BotaoDoModelo.Rotulos;
                    for (int i = 0; i <= ops.Length; i++)
                    {
                        bool voltar = i == ops.Length;
                        bool marcado = !voltar && lido?.Nivel == i;
                        Chip(p, dc, Grade(i, y + 22), voltar ? Lang.T("Voltar", "Back") : ops[i], marcado ? Marcado : Cor.Fraco, voltar ? Cor.Secundario : Cor.Branco, false);
                    }
                    Interruptor(p, dc, new RectangleF(0, YUltracode(y), L, 28), lido?.Ultracode ?? false, lido != null);
                    break;
                }
                case Modo.Mensagem:
                {
                    Icone.Retangulo(dc, 0, y, L, 34, 10, Cor.Fraco);
                    // o nome da sessão no app pode resumir a conversa: na tela, só o projeto
                    string vazio = Lang.T("Mensagem para a sessão de ", "Message to the session of ") + Projeto(sm);
                    p.Texto.DesenharCampo(dc, S.Entrada.Aberta ? S.Entrada.Texto : _rascunho, S.Entrada.Cursor, false, new Vector2(10, y + 7), L - 20, 15, Cor.Branco, vazio, S.Entrada.Aberta);
                    p.Texto.Desenhar(dc, Lang.T("Enter envia · Esc cancela", "Enter sends · Esc cancels"), new Vector2(0, y + 42), 12, Cor.Secundario, FontWeight.Normal);
                    break;
                }
                default:
                {
                    float yy = y;
                    var pd = PedidoDe(sm);
                    if (pd != null)
                    {
                        p.Texto.Desenhar(dc, Lang.T("Pede permissão: ", "Asks for permission: ") + pd.Ferramenta, new Vector2(0, yy - 2), 18, Cor.Branco, largura: L);
                        if (pd.Resumo.Length > 0) p.Texto.Desenhar(dc, pd.Resumo, new Vector2(0, yy + 24), 13, Cor.Secundario, FontWeight.Normal, largura: L);
                        BotaoDeDecisao(p, dc, new RectangleF(0, yy + 46, 164, 30), Lang.T("Permitir", "Allow"), Cor.Verde);
                        BotaoDeDecisao(p, dc, new RectangleF(176, yy + 46, 164, 30), Lang.T("Negar", "Deny"), Cor.Vermelho);
                        yy += 82;
                    }
                    else
                    {
                        p.Texto.Desenhar(dc, Status(sm), new Vector2(0, yy - 2), 20, sm.Fase is Fase.Terminou or Fase.Parada ? Cor.Secundario : Cor.Branco, largura: L);
                        p.Texto.Desenhar(dc, Subtitulo(sm), new Vector2(0, yy + 25), 12, Cor.Secundario, FontWeight.Normal, largura: L);
                        yy += 46;
                    }
                    // modelo, esforço e mensagem
                    var lm = sm.Nome.Length > 0 && _lidos.TryGetValue(sm.Nome, out var lx) ? lx.Painel : null;
                    string tm = lm?.Modelo is { Length: > 0 } mo ? mo : _lendo ? Lang.T("Lendo…", "Reading…") : Lang.T("Modelo", "Model");
                    string te = lm != null && BotaoDoModelo.Rotulo(lm.Nivel, lm.Ultracode, lm.Esforco) is { } es ? CaberEsforco(p, es, CHIP_ESFORCO.Width - 32)
                        : _lendo ? "…" : Lang.T("Esforço", "Effort");
                    Chip(p, dc, CHIP_MODELO with { Y = yy }, tm, Cor.Fraco, Cor.Branco, true);
                    Chip(p, dc, CHIP_ESFORCO with { Y = yy }, te, Cor.Fraco, Cor.Branco, true);
                    Chip(p, dc, CHIP_MENSAGEM with { Y = yy }, Lang.T("Mensagem", "Message"), Cor.Fraco, Cor.Branco, false);
                    yy += 34;
                    if (_aviso != null)
                    {
                        p.Texto.Desenhar(dc, _aviso, new Vector2(0, yy - 2), 12, _avisoErro ? Cor.Vermelho : Cor.Secundario, FontWeight.Normal, largura: L);
                        yy += 20;
                    }
                    if (outras.Count > 0)
                    {
                        yy += 8;
                        foreach (var o in outras)
                        {
                            var corPonto = PedidoDe(o) != null ? Cor.Coral : o.Trabalhando ? Cor.Verde : Cor.Secundario;
                            Icone.Circulo(dc, new Vector2(4, yy + 9), 3.5f, corPonto);
                            var tp = p.Texto.Medir(Projeto(o), 13);
                            float lp = MathF.Min(tp.X, 130);
                            p.Texto.Desenhar(dc, Projeto(o), new Vector2(14, yy), 13, Cor.Branco, largura: 130);
                            p.Texto.Desenhar(dc, Status(o), new Vector2(14 + lp + 8, yy), 13, Cor.Secundario, FontWeight.Normal, largura: L - 22 - lp);
                            yy += 22;
                        }
                    }
                    break;
                }
            }
        });

        // botões (mesma geometria do desenho)
        if (s != null)
        {
            switch (_modo)
            {
                case Modo.Modelos:
                {
                    var ops = Modelos();
                    for (int i = 0; i <= ops.Count; i++)
                    {
                        int k = i;
                        Action a = i == ops.Count ? () => { _modo = Modo.Normal; Atualizar(); } : () => EscolherModelo(ops[k]);
                        botoes.Add(new Botao { Area = Grade(i, y + 22), Acao = a });
                    }
                    break;
                }
                case Modo.Esforcos:
                {
                    int n = BotaoDoModelo.Rotulos.Length;
                    for (int i = 0; i <= n; i++)
                    {
                        int k = i;
                        Action a = i == n ? () => { _modo = Modo.Normal; Atualizar(); } : () => EscolherEsforco(k);
                        botoes.Add(new Botao { Area = Grade(i, y + 22), Acao = a });
                    }
                    botoes.Add(new Botao { Area = new RectangleF(0, YUltracode(y), L, 28), Acao = AlternarUltracode });
                    break;
                }
                case Modo.Mensagem:
                    botoes.Add(new Botao { Area = new RectangleF(0, y, L, 34), Acao = AbrirMensagem });
                    break;
                default:
                {
                    float yy = y;
                    if (pedido != null)
                    {
                        var pd = pedido;
                        long apareceu = Environment.TickCount64;
                        // o pedido na tela já não é este (saiu entre a troca e o Atualizar): só refaz, não decide nada
                        botoes.Add(new Botao { Area = new RectangleF(0, yy + 46, 164, 30), Acao = () =>
                        {
                            if (PedidoDe(Mostrada) != pd) { Atualizar(); return; }
                            if (PedidoPermissao.PassouCarencia(apareceu, Environment.TickCount64)) Decidir(pd, "allow");
                        } });
                        botoes.Add(new Botao { Area = new RectangleF(176, yy + 46, 164, 30), Acao = () =>
                        {
                            if (PedidoDe(Mostrada) != pd) { Atualizar(); return; }
                            Decidir(pd, "deny");
                        } });
                        yy += 82;
                    }
                    else yy += 46;
                    botoes.Add(new Botao { Area = CHIP_MODELO with { Y = yy }, Acao = () => AbrirLista(Modo.Modelos) });
                    botoes.Add(new Botao { Area = CHIP_ESFORCO with { Y = yy }, Acao = () => AbrirLista(Modo.Esforcos) });
                    botoes.Add(new Botao { Area = CHIP_MENSAGEM with { Y = yy }, Acao = AbrirMensagem });
                    yy += 34 + (_aviso != null ? 20 : 0);
                    if (outras.Count > 0)
                    {
                        yy += 8;
                        foreach (var o in outras)
                        {
                            string id = o.Id;
                            botoes.Add(new Botao { Area = new RectangleF(0, yy - 2, L, 22), Acao = () => { _escolhida = id; Atualizar(); } });
                            yy += 22;
                        }
                    }
                    break;
                }
            }
        }
        c.Botoes.AddRange(botoes);
        return c;
    }

    private static RectangleF Grade(int i, float y0) => new((i % 2) * 176, y0 + (i / 2) * 34, 164, 28);

    // model, effort and message chips of the normal card (340 wide with 8 px gaps); the effort one is wider for "Alto · Ultracode"
    private static readonly RectangleF CHIP_MODELO = new(0, 0, 112, 26);
    private static readonly RectangleF CHIP_ESFORCO = new(120, 0, 128, 26);
    private static readonly RectangleF CHIP_MENSAGEM = new(256, 0, 84, 26);
    private static readonly Color4 Marcado = new(0.85f, 0.47f, 0.34f, 0.35f);

    /// <summary>Top of the Ultracode row in the effort list: under the 3 grid rows (5 levels and Back), with a small gap.</summary>
    private static float YUltracode(float y) => y + 22 + 3 * 34 + 6;

    /// <summary>"Alto · Ultracode" when it fits in the chip, else "Alto · Ultra".</summary>
    private static string CaberEsforco(Palco p, string texto, float largura) =>
        p.Texto.Medir(texto, 13).X <= largura ? texto : texto.Replace(BotaoDoModelo.ULTRACODE, "Ultra");

    /// <summary>The Ultracode row: name on the left, the state and a pill switch on the right (coral, knob right when on).</summary>
    private static void Interruptor(Palco p, ID2D1DeviceContext dc, RectangleF r, bool ligado, bool conhecido)
    {
        Icone.Retangulo(dc, r.X, r.Y, r.Width, r.Height, r.Height / 2, ligado ? Marcado : Cor.Fraco);
        var tam = p.Texto.Medir(BotaoDoModelo.ULTRACODE, 13);
        p.Texto.Desenhar(dc, BotaoDoModelo.ULTRACODE, new Vector2(r.X + 12, r.Y + (r.Height - tam.Y) / 2), 13, Cor.Branco);
        string estado = !conhecido ? Lang.T("não lido", "not read") : ligado ? Lang.T("ligado", "on") : Lang.T("desligado", "off");
        var te = p.Texto.Medir(estado, 12, FontWeight.Normal);
        float tw = 32, th = 18, tx = r.Right - 10 - tw, ty = r.Y + (r.Height - th) / 2;
        p.Texto.Desenhar(dc, estado, new Vector2(tx - 8 - te.X, r.Y + (r.Height - te.Y) / 2), 12, Cor.Secundario, FontWeight.Normal);
        Icone.Retangulo(dc, tx, ty, tw, th, th / 2, ligado ? Cor.Coral : new Color4(1f, 1f, 1f, 0.22f));
        Icone.Circulo(dc, new Vector2(ligado ? tx + tw - th / 2 : tx + th / 2, ty + th / 2), th / 2 - 3, Cor.Branco);
    }

    private static void Chip(Palco p, ID2D1DeviceContext dc, RectangleF r, string texto, Color4 fundo, Color4 cor, bool seta)
    {
        Icone.Retangulo(dc, r.X, r.Y, r.Width, r.Height, r.Height / 2, fundo);
        float folga = seta ? 22 : 12;
        var tam = p.Texto.Medir(texto, 13);
        float l = MathF.Min(tam.X, r.Width - folga - 10);
        float x = seta ? r.X + 12 : r.X + (r.Width - l) / 2;
        p.Texto.Desenhar(dc, texto, new Vector2(x, r.Y + (r.Height - tam.Y) / 2), 13, cor, largura: r.Width - folga - 10);
        if (seta)
        {
            float sx = r.Right - 16, sy = r.Y + r.Height / 2 - 2;
            Icone.Triangulo(dc, p.G.Fabrica, new Vector2(sx, sy), new Vector2(sx + 8, sy), new Vector2(sx + 4, sy + 5), Cor.Secundario);
        }
    }

    private static void BotaoDeDecisao(Palco p, ID2D1DeviceContext dc, RectangleF r, string texto, Color4 cor)
    {
        Icone.Retangulo(dc, r.X, r.Y, r.Width, r.Height, 10, new Color4(cor.R, cor.G, cor.B, 0.28f));
        var tam = p.Texto.Medir(texto, 14);
        p.Texto.Desenhar(dc, texto, new Vector2(r.X + (r.Width - tam.X) / 2, r.Y + (r.Height - tam.Y) / 2), 14, Cor.Branco);
    }

    private string Subtitulo(Sessao s)
    {
        string quando(double t) => DateTimeOffset.FromUnixTimeMilliseconds((long)(t * 1000)).ToLocalTime().ToString("HH:mm", Lang.Culture);
        return s.Fase switch
        {
            Fase.Terminou => s.Duracao > 0 ? Lang.T($"Levou {Neutro.Duracao(s.Duracao)}", $"Took {Neutro.Duracao(s.Duracao)}") : Lang.T("Pronto", "Done"),
            Fase.Parada => s.Duracao > 0 ? Lang.T($"Parou depois de {Neutro.Duracao(s.Duracao)} (Esc ou erro)", $"Stopped after {Neutro.Duracao(s.Duracao)} (Esc or error)") : Lang.T("Parou (Esc ou erro)", "Stopped (Esc or error)"),
            Fase.Esperando => Lang.T("Esperando desde ", "Waiting since ") + quando(s.FaseDesde),
            _ => s.Inicio > 0 ? Lang.T("Trabalhando desde ", "Working since ") + quando(s.Inicio) : Lang.T("Trabalhando", "Working"),
        };
    }

    private List<string> Modelos()
    {
        var l = new List<string>(_cfg().Modelos.Where(m => !string.IsNullOrWhiteSpace(m)));
        foreach (var o in _opcoesDoApp) if (!l.Contains(o, StringComparer.OrdinalIgnoreCase)) l.Add(o);
        return l.Take(9).ToList();
    }

    // ------------------------------------------------------------------ ações

    private void Avisar(string texto, bool erro)
    {
        _aviso = texto;
        _avisoErro = erro;
        _tirarAviso.Stop();
        _tirarAviso.Start();
        Atualizar();
    }

    private string? Alvo(Sessao s)
    {
        if (s.Nome.Length > 0) return s.Nome;
        Avisar(Lang.T("Não sei o nome dessa sessão no app (o arquivo dela em .claude\\sessions não apareceu).", "I don't know this session's name in the app (its file in .claude\\sessions didn't show up)."), true);
        return null;
    }

    private void LerDoAppSePreciso(Sessao s)
    {
        if (_lendo || s.Nome.Length == 0) return;
        if (_lidos.TryGetValue(s.Nome, out var li) && _agora() - li.Em < 20) return;
        _lendo = true;
        _ponte.Estado(r => S.NaInterface(() =>
        {
            _lendo = false;
            if (r.Ok) foreach (var pl in r.Paineis) if (pl.Nome != null) _lidos[pl.Nome] = (pl, _agora());
            if (!r.Ok && r.Erro != null) Avisar(r.Erro, true);
            else Redesenhar();
        }));
    }

    private void AbrirLista(Modo m)
    {
        var s = Mostrada;
        if (s == null || Alvo(s) == null) return;
        _modo = m;
        Atualizar();
    }

    private void EscolherModelo(string modelo)
    {
        var s = Mostrada;
        _modo = Modo.Normal;
        if (s == null || Alvo(s) is not { } alvo) { Atualizar(); return; }
        Avisar(Lang.T($"Trocando para {modelo}…", $"Switching to {modelo}…"), false);
        _ponte.TrocarModelo(alvo, modelo, r => S.NaInterface(() =>
        {
            foreach (var o in r.Opcoes) if (!_opcoesDoApp.Contains(o, StringComparer.OrdinalIgnoreCase)) _opcoesDoApp.Add(o);
            if (r.Painel?.Nome != null) _lidos[r.Painel.Nome] = (r.Painel, _agora());
            Avisar(r.Ok ? Lang.T("Modelo: ", "Model: ") + (r.Painel?.Modelo ?? modelo) : r.Erro ?? Lang.T("Não deu para trocar o modelo.", "Couldn't switch the model."), !r.Ok);
        }));
    }

    private void EscolherEsforco(int nivel)
    {
        var s = Mostrada;
        _modo = Modo.Normal;
        if (s == null || Alvo(s) is not { } alvo) { Atualizar(); return; }
        Avisar(Lang.T($"Trocando o esforço para {BotaoDoModelo.Rotulos[nivel]}…", $"Switching the effort to {BotaoDoModelo.Rotulos[nivel]}…"), false);
        _ponte.TrocarEsforco(alvo, nivel, r => S.NaInterface(() =>
        {
            if (r.Painel?.Nome != null) _lidos[r.Painel.Nome] = (r.Painel, _agora());
            string feito = (r.Painel != null ? BotaoDoModelo.Rotulo(r.Painel.Nivel, r.Painel.Ultracode, r.Painel.Esforco) : null) ?? BotaoDoModelo.Rotulos[nivel];
            Avisar(r.Ok ? Lang.T("Esforço: ", "Effort: ") + feito : r.Erro ?? Lang.T("Não deu para trocar o esforço.", "Couldn't switch the effort."), !r.Ok);
        }));
    }

    /// <summary>Flips Ultracode from the state last read (unknown counts as off); the base level stays.</summary>
    private void AlternarUltracode()
    {
        var s = Mostrada;
        _modo = Modo.Normal;
        if (s == null || Alvo(s) is not { } alvo) { Atualizar(); return; }
        bool ligar = !(_lidos.TryGetValue(alvo, out var li) && li.Painel.Ultracode);
        Avisar(ligar ? Lang.T("Ligando o Ultracode…", "Turning Ultracode on…") : Lang.T("Desligando o Ultracode…", "Turning Ultracode off…"), false);
        _ponte.DefinirUltracode(alvo, ligar, r => S.NaInterface(() =>
        {
            if (r.Painel?.Nome != null) _lidos[r.Painel.Nome] = (r.Painel, _agora());
            string feito = (r.Painel?.Ultracode ?? ligar) ? Lang.T("Ultracode ligado", "Ultracode on") : Lang.T("Ultracode desligado", "Ultracode off");
            Avisar(r.Ok ? feito : r.Erro ?? Lang.T("Não deu para trocar o Ultracode.", "Couldn't switch Ultracode."), !r.Ok);
        }));
    }

    private void AbrirMensagem()
    {
        var s = Mostrada;
        if (s == null || Alvo(s) is not { } alvo) return;
        string id = s.Id;
        _escolhida = id;
        S.Ilha.Expandir(this);
        _modo = Modo.Mensagem;
        Atualizar();
        S.Entrada.Abrir(_rascunho, t => _rascunho = t, texto =>
        {
            texto = texto.Trim();
            if (texto.Length == 0) return;
            _rascunho = texto;
            _modo = Modo.Normal;
            S.Entrada.Fechar();
            Avisar(Lang.T("Enviando…", "Sending…"), false);
            _ponte.Enviar(alvo, texto, r => S.NaInterface(() =>
            {
                if (r.Ok) _rascunho = "";
                Avisar(r.Ok ? Lang.T("Mensagem enviada", "Message sent") : (r.Erro ?? Lang.T("Não deu para enviar.", "Couldn't send.")) + Lang.T(" O texto ficou guardado.", " The text was kept."), !r.Ok);
            }));
        }, fechou: () =>
        {
            if (_modo == Modo.Mensagem) { _modo = Modo.Normal; Atualizar(); }
        });
    }

    /// <summary>Permitir ou negar pelo cartão ou pelo alerta.</summary>
    public Action<PedidoPermissao, string> Decidir = (_, _) => { };
}
