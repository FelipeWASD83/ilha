using System;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.IA;

/// <summary>
/// O tradutor: o atalho pega o texto selecionado no app da frente (UI Automation; se não der, Ctrl+C devolvendo
/// a área de transferência como estava) e mostra original e tradução na ilha. Para o português, ou para o inglês
/// se já estava em português. Até 1.200 caracteres vai pelo Google (a tela avisa que passa por lá); acima,
/// pela IA no par barato e sem raciocínio. Enter copia a tradução; digitar outro texto e Enter traduz ele.
/// Nunca lê nada com jogo competitivo na frente.
/// </summary>
internal sealed class AtividadeTradutor : Atividade
{
    public override string Tipo => "tradutor";
    public override string Nome => "Tradutor";
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    private const float L = 380, A = 250;
    private const float TRAD_Y = 104, TRAD_A = 92;

    private enum Fase { Lendo, Vazio, Traduzindo, Pronto, Erro }

    private readonly Servicos S;
    private readonly Tradutor _tradutor;
    private readonly Agendador _redesenho;
    private Paragrafo? _par;
    private Superficie? _supTexto, _supCampo;

    private Fase _fase = Fase.Vazio;
    private string _original = "";
    private readonly StringBuilder _traducao = new();
    private Tradutor.Resultado? _resultado;
    private string _erro = "";
    private bool _viaIA, _copiado;
    private float _rolagem, _alturaTrad;
    private CancellationTokenSource? _cts;
    private int _geracao;
    private bool _entradaNossa, _trocandoEntrada;

    public AtividadeTradutor(Servicos s, Tradutor tradutor)
    {
        S = s;
        _tradutor = tradutor;
        _redesenho = new Agendador(() => _supTexto?.Redesenhar());
    }

    public async void Atalho(int teclaDoAtalho)
    {
        try { await Abrir(teclaDoAtalho); }
        catch (Exception e) { Log.Escrever("tradutor: " + Segredo.Limpar(e.ToString())); }
    }

    private async Task Abrir(int teclaDoAtalho)
    {
        if (S.ContextoAtual().Competitivo) return;
        if (S.Ilha.Expandida == this) { S.Ilha.Recolher(); return; }
        int g = ++_geracao;
        _cts?.Cancel();
        IntPtr alvo = Nativo.GetForegroundWindow();
        Nativo.GetWindowThreadProcessId(alvo, out uint pid);
        bool nossa = pid == (uint)Environment.ProcessId;

        _fase = Fase.Lendo;
        _original = "";
        _traducao.Clear();
        _resultado = null;
        _erro = "";
        _copiado = false;
        _rolagem = 0;
        MudouTamanho();
        // a ilha não ativa a janela dela: o app da frente continua com o foco para a leitura da seleção
        S.Ilha.Expandir(this);

        string? texto = null;
        if (!nossa)
        {
            texto = await Selecao.PorUiAutomation();
            if (g != _geracao) return;
            if (texto == null && !Selecao.EhTerminal(alvo) && await Selecao.EsperarSoltar(teclaDoAtalho) && g == _geracao
                && Nativo.GetForegroundWindow() == alvo && !S.ContextoAtual().Competitivo)
                texto = await Selecao.PorCtrlC();
            if (g != _geracao) return;
        }
        if (S.Ilha.Expandida != this) return;
        AbrirCampo();
        if (string.IsNullOrWhiteSpace(texto)) { _fase = Fase.Vazio; Redesenhar(); return; }
        _ = Traduzir(texto);
    }

    private async Task Traduzir(string texto)
    {
        int g = ++_geracao;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _original = texto.Trim();
        _traducao.Clear();
        _resultado = null;
        _erro = "";
        _copiado = false;
        _rolagem = 0;
        _viaIA = Tradutor.Decidir(Tradutor.Cortar(_original, _tradutor.LimiteIA).Texto, _tradutor.LimiteGoogle, _tradutor.TemChaveIA) == "ia";
        _fase = Fase.Traduzindo;
        Redesenhar();
        try
        {
            var r = await _tradutor.Traduzir(_original, t => S.NaInterface(() =>
            {
                if (g != _geracao) return;
                if (S.Ilha.Expandida != this) { _cts?.Cancel(); return; }
                _traducao.Append(t);
                _redesenho.Pedir();
            }), ct);
            if (g != _geracao) return;
            _resultado = r;
            _viaIA = r.Motor == "ia";
            _traducao.Clear();
            _traducao.Append(r.Texto);
            _fase = Fase.Pronto;
        }
        catch (OperationCanceledException) { if (g == _geracao) { _fase = Fase.Erro; _erro = "Interrompida."; } }
        catch (ErroIA e) { if (g == _geracao) { _fase = Fase.Erro; _erro = e.Message; } }
        catch (Exception e)
        {
            if (g == _geracao) { _fase = Fase.Erro; _erro = "Deu um erro inesperado (" + e.GetType().Name + ")."; }
            Log.Escrever("tradutor: " + Segredo.Limpar(e.ToString()));
        }
        if (g == _geracao) Redesenhar();
    }

