using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Windows.UI.Composition;

namespace Ilha.Gravando;

/// <summary>
/// Gravando a tela: bolinha vermelha pulsando (no compositor) e o tempo. O OBS vem primeiro (gravando de verdade,
/// com pausar e parar); sem ele, o app que está capturando a tela pelo Windows, mostrado como "capturando a tela"
/// (pode ser compartilhamento). Pela entrada do menu o cartão abre mesmo sem nada gravando, com o estado do OBS e o
/// campo da senha. Não anuncia chegada: o som da chegada entraria na própria gravação.
/// </summary>
internal sealed class AtividadeGravando : Atividade
{
    private const float LC = 196, L = 320;
    private const float FIM_ROTULO = 60, RESERVA_TEMPO = 66;
    private static readonly Color4 FundoBotao = new(1f, 1f, 1f, 0.12f);

    private readonly Servicos S;
    private readonly Func<string, bool> _pedir;
    private readonly Func<string, string?> _guardarSenha;
    private readonly System.Windows.Forms.Timer _segundo = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _confirmar = new() { Interval = 3000 };
    private readonly System.Windows.Forms.Timer _limparAviso = new() { Interval = 6000 };

    private EstadoObs _obs = EstadoObs.Fechado;
    private GravacaoObs _gravacao = GravacaoObs.Parada;
    private List<CapturaEmUso> _capturas = new();
    private bool _obsLigado = true, _capturaLigada = true, _parado;
    private bool _confirmando, _digitando;
    private string? _aviso, _avisoEntrada;
    private long _desenhado = -1;
    private string _layout = "";

    public override string Tipo => "gravando";
    public override string Nome => Lang.T("Gravação", "Recording");
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;
    public override (float De, float Ate)? MeioLivreCompacta => (FIM_ROTULO + 6, LC - RESERVA_TEMPO);

    public AtividadeGravando(Servicos s, Func<string, bool> pedir, Func<string, string?> guardarSenha)
    {
        S = s;
        _pedir = pedir;
        _guardarSenha = guardarSenha;
        // só anda com uma cena na tela e o tempo correndo, e só redesenha quando o segundo vira
        _segundo.Tick += (_, _) =>
        {
            if (!TemCenaViva || !Correndo) { _segundo.Stop(); return; }
            if (Segundos() != _desenhado) Redesenhar();
        };
        _confirmar.Tick += (_, _) => { _confirmar.Stop(); _confirmando = false; AtualizarTela(); };
        _limparAviso.Tick += (_, _) => { _limparAviso.Stop(); _aviso = null; AtualizarTela(); };
    }

    private bool Aberta => S.Ilha.Expandida == this;
    private bool ObsNaFrente => _obsLigado && _gravacao.Ativa;
    private bool CapturaNaFrente => !ObsNaFrente && _capturaLigada && _capturas.Count > 0;
    private bool Gravando => ObsNaFrente || CapturaNaFrente;
    private bool Correndo => ObsNaFrente ? _gravacao.Correndo : CapturaNaFrente;

    // ------------------------------------------------------------------ estado

    public void Atualizar(EstadoObs obs, GravacaoObs gravacao, List<CapturaEmUso> capturas, bool obsLigado, bool capturaLigada)
    {
        if (_parado) return;
        if (gravacao.Ativa != _gravacao.Ativa || !gravacao.Ativa) _confirmando = false;
        _obs = obs;
        _gravacao = gravacao;
        _capturas = capturas;
        _obsLigado = obsLigado;
        _capturaLigada = capturaLigada;
        if (Gravando) Ligar(); else Desligar();
        S.Indicadores.Gravacao("gravando", Gravando, ObsNaFrente ? "OBS" : string.Join(", ", _capturas.Select(c => c.Nome)));
        if (Correndo && TemCenaViva && !_segundo.Enabled) _segundo.Start();
        AtualizarTela();
    }

    /// <summary>Um pedido ao OBS foi recusado ou a conexão não deixou mandar.</summary>
    public void Avisar(string texto)
    {
        if (_parado) return;
        _aviso = texto;
        _limparAviso.Stop();
        _limparAviso.Start();
        AtualizarTela();
    }

