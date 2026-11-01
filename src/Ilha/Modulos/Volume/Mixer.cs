using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Ilha.Volume;

/// <summary>
/// O volume de cada app na saída padrão. Os avisos de sessão só chegam numa thread MTA e só depois do GetCount no
/// enumerador, então tudo de sessão vive numa thread MTA própria, dona de todos os objetos COM daqui: ela lê as
/// sessões, assina os eventos e aplica os volumes. Os callbacks do sistema só enfileiram trabalho para ela; a lista
/// pronta vai para a interface pelo `despachar`. Parada (sem evento), a thread fica bloqueada na fila: 0% de CPU.
/// O eco do que a ilha mandou (aviso com o contexto dela) não relê nada; o valor dela é confirmado pela leitura que
/// vem na fila depois do Set.
/// </summary>
internal sealed class Mixer : IDisposable
{
    private sealed class Sessao
    {
        public required string Instancia;
        public required IAudioSessionControl2 Controle;
        public required ISimpleAudioVolume Volume;
        public required Eventos Eventos;
        public bool Registrada, MudouDeFora;
        public uint Pid;
        public bool Sistema;
        public string? Caminho, Exe, Exibicao, Descricao;
    }

    private readonly Action<Action> _despachar;
    private readonly BlockingCollection<Action> _fila = new();
    private readonly ConcurrentDictionary<string, float> _volumes = new();
    private volatile string[] _competitivos;
    private int _pedidoLer, _pedidoReler, _pedidoVolume, _processosVelhos = 1;

    // só a thread do mixer mexe daqui para baixo
    private IMMDeviceEnumerator? _enum;
    private IAudioSessionManager2? _gerente;
    private readonly AvisoDeSessao _aviso;
    private bool _avisoRegistrado;
    private string? _aparelho;
    private readonly Dictionary<string, Sessao> _sessoes = new(StringComparer.Ordinal);
    private Dictionary<string, List<Sessao>> _grupos = new();
    private HashSet<uint> _vivos = new();
    private readonly AppsDoSistema _apps = new();
    // até a próxima publicação: apps em que a ilha mexeu (a leitura sai depois do Set) e os que mudaram por fora
    private HashSet<string> _confirmar = new(StringComparer.Ordinal), _deFora = new(StringComparer.Ordinal);
    private Timer? _reabrir;
    private int _tentativas;

    /// <summary>A lista mudou (na thread da interface). Ok false = o mixer não abriu neste aparelho.</summary>
    public event Action<LeituraDoMixer>? Mudou;

    public Mixer(Action<Action> despachar, IEnumerable<string> competitivos)
    {
        _despachar = despachar;
        _competitivos = competitivos.ToArray();
        _aviso = new AvisoDeSessao(this);
    }

    /// <summary>Sobe a thread e abre as sessões do aparelho dado (null = o padrão de multimídia).</summary>
    public void Iniciar(string? aparelho)
    {
        var t = new Thread(Laco) { IsBackground = true, Name = "ilha-volume-mixer" };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        TrocarAparelho(aparelho);
    }

    /// <summary>A saída padrão mudou: as sessões são por aparelho, então tudo é relido no novo.</summary>
    public void TrocarAparelho(string? id) => Enfileirar(() => Abrir(id));

    public void TrocarCompetitivos(IEnumerable<string> lista) => _competitivos = lista.ToArray();

    /// <summary>
    /// Volume de um app (todas as sessões dele). Arrastando, só o último valor de cada app é aplicado e nada é relido;
    /// `confirmar` (o fim do arraste) relê depois do Set e publica o app como confirmado.
    /// </summary>
    public void DefinirVolume(string chave, float escala, bool confirmar = false)
    {
        _volumes[chave] = Math.Clamp(escala, 0f, 1f);
        if (Interlocked.Exchange(ref _pedidoVolume, 1) == 0) Enfileirar(AplicarVolumes);
        // na fila depois do AplicarVolumes que leva este valor (ou de um que já está rodando e não o pegou)
        if (confirmar) Enfileirar(() => { _confirmar.Add(chave); PedirLer(); });
    }

    public void DefinirMudo(string chave, bool mudo) => Enfileirar(() =>
    {
        var ctx = Audio.ContextoDaIlha;
        if (_grupos.TryGetValue(chave, out var ss))
            foreach (var s in ss)
            {
                int hr = s.Volume.SetMute(mudo, ref ctx);
                if (hr != 0) Log.Escrever($"mixer: SetMute 0x{hr:X8}");
            }
        // mesmo sem o app (ou com o Set falhando) a leitura confirma: a ilha passa a mostrar o que o Windows tem
        _confirmar.Add(chave);
        PedirLer();
    });

    public void Dispose()
    {
        try { _fila.CompleteAdding(); } catch { }
    }

