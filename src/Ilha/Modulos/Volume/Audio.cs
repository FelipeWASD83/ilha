using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ilha.Volume;

/// <summary>
/// O volume do aparelho padrão (render, multimídia), por evento, e a lista de saídas. Os objetos COM nascem e são
/// usados só na thread da interface; os callbacks do sistema chegam em outras threads e só anotam e despacham
/// (`despachar`), sem registrar nem soltar nada lá dentro.
/// </summary>
internal sealed class Audio : IDisposable
{
    // contexto das mudanças feitas pela ilha: o eco delas chega no OnNotify com este GUID e não abre a ilha de novo
    public static readonly Guid ContextoDaIlha = new("6c1f3a9e-2b7d-4e58-9a41-7d0e5b3c8f12");

    private readonly Action<Action> _despachar;
    private IMMDeviceEnumerator? _enum;
    private IAudioEndpointVolume? _vol;
    private IPolicyConfig? _politica;
    private readonly Notificacoes _notif;
    private readonly AvisoDeVolume _avisoVol;
    private bool _notifRegistrada, _volRegistrado;
    private readonly Dictionary<string, uint> _estados = new(StringComparer.OrdinalIgnoreCase);

    // último volume que chegou pelo callback (escrito na thread do sistema, lido na da interface)
    private readonly object _trava = new();
    private float _escalaNova;
    private bool _mudoNovo, _deFora, _pendente;

    public string? IdPadrao { get; private set; }
    public float Escala { get; private set; }
    public bool Mudo { get; private set; }
    public float Db { get; private set; } = float.NegativeInfinity;
    public bool TemAparelho => _vol != null;
    public List<Saida> Saidas { get; private set; } = new();
    /// <summary>IPolicyConfig respondeu ao QueryInterface: dá para trocar a saída pela ilha.</summary>
    public bool PodeTrocar => _politica != null;

    /// <summary>Volume ou mudo mudou; true = não foi a ilha que mudou.</summary>
    public event Action<bool>? VolumeMudou;
    /// <summary>A lista de saídas ou o padrão mudou.</summary>
    public event Action? ListaMudou;
    /// <summary>A saída padrão (multimídia) trocou; IdPadrao já é a nova (null = nenhuma).</summary>
    public event Action? PadraoMudou;
    /// <summary>Um aparelho conectou (true) ou desconectou (false); o id é o do endpoint que mudou.</summary>
    public event Action<string, bool>? Conexao;

    public Audio(Action<Action> despachar)
    {
        _despachar = despachar;
        _notif = new Notificacoes(this);
        _avisoVol = new AvisoDeVolume(this);
    }

    /// <summary>Liga tudo (thread da interface). Falha em pedaço opcional (IPolicyConfig) não impede o resto.</summary>
    public void Iniciar()
    {
        _enum = (IMMDeviceEnumerator)ComDeAudio.NovoEnumerador();
        try
        {
            _politica = (IPolicyConfig)new PolicyConfigClientCom();
            Log.Escrever("volume: IPolicyConfig respondeu (troca de saída disponível)");
        }
        catch (Exception e)
        {
            _politica = null;
            Log.Escrever("volume: IPolicyConfig não respondeu, a troca de saída fica desligada: " + e.Message);
        }
        LerEstados();
        AtualizarLista();
        TrocarPadrao();
        int hr = _enum.RegisterEndpointNotificationCallback(_notif);
        _notifRegistrada = hr == 0;
        if (hr != 0) Log.Escrever($"volume: aviso de aparelho não registrou (0x{hr:X8})");
    }

