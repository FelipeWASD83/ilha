using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace Ilha.Mascote;

/// <summary>
/// O mascote (pixel art exportada do Aseprite) morando dentro da ilha. Fica na pílula quando a área de trabalho está
/// na frente e não tem mais nada, dorme depois de um tempo sem mexer no mouse e no teclado, dança com música tocando
/// (qualquer player que fale com o Windows, pelo SMTC) e trabalha quando o Claude Code está trabalhando (pela fila de
/// eventos dos hooks, só leitura). Tudo por evento: SMTC, FileSystemWatcher e troca de janela; o único relógio é o do
/// sono, e só roda com ele aparecendo sozinho na área de trabalho.
/// </summary>
[Modulo("mascote", "Mascote", LigadoPorPadrao = false)]
internal sealed partial class ModuloMascote : Modulo
{
    private ConfigMascote _cfg = new();
    private AtividadeMascote? _atv;
    private Biblioteca? _bib;
    private Contexto _ctx = new();
    private bool _parou;

    // Claude Code
    private readonly SessoesDoClaude _claude = new();
    private readonly LeitorDeLinhas _leitor = new();
    private FileSystemWatcher? _vigiaEventos;
    private string _arquivoVigiado = "";
    private System.Windows.Forms.Timer? _esperaEventos, _vencimento;

    // folhas
    private FileSystemWatcher? _vigiaFolhas;
    private string _pastaVigiada = "";
    private System.Windows.Forms.Timer? _esperaFolhas;

    // música
    private GlobalSystemMediaTransportControlsSessionManager? _gerente;
    private readonly List<GlobalSystemMediaTransportControlsSession> _sessoes = new();
    private bool _tocando;

    // sono
    private System.Windows.Forms.Timer? _relogioSono;