    public void Abrir()
    {
        if (!_parado) S.Ilha.Expandir(this);
    }

    public void Parar()
    {
        _parado = true;
        _segundo.Stop();
        _confirmar.Stop();
        _limparAviso.Stop();
        if (_digitando && S.Entrada.Aberta) S.Entrada.Fechar(devolverFoco: false);
        _digitando = false;
        if (Aberta) S.Ilha.Recolher();
        S.Indicadores.Gravacao("gravando", false, "");
        Desligar();
    }

    public override void ExpandidaMudou(bool aberta)
    {
        if (aberta) return;
        if (_digitando && S.Entrada.Aberta) S.Entrada.Fechar(devolverFoco: false);
        _digitando = false;
        if (_confirmando) { _confirmando = false; _confirmar.Stop(); AtualizarTela(); }
    }

    /// <summary>A linha pequena da entrada no menu.</summary>
    public string? DetalheDoMenu()
    {
        if (_parado) return null;
        if (ObsNaFrente) return _gravacao.Pausada ? Lang.T("pausado", "paused") : Lang.T("gravando", "recording");
        if (CapturaNaFrente) return Lang.T("capturando", "capturing");
        if (!_obsLigado) return null;
        return _obs switch
        {
            EstadoObs.PedeSenha => Lang.T("falta a senha", "password missing"),
            EstadoObs.SenhaRecusada => Lang.T("senha recusada", "password rejected"),
            EstadoObs.Conectado => Lang.T("OBS conectado", "OBS connected"),
            _ => null,
        };
    }

    /// <summary>Para o ícone do menu: a bolinha fica vermelha gravando.</summary>
    public bool AcesoNoMenu => Gravando && !(ObsNaFrente && _gravacao.Pausada);

    private string? _detalheMostrado;
    public event Action? MenuMudou;

    private string ChaveLayout()
    {
        if (_digitando) return "senha:" + (_avisoEntrada != null);
        string modo = ObsNaFrente ? (_gravacao.Pausada ? "obs-p" : "obs") : CapturaNaFrente ? "cap" : "parado:" + _obs + ":" + _obsLigado;
        return $"{modo}|{Extras().Count}|{_confirmando}|{BotaoSenha}|{AnunciaSenha}";
    }

    private void AtualizarTela()
    {
        string d = DetalheDoMenu() + "|" + AcesoNoMenu;
        if (d != _detalheMostrado) { _detalheMostrado = d; MenuMudou?.Invoke(); }
        string k = ChaveLayout();
        if (k != _layout) { _layout = k; MudouTamanho(); }
        else Redesenhar();
    }

    // ------------------------------------------------------------------ textos

