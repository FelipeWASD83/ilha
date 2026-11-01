using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Volume;

/// <summary>
/// O volume na ilha: aparece sozinho por uns segundos quando o volume muda por fora (tecla, roda do headset,
/// AirPods, outro app) e quando um aparelho conecta ou desconecta. Rodinha em cima muda o volume; clique abre o
/// cartão com a barra arrastável, o mudo e duas abas: os apps tocando som (volume e mudo de cada um) e as saídas.
/// </summary>
internal sealed class AtividadeVolume : Atividade
{
    public override string Tipo => "volume";
    public override string Nome => "Volume";
    public override bool AnunciaChegada => false;

    private enum Aba { Apps, Saidas }

    private readonly Audio _audio;
    private readonly Func<ConfigVolume> _cfg;
    private readonly Func<bool> _expandida;
    private readonly Func<Mixer?> _mixer;
    private readonly IconesDeApps _icones;
    private readonly Exibicao _exib = new();
    private readonly System.Windows.Forms.Timer _prazo = new();
    private int _rodaAcumulada;
    private int _linhas = -1;
    private bool _arrastando;

    // mixer por app (tudo na thread da interface)
    private List<AppDeSom> _apps = new();
    private bool _mixerOk = true;
    private Aba? _abaDaVez, _abaEscolhida;
    private readonly HashSet<string> _mostrados = new();
    private int _rolagem, _rodaLista, _linhasApps = -1;
    private string? _arrasteApp;
    private int _arrasteLinha = -1;
    private bool _arrasteMudo;
    // o que a ilha mandou e o mixer ainda não confirmou (a barra não volta para trás debaixo do dedo)
    private readonly Dictionary<string, float> _volumesLocais = new();
    private readonly Dictionary<string, bool> _mudosLocais = new();

    private const float CARTAO_L = 340, LINHA_A = 32, LISTA_Y = 100, BARRA_X = 48, MAX_LINHAS = 6;
    private const float APP_A = 36, MAX_APPS = 5, ICONE = 22, APP_X = 32, APP_FIM = 296, MUDO_X = 322;
    private static float BarraL => CARTAO_L - BARRA_X - 50;

    public AtividadeVolume(Audio audio, Func<ConfigVolume> cfg, Func<bool> expandida, Func<Mixer?> mixer, IconesDeApps icones)
    {
        _audio = audio;
        _cfg = cfg;
        _expandida = expandida;
        _mixer = mixer;
        _icones = icones;
        _prazo.Tick += (_, _) => Conferir();
        _icones.Chegou += () => { if (TemCenaViva) Redesenhar(); };
    }

    /// <summary>Pelo menu ("Som"): liga a atividade; o módulo abre o cartão em seguida.</summary>
    public void AbrirCartao() => Mostrar();

    // ---------------------------------------------------------------- quando aparece

    /// <summary>O volume mudou: redesenha; se `mostrar`, aparece (ou renova o prazo).</summary>
    public void VolumeMudou(bool mostrar)
    {
        if (mostrar) Mostrar();
        else if (Ativa) Redesenhar();
    }

    private void Mostrar()
    {
        bool mudouModo = _exib.Volume(DateTime.Now, _cfg().Segundos);
        Armar();
        if (mudouModo) MudouTamanho();
        else Redesenhar();
        Ligar();
    }

    public void Aviso(bool conectou, string aparelho)
    {
        // aviso de aparelho não passa por cima do volume que está sendo mexido agora
        if (Ativa && _exib.Atual == Exibicao.Modo.Volume && _exib.Ate > DateTime.Now) return;
        bool mudou = _exib.Aviso(DateTime.Now, conectou, aparelho, Math.Max(3, _cfg().Segundos));
        Armar();
        if (mudou) MudouTamanho();
        Ligar();
    }

    public void ListaMudou()
    {
        if (!Ativa) return;
        int n = Math.Min(Math.Max(_audio.Saidas.Count, 1), (int)MAX_LINHAS);
        // a lista só aparece no cartão: na pílula, mudar o tamanho refaria a entrada dela à toa
        if (_expandida() && AbaAtual() == Aba.Saidas && n != _linhas) { _linhas = n; MudouTamanho(); }
        else Redesenhar();
    }