    // ---------------------------------------------------------------- fila

    private void Enfileirar(Action a)
    {
        try { _fila.TryAdd(a); } catch { /* já fechou */ }
    }

    /// <summary>Volume, mudo ou estado de uma sessão mudou: relê os valores (vários avisos seguidos viram uma leitura).</summary>
    private void PedirLer(bool processos = false)
    {
        if (processos) Volatile.Write(ref _processosVelhos, 1);
        if (Interlocked.Exchange(ref _pedidoLer, 1) == 0) Enfileirar(Ler);
    }

    /// <summary>Sessão nova ou desconectada: relê a lista do gerente.</summary>
    private void PedirReler()
    {
        if (Interlocked.Exchange(ref _pedidoReler, 1) == 0) Enfileirar(Reler);
    }

    private void Laco()
    {
        try
        {
            foreach (var a in _fila.GetConsumingEnumerable())
            {
                try { a(); }
                catch (Exception e) { Log.Escrever("mixer: " + e.Message); }
            }
        }
        catch (Exception e) { Log.Escrever("mixer: laço: " + e.Message); }
        finally
        {
            PararReabrir();
            try { Fechar(); } catch { }
            Com.Soltar(_enum);
            _enum = null;
        }
    }

    // ---------------------------------------------------------------- na thread do mixer

    /// <summary>Abre as sessões do aparelho. `tentativa` = reabertura depois que o serviço de áudio caiu.</summary>
    private void Abrir(string? id, bool tentativa = false)
    {
        if (!tentativa)
        {
            // saída nova (ou a primeira): conta do zero
            PararReabrir();
            _tentativas = 0;
        }
        Fechar();
        _aparelho = id;
        try
        {
            _enum ??= (IMMDeviceEnumerator)ComDeAudio.NovoEnumerador();
            IMMDevice d;
            int hr = id != null ? _enum.GetDevice(id, out d) : _enum.GetDefaultAudioEndpoint(FluxoAudio.Render, PapelAudio.Multimidia, out d);
            if (hr != 0 || d == null)
            {
                // sem saída nenhuma: lista vazia, sem erro
                Publicar(new List<AppDeSom>(), true);
                return;
            }
            try
            {
                var iid = typeof(IAudioSessionManager2).GUID;
                hr = d.Activate(ref iid, Com.CLSCTX_ALL, IntPtr.Zero, out var obj);
                if (hr != 0)
                {
                    Log.Escrever($"mixer: IAudioSessionManager2 não ativou (0x{hr:X8})");
                    Publicar(new List<AppDeSom>(), false);
                    AgendarReabrir();
                    return;
                }
                _gerente = (IAudioSessionManager2)obj;
            }
            finally { Com.Soltar(d); }
            // registra antes de enumerar para não perder sessão criada no meio; o GetCount do Reler liga os avisos
            hr = _gerente.RegisterSessionNotification(_aviso);
            _avisoRegistrado = hr == 0;
            if (hr != 0) Log.Escrever($"mixer: aviso de sessão nova não registrou (0x{hr:X8})");
            Reler();
        }
        catch (Exception e)
        {
            Log.Escrever("mixer: abrir: " + e.Message);
            Fechar();
            Publicar(new List<AppDeSom>(), false);
            AgendarReabrir();
        }
    }

    /// <summary>
    /// O gerente não abriu ou morreu (o serviço de áudio reiniciando): tenta de novo com espera crescente, que o
    /// serviço leva uns segundos para voltar. Sem uma tentativa marcada, não há timer nenhum.
    /// </summary>
    private void AgendarReabrir()
    {
        if (_reabrir != null) return;
        int ms = LogicaDoMixer.EsperaParaReabrir(_tentativas);
        if (ms < 0)
        {
            Log.Escrever("mixer: o gerente de sessões não voltou; tenta de novo quando a saída padrão mudar");
            return;
        }
        _tentativas++;
        _reabrir = new Timer(_ => Enfileirar(Reabrir), null, ms, Timeout.Infinite);
    }

    private void Reabrir()
    {
        PararReabrir();
        Abrir(_aparelho, tentativa: true);
    }

    private void PararReabrir()
    {
        _reabrir?.Dispose();
        _reabrir = null;
    }

    /// <summary>Uma sessão avisou que o serviço de áudio desligou (chega uma vez por sessão).</summary>
    private void ServicoCaiu()
    {
        if (_gerente == null) return;
        Log.Escrever("mixer: o serviço de áudio desligou; reabre quando ele voltar");
        Fechar();
        Publicar(new List<AppDeSom>(), false);
        _tentativas = 0;
        AgendarReabrir();
    }