    public void Interromper(bool fecharCampo)
    {
        _geracao++;
        _cts?.Cancel();
        if (fecharCampo && _entradaNossa && S.Entrada.Aberta)
        {
            _trocandoEntrada = true;
            try { S.Entrada.Fechar(); }
            finally { _trocandoEntrada = false; }
        }
        _entradaNossa = false;
    }

    private void AbrirCampo()
    {
        if (_entradaNossa && S.Entrada.Aberta) return;
        _entradaNossa = true;
        S.Entrada.Abrir("",
            mudou: _ => _supCampo?.Redesenhar(),
            enter: Enter,
            fechou: () =>
            {
                if (_trocandoEntrada) return;
                _entradaNossa = false;
                _geracao++;
                _cts?.Cancel();
                if (S.Ilha.Expandida == this) S.Ilha.Recolher();
            });
    }

    private async void Enter(string texto)
    {
        if (texto.Trim().Length > 0)
        {
            S.Entrada.Definir("");
            _ = Traduzir(texto);
            return;
        }
        if (_fase != Fase.Pronto || _traducao.Length == 0) return;
        try { Clipboard.SetDataObject(_traducao.ToString(), true, 5, 20); }
        catch (Exception e) { _erro = "Não consegui copiar (" + e.GetType().Name + ")."; Redesenhar(); return; }
        _copiado = true;
        Redesenhar();
        await Task.Delay(450);
        // fecha e devolve o foco para o app, pronto para colar
        if (_entradaNossa && S.Entrada.Aberta && S.Ilha.Expandida == this) S.Entrada.Fechar();
    }

    public override bool Rolar(int delta)
    {
        if (S.Ilha.Expandida != this) return false;
        _rolagem = Math.Clamp(_rolagem - delta / 120f * 30f, 0, MathF.Max(0, _alturaTrad - TRAD_A));
        _supTexto?.Redesenhar();
        return true;
    }

    // ------------------------------------------------------------------ cena

    protected override Cena Montar(Palco p, Forma f)
    {
        _par ??= new Paragrafo(p.G.Escrita);
        if (f != Forma.Expandida && f != Forma.Alerta)
        {
            var pc = p.NovaCena(f == Forma.Bolha ? new Vector2(22, 22) : new Vector2(120, 24));
            p.Desenho(pc, Vector2.Zero, pc.Tamanho, (dc, t) =>
            {
                var tam = p.Texto.Medir("Tradutor", 13);
                p.Texto.Desenhar(dc, f == Forma.Bolha ? "Aa" : "Tradutor", new Vector2(f == Forma.Bolha ? 1 : t.X - tam.X, (t.Y - tam.Y) / 2), 13, Cor.Azul);
            });
            return pc;
        }
        var c = p.NovaCena(new Vector2(L, A));
        var texto = p.Desenho(c, Vector2.Zero, new Vector2(L, 212), (dc, t) => DesenharTexto(dc, p, t));
        var campo = p.Desenho(c, new Vector2(0, 216), new Vector2(L, 34), (dc, t) =>
        {
            Icone.Retangulo(dc, 0, 0, t.X, t.Y, 13, new Color4(1, 1, 1, 0.10f));
            bool aberto = _entradaNossa && S.Entrada.Aberta;
            string vazio = _copiado ? "Copiada." : _fase == Fase.Pronto ? "Enter copia a tradução. Ou digite outro texto." : "Digite ou cole um texto e aperte Enter.";
            p.Texto.DesenharCampo(dc, aberto ? S.Entrada.Texto : "", aberto ? S.Entrada.Cursor : 0, false, new Vector2(12, 8), t.X - 24, 13,
                _copiado ? Cor.Verde : Cor.Branco, vazio, aberto);
        });
        c.Botoes.Add(new Botao { Area = new(0, 216, L, 34), Acao = () => { if (!S.Entrada.Aberta) AbrirCampo(); } });
        _supTexto = texto;
        _supCampo = campo;
        c.AoSoltar += () =>
        {
            if (_supTexto == texto) _supTexto = null;
            if (_supCampo == campo) _supCampo = null;
        };
        return c;
    }