    /// <summary>O mixer leu a lista de apps de novo (Ok false = não abriu nesta saída).</summary>
    public void AppsMudaram(LeituraDoMixer l)
    {
        bool igual = l.Ok == _mixerOk && l.Apps.SequenceEqual(_apps);
        _apps = l.Apps.ToList();
        _mixerOk = l.Ok;
        bool soltou = LogicaDoMixer.Reconciliar(_volumesLocais, _mudosLocais, _apps, l.Confirmados, l.DeFora, _arrasteApp);
        if (igual && !soltou) return;
        var vis = AppsVisiveis();
        bool aberta = _expandida();
        if (aberta && AbaAtual() == Aba.Apps) foreach (var a in vis) _mostrados.Add(a.Chave);
        _rolagem = LogicaDoMixer.Rolagem(_rolagem, 0, vis.Count, (int)MAX_APPS);
        // a pílula e a bolha não mostram os apps: com o cartão fechado não há o que redesenhar (o cartão que abrir
        // já nasce com a lista nova)
        if (!Ativa || !aberta) return;
        if (AbaAtual() == Aba.Apps && LinhasApps(vis) != _linhasApps) MudouTamanho();
        else Redesenhar();
    }

    /// <summary>Sem o mixer (desligado na config ou modo competitivo) a lista some.</summary>
    public void SemMixer() => AppsMudaram(LeituraDoMixer.Vazia(true));

    public override void ExpandidaMudou(bool aberta)
    {
        if (aberta) return;
        _mostrados.Clear();
        _rolagem = 0;
        _rodaLista = 0;
        _abaDaVez = null;
        // o cartão fechou no meio de um arraste (o soltar não vem mais): nada fica preso
        _arrastando = false;
        FimDoArrasteApp();
    }

    private bool MixerLigado => _cfg().Mixer && _mixer() != null;

    private List<AppDeSom> AppsVisiveis() => LogicaDoMixer.Visiveis(_apps, _cfg().MostrarParados, _mostrados);

    private static int LinhasApps(List<AppDeSom> vis) => Math.Min(Math.Max(vis.Count, 1), (int)MAX_APPS);

    /// <summary>A aba desta abertura do cartão: a que ele escolheu por último; senão os apps, se algum estiver tocando.</summary>
    private Aba AbaAtual()
    {
        if (!MixerLigado) return Aba.Saidas;
        return _abaDaVez ?? _abaEscolhida ?? (AppsVisiveis().Count > 0 ? Aba.Apps : Aba.Saidas);
    }

    private void TrocarAba(Aba a)
    {
        FimDoArrasteApp();
        _abaEscolhida = a;
        if (_abaDaVez == a) return;
        _abaDaVez = a;
        _rolagem = 0;
        MudouTamanho();
    }

    private void Armar()
    {
        _prazo.Stop();
        var ms = (_exib.Ate - DateTime.Now).TotalMilliseconds;
        _prazo.Interval = (int)Math.Clamp(ms, 50, 60_000);
        _prazo.Start();
    }

    private void Conferir()
    {
        _prazo.Stop();
        var antes = _exib.Atual;
        var prox = _exib.Conferir(DateTime.Now, _expandida() || _arrastando);
        if (prox is DateTime p)
        {
            _prazo.Interval = (int)Math.Clamp((p - DateTime.Now).TotalMilliseconds, 50, 60_000);
            _prazo.Start();
            return;
        }
        Desligar();
        // saiu do aviso de aparelho: a próxima aparição volta à cena do volume, não reaproveita a do aviso
        if (antes != Exibicao.Modo.Volume) MudouTamanho();
    }

    public void Esconder()
    {
        _prazo.Stop();
        var antes = _exib.Atual;
        _exib.Desligar();
        Desligar();
        if (antes != Exibicao.Modo.Volume) MudouTamanho();
    }

    // ---------------------------------------------------------------- mexer no volume

