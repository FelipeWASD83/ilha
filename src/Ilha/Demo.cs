using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Windows.UI.Composition;

namespace Ilha;

/// <summary>Atividade de mentira da fase 1: liga e desliga pelo menu da bandeja, para testar os estados.</summary>
internal abstract class DeMentira : Atividade
{
    public void Alternar()
    {
        if (Ativa) Parar(); else Comecar();
    }

    public abstract void Comecar();
    public abstract void Parar();
}

internal sealed class TimerDeMentira : DeMentira
{
    public override string Tipo => "timer";
    public override string Nome => Lang.T("Timer de 1 minuto", "1-minute timer");
    public override bool PermitidaNoCompetitivo => true;

    private readonly System.Windows.Forms.Timer _tique = new() { Interval = 250 };
    private DateTime _fim;
    private TimeSpan _pausadoCom;
    private bool _pausado;
    private int _ultimoSegundo = -1;
    private const int DURACAO = 60;

    public TimerDeMentira()
    {
        _tique.Tick += (_, _) =>
        {
            var r = Restante;
            int s = (int)Math.Ceiling(r.TotalSeconds);
            if (s != _ultimoSegundo) { _ultimoSegundo = s; Redesenhar(); }
            if (r <= TimeSpan.Zero) Acabou();
        };
    }

    private TimeSpan Restante => _pausado ? _pausadoCom : (_fim - DateTime.Now) < TimeSpan.Zero ? TimeSpan.Zero : _fim - DateTime.Now;
    private float Progresso => 1f - (float)(Restante.TotalSeconds / DURACAO);
    private string Texto { get { var r = Restante; int s = (int)Math.Ceiling(r.TotalSeconds); return $"{s / 60}:{s % 60:00}"; } }

    public override void Comecar()
    {
        _fim = DateTime.Now.AddSeconds(DURACAO);
        _pausado = false;
        _tique.Start();
        Ligar();
    }

    public override void Parar()
    {
        _tique.Stop();
        Desligar();
    }

    private void Acabou()
    {
        Parar();
        Alertar(new Alerta
        {
            Tipo = "timer",
            Importante = true,
            PermitidoNoCompetitivo = true,
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(300, 52));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    Icone.Anel(dc, p.G.Fabrica, new Vector2(26, 26), 20, 4, 1, Cor.Laranja);
                    p.Texto.Desenhar(dc, Lang.T("Timer acabou", "Timer done"), new Vector2(60, 4), 18, Cor.Branco);
                    p.Texto.Desenhar(dc, Lang.T("1 minuto, de mentira", "1 minute, fake"), new Vector2(60, 28), 13, Cor.Secundario, FontWeight.Normal);
                });
                return c;
            },
        });
    }

    private void PausarOuContinuar()
    {
        if (_pausado) { _fim = DateTime.Now + _pausadoCom; _pausado = false; }
        else { _pausadoCom = Restante; _pausado = true; }
        Redesenhar();
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(24, 24));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) => Icone.Anel(dc, p.G.Fabrica, new Vector2(12, 12), 9, 3, Progresso, Cor.Laranja));
                return c;
            }
            case Forma.Compacta:
            {
                var c = p.NovaCena(new Vector2(150, 24));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    Icone.Anel(dc, p.G.Fabrica, new Vector2(12, 12), 9, 3, Progresso, Cor.Laranja);
                    var tam = p.Texto.Medir(Texto, 15, FontWeight.SemiBold, tabular: true);
                    p.Texto.Desenhar(dc, Texto, new Vector2(t.X - tam.X, (t.Y - tam.Y) / 2), 15, Cor.Laranja, FontWeight.SemiBold, tabular: true);
                });
                return c;
            }
            default:
            {
                var c = p.NovaCena(new Vector2(320, 92));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    p.Texto.Desenhar(dc, _pausado ? Lang.T("Timer pausado", "Timer paused") : "Timer", new Vector2(0, 2), 13, Cor.Secundario);
                    p.Texto.Desenhar(dc, Texto, new Vector2(-2, 22), 48, Cor.Laranja, FontWeight.Medium, tabular: true);
                    // pausar/continuar e encerrar
                    Icone.Circulo(dc, new Vector2(t.X - 82, 50), 21, new Color4(1f, 0.62f, 0.04f, 0.22f));
                    if (_pausado) Icone.Triangulo(dc, p.G.Fabrica, new Vector2(t.X - 87, 41), new Vector2(t.X - 74, 50), new Vector2(t.X - 87, 59), Cor.Laranja);
                    else
                    {
                        Icone.Retangulo(dc, t.X - 88, 41, 4.5f, 18, 1.5f, Cor.Laranja);
                        Icone.Retangulo(dc, t.X - 80.5f, 41, 4.5f, 18, 1.5f, Cor.Laranja);
                    }
                    Icone.Circulo(dc, new Vector2(t.X - 24, 50), 21, Cor.Fraco);
                    Icone.Linha(dc, new Vector2(t.X - 31, 43), new Vector2(t.X - 17, 57), 3, Cor.Branco, p.G.Fabrica);
                    Icone.Linha(dc, new Vector2(t.X - 17, 43), new Vector2(t.X - 31, 57), 3, Cor.Branco, p.G.Fabrica);
                });
                c.Botoes.Add(new Botao { Area = new(320 - 103, 29, 42, 42), Acao = PausarOuContinuar });
                c.Botoes.Add(new Botao { Area = new(320 - 45, 29, 42, 42), Acao = Parar });
                return c;
            }
        }
    }
}

