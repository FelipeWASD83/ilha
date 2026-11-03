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
[Modulo("midia", "Mídia", English = "Media")]
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
    // sessions that started failing (Windows hands back null for a session whose app is going away), by identity: they
    // are not followed again for a while. Calling them again and again ended in a native crash inside
    // Windows.Media.MediaControl.dll on 03/11/2026, which no catch can stop.
    private readonly Dictionary<IntPtr, DateTime> _quebradas = new();
    private static readonly TimeSpan QUEBRADA = TimeSpan.FromMinutes(2);

    private readonly System.Windows.Forms.Timer _ressinc = new() { Interval = 20_000 };
    private readonly System.Windows.Forms.Timer _espera = new() { Interval = 150 };
    private Microsoft.Win32.PowerModeChangedEventHandler? _energia;

    public override Type? TipoConfig => typeof(ConfigMidia);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigMidia>(cfg);
        _atv = new AtividadeMidia
        {
            AoAnterior = () => { _atv?.MarkSkip(-1); Comando(s => s.TrySkipPreviousAsync()); },
            AoProxima = () => { _atv?.MarkSkip(1); Comando(s => s.TrySkipNextAsync()); },
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
            // event arguments are new objects every time: released on the interface thread (see WinRtRelease)
            _sessoesMudaram = (_, e) => S.NaInterface(() => { WinRtRelease.Now(e); Ressincronizar(); });
            _atualMudou = (_, e) => S.NaInterface(() => { WinRtRelease.Now(e); Ressincronizar(); });
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
        try
        {
            // the list object is new on every call (the sessions inside are the same objects each time, cached by
            // identity, so those are never released by hand): copied and released right away
            var nativa = _gerente.GetSessions();
            // by index, not foreach: an enumerator would be one more native object left to the finalizer
            var copia = new List<Sessao>(nativa.Count);
            for (int i = 0; i < nativa.Count; i++) copia.Add(nativa[i]);
            lista = copia;
            WinRtRelease.Now(nativa);
        }
        catch (Exception e) { Log.Escrever("mídia: GetSessions: " + e.Message); return; }

        var agora = DateTime.Now;
        var vistas = new List<Acompanhada>();
        var usadas = new HashSet<string>();
        var presentes = new HashSet<IntPtr>();
        foreach (var s in lista) presentes.Add(Identidade(s));
        foreach (var k in _quebradas.Where(x => agora - x.Value > QUEBRADA || !presentes.Contains(x.Key)).Select(x => x.Key).ToList())
            _quebradas.Remove(k);
        // a escolhida que ganhou outro objeto: o cartão precisa recarregar tudo dela
        Acompanhada? trocada = null;
        foreach (var s in lista)
        {
            var id = Identidade(s);
            if (_quebradas.ContainsKey(id)) continue;
            string aumid;
            try { aumid = s.SourceAppUserModelId ?? ""; } catch { continue; }
            string chave = aumid;
            for (int n = 2; !usadas.Add(chave); n++) chave = aumid + "#" + n;
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
            catch (Exception e) when (!Morta(e))
            {
                // a passing hiccup (an empty reading): the session stays
                Log.Escrever($"mídia: estado da sessão {chave}: {e.Message} (segue)");
            }
            catch (Exception e)
            {
                Log.Escrever($"mídia: estado da sessão {chave}: {e.Message} (largada)");
                _quebradas[id] = agora;
                Soltar(a);
                if (_escolhida == a) _escolhida = null;
                continue;
            }
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
        a.Info = (_, e) => S.NaInterface(() => { WinRtRelease.Now(e); InfoMudou(a); });
        a.Tempo = (_, e) => S.NaInterface(() => { WinRtRelease.Now(e); TempoMudou(a); });
        a.Props = (_, e) => S.NaInterface(() => { WinRtRelease.Now(e); PropsMudaram(a); });
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
        catch (Exception e) { Quebrou(a, "PlaybackInfo", e); return; }
        Reavaliar();
        if (a == _escolhida) AtualizarEstado(a, novaLinha: false);
    }

    private void TempoMudou(Acompanhada a)
    {
        if (_parado || a != _escolhida || _atv == null) return;
        try
        {
            var (tocando, _, taxa) = Estado(a.Sessao);
            // Apple Music sometimes hands back no timeline for a moment: nothing to read this time, the session is fine
            if (LerInstante(a.Sessao, tocando, taxa) is { } instante) _atv.Linha.Recebeu(instante, DateTimeOffset.Now);
            AtualizarEstado(a, novaLinha: false);
            _atv.LinhaMudou();
        }
        catch (Exception e) { Quebrou(a, "Timeline", e); }
    }

    /// <summary>
    /// A call on a session failed: that session object is dead or dying. It stops being followed (events off, out of the
    /// list, and skipped by the next readings for a while) and the list is read again, where the app usually shows up
    /// with a new session.
    /// </summary>
    private void Quebrou(Acompanhada a, string onde, Exception e)
    {
        // only a session that is gone is dropped (3.7.1: in 3.5.0 any error dropped it, and Apple Music's empty timelines
        // kept making the music bubble vanish for two minutes at a time)
        if (!Morta(e))
        {
            if (DateTime.Now - _ultimoErro > TimeSpan.FromMinutes(1)) Log.Escrever($"mídia: {onde}: {e.Message} (sessão {a.Rastro.Chave} segue)");
            _ultimoErro = DateTime.Now;
            return;
        }
        Log.Escrever($"mídia: {onde}: {e.Message} (sessão {a.Rastro.Chave} largada)");
        _quebradas[a.Identidade] = DateTime.Now;
        Soltar(a);
        _sessoes.Remove(a);
        if (_escolhida == a) _escolhida = null;
        Ressincronizar();
    }

    private DateTime _ultimoErro;

    /// <summary>The errors of a session whose app went away: the COM proxy disconnected or the server unavailable.</summary>
    internal static bool Morta(Exception e) => e is ObjectDisposedException or InvalidComObjectException
        || e is COMException c && (uint)c.HResult is 0x80010108 or 0x800706BA or 0x800706BE or 0x800706BF or 0x80010012;

    private void PropsMudaram(Acompanhada a)
    {
        if (_parado || a != _escolhida) return;
        // chega mais de uma vez por faixa (às vezes o título antes da capa): espera assentar
        _espera.Stop();
        _espera.Start();
    }

    private static Instante? LerInstante(Sessao s, bool tocando, double taxa)
    {
        var t = s.GetTimelineProperties();
        if (t == null) return null;
        return new Instante(t.StartTime, t.EndTime, t.Position, t.LastUpdatedTime, tocando, taxa);
    }

    private void AtualizarEstado(Acompanhada a, bool novaLinha)
    {
        if (_atv == null) return;
        try
        {
            var info = a.Sessao.GetPlaybackInfo();
            if (info?.Controls is not { } c) return;
            bool tocando = info.PlaybackStatus == Status.Playing;
            double taxa = info.PlaybackRate ?? 1.0;
            if (novaLinha && LerInstante(a.Sessao, tocando, taxa) is { } instante) _atv.DefinirLinha(new Linha(instante));
            else _atv.Linha.MudouEstado(tocando, taxa, DateTimeOffset.Now);
            bool tocarPausar = c.IsPlayPauseToggleEnabled || (tocando ? c.IsPauseEnabled : c.IsPlayEnabled);
            _atv.DefinirEstado(tocando, c.IsPreviousEnabled, c.IsNextEnabled, tocarPausar,
                c.IsPlaybackPositionEnabled && _atv.Linha.Atual.TemDuracao);
            _atv.LinhaMudou();
        }
        catch (Exception e) { Quebrou(a, "estado", e); }
    }

    private async void CarregarPropriedades()
    {
        var a = _escolhida;
        if (a == null || _atv == null) return;
        int geracao = ++_geracaoProps;
        GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
        IAsyncOperation<GlobalSystemMediaTransportControlsSessionMediaProperties>? op = null;
        try
        {
            // the operation, the properties and the cover reference are new on every call: released when done
            op = a.Sessao.TryGetMediaPropertiesAsync();
            props = await op;
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
        finally
        {
            try { WinRtRelease.Now(props?.Thumbnail); } catch (Exception) { }
            WinRtRelease.Now(props);
            WinRtRelease.Now(op);
        }
    }

    // ------------------------------------------------------------------ comandos

    private async void Comando(Func<Sessao, IAsyncOperation<bool>> acao)
    {
        var a = _escolhida;
        if (a == null) return;
        IAsyncOperation<bool>? op = null;
        try { op = acao(a.Sessao); await op; }
        catch (Exception e) { Log.Escrever("mídia: comando: " + e.Message); }
        finally { WinRtRelease.Now(op); }
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
            var op = a.Sessao.TryChangePlaybackPositionAsync(alvo.Ticks);
            bool ok = await op;
            WinRtRelease.Now(op);
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
    [Label("Minutos que a mídia pausada fica", "Minutes paused media stays")]
    [JsonPropertyName("pausadaMinutos")] public double PausadaMinutos { get; set; } = 10;
    /// <summary>Barras animadas na pílula e no cartão (escuta o que sai no dispositivo padrão).</summary>
    [Label("Visualizador", "Visualizer")]
    [JsonPropertyName("visualizador")] public bool Visualizador { get; set; } = true;
    /// <summary>Quanto cada dente da rodinha muda o volume, em por cento.</summary>
    [Label("Passo do volume na rodinha (%)", "Scroll wheel volume step (%)")]
    [JsonPropertyName("passoVolume")] public int PassoVolume { get; set; } = 2;
}