    public override bool Rolar(int delta)
    {
        // com o cartão nos apps e mais apps do que cabe, a rodinha rola a lista; senão mexe no volume geral
        if (_expandida() && AbaAtual() == Aba.Apps)
        {
            var vis = AppsVisiveis();
            if (vis.Count > MAX_APPS)
            {
                _rodaLista += delta;
                int d = _rodaLista / 120;
                if (d == 0) return true;
                _rodaLista -= d * 120;
                int rolagem = LogicaDoMixer.Rolagem(_rolagem, d, vis.Count, (int)MAX_APPS);
                if (rolagem != _rolagem) { _rolagem = rolagem; Redesenhar(); }
                return true;
            }
        }
        if (!_audio.TemAparelho) return false;
        _rodaAcumulada += delta;
        int dentes = _rodaAcumulada / 120;
        if (dentes == 0) return true;
        _rodaAcumulada -= dentes * 120;
        var nova = Logica.Rolar(_audio.Escala, dentes * 120, _cfg().PassoRolar);
        // subir com o som mudo tira o mudo, como a tecla de volume do Windows
        if (dentes > 0 && _audio.Mudo) _audio.DefinirMudo(false);
        _audio.DefinirEscala(nova);
        Mostrar();
        return true;
    }

    private void Arrastar(float x)
    {
        _audio.DefinirEscala(Logica.DaBarra(x, BARRA_X, BarraL));
        if (_audio.Mudo && _audio.Escala > 0) _audio.DefinirMudo(false);
        Redesenhar();
    }

    private void AlternarMudo()
    {
        FimDoArrasteApp();
        _audio.DefinirMudo(!_audio.Mudo);
        Mostrar();
    }

    private void Trocar(Saida s)
    {
        if (_audio.TrocarSaida(s.Id)) Mostrar();
        else Redesenhar();
    }

    // ---------------------------------------------------------------- desenho

    private List<Saida> SaidasVisiveis()
    {
        var todas = _audio.Saidas;
        if (todas.Count <= MAX_LINHAS) return todas;
        var atual = todas.FirstOrDefault(x => x.Contem(_audio.IdPadrao));
        var r = todas.Where(x => x != atual).Take((int)MAX_LINHAS - (atual != null ? 1 : 0)).ToList();
        if (atual != null) r.Insert(Math.Min(todas.IndexOf(atual), r.Count), atual);
        return r;
    }