    public override Type? TipoConfig => typeof(ConfigMascote);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigMascote>(cfg);
        _ctx = S.ContextoAtual();
        _atv = new AtividadeMascote();
        S.Ilha.Adicionar(_atv);
        _esperaEventos = UmaVez(40, LerEventos);
        _esperaFolhas = UmaVez(600, CarregarFolhas);
        _vencimento = UmaVez(1000, Reavaliar);
        _relogioSono = UmaVez(30000, Reavaliar);
        CarregarFolhas();
        VigiarEventos();
        LigarMidia();
        Reavaliar();
    }

    private static System.Windows.Forms.Timer UmaVez(int ms, Action acao)
    {
        var t = new System.Windows.Forms.Timer { Interval = ms };
        t.Tick += (_, _) => { t.Stop(); acao(); };
        return t;
    }

    private void Rearmar(System.Windows.Forms.Timer? t, double ms)
    {
        // evento do sistema que chegou depois do Parar: os relógios já foram descartados
        if (t == null || _parou) return;
        t.Stop();
        t.Interval = (int)Math.Clamp(ms, 1, int.MaxValue);
        t.Start();
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigMascote>(cfg);
        if (Normal(_cfg.PastaFolhas) != _pastaVigiada) CarregarFolhas();
        if (Normal(_cfg.ArquivoEventos) != _arquivoVigiado) VigiarEventos();
        Reavaliar();
    }

    public override void Contexto(Contexto ctx)
    {
        _ctx = ctx;
        // os eventos do SMTC às vezes não chegam: a troca de janela é uma ressincronização barata
        ConferirTocando(avisar: false);
        Reavaliar();
    }

    public override void Parar()
    {
        _parou = true;
        foreach (var t in new[] { _esperaEventos, _esperaFolhas, _vencimento, _relogioSono }) { t?.Stop(); t?.Dispose(); }
        _vigiaEventos?.Dispose();
        _vigiaEventos = null;
        _vigiaFolhas?.Dispose();
        _vigiaFolhas = null;
        SoltarMidia();
        _atv?.Parar();
    }

    private static string Normal(string? caminho) =>
        string.IsNullOrWhiteSpace(caminho) ? "" : Environment.ExpandEnvironmentVariables(caminho.Trim().Trim('"'));

    // ------------------------------------------------------------------ estado

    private void Reavaliar()
    {
        if (_parou || _atv == null) return;
        double agora = SessoesDoClaude.Agora();
        bool claude = _arquivoVigiado.Length > 0 && _claude.Trabalhando(agora);
        bool musica = _tocando;
        bool temFolhas = _bib is { Quantas: > 0 };
        bool areaDeTrabalho = _ctx.Desktop && !_ctx.Competitivo;
        bool aparece = Escolha.Aparece(temFolhas, claude, musica, areaDeTrabalho) && !_ctx.Competitivo;

        // o sono só importa com ele parado na área de trabalho; fora disso o relógio nem roda
        var dormir = TimeSpan.FromMinutes(Math.Max(0, _cfg.DormirDepoisMinutos));
        bool vigiarSono = aparece && areaDeTrabalho && !claude && !musica && dormir > TimeSpan.Zero;
        var ocioso = vigiarSono ? Ocioso() : TimeSpan.Zero;
        var estado = Escolha.Estado(claude, musica, ocioso, dormir);
        _atv.Atualizar(estado, aparece);

        if (vigiarSono)
            // acordado: confere quando o sono chegaria (no mínimo a cada 30 s); dormindo: a cada 5 s, para acordar logo
            Rearmar(_relogioSono, estado == EstadoMascote.Dormindo ? 5000 : Math.Max(30000, (dormir - ocioso).TotalMilliseconds + 500));
        else _relogioSono?.Stop();

        if (claude && _claude.Vencimento(agora) is double venc) Rearmar(_vencimento, (venc - agora) * 1000 + 500);
        else _vencimento?.Stop();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize, dwTime; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("kernel32.dll")] private static extern uint GetTickCount();

    /// <summary>Quanto tempo sem mouse e sem teclado (o contador do Windows, de 32 bits, dá a volta em 49 dias).</summary>
    internal static TimeSpan Ocioso()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref li)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked(GetTickCount() - li.dwTime));
    }

    // ------------------------------------------------------------------ folhas

    private void CarregarFolhas()
    {
        if (_parou) return;
        string pasta = Normal(_cfg.PastaFolhas);
        if (pasta != _pastaVigiada)
        {
            _vigiaFolhas?.Dispose();
            _vigiaFolhas = null;
            _pastaVigiada = pasta;
            if (pasta.Length > 0 && Directory.Exists(pasta))
            {
                try
                {
                    // reexportou do Aseprite: recarrega sozinho (espera a rajada de arquivos acabar)
                    _vigiaFolhas = new FileSystemWatcher(pasta) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                    FileSystemEventHandler h = (_, _) => S.NaInterface(() => Rearmar(_esperaFolhas, 600));
                    _vigiaFolhas.Changed += h;
                    _vigiaFolhas.Created += h;
                    _vigiaFolhas.Deleted += h;
                    _vigiaFolhas.Renamed += (_, _) => S.NaInterface(() => Rearmar(_esperaFolhas, 600));
                    _vigiaFolhas.EnableRaisingEvents = true;
                }
                catch (Exception e) when (e is ArgumentException or IOException) { Log.Escrever("mascote: não deu para vigiar a pasta das folhas: " + e.Message); }
            }
        }
        try { _bib = pasta.Length > 0 ? Biblioteca.Carregar(pasta) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Escrever("mascote: a pasta das folhas não abriu: " + e.Message);
            _bib = null;
        }
        if (pasta.Length > 0) Log.Escrever($"mascote: {_bib?.Quantas ?? 0} animações ({string.Join(", ", _bib?.Nomes ?? Array.Empty<string>())})");
        _atv?.Folhas(_bib);
        Reavaliar();
    }

    // ------------------------------------------------------------------ Claude Code

    private void VigiarEventos()
    {
        _vigiaEventos?.Dispose();
        _vigiaEventos = null;
        _arquivoVigiado = Normal(_cfg.ArquivoEventos);
        _leitor.Recomecar();
        if (_arquivoVigiado.Length == 0) { Reavaliar(); return; }
        string? pasta = Path.GetDirectoryName(_arquivoVigiado);
        if (pasta != null && Directory.Exists(pasta))
        {
            try
            {
                _vigiaEventos = new FileSystemWatcher(pasta, Path.GetFileName(_arquivoVigiado))
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 16384,
                };
                FileSystemEventHandler h = (_, _) => S.NaInterface(() => Rearmar(_esperaEventos, 40));
                _vigiaEventos.Changed += h;
                _vigiaEventos.Created += h;
                _vigiaEventos.Renamed += (_, _) => S.NaInterface(() => Rearmar(_esperaEventos, 40));
                _vigiaEventos.Error += (_, _) => S.NaInterface(() => Rearmar(_esperaEventos, 40));
                _vigiaEventos.EnableRaisingEvents = true;
            }
            catch (Exception e) when (e is ArgumentException or IOException) { Log.Escrever("mascote: não deu para vigiar a fila de eventos: " + e.Message); }
        }
        else Log.Escrever("mascote: a pasta da fila de eventos não existe: " + pasta);
        LerEventos();
    }

    private void LerEventos()
    {
        if (_parou || _arquivoVigiado.Length == 0) return;
        foreach (var l in _leitor.Novas(_arquivoVigiado)) _claude.Receber(l);
        Reavaliar();
    }

    // ------------------------------------------------------------------ música (SMTC)

    private async void LigarMidia()
    {
        try
        {
            var g = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().ConfigureAwait(false);
            S.NaInterface(() =>
            {
                if (_parou) return;
                _gerente = g;
                _gerente.SessionsChanged += AoMudarSessoes;
                RefazerSessoes();
            });
        }
        catch (Exception e) { Log.Escrever("mascote: SMTC indisponível: " + e.Message); }
    }

    private void AoMudarSessoes(GlobalSystemMediaTransportControlsSessionManager s, SessionsChangedEventArgs a) => S.NaInterface(RefazerSessoes);

    private void AoMudarTocando(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs a) => S.NaInterface(() => ConferirTocando(avisar: true));

    private void RefazerSessoes()
    {
        if (_parou || _gerente == null) return;
        foreach (var s in _sessoes) s.PlaybackInfoChanged -= AoMudarTocando;
        _sessoes.Clear();
        try
        {
            // guardar as sessões: coletadas, os eventos param de chegar calados
            foreach (var s in _gerente.GetSessions())
            {
                s.PlaybackInfoChanged += AoMudarTocando;
                _sessoes.Add(s);
            }
        }
        catch (Exception e) { Log.Escrever("mascote: sessões do SMTC: " + e.Message); }
        ConferirTocando(avisar: true);
    }

    private void ConferirTocando(bool avisar)
    {
        if (_gerente == null) return;
        bool tocando = false;
        foreach (var s in _sessoes)
        {
            try { if (s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) { tocando = true; break; } }
            catch (Exception) { /* sessão que fechou no meio da conta */ }
        }
        if (tocando == _tocando) return;
        _tocando = tocando;
        if (avisar) Reavaliar();
    }

    private void SoltarMidia()
    {
        foreach (var s in _sessoes) { try { s.PlaybackInfoChanged -= AoMudarTocando; } catch (Exception) { } }
        _sessoes.Clear();
        if (_gerente != null) { try { _gerente.SessionsChanged -= AoMudarSessoes; } catch (Exception) { } }
        _gerente = null;
        _tocando = false;
    }
}

public sealed class ConfigMascote
{
    /// <summary>A pasta das folhas do personagem (PNG + JSON do Aseprite, ou PNG em tira com animacoes.json). Vazia = não aparece.</summary>
    [JsonPropertyName("pastaFolhas")] public string PastaFolhas { get; set; } = "";
    /// <summary>A fila de eventos dos hooks do Claude Code (um JSON por linha). Vazia = sem o "trabalhando".</summary>
    [JsonPropertyName("arquivoEventos")] public string ArquivoEventos { get; set; } = "";
    /// <summary>Minutos sem mouse e teclado até ele dormir na área de trabalho (0 = nunca dorme).</summary>
    [JsonPropertyName("dormirDepoisMinutos")] public double DormirDepoisMinutos { get; set; } = 20;
}