internal sealed class MidiaDeMentira : DeMentira
{
    public override string Tipo => "midia";
    public override string Nome => Lang.T("Música tocando", "Music playing");

    private bool _tocando = true;
    private static readonly Color4 CorDaCapa = new(0.93f, 0.42f, 0.36f, 1f);

    public override void Comecar() { _tocando = true; Ligar(); }
    public override void Parar() => Desligar();

    private static void Capa(ID2D1DeviceContext dc, float x, float y, float l, float raio)
    {
        Icone.Retangulo(dc, x, y, l, l, raio, new Color4(0.36f, 0.16f, 0.44f, 1f));
        Icone.Circulo(dc, new Vector2(x + l * 0.68f, y + l * 0.36f), l * 0.36f, CorDaCapa);
        Icone.Circulo(dc, new Vector2(x + l * 0.3f, y + l * 0.72f), l * 0.22f, new Color4(1f, 0.78f, 0.3f, 1f));
    }

    /// <summary>Barras que sobem e descem sozinhas no compositor (o visualizador de verdade vem na fase 2).</summary>
    private void Visualizador(Palco p, Cena c, Vector2 pos, int barras, float altura)
    {
        float[] periodos = { 0.62f, 0.45f, 0.71f, 0.53f, 0.66f };
        for (int i = 0; i < barras; i++)
        {
            var v = p.Bloco(c, new Vector2(pos.X + i * 5.5f, pos.Y), new Vector2(3, altura), CorDaCapa);
            v.CenterPoint = new Vector3(v.Size.X / 2, v.Size.Y / 2, 0);
            if (!_tocando) { v.Scale = new Vector3(1, 0.2f, 1); continue; }
            var a = p.C.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(0f, 0.25f);
            a.InsertKeyFrame(0.5f, 1f);
            a.InsertKeyFrame(1f, 0.25f);
            a.Duration = TimeSpan.FromSeconds(periodos[i % periodos.Length]);
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            v.StartAnimation("Scale.Y", a);
        }
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(22, 22));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) => Capa(dc, 0, 0, 22, 6));
                return c;
            }
            case Forma.Compacta:
            {
                var c = p.NovaCena(new Vector2(170, 24));
                p.Desenho(c, Vector2.Zero, new Vector2(24, 24), (dc, t) => Capa(dc, 1, 1, 22, 6));
                Visualizador(p, c, new Vector2(170 - 4 * 5.5f, 4), 4, 16);
                return c;
            }
            default:
            {
                var c = p.NovaCena(new Vector2(340, 126));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    Capa(dc, 0, 0, 62, 14);
                    p.Texto.Desenhar(dc, Lang.T("Música de mentira", "Fake song"), new Vector2(76, 8), 16, Cor.Branco, largura: 200);
                    p.Texto.Desenhar(dc, Lang.T("Ilha · demonstração", "Ilha · demo"), new Vector2(76, 32), 13, Cor.Secundario, FontWeight.Normal, largura: 200);
                    // barra de progresso parada em 35%
                    Icone.Retangulo(dc, 0, 82, t.X, 4, 2, Cor.Fraco);
                    Icone.Retangulo(dc, 0, 82, t.X * 0.35f, 4, 2, Cor.Branco);
                    p.Texto.Desenhar(dc, "1:12", new Vector2(0, 90), 11, Cor.Secundario, FontWeight.Normal, tabular: true);
                    var fim = p.Texto.Medir("-2:14", 11, FontWeight.Normal, true);
                    p.Texto.Desenhar(dc, "-2:14", new Vector2(t.X - fim.X, 90), 11, Cor.Secundario, FontWeight.Normal, tabular: true);
                    // anterior, tocar/pausar, próxima
                    float cy = 112, mx = t.X / 2;
                    var F = p.G.Fabrica;
                    Icone.Triangulo(dc, F, new Vector2(mx - 52, cy), new Vector2(mx - 42, cy - 7), new Vector2(mx - 42, cy + 7), Cor.Branco);
                    Icone.Triangulo(dc, F, new Vector2(mx - 62, cy), new Vector2(mx - 52, cy - 7), new Vector2(mx - 52, cy + 7), Cor.Branco);
                    if (_tocando)
                    {
                        Icone.Retangulo(dc, mx - 7, cy - 9, 5, 18, 1.5f, Cor.Branco);
                        Icone.Retangulo(dc, mx + 2, cy - 9, 5, 18, 1.5f, Cor.Branco);
                    }
                    else Icone.Triangulo(dc, F, new Vector2(mx - 6, cy - 10), new Vector2(mx + 9, cy), new Vector2(mx - 6, cy + 10), Cor.Branco);
                    Icone.Triangulo(dc, F, new Vector2(mx + 52, cy), new Vector2(mx + 42, cy - 7), new Vector2(mx + 42, cy + 7), Cor.Branco);
                    Icone.Triangulo(dc, F, new Vector2(mx + 62, cy), new Vector2(mx + 52, cy - 7), new Vector2(mx + 52, cy + 7), Cor.Branco);
                });
                Visualizador(p, c, new Vector2(340 - 4 * 5.5f, 12), 4, 18);
                c.Botoes.Add(new Botao { Area = new(170 - 18, 96, 36, 32), Acao = () => { _tocando = !_tocando; MudouTamanho(); } });
                return c;
            }
        }
    }
}