    protected override Cena Montar(Palco p, Forma f)
    {
        switch (f)
        {
            case Forma.Bolha:
            {
                var c = p.NovaCena(new Vector2(24, 24));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                    Icone.Anel(dc, p.G.Fabrica, new Vector2(12, 12), 9, 3, _audio.Mudo ? 0 : _audio.Escala, _audio.Mudo ? Cor.Secundario : Cor.Branco));
                return c;
            }
            case Forma.Compacta when _exib.Atual != Exibicao.Modo.Volume:
            {
                var c = p.NovaCena(new Vector2(240, 24));
                bool conectou = _exib.Atual == Exibicao.Modo.Conectou;
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    Desenhos.Fone(dc, p.G.Fabrica, new Vector2(0, 0), 1f, conectou ? Cor.Verde : Cor.Secundario);
                    string estado = conectou ? "conectado" : "desconectado";
                    var te = p.Texto.Medir(estado, 12, FontWeight.Normal);
                    p.Texto.Desenhar(dc, estado, new Vector2(t.X - te.X, (t.Y - te.Y) / 2), 12, conectou ? Cor.Verde : Cor.Secundario, FontWeight.Normal);
                    var tn = p.Texto.Medir("Ág", 13);
                    p.Texto.Desenhar(dc, _exib.Aparelho, new Vector2(30, (t.Y - tn.Y) / 2), 13, Cor.Branco, largura: MathF.Max(20, t.X - 30 - te.X - 8));
                });
                return c;
            }
            case Forma.Compacta:
            {
                var c = p.NovaCena(new Vector2(196, 24));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
                {
                    bool mudo = _audio.Mudo;
                    Desenhos.AltoFalante(dc, p.G.Fabrica, Vector2.Zero, 1f, Cor.Branco, Logica.Ondas(_audio.Escala, mudo), mudo);
                    string num = _audio.TemAparelho ? Logica.Porcentagem(_audio.Escala).ToString() : "-";
                    var tn = p.Texto.Medir(num, 13, FontWeight.SemiBold, tabular: true);
                    p.Texto.Desenhar(dc, num, new Vector2(t.X - tn.X, (t.Y - tn.Y) / 2), 13, mudo ? Cor.Secundario : Cor.Branco, FontWeight.SemiBold, tabular: true);
                    float x0 = 32, l = t.X - x0 - 34;
                    Icone.Retangulo(dc, x0, 10, l, 4, 2, Cor.Fraco);
                    if (_audio.Escala > 0.005f) Icone.Retangulo(dc, x0, 10, MathF.Max(4, l * _audio.Escala), 4, 2, mudo ? Cor.Secundario : Cor.Branco);
                });
                return c;
            }
            default:
                return Cartao(p);
        }
    }

    private Cena Cartao(Palco p)
    {
        var saidas = SaidasVisiveis();
        var vis = AppsVisiveis();
        bool mixer = MixerLigado;
        if (mixer) _abaDaVez ??= AbaAtual();
        var aba = AbaAtual();
        // as linhas desta abertura não somem se o app pausar com o cartão aberto
        if (aba == Aba.Apps) foreach (var a in vis) _mostrados.Add(a.Chave);
        _linhas = Math.Max(saidas.Count, 1);
        _linhasApps = LinhasApps(vis);
        float altura = aba == Aba.Apps ? LISTA_Y + _linhasApps * APP_A : LISTA_Y + _linhas * LINHA_A;
        var c = p.NovaCena(new Vector2(CARTAO_L, altura));
        var F = p.G.Fabrica;
        float lApps = p.Texto.Medir("Apps", 12, FontWeight.SemiBold).X, lSaidas = p.Texto.Medir("Saídas", 12, FontWeight.SemiBold).X;
        float xSaidas = lApps + 20;
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            bool mudo = _audio.Mudo;
            float e = _audio.Escala;
            p.Texto.Desenhar(dc, "Volume", new Vector2(0, 0), 13, Cor.Secundario);
            string db = !_audio.TemAparelho ? "sem saída" : mudo ? "mudo" : Logica.TextoDb(_audio.Db);
            var td = p.Texto.Medir(db, 12, FontWeight.Normal, tabular: true);
            p.Texto.Desenhar(dc, db, new Vector2(t.X - td.X, 1), 12, Cor.Secundario, FontWeight.Normal, tabular: true);

            // mudo
            Icone.Circulo(dc, new Vector2(18, 42), 17, mudo ? new Color4(1f, 1f, 1f, 0.9f) : Cor.Fraco);
            Desenhos.AltoFalante(dc, F, new Vector2(6, 30), 1f, mudo ? new Color4(0f, 0f, 0f, 1f) : Cor.Branco, Logica.Ondas(e, mudo), mudo);

            // barra
            float l = BarraL;
            Icone.Retangulo(dc, BARRA_X, 39, l, 6, 3, Cor.Fraco);
            float cheio = l * Math.Clamp(e, 0f, 1f);
            if (cheio > 0.5f) Icone.Retangulo(dc, BARRA_X, 39, MathF.Max(6, cheio), 6, 3, mudo ? Cor.Secundario : Cor.Branco);
            Icone.Circulo(dc, new Vector2(BARRA_X + cheio, 42), 8, mudo ? new Color4(0.75f, 0.75f, 0.75f, 1f) : Cor.Branco);
            string num = _audio.TemAparelho ? Logica.Porcentagem(e).ToString() : "-";
            var tn = p.Texto.Medir(num, 18, FontWeight.SemiBold, tabular: true);
            p.Texto.Desenhar(dc, num, new Vector2(t.X - tn.X, 42 - tn.Y / 2), 18, mudo ? Cor.Secundario : Cor.Branco, FontWeight.SemiBold, tabular: true);

            Icone.Retangulo(dc, 0, 70, t.X, 1, 0.5f, Cor.Fraco);
            if (!mixer)
            {
                p.Texto.Desenhar(dc, _audio.PodeTrocar ? "Saídas" : "Saídas · trocar por aqui não está disponível neste Windows", new Vector2(0, 78), 12, Cor.Secundario, largura: t.X);
                DesenharSaidas(dc, p, t, saidas);
                return;
            }
            p.Texto.Desenhar(dc, "Apps", new Vector2(0, 78), 12, aba == Aba.Apps ? Cor.Branco : Cor.Secundario, FontWeight.SemiBold);
            p.Texto.Desenhar(dc, "Saídas", new Vector2(xSaidas, 78), 12, aba == Aba.Saidas ? Cor.Branco : Cor.Secundario, FontWeight.SemiBold);
            Icone.Retangulo(dc, aba == Aba.Apps ? 0 : xSaidas, 95, aba == Aba.Apps ? lApps : lSaidas, 2, 1, Cor.Branco);
            if (aba == Aba.Saidas)
            {
                if (!_audio.PodeTrocar)
                {
                    const string nota = "trocar não disponível neste Windows";
                    var tm = p.Texto.Medir(nota, 11, FontWeight.Normal);
                    p.Texto.Desenhar(dc, nota, new Vector2(t.X - tm.X, 79), 11, Cor.Secundario, FontWeight.Normal);
                }
                DesenharSaidas(dc, p, t, saidas);
            }
            else DesenharApps(dc, p, t);
        });

        c.Botoes.Add(new Botao { Area = new(0, 24, 36, 36), Acao = AlternarMudo });
        c.Botoes.Add(new Botao
        {
            Area = new(BARRA_X - 10, 26, BarraL + 20, 32),
            Arrastar = (x, y, soltou) =>
            {
                // apertou a barra geral com um arraste de app ainda aberto: o soltar daquele se perdeu
                FimDoArrasteApp();
                _arrastando = !soltou;
                Arrastar(x);
                if (soltou) Mostrar();
            },
        });
        if (mixer)
        {
            c.Botoes.Add(new Botao { Area = new(-6, 72, lApps + 14, 28), Acao = () => TrocarAba(Aba.Apps) });
            c.Botoes.Add(new Botao { Area = new(xSaidas - 8, 72, lSaidas + 16, 28), Acao = () => TrocarAba(Aba.Saidas) });
        }
        if (aba == Aba.Apps)
        {
            for (int i = 0; i < _linhasApps; i++)
            {
                int linha = i;
                float y = LISTA_Y + i * APP_A;
                c.Botoes.Add(new Botao { Area = new(APP_X - 8, y + 12, APP_FIM - APP_X + 16, APP_A - 12), Arrastar = (x, _, soltou) => ArrastarApp(linha, x, soltou) });
                c.Botoes.Add(new Botao { Area = new(MUDO_X - 18, y, CARTAO_L - MUDO_X + 18, APP_A), Acao = () => AlternarMudoApp(linha) });
            }
        }
        else if (_audio.PodeTrocar)
            for (int i = 0; i < saidas.Count; i++)
            {
                var s = saidas[i];
                if (s.Contem(_audio.IdPadrao)) continue;
                c.Botoes.Add(new Botao { Area = new(0, LISTA_Y + i * LINHA_A, CARTAO_L, LINHA_A), Acao = () => Trocar(s) });
            }
        return c;
    }

    private void DesenharSaidas(ID2D1DeviceContext dc, Palco p, Vector2 t, List<Saida> saidas)
    {
        var F = p.G.Fabrica;
        if (saidas.Count == 0)
        {
            p.Texto.Desenhar(dc, "Nenhuma saída de áudio ativa", new Vector2(26, LISTA_Y + 7), 14, Cor.Secundario, FontWeight.Normal, largura: t.X - 26);
            return;
        }
        for (int i = 0; i < saidas.Count; i++)
        {
            float y = LISTA_Y + i * LINHA_A;
            bool atual = saidas[i].Contem(_audio.IdPadrao);
            var centro = new Vector2(8, y + 16);
            if (atual)
            {
                Icone.Circulo(dc, centro, 8, Cor.Verde);
                Icone.Linha(dc, new Vector2(centro.X - 3.5f, centro.Y + 0.2f), new Vector2(centro.X - 1, centro.Y + 2.8f), 1.8f, Cor.Branco, F);
                Icone.Linha(dc, new Vector2(centro.X - 1, centro.Y + 2.8f), new Vector2(centro.X + 3.8f, centro.Y - 2.6f), 1.8f, Cor.Branco, F);
            }
            else Icone.Anel(dc, F, centro, 7, 1.6f, 0, Cor.Branco);
            p.Texto.Desenhar(dc, saidas[i].Nome, new Vector2(26, y + 6), 14, atual ? Cor.Branco : new Color4(1f, 1f, 1f, 0.82f),
                atual ? FontWeight.SemiBold : FontWeight.Normal, largura: t.X - 26);
        }
    }

    private float VolumeDe(AppDeSom a) => _volumesLocais.TryGetValue(a.Chave, out var v) ? v : a.Volume;
    private bool MudoDe(AppDeSom a) => _mudosLocais.TryGetValue(a.Chave, out var m) ? m : a.Mudo;

    private void DesenharApps(ID2D1DeviceContext dc, Palco p, Vector2 t)
    {
        var F = p.G.Fabrica;
        var vis = AppsVisiveis();
        if (!_mixerOk || vis.Count == 0)
        {
            string msg = !_mixerOk ? "O volume por app não abriu nesta saída" : "Nenhum app tocando som agora";
            p.Texto.Desenhar(dc, msg, new Vector2(0, LISTA_Y + 9), 13, Cor.Secundario, FontWeight.Normal, largura: t.X);
            return;
        }
        int lado = (int)MathF.Round(ICONE * p.Escala);
        float largura = APP_FIM - APP_X;
        for (int i = 0; i < _linhasApps && _rolagem + i < vis.Count; i++)
        {
            var a = vis[_rolagem + i];
            float y = LISTA_Y + i * APP_A;
            float v = VolumeDe(a);
            bool mudo = MudoDe(a);

            float iy = y + (APP_A - ICONE) / 2;
            byte[]? px = a.Caminho != null ? _icones.Pixels(a.Caminho, lado) : null;
            if (px != null) IconesDeApps.Desenhar(dc, p, px, lado, 0, iy);
            else if (a.Sistema) Desenhos.Sino(dc, F, new Vector2(-1, iy - 1), 1f, Cor.Secundario);
            else Desenhos.App(dc, new Vector2(0, iy), ICONE, Cor.Secundario);

            string pct = Logica.Porcentagem(v).ToString();
            var tp = p.Texto.Medir(pct, 12, FontWeight.Normal, tabular: true);
            p.Texto.Desenhar(dc, pct, new Vector2(APP_FIM - tp.X, y + 2), 12, Cor.Secundario, FontWeight.Normal, tabular: true);
            p.Texto.Desenhar(dc, a.Nome, new Vector2(APP_X, y + 1), 13, a.Tocando ? Cor.Branco : Cor.Secundario, FontWeight.Medium,
                largura: largura - tp.X - 8);

            Icone.Retangulo(dc, APP_X, y + 24, largura, 4, 2, Cor.Fraco);
            float cheio = largura * Math.Clamp(v, 0f, 1f);
            if (cheio > 0.5f) Icone.Retangulo(dc, APP_X, y + 24, MathF.Max(4, cheio), 4, 2, mudo ? Cor.Secundario : Cor.Branco);
            Icone.Circulo(dc, new Vector2(APP_X + cheio, y + 26), 6, mudo ? new Color4(0.75f, 0.75f, 0.75f, 1f) : Cor.Branco);

            Icone.Circulo(dc, new Vector2(MUDO_X, y + 18), 14, mudo ? new Color4(1f, 1f, 1f, 0.9f) : Cor.Fraco);
            Desenhos.AltoFalante(dc, F, new Vector2(MUDO_X - 9, y + 9), 0.75f, mudo ? new Color4(0f, 0f, 0f, 1f) : Cor.Branco, Logica.Ondas(v, mudo), mudo);
        }
        if (vis.Count > _linhasApps)
        {
            // trilho fininho na borda: mostra que tem mais app embaixo (a rodinha rola)
            float h = _linhasApps * APP_A - 4;
            Icone.Retangulo(dc, CARTAO_L - 2, LISTA_Y + 2, 2, h, 1, Cor.Fraco);
            float alto = MathF.Max(12, h * _linhasApps / vis.Count);
            float topo = (h - alto) * _rolagem / Math.Max(1, vis.Count - _linhasApps);
            Icone.Retangulo(dc, CARTAO_L - 2, LISTA_Y + 2 + topo, 2, alto, 1, Cor.Secundario);
        }
    }

    private AppDeSom? AppNaLinha(int linha)
    {
        var vis = AppsVisiveis();
        int i = _rolagem + linha;
        return i >= 0 && i < vis.Count ? vis[i] : null;
    }

    private void ArrastarApp(int linha, float x, bool soltou)
    {
        // um arraste vem sempre do mesmo botão: chamada de outra linha é um aperto novo (o soltar do anterior se perdeu)
        if (_arrasteApp != null && linha != _arrasteLinha) FimDoArrasteApp();
        if (_arrasteApp == null)
        {
            // preso ao app da linha apertada: a lista pode mudar debaixo do dedo sem trocar o app arrastado
            if (AppNaLinha(linha) is not { } a) return;
            _arrasteApp = a.Chave;
            _arrasteLinha = linha;
            _arrasteMudo = MudoDe(a);
        }
        string chave = _arrasteApp;
        _arrastando = !soltou;
        float v = Logica.DaBarra(x, APP_X, APP_FIM - APP_X);
        _volumesLocais[chave] = v;
        var m = _mixer();
        m?.DefinirVolume(chave, v, confirmar: soltou);
        // subir um app mudo tira o mudo dele, como na barra geral
        if (_arrasteMudo && v > 0)
        {
            _arrasteMudo = false;
            _mudosLocais[chave] = false;
            m?.DefinirMudo(chave, false);
        }
        if (soltou)
        {
            _arrasteApp = null;
            _arrasteLinha = -1;
        }
        Redesenhar();
    }

    /// <summary>
    /// Fecha um arraste de app que ficou aberto (o núcleo perdeu o soltar, ou o cartão fechou): o último valor é
    /// mandado de novo pedindo confirmação, para o valor local não ficar valendo para sempre.
    /// </summary>
    private void FimDoArrasteApp()
    {
        if (_arrasteApp is not { } chave) return;
        _arrasteApp = null;
        _arrasteLinha = -1;
        if (_volumesLocais.TryGetValue(chave, out var v)) _mixer()?.DefinirVolume(chave, v, confirmar: true);
    }

    private void AlternarMudoApp(int linha)
    {
        FimDoArrasteApp();
        if (AppNaLinha(linha) is not { } a) return;
        bool novo = !MudoDe(a);
        _mudosLocais[a.Chave] = novo;
        _mixer()?.DefinirMudo(a.Chave, novo);
        Redesenhar();
    }

    public override void Dispose()
    {
        _prazo.Stop();
        _prazo.Dispose();
    }
}