    private void Fechar()
    {
        foreach (var s in _sessoes.Values) Soltar(s);
        _sessoes.Clear();
        _grupos = new();
        if (_gerente != null)
        {
            if (_avisoRegistrado) try { _gerente.UnregisterSessionNotification(_aviso); } catch { }
            Com.Soltar(_gerente);
        }
        _gerente = null;
        _avisoRegistrado = false;
    }

    private static void Soltar(Sessao s)
    {
        if (s.Registrada) try { s.Controle.UnregisterAudioSessionNotification(s.Eventos); } catch { }
        s.Registrada = false;
        // Volume e Controle são o mesmo objeto (QueryInterface): soltar uma vez só
        Com.Soltar(s.Controle);
    }

    private void Reler()
    {
        Interlocked.Exchange(ref _pedidoReler, 0);
        if (_gerente == null) return;
        int hr = _gerente.GetSessionEnumerator(out var lista);
        if (hr != 0 || lista == null)
        {
            Log.Escrever($"mixer: GetSessionEnumerator 0x{hr:X8}");
            // o serviço de áudio reiniciou: o gerente morreu junto; reabre quando o serviço voltar
            Fechar();
            Publicar(new List<AppDeSom>(), false);
            AgendarReabrir();
            return;
        }
        _tentativas = 0;
        var vistas = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            lista.GetCount(out int n);
            // o nome do .exe de cada sessão nova sai da lista de processos
            AtualizarProcessos();
            for (int i = 0; i < n; i++)
            {
                if (lista.GetSession(i, out var obj) != 0 || obj == null) continue;
                IAudioSessionControl2? c2 = null;
                bool guardou = false;
                try
                {
                    c2 = (IAudioSessionControl2)obj;
                    if (c2.GetSessionInstanceIdentifier(out var inst) != 0 || string.IsNullOrEmpty(inst)) continue;
                    vistas.Add(inst);
                    if (_sessoes.ContainsKey(inst)) continue;
                    if (c2.GetState(out int estado) == 0 && estado == LogicaDoMixer.Expirada) continue;
                    var s = Nova(inst, c2);
                    if (s == null) continue;
                    _sessoes[inst] = s;
                    guardou = true;
                }
                catch (Exception e) { Log.Escrever("mixer: sessão: " + e.Message); }
                finally
                {
                    if (!guardou) { Com.Soltar(c2); if (!ReferenceEquals(c2, obj)) Com.Soltar(obj); }
                }
            }
        }
        finally { Com.Soltar(lista); }
        foreach (var inst in _sessoes.Keys.Where(k => !vistas.Contains(k)).ToList())
        {
            Soltar(_sessoes[inst]);
            _sessoes.Remove(inst);
        }
        Ler();
    }

    private Sessao? Nova(string inst, IAudioSessionControl2 c2)
    {
        ISimpleAudioVolume vol;
        try { vol = (ISimpleAudioVolume)c2; }
        catch { return null; }
        var s = new Sessao { Instancia = inst, Controle = c2, Volume = vol, Eventos = new Eventos(this) };
        c2.GetProcessId(out s.Pid);
        s.Sistema = c2.IsSystemSoundsSession() == 0;
        if (!s.Sistema)
        {
            s.Exe = _apps.ExeDoPid(s.Pid);
            c2.GetSessionIdentifier(out var id);
            s.Caminho = _apps.Caminho(id, s.Pid, s.Exe, _competitivos);
            s.Descricao = _apps.Descricao(s.Caminho);
            if (c2.GetDisplayName(out var nome) == 0) s.Exibicao = AppsDoSistema.Exibicao(nome);
        }
        s.Registrada = c2.RegisterAudioSessionNotification(s.Eventos) == 0;
        return s;
    }

    private void AtualizarProcessos()
    {
        Volatile.Write(ref _processosVelhos, 0);
        _vivos = new HashSet<uint>(_apps.Processos().Keys);
    }

    private void Ler()
    {
        Interlocked.Exchange(ref _pedidoLer, 0);
        if (Volatile.Read(ref _processosVelhos) == 1) AtualizarProcessos();
        var lidas = new List<SessaoDeSom>();
        var porId = new Dictionary<string, Sessao>(StringComparer.Ordinal);
        foreach (var s in _sessoes.Values.ToList())
        {
            int estado;
            if (s.Controle.GetState(out estado) != 0) estado = LogicaDoMixer.Inativa;
            if (estado == LogicaDoMixer.Expirada)
            {
                Soltar(s);
                _sessoes.Remove(s.Instancia);
                continue;
            }
            float v = s.Volume.GetMasterVolume(out float e) == 0 ? e : 1f;
            bool m = s.Volume.GetMute(out bool mm) == 0 && mm;
            lidas.Add(new SessaoDeSom(s.Instancia, s.Pid, s.Sistema, s.Caminho, s.Exe, s.Exibicao, s.Descricao,
                estado, v, m, s.Pid == 0 || _vivos.Contains(s.Pid)));
            porId[s.Instancia] = s;
        }
        var apps = LogicaDoMixer.Agrupar(lidas);
        var grupos = new Dictionary<string, List<Sessao>>();
        foreach (var l in lidas)
        {
            string ch = LogicaDoMixer.Chave(l);
            var s = porId[l.Id];
            if (!grupos.TryGetValue(ch, out var g)) grupos[ch] = g = new();
            g.Add(s);
            if (s.MudouDeFora) { _deFora.Add(ch); s.MudouDeFora = false; }
        }
        _grupos = grupos;
        Publicar(apps, true);
    }

    private void AplicarVolumes()
    {
        Interlocked.Exchange(ref _pedidoVolume, 0);
        var ctx = Audio.ContextoDaIlha;
        foreach (var chave in _volumes.Keys.ToList())
        {
            if (!_volumes.TryRemove(chave, out float v) || !_grupos.TryGetValue(chave, out var ss)) continue;
            foreach (var s in ss)
            {
                int hr = s.Volume.SetMasterVolume(v, ref ctx);
                if (hr != 0) Log.Escrever($"mixer: SetMasterVolume 0x{hr:X8}");
            }
        }
    }

    /// <summary>Na thread do mixer: leva junto (e zera) os confirmados e os mexidos por fora desde a anterior.</summary>
    private void Publicar(List<AppDeSom> apps, bool ok)
    {
        var leitura = new LeituraDoMixer(apps, ok, _confirmar, _deFora);
        _confirmar = new(StringComparer.Ordinal);
        _deFora = new(StringComparer.Ordinal);
        _despachar(() =>
        {
            if (_fila.IsAddingCompleted) return;
            try { Mudou?.Invoke(leitura); }
            catch (Exception e) { Log.Escrever("mixer: lista: " + e); }
        });
    }

    /// <summary>Uma sessão mudou de volume ou mudo por fora da ilha: marca para a próxima leitura levar o app em `DeFora`.</summary>
    private void MudouDeFora(Eventos ev) => Enfileirar(() =>
    {
        foreach (var s in _sessoes.Values)
            if (s.Eventos == ev) s.MudouDeFora = true;
        PedirLer();
    });

    /// <summary>O GUID de contexto de um aviso de sessão (ponteiro nulo = sem contexto).</summary>
    internal static Guid LerContexto(IntPtr contexto) =>
        contexto == IntPtr.Zero ? Guid.Empty : System.Runtime.InteropServices.Marshal.PtrToStructure<Guid>(contexto);

    private void NomeMudou(Eventos ev, string? nome) => Enfileirar(() =>
    {
        foreach (var s in _sessoes.Values)
            if (s.Eventos == ev && !s.Sistema) s.Exibicao = AppsDoSistema.Exibicao(nome);
        PedirLer();
    });

    // ---------------------------------------------------------------- callbacks (threads do sistema)

    private sealed class AvisoDeSessao : IAudioSessionNotification
    {
        private readonly Mixer _m;
        public AvisoDeSessao(Mixer m) => _m = m;

        public int OnSessionCreated(IntPtr sessao)
        {
            try { _m.PedirReler(); } catch { }
            return 0;
        }
    }

    private sealed class Eventos : IAudioSessionEvents
    {
        private readonly Mixer _m;
        public Eventos(Mixer m) => _m = m;

        public int OnDisplayNameChanged(string? nome, IntPtr contexto)
        {
            try { _m.NomeMudou(this, nome); } catch { }
            return 0;
        }

        public int OnIconPathChanged(string? caminho, IntPtr contexto) => 0;

        public int OnSimpleVolumeChanged(float escala, bool mudo, IntPtr contexto)
        {
            try
            {
                // eco do Set da ilha: nada a reler (a ilha já mostra o valor; a confirmação vem da leitura depois do Set)
                if (!Audio.DaIlha(LerContexto(contexto))) _m.MudouDeFora(this);
            }
            catch { }
            return 0;
        }

        public int OnChannelVolumeChanged(uint canais, IntPtr volumes, uint canalMudado, IntPtr contexto) => 0;
        public int OnGroupingParamChanged(IntPtr grupo, IntPtr contexto) => 0;

        public int OnStateChanged(int estado)
        {
            // parou ou começou a tocar; expirada sai na leitura
            try { _m.PedirLer(processos: true); } catch { }
            return 0;
        }

        public int OnSessionDisconnected(int motivo)
        {
            try
            {
                if (motivo == LogicaDoMixer.MotivoServicoDesligou) _m.Enfileirar(_m.ServicoCaiu);
                else _m.PedirReler();
            }
            catch { }
            return 0;
        }
    }
}