/// <summary>
/// Atividade com uma logo animada: guarda a animação do momento e manda todas as cenas vivas (pílula, bolha,
/// cartão) trocarem juntas; cena que nasce depois já entra direto no loop atual.
/// </summary>
internal abstract class ComLogo : DeMentira
{
    private readonly List<LogoAnimada> _logos = new();
    private string[] _atual = System.Array.Empty<string>();

    protected LogoAnimada Logo(Palco p, Cena c, Vector2 pos, float lado)
    {
        var l = p.Logo(c, pos, lado);
        _logos.Add(l);
        c.AoSoltar += () => _logos.Remove(l);
        if (_atual.Length > 0) l.Tocar(_atual[^1]);
        return l;
    }

    protected void Animar(params string[] seq)
    {
        _atual = seq;
        foreach (var l in _logos.ToArray()) l.Tocar(seq);
    }
}

internal sealed class ClaudeDeMentira : ComLogo
{
    public override string Tipo => "claude";
    public override string Nome => Lang.T("Claude Code trabalhando", "Claude Code working");

    private static readonly (string texto, string en, int ms, bool pensa)[] Passos =
    {
        ("Lendo Ilha.cs", "Reading Ilha.cs", 2500, false), ("Pensando", "Thinking", 5000, true), ("Editando Ilha.cs", "Editing Ilha.cs", 2500, false),
        ("Rodando dotnet build", "Running dotnet build", 2500, false), ("Pensando", "Thinking", 5000, true), ("Editando Demo.cs", "Editing Demo.cs", 2500, false),
        ("Rodando os testes", "Running the tests", 2500, false),
    };
    // uma animação de bolinhas diferente a cada vez que ele pensa
    private static readonly string[] Bolinhas = { "orbita", "cometa", "onda", "divide", "anel" };
    private readonly System.Windows.Forms.Timer _passo = new();
    private int _i, _vezes;
    private bool _terminou;
    private string? _bolinha;

    public ClaudeDeMentira()
    {
        _passo.Tick += (_, _) =>
        {
            _i++;
            if (_i >= Passos.Length) { Terminar(); return; }
            Entrar(Passos[_i]);
            Redesenhar();
        };
    }

