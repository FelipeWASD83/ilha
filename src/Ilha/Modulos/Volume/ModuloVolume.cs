using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ilha.Volume;

public sealed class ConfigVolume
{
    // quanto tempo a ilha fica com o volume na tela depois da última mudança
    [Label("Segundos na tela", "Seconds on screen")]
    [JsonPropertyName("segundos")] public double Segundos { get; set; } = 1.8;
    // porcentagem por dente da rodinha do mouse em cima da ilha
    [Label("Passo da rodinha (%)", "Scroll wheel step (%)")]
    [JsonPropertyName("passoRolar")] public int PassoRolar { get; set; } = 2;
    // mostrar "conectado"/"desconectado" quando um fone, headset ou caixa entra ou sai
    [Label("Mostrar conexões de aparelhos", "Show device connections")]
    [JsonPropertyName("mostrarConexoes")] public bool MostrarConexoes { get; set; } = true;
    // EXPERIMENTAL: some com o pop-up de volume do Windows (some junto com o de brilho, modo avião e área de trabalho virtual)
    [Label("Esconder o pop-up de volume do Windows (experimental)", "Hide the Windows volume pop-up (experimental)")]
    [JsonPropertyName("esconderOsdDoWindows")] public bool EsconderOsdDoWindows { get; set; }
    // volume de cada app (Spotify, Discord, navegador, jogo) no cartão, na aba "Apps"
    [Label("Volume por app", "Per-app volume")]
    [JsonPropertyName("mixer")] public bool Mixer { get; set; } = true;
    // mostrar também os apps com som aberto mas parado agora (como o mixer antigo do Windows)
    [Label("Mostrar apps parados", "Show idle apps")]
    [JsonPropertyName("mostrarParados")] public bool MostrarParados { get; set; }

    internal void Corrigir()
    {
        Segundos = double.IsFinite(Segundos) ? Math.Clamp(Segundos, 0.5, 30) : 1.8;
        PassoRolar = Math.Clamp(PassoRolar, 1, 25);
    }
}

[Modulo("volume", "Volume", English = "Volume")]
internal sealed class ModuloVolume : Modulo
{
    private ConfigVolume _cfg = new();
    private Audio? _audio;
    private AtividadeVolume? _atv;
    private OsdDoWindows? _osd;
    private Mixer? _mixer;
    private IconesDeApps? _icones;
    private bool _competitivo;
    private List<string>? _listaCompetitiva;
    private string? _detalheMenu;
    private readonly Dictionary<string, DateTime> _avisados = new();