/// <summary>Ícones do volume desenhados em Direct2D, numa caixa de 24x24 DIP (vezes `s`).</summary>
internal static class Desenhos
{
    public static void AltoFalante(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 o, float s, Color4 cor, int ondas, bool mudo)
    {
        Vector2 P(float x, float y) => new(o.X + x * s, o.Y + y * s);
        Icone.Retangulo(dc, o.X + 2.5f * s, o.Y + 8.5f * s, 4f * s, 7f * s, 1f * s, cor);
        Poligono(dc, f, cor, P(6f, 8.5f), P(11.5f, 4f), P(11.5f, 20f), P(6f, 15.5f));
        if (mudo)
        {
            Icone.Linha(dc, P(14.5f, 9f), P(20.5f, 15f), 1.9f * s, cor, f);
            Icone.Linha(dc, P(20.5f, 9f), P(14.5f, 15f), 1.9f * s, cor, f);
            return;
        }
        for (int i = 0; i < ondas; i++)
            Arco(dc, f, P(11.5f, 12f), (4.2f + i * 3.6f) * s, -48, 48, 1.8f * s, cor);
    }

    /// <summary>Sino (os sons do sistema no mixer).</summary>
    public static void Sino(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 o, float s, Color4 cor)
    {
        Vector2 P(float x, float y) => new(o.X + x * s, o.Y + y * s);
        Icone.Circulo(dc, P(12f, 10f), 5f * s, cor);
        Poligono(dc, f, cor, P(7f, 10f), P(17f, 10f), P(18.5f, 17f), P(5.5f, 17f));
        Icone.Retangulo(dc, o.X + 4f * s, o.Y + 16.5f * s, 16f * s, 2.2f * s, 1.1f * s, cor);
        Icone.Circulo(dc, P(12f, 20.3f), 1.9f * s, cor);
        Icone.Retangulo(dc, o.X + 11.2f * s, o.Y + 3.4f * s, 1.6f * s, 2.4f * s, 0.8f * s, cor);
    }