    private void Entrar((string texto, string en, int ms, bool pensa) passo)
    {
        _passo.Interval = passo.ms;
        if (passo.pensa)
        {
            _bolinha = "claude-bola-" + Bolinhas[_vezes++ % Bolinhas.Length];
            Animar("claude-bola", "claude-bola-cresce", _bolinha + "-entrada", _bolinha);
        }
        else if (_bolinha != null)
        {
            Animar(_bolinha + "-saida", "claude-bola-encolhe", "claude-surge", "claude-loop");
            _bolinha = null;
        }
    }

    private void Terminar()
    {
        _terminou = true;
        _passo.Stop();
        Animar("claude-saida", "claude-pulso");
        Redesenhar();
        Alertar(new Alerta
        {
            Tipo = "claude",
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(300, 52));
                p.Logo(c, new Vector2(8, 10), 32).Tocar("claude-volta");
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    p.Texto.Desenhar(dc, Lang.T("Claude Code terminou", "Claude Code finished"), new Vector2(56, 4), 17, Cor.Branco);
                    p.Texto.Desenhar(dc, Lang.T("ilha · 7 passos, de mentira", "ilha · 7 steps, fake"), new Vector2(56, 28), 13, Cor.Secundario, FontWeight.Normal);
                });
                return c;
            },
        });
    }

    private string Agora => _terminou ? Lang.T("Esperando você", "Waiting for you") : Lang.T(Passos[_i % Passos.Length].texto, Passos[_i % Passos.Length].en);

    public override void Comecar()
    {
        _i = 0;
        _terminou = false;
        _bolinha = null;
        Animar("claude-entrada", "claude-loop");
        Entrar(Passos[0]);
        _passo.Start();
        Ligar();
    }

    public override void Parar() { _passo.Stop(); Desligar(); }

    protected override Cena Montar(Palco p, Forma f)
    {
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(24, 24));
                Logo(p, c, Vector2.Zero, 24);
                return c;
            }
            case Forma.Compacta:
            {
                var c = p.NovaCena(new Vector2(210, 24));
                Logo(p, c, Vector2.Zero, 24);
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    var tam = p.Texto.Medir(Agora, 13);
                    float l = MathF.Min(tam.X, 170);
                    p.Texto.Desenhar(dc, Agora, new Vector2(t.X - l, (t.Y - tam.Y) / 2), 13, _terminou ? Cor.Secundario : Cor.Branco, largura: 170);
                });
                return c;
            }
            default:
            {
                var c = p.NovaCena(new Vector2(340, 80));
                Logo(p, c, new Vector2(-4, -5), 30);
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    p.Texto.Desenhar(dc, "Claude Code · ilha", new Vector2(32, 1), 13, Cor.Secundario);
                    p.Texto.Desenhar(dc, Agora, new Vector2(0, 28), 20, Cor.Branco, largura: t.X);
                    p.Texto.Desenhar(dc, Lang.T("Opus 5.5 · Alto, de mentira", "Opus 5.5 · High, fake"), new Vector2(0, 60), 12, Cor.Secundario, FontWeight.Normal);
                });
                return c;
            }
        }
    }
}

internal sealed class GptDeMentira : ComLogo
{
    public override string Tipo => "ia";
    public override string Nome => Lang.T("GPT respondendo", "GPT replying");

    private static readonly (string texto, string en, int ms, bool escreve)[] Passos =
    {
        ("Pensando", "Thinking", 4500, false), ("Escrevendo a resposta", "Writing the reply", 4000, true),
        ("Pensando", "Thinking", 4500, false), ("Escrevendo a resposta", "Writing the reply", 3500, true),
    };
    private static readonly string[] Bolinhas = { "cometa", "orbita", "divide", "onda", "anel" };
    private readonly System.Windows.Forms.Timer _passo = new();
    private int _i, _vezes;
    private bool _terminou;
    private string? _bolinha;

    public GptDeMentira()
    {
        _passo.Tick += (_, _) =>
        {
            _i++;
            if (_i >= Passos.Length) { Terminar(); return; }
            Entrar(Passos[_i]);
            Redesenhar();
        };
    }

