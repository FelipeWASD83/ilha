using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Control;
using Sessao = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using Status = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

// não é Ilha.Modulos.Midia: o núcleo tem a classe estática Ilha.Modulos e o namespace colidiria com ela
namespace Ilha.Midia;

/// <summary>
/// Qualquer coisa tocando no Windows (Spotify, YouTube no navegador...) pelos controles de mídia do sistema (SMTC).
/// A lista de sessões é mantida aqui, relida em qualquer evento e de 20 em 20 s enquanto houver sessão (os eventos
/// do SMTC às vezes param de chegar), e a escolhida é a que começou a tocar por último.
/// </summary>
[Modulo("midia", "Mídia")]
internal sealed class ModuloMidia : Modulo
{
    private sealed class Acompanhada
    {
        public required Sessao Sessao;
        public required Rastro Rastro;
        public IntPtr Identidade;
        public TypedEventHandler<Sessao, PlaybackInfoChangedEventArgs>? Info;
        public TypedEventHandler<Sessao, TimelinePropertiesChangedEventArgs>? Tempo;
        public TypedEventHandler<Sessao, MediaPropertiesChangedEventArgs>? Props;
    }

    private ConfigMidia _cfg = new();
    private AtividadeMidia? _atv;
    private bool _parado;

    // referências fortes: manager, sessões e handlers coletados = eventos que param de chegar calados
    private GlobalSystemMediaTransportControlsSessionManager? _gerente;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs>? _sessoesMudaram;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs>? _atualMudou;
    private readonly List<Acompanhada> _sessoes = new();
    private Acompanhada? _escolhida;
    private int _geracaoProps;

    private readonly System.Windows.Forms.Timer _ressinc = new() { Interval = 20_000 };
    private readonly System.Windows.Forms.Timer _espera = new() { Interval = 150 };
    private Microsoft.Win32.PowerModeChangedEventHandler? _energia;