    /// <summary>App sem ícone no arquivo: uma janelinha (moldura e barra de título).</summary>
    public static void App(ID2D1DeviceContext dc, Vector2 o, float lado, Color4 cor)
    {
        float m = lado * 0.08f;
        Icone.Retangulo(dc, o.X + m, o.Y + m, lado - 2 * m, lado - 2 * m, lado * 0.22f, new Color4(cor.R, cor.G, cor.B, cor.A * 0.45f));
        Icone.Retangulo(dc, o.X + lado * 0.24f, o.Y + lado * 0.26f, lado * 0.52f, lado * 0.12f, lado * 0.06f, cor);
        Icone.Retangulo(dc, o.X + lado * 0.24f, o.Y + lado * 0.48f, lado * 0.34f, lado * 0.1f, lado * 0.05f, cor);
    }

    /// <summary>Fone de ouvido: arco da tiara e as duas conchas.</summary>
    public static void Fone(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 o, float s, Color4 cor)
    {
        Arco(dc, f, new Vector2(o.X + 12 * s, o.Y + 13 * s), 8.5f * s, -180, 0, 2f * s, cor);
        Icone.Retangulo(dc, o.X + 2.5f * s, o.Y + 12.5f * s, 4.5f * s, 8f * s, 2f * s, cor);
        Icone.Retangulo(dc, o.X + 17f * s, o.Y + 12.5f * s, 4.5f * s, 8f * s, 2f * s, cor);
    }