    private void Entrar((string texto, string en, int ms, bool escreve) passo)
    {
        _passo.Interval = passo.ms;
        if (!passo.escreve)
        {
            _bolinha = "chatgpt-bola-" + Bolinhas[_vezes++ % Bolinhas.Length];
            Animar("chatgpt-bola", "chatgpt-bola-cresce", _bolinha + "-entrada", _bolinha);
        }
        else if (_bolinha != null)
        {
            Animar(_bolinha + "-saida", "chatgpt-bola-encolhe", "chatgpt-surge", "chatgpt-loop");
            _bolinha = null;
        }
    }

    private void Terminar()
    {
        _terminou = true;
        _passo.Stop();
        Animar("chatgpt-saida");
        Redesenhar();
        Alertar(new Alerta
        {
            Tipo = "ia",
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(300, 52));
                p.Logo(c, new Vector2(8, 10), 32).Tocar("chatgpt-volta");
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    p.Texto.Desenhar(dc, Lang.T("O GPT respondeu", "GPT replied"), new Vector2(56, 4), 17, Cor.Branco);
                    p.Texto.Desenhar(dc, Lang.T("Chat da ilha, de mentira", "Ilha chat, fake"), new Vector2(56, 28), 13, Cor.Secundario, FontWeight.Normal);
                });
                return c;
            },
        });
    }

    private string Agora => _terminou ? Lang.T("Resposta pronta", "Reply ready") : Lang.T(Passos[_i % Passos.Length].texto, Passos[_i % Passos.Length].en);

    public override void Comecar()
    {
        _i = 0;
        _terminou = false;
        _bolinha = null;
        Animar("chatgpt-entrada");
        Entrar(Passos[0]);
        _passo.Start();
        Ligar();
    }

    public override void Parar() { _passo.Stop(); Desligar(); }

    protected override Cena Montar(Palco p, Forma f)
    {
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(22, 22));
                Logo(p, c, Vector2.Zero, 22);
                return c;
            }
            case Forma.Compacta:
            {
                var c = p.NovaCena(new Vector2(210, 24));
                Logo(p, c, new Vector2(1, 1), 22);
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    var tam = p.Texto.Medir(Agora, 13);
                    float l = MathF.Min(tam.X, 170);
                    p.Texto.Desenhar(dc, Agora, new Vector2(t.X - l, (t.Y - tam.Y) / 2), 13, _terminou ? Cor.Secundario : Cor.Branco, largura: 170);
                });
                return c;
            }
            default:
            {
                var c = p.NovaCena(new Vector2(340, 80));
                Logo(p, c, new Vector2(0, 0), 24);
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    p.Texto.Desenhar(dc, Lang.T("Chat · GPT-6 Luna, de mentira", "Chat · GPT-6 Luna, fake"), new Vector2(32, 2), 13, Cor.Secundario);
                    p.Texto.Desenhar(dc, Agora, new Vector2(0, 28), 20, Cor.Branco, largura: t.X);
                    p.Texto.Desenhar(dc, Lang.T("Na fase 5 isto vira o chat de verdade", "In phase 5 this becomes the real chat"), new Vector2(0, 60), 12, Cor.Secundario, FontWeight.Normal);
                });
                return c;
            }
        }
    }
}

/// <summary>Todas as animações das logos e das bolinhas num cartão, repetindo, para ver de uma vez.</summary>
internal sealed class GaleriaDeMentira : DeMentira
{
    public override string Tipo => "galeria";
    public override string Nome => Lang.T("Galeria de animações", "Animation gallery");

    private static readonly (string titulo, string en, string[] nomes)[] Linhas =
    {
        ("Claude", "Claude", new[] { "claude-loop", "claude-volta", "claude-saida", "claude-entrada", "claude-pulso", "claude-some", "claude-surge", "claude-bola" }),
        ("Bolinhas do Claude", "Claude dots", new[] { "claude-bola-cresce", "claude-bola-orbita", "claude-bola-cometa", "claude-bola-onda", "claude-bola-divide", "claude-bola-anel", "claude-bola-encolhe", "claude-bola-orbita-entrada" }),
        ("GPT", "GPT", new[] { "chatgpt-loop", "chatgpt-volta", "chatgpt-saida", "chatgpt-entrada", "chatgpt-pulso", "chatgpt-some", "chatgpt-surge", "chatgpt-bola" }),
        ("Bolinhas do GPT", "GPT dots", new[] { "chatgpt-bola-cresce", "chatgpt-bola-orbita", "chatgpt-bola-cometa", "chatgpt-bola-onda", "chatgpt-bola-divide", "chatgpt-bola-anel", "chatgpt-bola-encolhe", "chatgpt-bola-orbita-entrada" }),
    };