    public override Type? TipoConfig => typeof(ConfigMidia);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigMidia>(cfg);
        _atv = new AtividadeMidia
        {
            AoAnterior = () => Comando(s => s.TrySkipPreviousAsync()),
            AoProxima = () => Comando(s => s.TrySkipNextAsync()),
            AoTocarPausar = TocarOuPausar,
            AoBuscar = Buscar,
        };
        AplicarConfig();
        S.Ilha.Adicionar(_atv);
        _ressinc.Tick += (_, _) => Ressincronizar();
        _espera.Tick += (_, _) =>
        {
            _espera.Stop();
            CarregarPropriedades();
        };
        _energia = (_, e) =>
        {
            if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
            S.NaInterface(() =>
            {
                if (_parado) return;
                Ressincronizar();
                _atv?.ReiniciarVisualizador();
            });
        };
        Microsoft.Win32.SystemEvents.PowerModeChanged += _energia;
        _ = Conectar();
    }

    private void AplicarConfig()
    {
        if (_atv == null) return;
        _atv.VisualizadorLigado = _cfg.Visualizador;
        _atv.PassoVolume = Math.Clamp(_cfg.PassoVolume, 1, 20) / 100f;
        _atv.ConfigMudou();
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigMidia>(cfg);
        AplicarConfig();
        Reavaliar();
    }

    private async Task Conectar()
    {
        try
        {
            var g = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_parado) return;
            _gerente = g;
            _sessoesMudaram = (_, _) => S.NaInterface(Ressincronizar);
            _atualMudou = (_, _) => S.NaInterface(Ressincronizar);
            g.SessionsChanged += _sessoesMudaram;
            g.CurrentSessionChanged += _atualMudou;
            Ressincronizar();
        }
        catch (Exception e) { Log.Escrever("mídia: controles de mídia do sistema não abriram: " + e.Message); }
    }

    public override void Parar()
    {
        _parado = true;
        _ressinc.Stop();
        _espera.Stop();
        if (_energia != null) Microsoft.Win32.SystemEvents.PowerModeChanged -= _energia;
        _energia = null;
        foreach (var a in _sessoes.ToArray()) Soltar(a);
        _sessoes.Clear();
        _escolhida = null;
        if (_gerente != null)
        {
            try
            {
                if (_sessoesMudaram != null) _gerente.SessionsChanged -= _sessoesMudaram;
                if (_atualMudou != null) _gerente.CurrentSessionChanged -= _atualMudou;
            }
            catch { }
            _gerente = null;
        }
        if (_atv != null)
        {
            _atv.Mostrar(false);
            _atv.Dispose();
        }
        _ressinc.Dispose();
        _espera.Dispose();
    }

    // ------------------------------------------------------------------ sessões

    private static IntPtr Identidade(object o)
    {
        // o IUnknown identifica o objeto do outro lado: a mesma sessão relida devolve o mesmo
        IntPtr p = ((WinRT.IWinRTObject)o).NativeObject.ThisPtr;
        var iid = new Guid("00000000-0000-0000-C000-000000000046");
        if (Marshal.QueryInterface(p, in iid, out IntPtr u) != 0) return p;
        Marshal.Release(u);
        return u;
    }

    private static (bool Tocando, bool Pausada, double Taxa) Estado(Sessao s)
    {
        var info = s.GetPlaybackInfo();
        var st = info.PlaybackStatus;
        double taxa = info.PlaybackRate ?? 1.0;
        return (st == Status.Playing, st is Status.Paused or Status.Stopped or Status.Changing, taxa);
    }

    private void Ressincronizar()
    {
        if (_parado || _gerente == null) return;
        IReadOnlyList<Sessao> lista;
        try { lista = _gerente.GetSessions(); }
        catch (Exception e) { Log.Escrever("mídia: GetSessions: " + e.Message); return; }

        var agora = DateTime.Now;
        var vistas = new List<Acompanhada>();
        var usadas = new HashSet<string>();
        var presentes = new HashSet<IntPtr>();
        foreach (var s in lista) presentes.Add(Identidade(s));
        // a escolhida que ganhou outro objeto: o cartão precisa recarregar tudo dela
        Acompanhada? trocada = null;
        foreach (var s in lista)
        {
            string aumid;
            try { aumid = s.SourceAppUserModelId ?? ""; } catch { continue; }
            string chave = aumid;
            for (int n = 2; !usadas.Add(chave); n++) chave = aumid + "#" + n;
            var id = Identidade(s);
            // a mesma sessão segue com o histórico e os eventos dela, mesmo que o "#2" tenha passado para outra aba
            var a = _sessoes.Find(x => x.Identidade == id && !vistas.Contains(x));
            bool nova = false;
            if (a != null) a.Rastro.Chave = chave;
            else
            {
                // o app recriou a sessão com o mesmo nome: troca o objeto, mantém o histórico
                var antiga = _sessoes.Find(x => x.Rastro.Chave == chave && !vistas.Contains(x) && !presentes.Contains(x.Identidade));
                if (antiga != null)
                {
                    Soltar(antiga);
                    a = Acompanhar(s, id, antiga.Rastro);
                    if (_escolhida == antiga) { _escolhida = a; trocada = a; }
                }
                else
                {
                    a = Acompanhar(s, id, new Rastro { Chave = chave });
                    nova = true;
                }
            }
            try
            {
                var (tocando, pausada, _) = Estado(s);
                DateTime? ultima = null;
                if (nova)
                {
                    try { ultima = s.GetTimelineProperties().LastUpdatedTime.LocalDateTime; } catch { }
                }
                a.Rastro.Atualizar(tocando, pausada, agora, ultima, nova);
            }
            catch (Exception e) { Log.Escrever("mídia: estado da sessão: " + e.Message); }
            vistas.Add(a);
        }
        foreach (var velha in _sessoes)
            if (!vistas.Contains(velha)) Soltar(velha);
        _sessoes.Clear();
        _sessoes.AddRange(vistas);
        if (_escolhida != null && !_sessoes.Contains(_escolhida)) _escolhida = null;
        _ressinc.Enabled = _sessoes.Count > 0;
        Reavaliar();
        // o Reavaliar só recarrega quando a escolha muda de linha; se ela continua na que trocou de objeto, recarrega aqui
        if (trocada != null && trocada == _escolhida && _atv != null)
        {
            AtualizarEstado(trocada, novaLinha: true);
            _atv.Definir("", "", null, null);
            CarregarPropriedades();
        }
    }

    private Acompanhada Acompanhar(Sessao s, IntPtr id, Rastro r)
    {
        var a = new Acompanhada { Sessao = s, Rastro = r, Identidade = id };
        a.Info = (_, _) => S.NaInterface(() => InfoMudou(a));
        a.Tempo = (_, _) => S.NaInterface(() => TempoMudou(a));
        a.Props = (_, _) => S.NaInterface(() => PropsMudaram(a));
        try
        {
            s.PlaybackInfoChanged += a.Info;
            s.TimelinePropertiesChanged += a.Tempo;
            s.MediaPropertiesChanged += a.Props;
        }
        catch (Exception e) { Log.Escrever("mídia: eventos da sessão: " + e.Message); }
        return a;
    }

    private static void Soltar(Acompanhada a)
    {
        try
        {
            if (a.Info != null) a.Sessao.PlaybackInfoChanged -= a.Info;
            if (a.Tempo != null) a.Sessao.TimelinePropertiesChanged -= a.Tempo;
            if (a.Props != null) a.Sessao.MediaPropertiesChanged -= a.Props;
        }
        catch { }
        a.Info = null;
        a.Tempo = null;
        a.Props = null;
    }

    private void Reavaliar()
    {
        if (_parado || _atv == null) return;
        var agora = DateTime.Now;
        int i = Escolha.Sessao(_sessoes.Select(x => x.Rastro).ToList(), agora, TimeSpan.FromMinutes(Math.Max(0.1, _cfg.PausadaMinutos)), _escolhida?.Rastro.Chave);
        var nova = i >= 0 ? _sessoes[i] : null;
        if (nova != _escolhida)
        {
            _escolhida = nova;
            if (nova != null)
            {
                AtualizarEstado(nova, novaLinha: true);
                _atv.Definir("", "", null, null);
                CarregarPropriedades();
            }
        }
        _atv.Mostrar(_escolhida != null);
    }

    // ------------------------------------------------------------------ eventos da sessão

    private void InfoMudou(Acompanhada a)
    {
        if (_parado || !_sessoes.Contains(a)) return;
        try
        {
            var (tocando, pausada, _) = Estado(a.Sessao);
            a.Rastro.Atualizar(tocando, pausada, DateTime.Now, null, false);
        }
        catch (Exception e) { Log.Escrever("mídia: PlaybackInfo: " + e.Message); }
        Reavaliar();
        if (a == _escolhida) AtualizarEstado(a, novaLinha: false);
    }

    private void TempoMudou(Acompanhada a)
    {
        if (_parado || a != _escolhida || _atv == null) return;
        try
        {
            var (tocando, _, taxa) = Estado(a.Sessao);
            _atv.Linha.Recebeu(LerInstante(a.Sessao, tocando, taxa), DateTimeOffset.Now);
            AtualizarEstado(a, novaLinha: false);
            _atv.LinhaMudou();
        }
        catch (Exception e) { Log.Escrever("mídia: Timeline: " + e.Message); }
    }

    private void PropsMudaram(Acompanhada a)
    {
        if (_parado || a != _escolhida) return;
        // chega mais de uma vez por faixa (às vezes o título antes da capa): espera assentar
        _espera.Stop();
        _espera.Start();
    }

    private static Instante LerInstante(Sessao s, bool tocando, double taxa)
    {
        var t = s.GetTimelineProperties();
        return new Instante(t.StartTime, t.EndTime, t.Position, t.LastUpdatedTime, tocando, taxa);
    }

    private void AtualizarEstado(Acompanhada a, bool novaLinha)
    {
        if (_atv == null) return;
        try
        {
            var info = a.Sessao.GetPlaybackInfo();
            var c = info.Controls;
            bool tocando = info.PlaybackStatus == Status.Playing;
            double taxa = info.PlaybackRate ?? 1.0;
            if (novaLinha) _atv.DefinirLinha(new Linha(LerInstante(a.Sessao, tocando, taxa)));
            else _atv.Linha.MudouEstado(tocando, taxa, DateTimeOffset.Now);
            bool tocarPausar = c.IsPlayPauseToggleEnabled || (tocando ? c.IsPauseEnabled : c.IsPlayEnabled);
            _atv.DefinirEstado(tocando, c.IsPreviousEnabled, c.IsNextEnabled, tocarPausar,
                c.IsPlaybackPositionEnabled && _atv.Linha.Atual.TemDuracao);
            _atv.LinhaMudou();
        }
        catch (Exception e) { Log.Escrever("mídia: estado: " + e.Message); }
    }

    private async void CarregarPropriedades()
    {
        var a = _escolhida;
        if (a == null || _atv == null) return;
        int geracao = ++_geracaoProps;
        try
        {
            var props = await a.Sessao.TryGetMediaPropertiesAsync();
            if (geracao != _geracaoProps || _parado || props == null) return;
            string titulo = props.Title ?? "";
            string artista = props.Artist ?? "";
            if (artista.Length == 0) artista = props.AlbumTitle ?? "";
            if (artista.Length == 0) artista = Rotulo.App(a.Rastro.Chave.Split('#')[0]);
            Capa? capa = null;
            if (props.Thumbnail != null)
            {
                try { capa = await Capa.Decodificar(props.Thumbnail); }
                catch (Exception e) { Log.Escrever("mídia: capa não decodificou: " + e.Message); }
            }
            if (geracao != _geracaoProps || _parado) return;
            var cor = capa != null ? CorDaCapa.Escolher(capa.Pixels, capa.Lado, capa.Lado) : null;
            _atv.Definir(titulo, artista, capa, cor);
        }
        catch (Exception e) { Log.Escrever("mídia: propriedades: " + e.Message); }
    }

    // ------------------------------------------------------------------ comandos

    private async void Comando(Func<Sessao, IAsyncOperation<bool>> acao)
    {
        var a = _escolhida;
        if (a == null) return;
        try { await acao(a.Sessao); }
        catch (Exception e) { Log.Escrever("mídia: comando: " + e.Message); }
    }

    private void TocarOuPausar()
    {
        var a = _escolhida;
        if (a == null) return;
        GlobalSystemMediaTransportControlsSessionPlaybackControls c;
        try { c = a.Sessao.GetPlaybackInfo().Controls; }
        catch { return; }
        if (c.IsPlayPauseToggleEnabled) Comando(s => s.TryTogglePlayPauseAsync());
        else if (_atv?.Tocando == true) Comando(s => s.TryPauseAsync());
        else Comando(s => s.TryPlayAsync());
    }

    private async void Buscar(TimeSpan alvo)
    {
        var a = _escolhida;
        if (a == null || _atv == null) return;
        try
        {
            var t = a.Sessao.GetTimelineProperties();
            if (t.MaxSeekTime > t.MinSeekTime)
                alvo = alvo < t.MinSeekTime ? t.MinSeekTime : alvo > t.MaxSeekTime ? t.MaxSeekTime : alvo;
            var linha = _atv.Linha;
            linha.Pedir(alvo, DateTimeOffset.Now);
            _atv.LinhaMudou();
            bool ok = await a.Sessao.TryChangePlaybackPositionAsync(alvo.Ticks);
            if (!ok && linha == _atv.Linha)
            {
                linha.Recusado();
                _atv.LinhaMudou();
            }
        }
        catch (Exception e) { Log.Escrever("mídia: seek: " + e.Message); }
    }

    // ------------------------------------------------------------------ autoteste

    public override string Autoteste() => TestesMidia.Rodar();
}

public sealed class ConfigMidia
{
    /// <summary>Por quantos minutos a mídia pausada continua na ilha.</summary>
    [JsonPropertyName("pausadaMinutos")] public double PausadaMinutos { get; set; } = 10;
    /// <summary>Barras animadas na pílula e no cartão (escuta o que sai no dispositivo padrão).</summary>
    [JsonPropertyName("visualizador")] public bool Visualizador { get; set; } = true;
    /// <summary>Quanto cada dente da rodinha muda o volume, em por cento.</summary>
    [JsonPropertyName("passoVolume")] public int PassoVolume { get; set; } = 2;
}