    public static string Cronometro(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        long s = (long)t.TotalSeconds;
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    private TimeSpan Decorrido()
    {
        var agora = DateTime.UtcNow;
        if (ObsNaFrente) return _gravacao.Decorrido(agora);
        if (CapturaNaFrente) return agora - _capturas[0].InicioUtc;
        return TimeSpan.Zero;
    }

    private long Segundos() => (long)Decorrido().TotalSeconds;

    private string Tempo()
    {
        var t = Decorrido();
        _desenhado = (long)t.TotalSeconds;
        return Cronometro(t);
    }

    /// <summary>"Discord" ou "Discord e mais 2".</summary>
    public static string Apps(IReadOnlyList<CapturaEmUso> c) =>
        c.Count == 0 ? "" : c.Count == 1 ? c[0].Nome : Lang.T($"{c[0].Nome} e mais {c.Count - 1}", $"{c[0].Nome} and {c.Count - 1} more");

    public static string TextoObs(EstadoObs e, int porta) => e switch
    {
        EstadoObs.Desligado => Lang.T("OBS desligado na configuração", "OBS turned off in settings"),
        EstadoObs.Fechado => Lang.T("OBS fechado", "OBS closed"),
        EstadoObs.Conectando => Lang.T("Conectando ao OBS…", "Connecting to OBS…"),
        EstadoObs.SemServidor => Lang.T("OBS aberto, sem o servidor WebSocket", "OBS open, WebSocket server off"),
        EstadoObs.PedeSenha => Lang.T("O OBS pede a senha do WebSocket", "OBS asks for the WebSocket password"),
        EstadoObs.SenhaRecusada => Lang.T("O OBS recusou a senha", "OBS rejected the password"),
        EstadoObs.Conectado => Lang.T("OBS conectado, sem gravar", "OBS connected, not recording"),
        EstadoObs.Caiu => Lang.T("A conexão com o OBS caiu", "The connection to OBS dropped"),
        _ => "",
    };

    public static string DicaObs(EstadoObs e, int porta) => e switch
    {
        EstadoObs.SemServidor => Lang.T($"No OBS: Ferramentas › Configurações do servidor WebSocket (porta {porta})", $"In OBS: Tools › WebSocket Server Settings (port {porta})"),
        EstadoObs.PedeSenha => Lang.T("A senha fica na mesma tela do OBS (Mostrar informações de conexão)", "The password is on the same OBS screen (Show Connect Info)"),
        EstadoObs.SenhaRecusada => Lang.T("Confira a senha em Configurações do servidor WebSocket", "Check the password in WebSocket Server Settings"),
        EstadoObs.Fechado => Lang.T("Abra o OBS: a ilha conecta sozinha", "Open OBS: the island connects on its own"),
        _ => "",
    };

    public int Porta { get; set; } = 4455;

    private bool BotaoSenha => _obsLigado && !ObsNaFrente && !CapturaNaFrente;
    private bool AnunciaSenha => _obsLigado && CapturaNaFrente && _obs is EstadoObs.PedeSenha or EstadoObs.SenhaRecusada;

    /// <summary>As linhas pequenas embaixo do tempo (cada uma: texto e se é aviso vermelho).</summary>
    private List<(string Texto, bool Vermelho)> Extras()
    {
        var l = new List<(string, bool)>();
        if (_confirmando) l.Add((Lang.T("Clique de novo no quadrado para parar a gravação", "Click the square again to stop recording"), true));
        if (_aviso != null) l.Add((_aviso, true));
        if (ObsNaFrente)
        {
            if (_capturaLigada && _capturas.Count > 0) l.Add((Lang.T("Também capturando a tela: ", "Also capturing the screen: ") + string.Join(", ", _capturas.Select(c => c.Nome)), false));
        }
        else if (CapturaNaFrente)
        {
            l.Add((Lang.T("Visto pela captura de tela do Windows:", "Seen through Windows screen capture:"), false));
            l.Add((Lang.T("pode ser compartilhamento, não gravação.", "it may be sharing, not recording."), false));
            if (_capturas.Count > 1) l.Add((Lang.T("Também: ", "Also: ") + string.Join(", ", _capturas.Skip(1).Select(c => c.Nome)), false));
            if (AnunciaSenha) l.Add((TextoObs(_obs, Porta), false));
        }
        return l;
    }

    // ------------------------------------------------------------------ ações

    private void PausarOuRetomar()
    {
        if (!ObsNaFrente) return;
        if (!_pedir(_gravacao.Pausada ? "ResumeRecord" : "PauseRecord")) Avisar(Lang.T("Sem conexão com o OBS", "No connection to OBS"));
    }

    private void PararGravacao()
    {
        if (!ObsNaFrente) return;
        if (!_confirmando)
        {
            _confirmando = true;
            _confirmar.Stop();
            _confirmar.Start();
            AtualizarTela();
            return;
        }
        _confirmando = false;
        _confirmar.Stop();
        if (!_pedir("StopRecord")) Avisar(Lang.T("Sem conexão com o OBS", "No connection to OBS"));
        AtualizarTela();
    }

    private void AbrirEntrada()
    {
        // aberto pelo clique, o cartão recolheria quando o mouse saísse: fica preso como pelo atalho enquanto digita
        S.Ilha.Expandir(this);
        _digitando = true;
        _avisoEntrada = null;
        S.Entrada.Abrir("", _ => Redesenhar(), Confirmar, fechou: () => { _digitando = false; AtualizarTela(); }, senha: true);
        AtualizarTela();
    }

    private void Confirmar(string texto)
    {
        string? erro = _guardarSenha(texto);
        if (erro != null) { _avisoEntrada = erro; AtualizarTela(); return; }
        S.Entrada.Fechar();
    }

    // ------------------------------------------------------------------ cenas

    /// <summary>A bolinha num visual próprio: pulsa pela opacidade no compositor, sem redesenho do app.</summary>
    private static void Ponto(Palco p, Cena c, Vector2 centro, float raio, Color4 cor, bool pulsar)
    {
        float lado = raio * 2 + 2;
        var s = p.G.NovaSuperficie(new Vector2(lado, lado), (dc, t) => Icone.Circulo(dc, t / 2, raio, cor));
        var v = p.C.CreateSpriteVisual();
        v.Size = s.TamanhoPx;
        v.Offset = new Vector3(p.Px(centro.X - lado / 2), p.Px(centro.Y - lado / 2), 0);
        v.Brush = s.Pincel;
        c.Raiz.Children.InsertAtTop(v);
        c.Superficies.Add(s);
        if (!pulsar) return;
        var suave = p.C.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.55f, 1f));
        var a = p.C.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0f, 1f);
        a.InsertKeyFrame(0.5f, 0.28f, suave);
        a.InsertKeyFrame(1f, 1f, suave);
        a.Duration = TimeSpan.FromSeconds(1.5);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        v.StartAnimation("Opacity", a);
    }

    private Color4 CorDoPonto => ObsNaFrente && _gravacao.Pausada ? Cor.Laranja : Cor.Vermelho;

    /// <summary>Monitorzinho (captura de tela pelo Windows), desenhado em formas.</summary>
    private static void Tela(ID2D1DeviceContext dc, float x, float y, Color4 cor)
    {
        Icone.Retangulo(dc, x, y, 17, 11.5f, 2.2f, cor);
        Icone.Retangulo(dc, x + 1.8f, y + 1.8f, 13.4f, 7.9f, 1f, new Color4(0f, 0f, 0f, 1f));
        Icone.Retangulo(dc, x + 7, y + 11.5f, 3, 2, 0, cor);
        Icone.Retangulo(dc, x + 4.5f, y + 13, 8, 1.6f, 0.8f, cor);
    }

    private static void Pausa(ID2D1DeviceContext dc, Vector2 c, Color4 cor)
    {
        Icone.Retangulo(dc, c.X - 5.5f, c.Y - 7, 4, 14, 1.2f, cor);
        Icone.Retangulo(dc, c.X + 1.5f, c.Y - 7, 4, 14, 1.2f, cor);
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        if (Correndo) _segundo.Start();
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(24, 24));
                Ponto(p, c, new Vector2(12, 12), 5, CorDoPonto, Correndo);
                return c;
            }
            case Forma.Compacta:
            {
                var c = p.NovaCena(new Vector2(LC, 24));
                Ponto(p, c, new Vector2(6, 12), 5, CorDoPonto, Correndo);
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    if (ObsNaFrente)
                    {
                        string r = _gravacao.Pausada ? Lang.T("PAUSA", "PAUSE") : "REC";
                        var m = p.Texto.Medir(r, 12, FontWeight.Bold);
                        p.Texto.Desenhar(dc, r, new Vector2(17, (t.Y - m.Y) / 2), 12, CorDoPonto, FontWeight.Bold, largura: FIM_ROTULO - 17);
                    }
                    else Tela(dc, 17, 4.5f, Cor.Secundario);
                    string txt = Tempo();
                    var tam = p.Texto.Medir(txt, 15, FontWeight.SemiBold, tabular: true);
                    var cor = ObsNaFrente && _gravacao.Pausada ? Cor.Secundario : Cor.Branco;
                    p.Texto.Desenhar(dc, txt, new Vector2(t.X - tam.X, (t.Y - tam.Y) / 2), 15, cor, FontWeight.SemiBold, tabular: true);
                });
                return c;
            }
            default:
                if (_digitando) return CenaSenha(p);
                return Gravando ? CenaGravando(p) : CenaParado(p);
        }
    }

    private Cena CenaGravando(Palco p)
    {
        var extras = Extras();
        float y0 = 76;
        var c = p.NovaCena(new Vector2(L, y0 + extras.Count * 17 + (AnunciaSenha ? 38 : 0) - 2));
        Ponto(p, c, new Vector2(8, 47), 6.5f, CorDoPonto, Correndo);
        var pausar = new RectangleF(L - 92, 24, 40, 40);
        var parar = new RectangleF(L - 44, 24, 40, 40);
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var ex = Extras();
            string titulo = ObsNaFrente ? (_gravacao.Pausada ? Lang.T("Gravação pausada · OBS", "Recording paused · OBS") : Lang.T("Gravando · OBS", "Recording · OBS"))
                : Lang.T("Capturando a tela · ", "Capturing the screen · ") + Apps(_capturas);
            float larguraTempo = ObsNaFrente ? pausar.X - 26 : t.X - 22;
            p.Texto.Desenhar(dc, titulo, new Vector2(0, 0), 13, Cor.Secundario, largura: t.X);
            p.Texto.Desenhar(dc, Tempo(), new Vector2(20, 20), 40, ObsNaFrente && _gravacao.Pausada ? Cor.Secundario : Cor.Branco,
                FontWeight.Medium, tabular: true, largura: larguraTempo);
            if (ObsNaFrente)
            {
                var cp = new Vector2(pausar.X + 20, pausar.Y + 20);
                Icone.Circulo(dc, cp, 20, FundoBotao);
                if (_gravacao.Pausada)
                    Icone.Triangulo(dc, p.G.Fabrica, cp + new Vector2(-5, -8), cp + new Vector2(9, 0), cp + new Vector2(-5, 8), Cor.Branco);
                else Pausa(dc, cp, Cor.Branco);
                var cs = new Vector2(parar.X + 20, parar.Y + 20);
                var v = Cor.Vermelho;
                Icone.Circulo(dc, cs, 20, _confirmando ? v : new Color4(v.R, v.G, v.B, 0.22f));
                Icone.Retangulo(dc, cs.X - 6.5f, cs.Y - 6.5f, 13, 13, 2.5f, _confirmando ? Cor.Branco : v);
            }
            float y = y0;
            foreach (var (texto, vermelho) in ex)
            {
                p.Texto.Desenhar(dc, texto, new Vector2(0, y), 12, vermelho ? Cor.Vermelho : Cor.Secundario, FontWeight.Normal, largura: t.X);
                y += 17;
            }
            if (AnunciaSenha) BotaoPilula(dc, p, new RectangleF(0, y + 4, 130, 30), Lang.T("Senha do OBS", "OBS password"));
        });
        if (ObsNaFrente)
        {
            c.Botoes.Add(new Botao { Area = Inflar(pausar, 3), Acao = PausarOuRetomar });
            c.Botoes.Add(new Botao { Area = Inflar(parar, 3), Acao = PararGravacao });
        }
        if (AnunciaSenha) c.Botoes.Add(new Botao { Area = new RectangleF(0, y0 + extras.Count * 17 + 4, 130, 30), Acao = AbrirEntrada });
        return c;
    }

    private Cena CenaParado(Palco p)
    {
        bool senha = BotaoSenha;
        bool temAviso = _aviso != null;
        float alturaBase = senha ? 122 : 84;
        var c = p.NovaCena(new Vector2(L, alturaBase + (temAviso ? 18 : 0)));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            p.Texto.Desenhar(dc, Lang.T("Gravação", "Recording"), new Vector2(0, 0), 13, Cor.Secundario, largura: t.X);
            var estado = _obsLigado ? _obs : EstadoObs.Desligado;
            var corEstado = estado is EstadoObs.PedeSenha or EstadoObs.SenhaRecusada ? Cor.Laranja : Cor.Branco;
            p.Texto.Desenhar(dc, TextoObs(estado, Porta), new Vector2(0, 21), 15, corEstado, largura: t.X);
            string dica = DicaObs(estado, Porta);
            if (dica.Length > 0) p.Texto.Desenhar(dc, dica, new Vector2(0, 43), 11.5f, Cor.Secundario, FontWeight.Normal, largura: t.X);
            string cap = !_capturaLigada ? Lang.T("Captura de tela do Windows: desligada na configuração", "Windows screen capture: turned off in settings")
                : Lang.T("Captura de tela do Windows: nada agora", "Windows screen capture: nothing right now");
            p.Texto.Desenhar(dc, cap, new Vector2(0, 63), 12, Cor.Secundario, FontWeight.Normal, largura: t.X);
            float y = 84;
            if (senha) { BotaoPilula(dc, p, new RectangleF(0, 90, 130, 30), Lang.T("Senha do OBS", "OBS password")); y = 124; }
            if (_aviso != null) p.Texto.Desenhar(dc, _aviso, new Vector2(0, y), 12, Cor.Vermelho, FontWeight.Normal, largura: t.X);
        });
        if (senha) c.Botoes.Add(new Botao { Area = new RectangleF(0, 90, 130, 30), Acao = AbrirEntrada });
        return c;
    }

    private Cena CenaSenha(Palco p)
    {
        var c = p.NovaCena(new Vector2(L, _avisoEntrada != null ? 104 : 86));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            p.Texto.Desenhar(dc, Lang.T("Senha do servidor WebSocket do OBS", "OBS WebSocket server password"), new Vector2(0, 0), 13, Cor.Secundario, largura: t.X);
            Icone.Retangulo(dc, 0, 22, t.X, 36, 10, new Color4(1f, 1f, 1f, 0.09f));
            p.Texto.DesenharCampo(dc, S.Entrada.Texto, S.Entrada.Cursor, true, new Vector2(12, 30), t.X - 24, 15, Cor.Branco,
                vazio: Lang.T("a senha que o OBS mostra", "the password OBS shows"), comCursor: S.Entrada.Aberta);
            p.Texto.Desenhar(dc, Lang.T("Enter guarda no Gerenciador de Credenciais. Vazio apaga.", "Enter saves it in Credential Manager. Empty deletes it."), new Vector2(0, 66), 11.5f, Cor.Secundario, FontWeight.Normal, largura: t.X);
            if (_avisoEntrada != null) p.Texto.Desenhar(dc, _avisoEntrada, new Vector2(0, 86), 12, Cor.Vermelho, FontWeight.Normal, largura: t.X);
        });
        return c;
    }

    private static void BotaoPilula(ID2D1DeviceContext dc, Palco p, RectangleF r, string texto)
    {
        Icone.Retangulo(dc, r.X, r.Y, r.Width, r.Height, r.Height / 2, FundoBotao);
        // chave desenhada: argola e haste
        var c = new Vector2(r.X + 17, r.Y + r.Height / 2);
        Icone.Circulo(dc, c, 4.5f, Cor.Branco);
        Icone.Circulo(dc, c, 2f, new Color4(0.12f, 0.12f, 0.12f, 1f));
        Icone.Retangulo(dc, c.X + 3.5f, c.Y - 1.1f, 9, 2.2f, 1f, Cor.Branco);
        Icone.Retangulo(dc, c.X + 9.5f, c.Y, 2, 3.6f, 0.8f, Cor.Branco);
        var m = p.Texto.Medir(texto, 13);
        p.Texto.Desenhar(dc, texto, new Vector2(r.X + 34, r.Y + (r.Height - m.Y) / 2), 13, Cor.Branco, largura: r.Width - 38);
    }

    private static RectangleF Inflar(RectangleF r, float d) => new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

    public override void Dispose()
    {
        _segundo.Dispose();
        _confirmar.Dispose();
        _limparAviso.Dispose();
    }
}