    public override void Comecar() => Ligar();
    public override void Parar() => Desligar();

    private static string Rotulo(string nome) => nome.Replace("claude-bola-", "").Replace("chatgpt-bola-", "").Replace("claude-", "").Replace("chatgpt-", "");

    protected override Cena Montar(Palco p, Forma f)
    {
        if (f != Forma.Expandida && f != Forma.Alerta)
        {
            var c = p.NovaCena(f == Forma.Bolha ? new Vector2(22, 22) : new Vector2(190, 24));
            p.Logo(c, Vector2.Zero, f == Forma.Bolha ? 22 : 24).Tocar("claude-bola-cometa");
            if (f == Forma.Compacta)
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    string titulo = Lang.T("Galeria de animações", "Animation gallery");
                    var tam = p.Texto.Medir(titulo, 13);
                    p.Texto.Desenhar(dc, titulo, new Vector2(t.X - tam.X, (t.Y - tam.Y) / 2), 13, Cor.Branco);
                });
            return c;
        }
        const float CEL = 44, LOGO = 38, LINHA = 74;
        var cena = p.NovaCena(new Vector2(CEL * 8, LINHA * Linhas.Length));
        for (int r = 0; r < Linhas.Length; r++)
            for (int k = 0; k < Linhas[r].nomes.Length; k++)
                p.Logo(cena, new Vector2(k * CEL + (CEL - LOGO) / 2, r * LINHA + 18), LOGO).Tocar(true, Linhas[r].nomes[k]);
        p.Desenho(cena, Vector2.Zero, cena.Tamanho, (dc, t) =>
        {
            for (int r = 0; r < Linhas.Length; r++)
            {
                p.Texto.Desenhar(dc, Lang.T(Linhas[r].titulo, Linhas[r].en), new Vector2(2, r * LINHA), 12, Cor.Secundario);
                for (int k = 0; k < Linhas[r].nomes.Length; k++)
                {
                    string rot = Rotulo(Linhas[r].nomes[k]);
                    var tam = p.Texto.Medir(rot, 9.5f, FontWeight.Normal);
                    p.Texto.Desenhar(dc, rot, new Vector2(k * CEL + (CEL - MathF.Min(tam.X, CEL)) / 2, r * LINHA + 58), 9.5f, Cor.Secundario, FontWeight.Normal, largura: CEL);
                }
            }
        });
        return cena;
    }
}

internal sealed class UsoDeMentira : DeMentira
{
    public override string Tipo => "uso";
    public override string Nome => Lang.T("Uso do PC alto", "High PC usage");

    private readonly System.Windows.Forms.Timer _tique = new() { Interval = 1000 };
    private readonly List<float> _hist = new();
    private int _t;

    public UsoDeMentira()
    {
        _tique.Tick += (_, _) =>
        {
            _t++;
            _hist.Add(0.55f + 0.35f * MathF.Sin(_t * 0.7f) * MathF.Cos(_t * 0.23f));
            if (_hist.Count > 12) _hist.RemoveAt(0);
            Redesenhar();
        };
    }

    private int Cpu => _hist.Count == 0 ? 0 : (int)(_hist[^1] * 100);

    public override void Comecar() { _tique.Start(); Ligar(); }
    public override void Parar() { _tique.Stop(); Desligar(); }

    private void Grafico(ID2D1DeviceContext dc, float x, float y, float l, float a)
    {
        float passo = l / 12f;
        for (int i = 0; i < _hist.Count; i++)
        {
            float h = MathF.Max(2, _hist[i] * a);
            Icone.Retangulo(dc, x + i * passo, y + a - h, passo - 1.5f, h, 1, _hist[i] > 0.8f ? Cor.Vermelho : Cor.Verde);
        }
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(22, 22));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) => Grafico(dc, 0, 3, 22, 16));
                return c;
            }
            case Forma.Compacta:
            {
                var c = p.NovaCena(new Vector2(150, 24));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    p.Texto.Desenhar(dc, $"CPU {Cpu}%", new Vector2(0, 3), 13, Cor.Branco, tabular: true);
                    Grafico(dc, t.X - 52, 4, 52, 16);
                });
                return c;
            }
            default:
            {
                var c = p.NovaCena(new Vector2(320, 84));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    p.Texto.Desenhar(dc, Lang.T("Uso do PC, de mentira", "PC usage, fake"), new Vector2(0, 0), 13, Cor.Secundario);
                    p.Texto.Desenhar(dc, $"CPU {Cpu}%", new Vector2(0, 22), 26, Cor.Branco, tabular: true);
                    p.Texto.Desenhar(dc, "GPU 41% · 62 °C · VRAM 5,1 GB", new Vector2(0, 60), 12, Cor.Secundario, FontWeight.Normal);
                    Grafico(dc, t.X - 140, 26, 140, 50);
                });
                return c;
            }
        }
    }
}