    private void DesenharTexto(ID2D1DeviceContext dc, Palco p, Vector2 t)
    {
        var par = _par!;
        p.Texto.Desenhar(dc, "Tradutor", new Vector2(0, 0), 13, Cor.Secundario);
        string motor = _fase is Fase.Vazio or Fase.Lendo ? "" : _viaIA
            ? "IA · " + Modelos.Rotulo(_resultado?.Modelo ?? _tradutor.ModeloIA)
            : "Google · o texto passa pelo Google";
        if (motor.Length > 0)
        {
            var tm = p.Texto.Medir(motor, 11, FontWeight.Normal);
            p.Texto.Desenhar(dc, motor, new Vector2(t.X - MathF.Min(tm.X, 250), 2), 11, Cor.Secundario, FontWeight.Normal, largura: 250);
        }

        // original (apagado, até 3 linhas)
        string original = _fase switch
        {
            Fase.Lendo => "Lendo o texto selecionado…",
            Fase.Vazio => "Nada selecionado (ou o app não deixou ler). Digite ou cole o texto embaixo.",
            _ => _original,
        };
        dc.PushAxisAlignedClip(new System.Drawing.RectangleF(0, 22, t.X, 58), AntialiasMode.Aliased);
        using (var lo = par.Layout(original, 12, t.X)) Paragrafo.Desenhar(dc, lo, new Vector2(0, 22), Cor.Secundario);
        dc.PopAxisAlignedClip();

        if (_fase is Fase.Lendo or Fase.Vazio) return;
        string idiomas = "";
        if (_resultado is { Motor: "google" } rg)
            idiomas = $"{Tradutor.NomeDoIdioma(rg.Origem)} → {Tradutor.NomeDoIdioma(rg.Destino)}";
        else if (_fase == Fase.Pronto) idiomas = "para português (ou inglês, se já era português)";
        Icone.Retangulo(dc, 0, 86, t.X, 1, 0.5f, Cor.Fraco);
        if (idiomas.Length > 0) p.Texto.Desenhar(dc, idiomas, new Vector2(0, 88), 11, Cor.Secundario, FontWeight.Normal, largura: t.X);

        string trad = _fase == Fase.Erro && _traducao.Length == 0 ? _erro
            : _traducao.Length == 0 ? "Traduzindo…" : _traducao.ToString();
        var cor = _fase == Fase.Erro && _traducao.Length == 0 ? Cor.Vermelho : _traducao.Length == 0 ? Cor.Secundario : Cor.Branco;
        using (var lt = par.Layout(trad, 15, t.X - 6))
        {
            _alturaTrad = MathF.Ceiling(lt.Metrics.Height);
            _rolagem = Math.Clamp(_rolagem, 0, MathF.Max(0, _alturaTrad - TRAD_A));
            dc.PushAxisAlignedClip(new System.Drawing.RectangleF(0, TRAD_Y, t.X, TRAD_A), AntialiasMode.Aliased);
            Paragrafo.Desenhar(dc, lt, new Vector2(0, TRAD_Y - _rolagem), cor);
            dc.PopAxisAlignedClip();
            if (_alturaTrad > TRAD_A)
            {
                float h = MathF.Max(10, TRAD_A * TRAD_A / _alturaTrad);
                float y = TRAD_Y + (TRAD_A - h) * (_rolagem / MathF.Max(1, _alturaTrad - TRAD_A));
                Icone.Retangulo(dc, t.X - 3, y, 3, h, 1.5f, Cor.Fraco);
            }
        }
        string aviso = _resultado?.Aviso ?? "";
        if (_fase == Fase.Erro && _traducao.Length > 0) aviso = _erro;
        if (_resultado?.Custo is double cu) aviso = (aviso.Length > 0 ? aviso + " · " : "") + Modelos.Dinheiro(cu);
        if (aviso.Length > 0) p.Texto.Desenhar(dc, aviso, new Vector2(0, 198), 11, Cor.Laranja, FontWeight.Normal, largura: t.X);
    }

    public override void Dispose()
    {
        _cts?.Cancel();
        _redesenho.Dispose();
    }
}