    private static void Poligono(ID2D1DeviceContext dc, ID2D1Factory1 f, Color4 cor, params Vector2[] pts)
    {
        using var g = f.CreatePathGeometry();
        using (var sk = g.Open())
        {
            sk.BeginFigure(pts[0], FigureBegin.Filled);
            for (int i = 1; i < pts.Length; i++) sk.AddLine(pts[i]);
            sk.EndFigure(FigureEnd.Closed);
            sk.Close();
        }
        using var b = dc.CreateSolidColorBrush(cor);
        dc.FillGeometry(g, b);
    }

    /// <summary>Arco no sentido horário de `de` até `ate` graus (0 = direita, 90 = embaixo).</summary>
    private static void Arco(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float r, float de, float ate, float espessura, Color4 cor)
    {
        float a0 = de * MathF.PI / 180f, a1 = ate * MathF.PI / 180f;
        using var g = f.CreatePathGeometry();
        using (var sk = g.Open())
        {
            sk.BeginFigure(new Vector2(c.X + r * MathF.Cos(a0), c.Y + r * MathF.Sin(a0)), FigureBegin.Hollow);
            sk.AddArc(new ArcSegment
            {
                Point = new Vector2(c.X + r * MathF.Cos(a1), c.Y + r * MathF.Sin(a1)),
                Size = new Size(r, r),
                SweepDirection = SweepDirection.Clockwise,
                ArcSize = ate - de > 180 ? ArcSize.Large : ArcSize.Small,
            });
            sk.EndFigure(FigureEnd.Open);
            sk.Close();
        }
        using var b = dc.CreateSolidColorBrush(cor);
        using var traco = f.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        dc.DrawGeometry(g, b, espessura, traco);
    }
}