internal sealed class Demonstracao
{
    private readonly Ilha _ilha;
    public List<DeMentira> Atividades { get; } = new() { new TimerDeMentira(), new MidiaDeMentira(), new ClaudeDeMentira(), new UsoDeMentira(), new GptDeMentira(), new GaleriaDeMentira() };
    private readonly System.Windows.Forms.Timer _roteiro = new();
    private Queue<(int ms, Action passo)> _passos = new();

    public event Action<string>? Passo;

    public Demonstracao(Ilha ilha)
    {
        _ilha = ilha;
        foreach (var a in Atividades) ilha.Adicionar(a);
        _roteiro.Tick += (_, _) => ProximoPasso();
    }

    public void Alternar(DeMentira a) => a.Alternar();

    public void DesligarTodas()
    {
        foreach (var a in Atividades) if (a.Ativa) a.Parar();
        _ilha.Recolher();
    }

    public void AlertaDeTeste() => _ilha.Alertar(new Alerta
    {
        Tipo = "alerta",
        Criar = p =>
        {
            var c = p.NovaCena(new Vector2(300, 52));
            p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
            {
                Icone.Retangulo(dc, 6, 8, 36, 36, 10, Cor.Fraco);
                Icone.Circulo(dc, new Vector2(18, 26), 6, Cor.Branco);
                Icone.Circulo(dc, new Vector2(30, 26), 6, Cor.Branco);
                p.Texto.Desenhar(dc, Lang.T("AirPods Pro conectados", "AirPods Pro connected"), new Vector2(56, 4), 17, Cor.Branco);
                p.Texto.Desenhar(dc, Lang.T("Alerta de teste", "Test alert"), new Vector2(56, 28), 13, Cor.Secundario, FontWeight.Normal);
                Icone.Anel(dc, p.G.Fabrica, new Vector2(t.X - 20, 26), 14, 3.5f, 0.8f, Cor.Verde);
            });
            return c;
        },
    });

    /// <summary>Passeia por todos os estados, com pausa entre eles (também usado pela medição).</summary>
    public void Roteiro(Action? fim = null)
    {
        var T = (TimerDeMentira)Atividades[0];
        var M = (MidiaDeMentira)Atividades[1];
        var C = (ClaudeDeMentira)Atividades[2];
        _passos = new Queue<(int, Action)>(new (int, Action)[]
        {
            (100, () => { DesligarTodas(); Passo?.Invoke("limpo"); }),
            (1500, () => { T.Comecar(); Passo?.Invoke("timer chegou (expande e volta)"); }),
            (3800, () => { M.Comecar(); Passo?.Invoke("mídia chegou: dividida em pílula e bolha"); }),
            (3800, () => { _ilha.Atalho(); Passo?.Invoke("atalho: expandida"); }),
            (2500, () => { _ilha.Recolher(); Passo?.Invoke("recolhe para a compacta dividida"); }),
            (1500, () => { AlertaDeTeste(); Passo?.Invoke("alerta"); }),
            (5000, () => { M.Parar(); Passo?.Invoke("mídia saiu: só o timer"); }),
            (1500, () => { C.Comecar(); Passo?.Invoke("Claude chegou: Claude na pílula, timer na bolha"); }),
            (4000, () => { DesligarTodas(); Passo?.Invoke("tudo desligado"); }),
            (1500, () => { Passo?.Invoke("fim"); fim?.Invoke(); }),
        });
        Agendar();
    }

    private void Agendar()
    {
        if (_passos.Count == 0) return;
        _roteiro.Interval = Math.Max(1, _passos.Peek().ms);
        _roteiro.Start();
    }

    private void ProximoPasso()
    {
        _roteiro.Stop();
        if (_passos.Count == 0) return;
        var (_, passo) = _passos.Dequeue();
        passo();
        Agendar();
    }
}