    public override Type? TipoConfig => typeof(ConfigVolume);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigVolume>(cfg);
        _cfg.Corrigir();
        // se a ilha anterior morreu com o pop-up nativo escondido, ele volta antes de qualquer coisa
        OsdDoWindows.RestaurarDaUltimaVez();
        _audio = new Audio(S.NaInterface);
        _icones = new IconesDeApps(S.NaInterface);
        _atv = new AtividadeVolume(_audio, () => _cfg, () => S.Ilha.Expandida == _atv, () => _mixer, _icones);
        S.Ilha.Adicionar(_atv);
        _audio.VolumeMudou += AoMudarVolume;
        _audio.ListaMudou += () => _atv?.ListaMudou();
        _audio.Conexao += AoConectar;
        _audio.PadraoMudou += () => _mixer?.TrocarAparelho(_audio?.IdPadrao);
        _audio.Iniciar();
        AplicarOsd();
        _competitivo = S.ContextoAtual().Competitivo;
        AplicarMixer();
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "volume",
            Nome = Lang.T("Som", "Sound"),
            // a tall slider like the iPhone's, at the bottom of the menu
            Ordem = 70, Rows = 2,
            Abrir = AbrirCartao,
            Draw = DesenharNoMenu,
            Drag = ArrastarNoMenu,
            Wheel = RolarNoMenu,
        });
        _detalheMenu = DetalheDoMenu();
    }

    // ---------------------------------------------------------------- the menu's slider

    private readonly MenuSlider _slider = new();

    private void DesenharNoMenu(Vortice.Direct2D1.ID2D1DeviceContext dc, Palco p, System.Drawing.RectangleF r)
    {
        var a = _audio;
        bool tem = a is { TemAparelho: true }, mudo = a?.Mudo ?? false;
        MenuSlider.Draw(dc, p, r, tem ? a!.Escala : 0, mudo,
            (d, pos, cor) => Desenhos.AltoFalante(d, p.G.Fabrica, pos, 26 / 24f, cor, Logica.Ondas(_audio?.Escala ?? 0, mudo), mudo),
            DetalheDoMenu() ?? "");
    }

    private void ArrastarNoMenu(Vector2 pos, System.Drawing.SizeF tamanho, bool soltou)
    {
        if (_audio is not { TemAparelho: true } a) return;
        if (_slider.Drag(pos, tamanho.Height, soltou, a.Escala) is not float nivel) return;
        a.DefinirEscala(nivel);
        if (a.Mudo && nivel > 0) a.DefinirMudo(false);
    }

    private int _rodaMenu;

    /// <summary>The wheel over the slider: the same step as over the island, without popping the volume pill over the menu.</summary>
    private bool RolarNoMenu(int delta)
    {
        if (_audio is not { TemAparelho: true } a) return false;
        _rodaMenu += delta;
        int dentes = _rodaMenu / 120;
        if (dentes == 0) return true;
        _rodaMenu -= dentes * 120;
        if (dentes > 0 && a.Mudo) a.DefinirMudo(false);
        a.DefinirEscala(Logica.Rolar(a.Escala, dentes * 120, _cfg.PassoRolar));
        return true;
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigVolume>(cfg);
        _cfg.Corrigir();
        AplicarOsd();
        AplicarMixer();
    }

    public override void Contexto(Contexto ctx)
    {
        if (_mixer != null && !ReferenceEquals(_listaCompetitiva, S.Config().JogosCompetitivos))
        {
            _listaCompetitiva = S.Config().JogosCompetitivos;
            _mixer.TrocarCompetitivos(_listaCompetitiva);
        }
        if (ctx.Competitivo == _competitivo) return;
        _competitivo = ctx.Competitivo;
        AplicarMixer();
    }

    /// <summary>
    /// O mixer existe com ele ligado na config e fora do modo competitivo (no jogo competitivo as sessões, inclusive a
    /// do jogo, são soltas). Reabre sozinho ao sair do jogo.
    /// </summary>
    private void AplicarMixer()
    {
        bool quer = _cfg.Mixer && !_competitivo && _audio != null;
        if (quer && _mixer == null)
        {
            _listaCompetitiva = S.Config().JogosCompetitivos;
            var m = new Mixer(S.NaInterface, _listaCompetitiva);
            m.Mudou += l => { if (_mixer == m) _atv?.AppsMudaram(l); };
            _mixer = m;
            m.Iniciar(_audio!.IdPadrao);
        }
        else if (!quer && _mixer != null)
        {
            _mixer.Dispose();
            _mixer = null;
            _atv?.SemMixer();
        }
    }

    private void AbrirCartao()
    {
        if (_atv == null) return;
        _atv.AbrirCartao();
        S.Ilha.Expandir(_atv);
    }

    private string? DetalheDoMenu() =>
        _audio is not { TemAparelho: true } a ? Lang.T("sem saída", "no output") : a.Mudo ? Lang.T("mudo", "muted") : Logica.Porcentagem(a.Escala) + "%";

    private void AplicarOsd()
    {
        if (_cfg.EsconderOsdDoWindows) (_osd ??= new OsdDoWindows()).Ligar();
        else _osd?.Desligar();
    }

    private void AoMudarVolume(bool deFora)
    {
        if (_atv == null) return;
        if (deFora && _osd != null)
        {
            // só esconde o nativo quando a ilha vai mesmo aparecer no lugar dele
            var ctx = S.ContextoAtual();
            _osd.MudouDeFora(!ctx.Competitivo && !ctx.D3DExclusivo && !S.Config().Pausada);
        }
        _atv.VolumeMudou(deFora);
        // o menu só redesenha quando o número que ele mostra muda
        var d = DetalheDoMenu();
        if (d != _detalheMenu) { _detalheMenu = d; S.Menu.Redesenhar(); }
    }

    private void AoConectar(string id, bool conectou)
    {
        if (!_cfg.MostrarConexoes || _atv == null || _audio == null) return;
        string nome = _audio.NomeDoAparelho(id);
        // os AirPods mudam de estado nos dois endpoints (estéreo e Hands-Free): um aviso só
        string chave = (conectou ? "+" : "-") + nome;
        var agora = DateTime.Now;
        if (_avisados.TryGetValue(chave, out var quando) && (agora - quando).TotalSeconds < 4) return;
        _avisados[chave] = agora;
        _atv.Aviso(conectou, nome);
    }

    public override void Parar()
    {
        _osd?.Dispose();
        _osd = null;
        _mixer?.Dispose();
        _mixer = null;
        _icones?.Dispose();
        _icones = null;
        _atv?.Esconder();
        _audio?.Dispose();
        _audio = null;
    }

    // ---------------------------------------------------------------- autoteste

    public override string Autoteste()
    {
        var r = new StringBuilder();
        void Conferir(bool ok, string oque) => r.AppendLine((ok ? "ok      " : "FALHOU  ") + oque);
        bool Perto(float a, float b, float tol = 0.01f) => MathF.Abs(a - b) <= tol;

        // escala e porcentagem
        Conferir(Logica.Porcentagem(0f) == 0 && Logica.Porcentagem(0.5f) == 50 && Logica.Porcentagem(1f) == 100, "porcentagem de 0, 0,5 e 1");
        Conferir(Logica.Porcentagem(1.3f) == 100 && Logica.Porcentagem(-0.2f) == 0 && Logica.Porcentagem(float.NaN) == 0, "porcentagem presa entre 0 e 100 (e NaN vira 0)");
        Conferir(Logica.Porcentagem(0.333f) == 33 && Logica.Porcentagem(0.996f) == 100, "porcentagem arredonda como o Windows");
        Conferir(Perto(Logica.Escala(64), 0.64f) && Logica.Escala(150) == 1f && Logica.Escala(-3) == 0f, "porcentagem para escala");
        Conferir(Perto(Logica.Arrastado(0.25f, 70, 140), 0.75f) && Perto(Logica.Arrastado(0.25f, -14, 140), 0.15f)
            && Logica.Arrastado(0.9f, 100, 140) == 1f && Logica.Arrastado(0.1f, -100, 140) == 0f && Logica.Arrastado(0.4f, float.NaN, 140) == 0.4f,
            "menu slider: relative to where it was, the whole height is 0 to 100%, held at the ends");

        // decibéis
        Conferir(Perto(Logica.Amplitude(0f), 1f) && Perto(Logica.Amplitude(-6.0206f), 0.5f, 0.001f) && Perto(Logica.Amplitude(-20f), 0.1f, 0.0001f), "dB para amplitude");
        Conferir(Perto(Logica.Decibeis(0.5f), -6.0206f) && Perto(Logica.Decibeis(1f), 0f) && float.IsNegativeInfinity(Logica.Decibeis(0f)), "amplitude para dB");
        Conferir(Perto(Logica.Decibeis(Logica.Amplitude(-37.5f)), -37.5f), "dB ida e volta");
        string previousLanguage = Lang.Choice;
        Lang.Set("pt");
        Conferir(Logica.TextoDb(-12.54f) == "-12,5 dB" && Logica.TextoDb(-0.04f) == "0,0 dB" && Logica.TextoDb(float.NegativeInfinity) == "-∞ dB",
            $"texto dos dB ({Logica.TextoDb(-12.54f)}, {Logica.TextoDb(-0.04f)}, {Logica.TextoDb(float.NegativeInfinity)})");
        Conferir(LogicaDoMixer.NomeDoSistema == "Sons do sistema" && Logica.NomePadraoDoAparelho == "Aparelho de áudio", "nomes padrão em português");
        Lang.Set("en");
        Conferir(Logica.TextoDb(-12.54f) == "-12.5 dB" && Logica.TextoDb(-0.04f) == "0.0 dB", $"texto dos dB em inglês ({Logica.TextoDb(-12.54f)}, {Logica.TextoDb(-0.04f)})");
        Conferir(LogicaDoMixer.NomeDoSistema == "System sounds" && Logica.NomePadraoDoAparelho == "Audio device", "nomes padrão em inglês");
        Lang.Set(previousLanguage);

        // rodinha e barra
        Conferir(Logica.Porcentagem(Logica.Rolar(0.63f, 120, 2)) == 64 && Logica.Porcentagem(Logica.Rolar(0.64f, 120, 2)) == 66, "rodinha para cima anda na grade de 2%");
        Conferir(Logica.Porcentagem(Logica.Rolar(0.63f, -120, 2)) == 62 && Logica.Porcentagem(Logica.Rolar(0.64f, -240, 2)) == 60, "rodinha para baixo (um e dois dentes)");
        Conferir(Logica.Rolar(1f, 120, 2) == 1f && Logica.Rolar(0f, -120, 2) == 0f && Logica.Porcentagem(Logica.Rolar(0.01f, -120, 2)) == 0, "rodinha presa nos extremos");
        Conferir(Logica.Porcentagem(Logica.Rolar(0.5f, 360, 5)) == 65, "passo configurável (3 dentes de 5%)");
        Conferir(Logica.DaBarra(48, 48, 242) == 0f && Perto(Logica.DaBarra(169, 48, 242), 0.5f) && Logica.DaBarra(400, 48, 242) == 1f && Logica.DaBarra(-5, 48, 242) == 0f, "posição na barra para escala");
        Conferir(Logica.Ondas(0.2f, false) == 1 && Logica.Ondas(0.5f, false) == 2 && Logica.Ondas(0.9f, false) == 3 && Logica.Ondas(0.9f, true) == 0 && Logica.Ondas(0f, false) == 0, "ondas do alto-falante");

        // junção dos AirPods (nomes de exemplo, sem aparelho de ninguém)
        var bt = Guid.NewGuid();
        var eps = new List<Endpoint>
        {
            new("{0.0.0.00000000}.{monitor}", "VG34 (NVIDIA High Definition Audio)", "NVIDIA High Definition Audio", "VG34", "HDAUDIO", Logica.ConteinerDoPc, 9),
            new("{0.0.0.00000000}.{hf}", "Headset (AirPods Pro 3 Hands-Free)", "AirPods Pro 3 Hands-Free", "Headset", "BTHHFENUM", bt, 5),
            new("{0.0.0.00000000}.{g733}", "Alto-falantes (Logitech G733 Gaming Headset)", "Logitech G733 Gaming Headset", "Alto-falantes", "USB", Guid.NewGuid(), 1),
            new("{0.0.0.00000000}.{estereo}", "Fones de ouvido (AirPods Pro 3)", "AirPods Pro 3", "Fones de ouvido", "BTHENUM", bt, 3),
            new("{0.0.0.00000000}.{caixas}", "Alto-falantes (Realtek(R) Audio)", "Realtek(R) Audio", "Alto-falantes", "HDAUDIO", Logica.ConteinerDoPc, 1),
        };
        var j = Logica.Juntar(eps);
        var air = j.Find(s => s.Contem("{0.0.0.00000000}.{hf}"));
        Conferir(j.Count == 4, $"AirPods estéreo e Hands-Free numa linha só ({j.Count} linhas para 5 endpoints)");
        Conferir(air != null && air.Id == "{0.0.0.00000000}.{estereo}" && air.Ids.Count == 2 && air.Contem("{0.0.0.00000000}.{ESTEREO}"), "a linha dos AirPods troca para o estéreo e marca pelos dois ids");
        Conferir(j[0].Nome.StartsWith("VG34") && j[1] == air && j[2].Aparelho.StartsWith("Logitech") && j[3].Aparelho.StartsWith("Realtek"), "a ordem do Windows é mantida (a linha fica onde o primeiro endpoint estava)");
        Conferir(j.Find(s => s.Aparelho.StartsWith("Realtek"))!.Ids.Count == 1 && j[0].Ids.Count == 1, "saídas do próprio PC (mesmo contêiner) não se juntam");
        var semConteiner = Logica.Juntar(new[]
        {
            new Endpoint("a", "Fones de ouvido (Fone X)", "Fone X", "Fones de ouvido", "BTHENUM", Guid.Empty, 3),
            new Endpoint("b", "Headset (Fone X Hands-Free)", "Fone X Hands-Free", "Headset", "BTHHFENUM", Guid.Empty, 5),
        });
        Conferir(semConteiner.Count == 1 && semConteiner[0].Id == "a", "sem contêiner, junta pelo nome sem \"Hands-Free\"");
        var soHf = Logica.Juntar(new[] { new Endpoint("b", "Headset (Fone X Hands-Free)", "Fone X Hands-Free", "Headset", "BTHHFENUM", bt, 5) });
        Conferir(soHf.Count == 1 && soHf[0].Id == "b", "Hands-Free sozinho continua na lista");
        var iguais = Logica.Juntar(new[]
        {
            new Endpoint("m1", "Monitor (NVIDIA)", "NVIDIA High Definition Audio", "Monitor", "HDAUDIO", Guid.Empty, 9),
            new Endpoint("m2", "Monitor 2 (NVIDIA)", "NVIDIA High Definition Audio", "Monitor 2", "HDAUDIO", Guid.Empty, 9),
        });
        Conferir(iguais.Count == 2, "dois monitores com o mesmo nome de aparelho não se juntam");
        Conferir(Logica.SemHandsFree("AirPods Pro 3 Hands-Free") == "AirPods Pro 3" && Logica.SemHandsFree("G733") == "G733", "nome sem o \"Hands-Free\"");

        // conectou e desconectou
        Conferir(Logica.Classificar(EstadoAparelho.Desconectado, EstadoAparelho.Ativo) == Transicao.Conectou, "8 para 1 = conectou");
        Conferir(Logica.Classificar(EstadoAparelho.Ativo, EstadoAparelho.Desconectado) == Transicao.Desconectou && Logica.Classificar(EstadoAparelho.Ativo, EstadoAparelho.Ausente) == Transicao.Desconectou, "1 para 8 ou 4 = desconectou");
        Conferir(Logica.Classificar(EstadoAparelho.Desativado, EstadoAparelho.Ativo) == Transicao.Nada && Logica.Classificar(EstadoAparelho.Ativo, EstadoAparelho.Desativado) == Transicao.Nada, "ativar e desativar à mão não avisam");
        Conferir(Logica.Classificar(0x10000008, 0x10000001) == Transicao.Conectou, "bits altos do estado são ignorados");

        // quanto tempo fica na tela
        var ex = new Exibicao();
        var t0 = new DateTime(2026, 11, 1, 12, 0, 0);
        ex.Volume(t0, 1.8);
        Conferir(ex.Conferir(t0.AddSeconds(1), false) == t0.AddSeconds(1.8), "antes do prazo continua (devolve o prazo)");
        ex.Volume(t0.AddSeconds(1.5), 1.8);
        Conferir(ex.Conferir(t0.AddSeconds(2), false) != null && ex.Ligada, "nova mudança renova os 1,8 s");
        Conferir(ex.Conferir(t0.AddSeconds(3.4), true) is DateTime volta && Math.Abs((volta - t0.AddSeconds(4.4)).TotalMilliseconds) < 1 && ex.Ligada, "com o cartão aberto não some (confere de novo 1 s depois)");
        Conferir(ex.Conferir(t0.AddSeconds(4.4), false) == null && !ex.Ligada, "cartão fechado e prazo vencido: some");
        bool trocouParaAviso = ex.Aviso(t0, true, "Fone X", 3);
        bool modoAviso = ex.Atual == Exibicao.Modo.Conectou && ex.Aparelho == "Fone X";
        bool trocouParaVolume = ex.Volume(t0, 1.8);
        Conferir(trocouParaAviso && modoAviso && trocouParaVolume && ex.Atual == Exibicao.Modo.Volume && !ex.Volume(t0, 1.8), "aviso de aparelho e volume trocam de modo (e o layout só muda quando troca)");

        // notificação do Windows e o eco das mudanças da própria ilha
        var buf = Marshal.AllocHGlobal(32);
        try
        {
            var g = Guid.NewGuid();
            Marshal.StructureToPtr(g, buf, false);
            Marshal.WriteInt32(buf, 16, 1);
            Marshal.WriteInt32(buf, 20, BitConverter.SingleToInt32Bits(0.42f));
            Marshal.WriteInt32(buf, 24, 2);
            var (ctx, mudo, esc) = Audio.LerNotificacao(buf);
            Conferir(ctx == g && mudo && esc == 0.42f, "AUDIO_VOLUME_NOTIFICATION_DATA lido no lugar certo");
        }
        finally { Marshal.FreeHGlobal(buf); }
        var fila = new List<Action>();
        var audioTeste = new Audio(fila.Add);
        var vistos = new List<bool>();
        audioTeste.VolumeMudou += vistos.Add;
        audioTeste.ChegouVolume(0.3f, false, Audio.ContextoDaIlha);
        audioTeste.ChegouVolume(0.32f, false, Audio.ContextoDaIlha);
        foreach (var a in fila) a();
        fila.Clear();
        audioTeste.ChegouVolume(0.5f, true, Guid.Empty);
        foreach (var a in fila) a();
        fila.Clear();
        Conferir(vistos.Count == 2 && !vistos[0] && vistos[1] && audioTeste.Escala == 0.5f && audioTeste.Mudo, "eco da ilha não abre; mudança de fora abre; rajada vira um despacho só");
        audioTeste.ChegouVolume(0.52f, false, global::Ilha.Midia.VolumeDoSistema.Contexto);
        foreach (var a in fila) a();
        fila.Clear();
        Conferir(vistos.Count == 3 && !vistos[2] && audioTeste.Escala == 0.52f, "rodinha em cima da mídia também é eco da ilha (não abre o volume)");
        Conferir(Audio.ErroDeInterface(unchecked((int)0x80004002)) && Audio.ErroDeInterface(unchecked((int)0x80004001)), "E_NOINTERFACE e E_NOTIMPL desligam a troca de saída");
        Conferir(!Audio.ErroDeInterface(unchecked((int)0x80070490)) && !Audio.ErroDeInterface(unchecked((int)0x88890004)) && !Audio.ErroDeInterface(0), "aparelho sumido ou inválido não desliga a troca (só falha aquela vez)");

        // janela do pop-up nativo
        uint pid = 100;
        var altTab = new RetratoJanela(new IntPtr(1), pid, true, false, false, 0, 0, 3440, 1440);
        var osd = new RetratoJanela(new IntPtr(2), pid, true, false, false, 1614, 1304, 211, 76);
        Conferir(Logica.CandidataAoOsd(null, osd, 3440, 1440, pid), "pop-up pequeno que acabou de aparecer é aceito");
        Conferir(!Logica.CandidataAoOsd(null, altTab, 3440, 1440, pid), "ilha XAML da tela inteira (Alt+Tab) é recusada");
        Conferir(!Logica.CandidataAoOsd(osd, osd, 3440, 1440, pid), "o que já estava à vista antes da mudança é recusado");
        Conferir(Logica.CandidataAoOsd(osd with { Visivel = false }, osd, 3440, 1440, pid), "escondida antes e à vista agora é aceita");
        Conferir(!Logica.CandidataAoOsd(null, osd with { Pid = 7 }, 3440, 1440, pid) && !Logica.CandidataAoOsd(null, osd, 3440, 1440, 0), "janela de outro processo (ou sem shell) é recusada");
        Conferir(!Logica.CandidataAoOsd(null, osd with { Encoberta = true }, 3440, 1440, pid) && !Logica.CandidataAoOsd(null, osd with { X = -32000, Y = -32000 }, 3440, 1440, pid), "encoberta ou já fora da tela não conta");

        Conferir(Marshal.SizeOf<ValorDePropriedade>() == 24 && Marshal.SizeOf<ChaveDePropriedade>() == 20, "PROPVARIANT com 24 bytes e PROPERTYKEY com 20");

        TestarMixer(Conferir);
        Sonda(r, Conferir);
        SondaDoMixer(r, Conferir);
        return r.ToString().TrimEnd();
    }

    /// <summary>A lógica do mixer por app: identificador, caminho, nome, agrupamento e rolagem (nomes de exemplo).</summary>
    private static void TestarMixer(Action<bool, string> conferir)
    {
        const string vazio = "{00000000-0000-0000-0000-000000000000}";
        string idApp = "{0.0.0.00000000}.{6f1a}|\\Device\\HarddiskVolume3\\Programas\\Tocador\\tocador.exe%b" + vazio;
        conferir(LogicaDoMixer.CaminhoNoIdentificador(idApp) == "\\Device\\HarddiskVolume3\\Programas\\Tocador\\tocador.exe", "caminho do .exe tirado do identificador da sessão");
        conferir(LogicaDoMixer.CaminhoNoIdentificador("{0.0.0.00000000}.{6f1a}|#%b{A9EF3FD9-4240-455E-A4D5-F2B3301887B2}") == null, "identificador dos sons do sistema (\"#\") não tem caminho");
        conferir(LogicaDoMixer.CaminhoNoIdentificador(null) == null && LogicaDoMixer.CaminhoNoIdentificador("sem barra") == null
            && LogicaDoMixer.CaminhoNoIdentificador("x|\\Device\\HarddiskVolume3\\pasta%b" + vazio) == null, "identificador fora do formato (ou sem .exe) não inventa caminho");
        conferir(LogicaDoMixer.CaminhoNoIdentificador("x|\\Device\\HarddiskVolume3\\a.exe") == "\\Device\\HarddiskVolume3\\a.exe", "identificador sem o \"%b\" no fim também serve");

        var mapa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["\\Device\\HarddiskVolume3"] = "C:",
            ["\\Device\\HarddiskVolume30"] = "D:",
        };
        conferir(LogicaDoMixer.ParaCaminhoDos("\\Device\\HarddiskVolume3\\Programas\\x.exe", mapa) == "C:\\Programas\\x.exe", "\\Device\\HarddiskVolume3 vira C:");
        conferir(LogicaDoMixer.ParaCaminhoDos("\\Device\\HarddiskVolume30\\Jogos\\y.exe", mapa) == "D:\\Jogos\\y.exe", "Volume30 não casa com o prefixo do Volume3");
        conferir(LogicaDoMixer.ParaCaminhoDos("\\device\\harddiskvolume3\\z.exe", mapa) == "C:\\z.exe", "sem diferença de maiúscula no aparelho");
        conferir(LogicaDoMixer.ParaCaminhoDos("\\Device\\HarddiskVolume9\\z.exe", mapa) == null && LogicaDoMixer.ParaCaminhoDos("E:\\a.exe", mapa) == "E:\\a.exe", "aparelho sem letra devolve null; caminho com letra fica como está");

        conferir(LogicaDoMixer.Indireto("@%SystemRoot%\\System32\\AudioSrv.Dll,-202") && !LogicaDoMixer.Indireto("Tocador") && !LogicaDoMixer.Indireto(null), "nome indireto (@dll,-id) reconhecido");
        var lista = new[] { "jogo.exe", "Outro" };
        conferir(LogicaDoMixer.EhCompetitivo("JOGO.EXE", lista) && LogicaDoMixer.EhCompetitivo("outro.exe", lista) && LogicaDoMixer.EhCompetitivo("C:\\x\\Jogo.exe", lista), "lista competitiva com e sem \".exe\", sem diferença de maiúscula, pelo caminho");
        conferir(!LogicaDoMixer.EhCompetitivo("tocador.exe", lista) && !LogicaDoMixer.EhCompetitivo(null, lista) && !LogicaDoMixer.EhCompetitivo("", lista), "o que não está na lista não é competitivo");

        SessaoDeSom Se(string id, uint pid, string? cam, int estado = LogicaDoMixer.Ativa, float vol = 1f, bool mudo = false, bool sistema = false,
            string? exib = null, string? desc = null, string? exe = null, bool vivo = true) =>
            new(id, pid, sistema, cam, exe, exib, desc, estado, vol, mudo, vivo);

        const string nav = "C:\\Programas\\Navegador\\nav.exe";
        var sessoes = new List<SessaoDeSom>
        {
            Se("a", 10, nav, LogicaDoMixer.Inativa, 0.3f, desc: "Navegador Exemplo"),
            Se("b", 11, nav.ToUpperInvariant(), LogicaDoMixer.Ativa, 0.8f, desc: "Navegador Exemplo"),
            Se("c", 20, "C:\\Programas\\Chat\\chat.exe", LogicaDoMixer.Ativa, 0.5f, mudo: true, desc: "Chat Exemplo"),
            Se("d", 0, null, LogicaDoMixer.Inativa, 1f, sistema: true, exib: "@%SystemRoot%\\System32\\AudioSrv.Dll,-202"),
            Se("e", 30, "C:\\Jogos\\Velho\\velho.exe", LogicaDoMixer.Expirada),
            Se("f", 40, "C:\\Jogos\\Fechado\\fechado.exe", vivo: false),
            Se("g", 50, null, exe: "semcaminho.exe"),
            Se("h", 51, null, exe: "semcaminho.exe"),
            Se("i", 60, "C:\\Programas\\Musica\\musica.exe", LogicaDoMixer.Ativa, 0.4f, exib: "Música", desc: "Descrição do arquivo"),
        };
        var apps = LogicaDoMixer.Agrupar(sessoes);
        var navApp = apps.Find(a => a.Nome == "Navegador Exemplo");
        conferir(navApp != null && navApp.Sessoes == 2 && Math.Abs(navApp.Volume - 0.8f) < 0.001f && navApp.Tocando,
            "duas sessões do mesmo .exe (processos diferentes) viram uma linha; o volume é o da sessão tocando");
        conferir(apps.Find(a => a.Nome == "Chat Exemplo") is { Mudo: true }, "app com todas as sessões mudas aparece mudo");
        conferir(!apps.Any(a => a.Nome.Contains("velho")) && !apps.Any(a => a.Nome.Contains("fechado")), "sessão expirada e processo fechado ficam de fora");
        conferir(apps.Count(a => a.Nome == "semcaminho") == 2, "sem caminho, o agrupamento é por processo (dois pids, duas linhas)");
        conferir(apps.Count > 0 && apps[^1].Sistema && apps[^1].Nome == LogicaDoMixer.NomeDoSistema && apps[^1].Chave == LogicaDoMixer.ChaveDoSistema,
            "sons do sistema com nome próprio, no fim da lista");
        conferir(apps.Find(a => a.Caminho?.EndsWith("musica.exe") == true)?.Nome == "Música", "nome de exibição da sessão vem antes da descrição do arquivo");
        var naoSistema = apps.Where(a => !a.Sistema).Select(a => a.Nome).ToList();
        conferir(naoSistema.SequenceEqual(naoSistema.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)), "ordem alfabética (a lista não pula quando um app para)");

        conferir(LogicaDoMixer.Nome(new[] { Se("x", 1, "C:\\a\\Coisa.exe") }) == "Coisa", "sem nome nem descrição: nome do .exe sem a extensão");
        conferir(LogicaDoMixer.Nome(new[] { Se("x", 1, null, exe: "coisa.exe", exib: "  ", desc: "") }) == "coisa", "nome em branco não conta; cai para o .exe da lista de processos");
        conferir(LogicaDoMixer.Nome(new[] { Se("x", 1, null, exib: "@arquivo.dll,-5") }) == "App", "nome indireto não resolvido nunca aparece cru");

        var parados = LogicaDoMixer.Visiveis(apps, false, new HashSet<string>());
        conferir(!parados.Any(a => !a.Tocando), "sem \"mostrarParados\" só aparecem os apps tocando");
        conferir(LogicaDoMixer.Visiveis(apps, true, new HashSet<string>()).Count == apps.Count, "com \"mostrarParados\" aparecem todos");
        conferir(LogicaDoMixer.Visiveis(apps, false, new HashSet<string> { LogicaDoMixer.ChaveDoSistema }).Any(a => a.Sistema), "com o cartão aberto, o app que já apareceu fica mesmo parado");

        conferir(LogicaDoMixer.Rolagem(0, -1, 8, 5) == 1 && LogicaDoMixer.Rolagem(3, -1, 8, 5) == 3 && LogicaDoMixer.Rolagem(1, 3, 8, 5) == 0, "rolagem presa entre o topo e o fim");
        conferir(LogicaDoMixer.Rolagem(2, 0, 3, 5) == 0, "lista encolheu: a rolagem volta para o topo");

        // valores locais (o que a ilha mandou) contra as leituras do mixer
        AppDeSom Ap(string chave, float vol = 0.5f, bool mudo = false) => new(chave, chave, null, false, vol, mudo, true, 1);
        var doisApps = new List<AppDeSom> { Ap("a", 0.2f), Ap("b", 0.9f, true) };
        var nada = new HashSet<string>();
        var vols = new Dictionary<string, float> { ["a"] = 0.7f, ["b"] = 0.4f };
        var mudos = new Dictionary<string, bool> { ["b"] = false };
        bool soltou = LogicaDoMixer.Reconciliar(vols, mudos, doisApps, nada, nada, null);
        conferir(!soltou && vols.Count == 2 && mudos.Count == 1, "leitura sem confirmação (de antes do Set) não solta o valor local: a barra e o mudo não voltam");
        soltou = LogicaDoMixer.Reconciliar(vols, mudos, doisApps, new HashSet<string> { "b" }, nada, null);
        conferir(soltou && vols.ContainsKey("a") && !vols.ContainsKey("b") && mudos.Count == 0, "app confirmado solta volume e mudo; o outro continua");
        vols = new() { ["a"] = 0.7f, ["b"] = 0.4f };
        mudos = new() { ["a"] = true };
        LogicaDoMixer.Reconciliar(vols, mudos, doisApps, new HashSet<string> { "a", "b" }, nada, "a");
        conferir(vols.Count == 1 && vols.ContainsKey("a") && mudos.Count == 0, "o volume do app arrastado fica com o do dedo mesmo confirmado (o mudo dele solta)");
        vols = new() { ["a"] = 0.7f };
        LogicaDoMixer.Reconciliar(vols, new Dictionary<string, bool>(), doisApps, nada, new HashSet<string> { "a" }, null);
        conferir(vols.Count == 0, "mudança por fora (outro contexto) solta o valor local");
        vols = new() { ["sumiu"] = 0.7f };
        mudos = new() { ["sumiu"] = true };
        LogicaDoMixer.Reconciliar(vols, mudos, doisApps, nada, nada, null);
        conferir(vols.Count == 0 && mudos.Count == 0, "app que saiu da lista não deixa valor local para trás");

        // o serviço de áudio reiniciando
        int total = 0, n = 0;
        for (; LogicaDoMixer.EsperaParaReabrir(n) >= 0; n++) total += LogicaDoMixer.EsperaParaReabrir(n);
        conferir(LogicaDoMixer.EsperaParaReabrir(0) == 1000 && LogicaDoMixer.EsperaParaReabrir(3) == 8000 && LogicaDoMixer.EsperaParaReabrir(4) == 15_000
            && LogicaDoMixer.EsperaParaReabrir(7) == 30_000, "reabrir o mixer espera 1, 2, 4, 8, 15 e 30 s");
        conferir(n == 8 && total == 120_000, $"desiste depois de 8 tentativas em 2 min ({n} em {total / 1000} s)");

        // o contexto do aviso de sessão
        var buf = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.StructureToPtr(Audio.ContextoDaIlha, buf, false);
            conferir(Audio.DaIlha(Mixer.LerContexto(buf)), "aviso de sessão com o GUID da ilha é eco (não relê)");
            var outro = Guid.NewGuid();
            Marshal.StructureToPtr(outro, buf, false);
            conferir(Mixer.LerContexto(buf) == outro && !Audio.DaIlha(outro), "aviso com outro GUID é mudança de fora");
        }
        finally { Marshal.FreeHGlobal(buf); }
        conferir(Mixer.LerContexto(IntPtr.Zero) == Guid.Empty && !Audio.DaIlha(Guid.Empty), "aviso sem contexto (ponteiro nulo) é mudança de fora");
    }

    /// <summary>
    /// Sonda real do mixer, só leitura, numa thread MTA (como o mixer de verdade): abre o gerente de sessões da saída
    /// padrão, registra e solta o aviso de sessão nova, e lista os apps com os nomes. Nenhum volume é mudado.
    /// </summary>
    private static void SondaDoMixer(StringBuilder r, Action<bool, string> conferir)
    {
        var linhas = new List<(bool Ok, string Texto)>();
        var info = new List<string>();
        var t = new System.Threading.Thread(() =>
        {
            IMMDeviceEnumerator? en = null;
            try
            {
                en = (IMMDeviceEnumerator)ComDeAudio.NovoEnumerador();
                if (en.GetDefaultAudioEndpoint(FluxoAudio.Render, PapelAudio.Multimidia, out var d) != 0) { info.Add("aviso   sonda do mixer: nenhuma saída padrão agora"); return; }
                IAudioSessionManager2 g;
                try
                {
                    var iid = typeof(IAudioSessionManager2).GUID;
                    int hr = d.Activate(ref iid, Com.CLSCTX_ALL, IntPtr.Zero, out var obj);
                    linhas.Add((hr == 0, $"sonda do mixer: IAudioSessionManager2 da saída padrão (0x{hr:X8})"));
                    if (hr != 0) return;
                    g = (IAudioSessionManager2)obj;
                }
                finally { Com.Soltar(d); }
                try
                {
                    var aviso = new AvisoDeSonda();
                    int r1 = g.RegisterSessionNotification(aviso);
                    int hrE = g.GetSessionEnumerator(out var lista);
                    linhas.Add((hrE == 0, $"sonda do mixer: GetSessionEnumerator (0x{hrE:X8})"));
                    var lidas = new List<SessaoDeSom>();
                    var apps = new AppsDoSistema();
                    apps.Processos();
                    var vivos = AppsDoSistema.ListarProcessos();
                    if (hrE == 0)
                    {
                        try
                        {
                            lista.GetCount(out int n);
                            for (int i = 0; i < n; i++)
                            {
                                if (lista.GetSession(i, out var o) != 0) continue;
                                try
                                {
                                    var c2 = (IAudioSessionControl2)o;
                                    var vol = (ISimpleAudioVolume)c2;
                                    c2.GetState(out int estado);
                                    c2.GetProcessId(out uint pid);
                                    bool sistema = c2.IsSystemSoundsSession() == 0;
                                    c2.GetSessionIdentifier(out var id);
                                    c2.GetDisplayName(out var nome);
                                    string? exe = apps.ExeDoPid(pid);
                                    // a sonda nunca abre processo: só o caminho que vem no identificador
                                    string? cam = sistema ? null : apps.Caminho(id, 0, null, Array.Empty<string>());
                                    vol.GetMasterVolume(out float v);
                                    vol.GetMute(out bool m);
                                    lidas.Add(new SessaoDeSom(i.ToString(), pid, sistema, cam, exe, AppsDoSistema.Exibicao(nome), apps.Descricao(cam), estado, v, m, pid == 0 || vivos.ContainsKey(pid)));
                                }
                                finally { Com.Soltar(o); }
                            }
                        }
                        finally { Com.Soltar(lista); }
                    }
                    int r2 = r1 == 0 ? g.UnregisterSessionNotification(aviso) : -1;
                    linhas.Add((r1 == 0 && r2 == 0, $"sonda do mixer: aviso de sessão nova registra e solta numa thread MTA (0x{r1:X8}, 0x{r2:X8})"));
                    int comCaminho = lidas.Count(x => !x.Sistema && x.Caminho != null), semCaminho = lidas.Count(x => !x.Sistema && x.Caminho == null);
                    var agrupados = LogicaDoMixer.Agrupar(lidas);
                    linhas.Add((true, $"sonda do mixer: {lidas.Count} sessões ({comCaminho} com o caminho pelo identificador, {semCaminho} sem), {agrupados.Count} linhas"));
                    foreach (var a in agrupados) info.Add($"          · {a.Nome}  [{(a.Tocando ? "tocando" : "parado")}, {Logica.Porcentagem(a.Volume)}%{(a.Mudo ? ", mudo" : "")}, {a.Sessoes} sessão(ões)]");
                }
                finally { Com.Soltar(g); }
            }
            catch (Exception e) { linhas.Add((false, "sonda do mixer: " + e.Message)); }
            finally { Com.Soltar(en); }
        }) { IsBackground = true, Name = "ilha-teste-mixer" };
        t.SetApartmentState(System.Threading.ApartmentState.MTA);
        t.Start();
        if (!t.Join(10_000)) { conferir(false, "sonda do mixer: não terminou em 10 s"); return; }
        foreach (var (ok, texto) in linhas) conferir(ok, texto);
        foreach (var l in info) r.AppendLine(l);
    }

    private sealed class AvisoDeSonda : IAudioSessionNotification
    {
        public int OnSessionCreated(IntPtr sessao) => 0;
    }

    /// <summary>Sonda real, só leitura: lista as saídas e lê o volume sem mudar nada no sistema.</summary>
    private static void Sonda(StringBuilder r, Action<bool, string> conferir)
    {
        IMMDeviceEnumerator en;
        try { en = (IMMDeviceEnumerator)ComDeAudio.NovoEnumerador(); }
        catch (Exception e) { conferir(false, "sonda: MMDeviceEnumerator não abriu: " + e.Message); return; }
        try
        {
            var eps = Audio.ListarAtivos(en);
            conferir(true, $"sonda: {eps.Count} endpoints de saída ativos");
            foreach (var e in eps) r.AppendLine($"          · {e.Nome}  [{e.Enumerador}, formato {e.Formato}{(Logica.EhHandsFree(e) ? ", Hands-Free" : "")}]");
            var linhas = Logica.Juntar(eps);
            conferir(linhas.Count <= eps.Count, $"sonda: {linhas.Count} linhas na lista da ilha");
            foreach (var s in linhas) r.AppendLine($"          · {s.Nome}{(s.Ids.Count > 1 ? $"  (junta {s.Ids.Count} endpoints)" : "")}");

            if (en.GetDefaultAudioEndpoint(FluxoAudio.Render, PapelAudio.Multimidia, out var d) != 0)
                r.AppendLine("aviso   sonda: nenhuma saída padrão agora");
            else
            {
                try
                {
                    d.GetId(out var id);
                    var iid = Com.IID_IAudioEndpointVolume;
                    int hr = d.Activate(ref iid, Com.CLSCTX_ALL, IntPtr.Zero, out var obj);
                    conferir(hr == 0, $"sonda: IAudioEndpointVolume do padrão (0x{hr:X8})");
                    if (hr == 0)
                    {
                        var vol = (IAudioEndpointVolume)obj;
                        try
                        {
                            vol.GetMasterVolumeLevelScalar(out float esc);
                            vol.GetMasterVolumeLevel(out float db);
                            vol.GetMute(out bool mudo);
                            vol.GetVolumeRange(out float min, out float max, out float passo);
                            var padrao = linhas.Find(s => s.Contem(id));
                            conferir(esc >= 0 && esc <= 1, $"sonda: padrão \"{padrao?.Nome ?? id}\" em {Logica.Porcentagem(esc)}% ({Logica.TextoDb(db)}, faixa {Logica.TextoDb(min)} a {Logica.TextoDb(max)}){(mudo ? ", mudo" : "")}");
                            conferir(padrao != null, "sonda: a saída padrão está na lista e seria marcada");
                            // registrar e soltar o callback não muda nada no sistema e prova que a interface gerenciada é aceita
                            var cb = new CallbackDeSonda();
                            int r1 = vol.RegisterControlChangeNotify(cb), r2 = r1 == 0 ? vol.UnregisterControlChangeNotify(cb) : -1;
                            conferir(r1 == 0 && r2 == 0, $"sonda: callback de volume registra e solta (0x{r1:X8}, 0x{r2:X8})");
                        }
                        finally { Com.Soltar(vol); }
                    }
                }
                finally { Com.Soltar(d); }
            }

            var nc = new NotificacaoDeSonda();
            int n1 = en.RegisterEndpointNotificationCallback(nc), n2 = n1 == 0 ? en.UnregisterEndpointNotificationCallback(nc) : -1;
            conferir(n1 == 0 && n2 == 0, $"sonda: aviso de aparelho registra e solta (0x{n1:X8}, 0x{n2:X8})");
        }
        catch (Exception e) { conferir(false, "sonda: " + e.Message); }
        finally { Com.Soltar(en); }

        try
        {
            var pc = (IPolicyConfig)new PolicyConfigClientCom();
            Com.Soltar(pc);
            conferir(true, "sonda: IPolicyConfig responde ao QueryInterface (a troca de saída fica disponível)");
        }
        catch (Exception e) { r.AppendLine("aviso   sonda: IPolicyConfig não respondeu (a ilha só lista as saídas): " + e.Message); }

        try
        {
            uint shell = OsdDoWindows.PidDoShell();
            var ilhas = OsdDoWindows.Retratar();
            int doShell = 0, aVista = 0;
            foreach (var j in ilhas.Values) { if (j.Pid == shell) doShell++; if (j.AVista) aVista++; }
            conferir(true, $"sonda: {doShell} ilhas XAML do explorer agora ({aVista} à vista); nenhuma foi tocada");
        }
        catch (Exception e) { r.AppendLine("aviso   sonda: janelas do explorer: " + e.Message); }
    }

    private sealed class CallbackDeSonda : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr dados) => 0;
    }

    private sealed class NotificacaoDeSonda : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string id, uint novoEstado) => 0;
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) => 0;
        public int OnDefaultDeviceChanged(FluxoAudio fluxo, PapelAudio papel, string? id) => 0;
        public int OnPropertyValueChanged(string id, ChaveDePropriedade chave) => 0;
    }
}