    private void LerEstados()
    {
        _estados.Clear();
        if (_enum == null || _enum.EnumAudioEndpoints(FluxoAudio.Render, EstadoAparelho.Todos, out var col) != 0) return;
        try
        {
            col.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var d) != 0) continue;
                try
                {
                    if (d.GetId(out var id) == 0 && d.GetState(out uint st) == 0) _estados[id] = st;
                }
                finally { Com.Soltar(d); }
            }
        }
        finally { Com.Soltar(col); }
    }

    /// <summary>Endpoints de saída ativos com os nomes e o que serve para juntar os do mesmo aparelho.</summary>
    public static List<Endpoint> ListarAtivos(IMMDeviceEnumerator en)
    {
        var lista = new List<Endpoint>();
        if (en.EnumAudioEndpoints(FluxoAudio.Render, EstadoAparelho.Ativo, out var col) != 0) return lista;
        try
        {
            col.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var d) != 0) continue;
                try
                {
                    if (d.GetId(out var id) != 0) continue;
                    if (d.OpenPropertyStore(Com.STGM_READ, out var loja) != 0) continue;
                    try
                    {
                        string nome = Com.LerTexto(loja, Propriedades.NomeAmigavel) ?? id;
                        string aparelho = Com.LerTexto(loja, Propriedades.NomeDoAparelho) ?? nome;
                        string desc = Com.LerTexto(loja, Propriedades.Descricao) ?? "";
                        string en2 = Com.LerTexto(loja, Propriedades.Enumerador) ?? "";
                        var cont = Com.LerGuid(loja, Propriedades.Conteiner) ?? Guid.Empty;
                        uint formato = Com.LerNumero(loja, Propriedades.Formato) ?? 0;
                        lista.Add(new Endpoint(id, nome, aparelho, desc, en2, cont, formato));
                    }
                    finally { Com.Soltar(loja); }
                }
                finally { Com.Soltar(d); }
            }
        }
        finally { Com.Soltar(col); }
        return lista;
    }

    public void AtualizarLista()
    {
        if (_enum == null) return;
        try { Saidas = Logica.Juntar(ListarAtivos(_enum)); }
        catch (Exception e) { Log.Escrever("volume: lista de saídas: " + e.Message); }
        ListaMudou?.Invoke();
    }

    /// <summary>O padrão mudou (ou é a primeira vez): solta o callback do aparelho antigo e registra no novo.</summary>
    public void TrocarPadrao()
    {
        if (_enum == null) return;
        SoltarVolume();
        IdPadrao = null;
        if (_enum.GetDefaultAudioEndpoint(FluxoAudio.Render, PapelAudio.Multimidia, out var d) != 0)
        {
            // nenhuma saída (tudo desconectado)
            Escala = 0;
            Mudo = false;
            Db = float.NegativeInfinity;
            ListaMudou?.Invoke();
            PadraoMudou?.Invoke();
            return;
        }
        try
        {
            if (d.GetId(out var id) == 0) IdPadrao = id;
            var iid = Com.IID_IAudioEndpointVolume;
            if (d.Activate(ref iid, Com.CLSCTX_ALL, IntPtr.Zero, out var obj) == 0)
            {
                _vol = (IAudioEndpointVolume)obj;
                Reler();
                _volRegistrado = _vol.RegisterControlChangeNotify(_avisoVol) == 0;
                if (!_volRegistrado) Log.Escrever("volume: callback de volume não registrou");
            }
        }
        catch (Exception e) { Log.Escrever("volume: aparelho padrão: " + e.Message); SoltarVolume(); }
        finally { Com.Soltar(d); }
        ListaMudou?.Invoke();
        PadraoMudou?.Invoke();
    }

    private void Reler()
    {
        if (_vol == null) return;
        if (_vol.GetMasterVolumeLevelScalar(out float e) == 0) Escala = e;
        if (_vol.GetMute(out bool m) == 0) Mudo = m;
        Db = _vol.GetMasterVolumeLevel(out float db) == 0 ? db : float.NegativeInfinity;
    }

    private void SoltarVolume()
    {
        if (_vol != null)
        {
            if (_volRegistrado) try { _vol.UnregisterControlChangeNotify(_avisoVol); } catch { }
            Com.Soltar(_vol);
        }
        _vol = null;
        _volRegistrado = false;
    }

    public void DefinirEscala(float escala)
    {
        if (_vol == null) return;
        escala = Math.Clamp(escala, 0f, 1f);
        var ctx = ContextoDaIlha;
        int hr = _vol.SetMasterVolumeLevelScalar(escala, ref ctx);
        if (hr != 0) { Log.Escrever($"volume: SetMasterVolumeLevelScalar 0x{hr:X8}"); return; }
        Reler();
    }

    public void DefinirMudo(bool mudo)
    {
        if (_vol == null) return;
        var ctx = ContextoDaIlha;
        int hr = _vol.SetMute(mudo, ref ctx);
        if (hr != 0) { Log.Escrever($"volume: SetMute 0x{hr:X8}"); return; }
        Reler();
    }

    /// <summary>
    /// Troca o padrão nos três papéis (como o painel de som faz). Falhou num papel: volta os anteriores para o aparelho
    /// de antes (nada fica meio trocado) e devolve false. Só desliga a troca quando a interface não serve; erro do
    /// aparelho (saiu de alcance, desativado) só recarrega a lista.
    /// </summary>
    public bool TrocarSaida(string id)
    {
        if (_politica == null || _enum == null) return false;
        var papeis = new[] { PapelAudio.Console, PapelAudio.Multimidia, PapelAudio.Comunicacoes };
        var antigos = new string?[papeis.Length];
        for (int i = 0; i < papeis.Length; i++)
        {
            if (_enum.GetDefaultAudioEndpoint(FluxoAudio.Render, papeis[i], out var d) != 0) continue;
            try { if (d.GetId(out var idAntigo) == 0) antigos[i] = idAntigo; }
            finally { Com.Soltar(d); }
        }
        for (int i = 0; i < papeis.Length; i++)
        {
            bool quebrou;
            try
            {
                int hr = _politica.SetDefaultEndpoint(id, papeis[i]);
                if (hr == 0) continue;
                quebrou = ErroDeInterface(hr);
                Log.Escrever($"volume: SetDefaultEndpoint({papeis[i]}) devolveu 0x{hr:X8}{(quebrou ? "; troca de saída desligada" : "")}");
            }
            catch (Exception e)
            {
                Log.Escrever($"volume: SetDefaultEndpoint({papeis[i]}): {e.Message}; troca de saída desligada");
                quebrou = true;
            }
            for (int j = 0; j < i; j++)
                if (antigos[j] != null) try { _politica.SetDefaultEndpoint(antigos[j]!, papeis[j]); } catch { }
            if (quebrou) DesligarTroca();
            AtualizarLista();
            return false;
        }
        return true;
    }

    /// <summary>E_NOINTERFACE ou E_NOTIMPL: a interface (não documentada) não serve neste Windows.</summary>
    internal static bool ErroDeInterface(int hr) => hr == unchecked((int)0x80004002) || hr == unchecked((int)0x80004001);

    private void DesligarTroca()
    {
        Com.Soltar(_politica);
        _politica = null;
        ListaMudou?.Invoke();
    }

    // ---------------------------------------------------------------- callbacks (threads do sistema)

    /// <summary>AUDIO_VOLUME_NOTIFICATION_DATA: GUID guidEventContext; BOOL bMuted; float fMasterVolume; UINT nChannels; ...</summary>
    internal static (Guid Contexto, bool Mudo, float Escala) LerNotificacao(IntPtr dados) =>
        (Marshal.PtrToStructure<Guid>(dados), Marshal.ReadInt32(dados, 16) != 0, BitConverter.Int32BitsToSingle(Marshal.ReadInt32(dados, 20)));

    /// <summary>A mudança veio da ilha: deste módulo ou da rodinha em cima da mídia (que tem o contexto dela).</summary>
    internal static bool DaIlha(Guid contexto) =>
        contexto == ContextoDaIlha || contexto == global::Ilha.Midia.VolumeDoSistema.Contexto;

    internal void ChegouVolume(float escala, bool mudo, Guid contexto)
    {
        bool postar;
        lock (_trava)
        {
            _escalaNova = escala;
            _mudoNovo = mudo;
            // várias notificações juntas (tecla segurada): basta uma de fora para abrir a ilha
            _deFora |= !DaIlha(contexto);
            postar = !_pendente;
            _pendente = true;
        }
        if (postar) _despachar(AplicarVolume);
    }

    private void AplicarVolume()
    {
        bool deFora;
        lock (_trava)
        {
            Escala = _escalaNova;
            Mudo = _mudoNovo;
            deFora = _deFora;
            _deFora = false;
            _pendente = false;
        }
        if (_vol != null && _vol.GetMasterVolumeLevel(out float db) == 0) Db = db;
        VolumeMudou?.Invoke(deFora);
    }

    private void ChegouEstado(string id, uint estado) => _despachar(() =>
    {
        uint antes = _estados.TryGetValue(id, out var a) ? a : EstadoAparelho.Ausente;
        _estados[id] = estado;
        var t = Logica.Classificar(antes, estado);
        AtualizarLista();
        if (t != Transicao.Nada) Conexao?.Invoke(id, t == Transicao.Conectou);
    });

    private void ChegouPadrao(FluxoAudio fluxo, PapelAudio papel)
    {
        // chega uma vez por papel; só o de multimídia interessa
        if (fluxo != FluxoAudio.Render || papel != PapelAudio.Multimidia) return;
        _despachar(TrocarPadrao);
    }

    private void ChegouLista() => _despachar(() => { LerEstados(); AtualizarLista(); });

    /// <summary>Nome para o aviso de conexão: o da linha que contém o endpoint (o do aparelho, não "Fones de ouvido (...)").</summary>
    public string NomeDoAparelho(string id)
    {
        foreach (var s in Saidas) if (s.Contem(id)) return Logica.SemHandsFree(s.Aparelho);
        if (_enum != null && _enum.GetDevice(id, out var d) == 0)
        {
            try
            {
                if (d.OpenPropertyStore(Com.STGM_READ, out var loja) == 0)
                    try { return Logica.SemHandsFree(Com.LerTexto(loja, Propriedades.NomeDoAparelho) ?? Com.LerTexto(loja, Propriedades.NomeAmigavel) ?? "Aparelho de áudio"); }
                    finally { Com.Soltar(loja); }
            }
            finally { Com.Soltar(d); }
        }
        return "Aparelho de áudio";
    }

    public void Dispose()
    {
        SoltarVolume();
        if (_enum != null && _notifRegistrada) try { _enum.UnregisterEndpointNotificationCallback(_notif); } catch { }
        _notifRegistrada = false;
        Com.Soltar(_politica);
        _politica = null;
        Com.Soltar(_enum);
        _enum = null;
    }

    private sealed class Notificacoes : IMMNotificationClient
    {
        private readonly Audio _a;
        public Notificacoes(Audio a) => _a = a;

        public int OnDeviceStateChanged(string id, uint novoEstado) { _a.ChegouEstado(id, novoEstado); return 0; }
        public int OnDeviceAdded(string id) { _a.ChegouLista(); return 0; }
        public int OnDeviceRemoved(string id) { _a.ChegouLista(); return 0; }
        public int OnDefaultDeviceChanged(FluxoAudio fluxo, PapelAudio papel, string? id) { _a.ChegouPadrao(fluxo, papel); return 0; }

        public int OnPropertyValueChanged(string id, ChaveDePropriedade chave)
        {
            // dispara muito (alguns drivers a cada mudança de volume): só o nome interessa
            if (chave.Igual(Propriedades.NomeAmigavel)) _a.ChegouLista();
            return 0;
        }
    }

    private sealed class AvisoDeVolume : IAudioEndpointVolumeCallback
    {
        private readonly Audio _a;
        public AvisoDeVolume(Audio a) => _a = a;

        public int OnNotify(IntPtr dados)
        {
            if (dados == IntPtr.Zero) return 0;
            var (contexto, mudo, escala) = LerNotificacao(dados);
            _a.ChegouVolume(escala, mudo, contexto);
            return 0;
        }
    }
}
