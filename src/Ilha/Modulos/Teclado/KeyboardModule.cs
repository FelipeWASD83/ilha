using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Ilha.Games;

namespace Ilha.Keyboard;

/// <summary>
/// The Razer Huntsman V3 HE (4.2.0, his request on 06/11/2026). Synapse 4 keeps the profiles; the island:
/// - follows the profile in use from Synapse's log and tells when it changes ("SnapTapLess · Snap Tap desligado");
/// - reads the live Snap Tap straight from the keyboard (HID, the command Synapse uses) and can turn it on or off from
///   the card, keeping the key pairs (it lasts until the next profile change, when Synapse writes the profile again);
/// - guards CS2's competitive modes (Valve kicks Snap Tap there): a notice when it is on, or, if he asks, it turns it
///   off for the match and back on after.
/// The menu entry "Teclado" exists while the keyboard is plugged in.
/// </summary>
[Modulo("teclado", "Teclado Razer", English = "Razer keyboard")]
internal sealed class KeyboardModule : Modulo
{
    private ConfigTeclado _cfg = new();
    private KeyboardActivity? _activity;
    private readonly SynapseLog _log = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 2000 };
    private bool _stopped, _present, _reading;
    private int _ticks;
    private string? _lastProfile;
    private DateTime _snapReadAt = DateTime.MinValue;

    internal SynapseProfiles? Profiles { get; private set; }
    internal SnapTapState? Snap { get; private set; }
    internal string? Problem { get; private set; }
    internal bool Busy => _reading;

    // the CS2 guard: the competitive match being watched, and whether the island turned Snap Tap off for it
    private string? _guardedMatch;
    private bool _turnedOff;

    public override Type? TipoConfig => typeof(ConfigTeclado);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigTeclado>(cfg);
        _activity = new KeyboardActivity(S, this);
        S.Ilha.Adicionar(_activity);
        GamesModule.MatchChanged += OnMatch;
        _tick.Tick += (_, _) => Tick();
        _tick.Start();
        Tick();
    }

    public override void ConfigMudou(JsonObject cfg) => _cfg = Ler<ConfigTeclado>(cfg);

    public override void Parar()
    {
        _stopped = true;
        GamesModule.MatchChanged -= OnMatch;
        _tick.Stop();
        _tick.Dispose();
        if (_turnedOff) SetSnap(true, "a ilha fechou");
    }

    private void Tick()
    {
        if (_stopped) return;
        // presence every 10 s (SetupAPI), Synapse's log every 2 s (a size check when nothing changed)
        if (_ticks++ % 5 == 0)
        {
            bool now = RazerDevice.Present();
            if (now != _present)
            {
                _present = now;
                Log.Escrever(now ? "teclado: Huntsman V3 HE presente" : "teclado: Huntsman V3 HE saiu");
                if (now) AddMenu(); else S.Menu.Remover("teclado");
                if (now) ReadSnap(notify: false);
            }
        }
        if (_log.Poll() is { } p)
        {
            Profiles = p;
            if (_lastProfile != null && p.Active != _lastProfile)
            {
                Log.Escrever($"teclado: perfil {p.ActiveName ?? p.Active}");
                // Synapse writes the new profile to the keyboard right after logging it
                var later = new System.Windows.Forms.Timer { Interval = 1500 };
                later.Tick += (_, _) => { later.Stop(); later.Dispose(); ReadSnap(notify: true, profileChanged: true); };
                later.Start();
            }
            _lastProfile = p.Active;
            _activity?.Changed();
            S.Menu.Redesenhar();
        }
        // the Snap Tap can change in Synapse without a profile change: read it again now and then while plugged in
        if (_present && DateTime.Now - _snapReadAt > TimeSpan.FromSeconds(30)) ReadSnap(notify: true);
    }

    private void AddMenu() => S.Menu.Adicionar(new ItemDoMenu
    {
        Id = "teclado", Nome = Lang.T("Teclado", "Keyboard"), Ordem = 66,
        Abrir = () => _activity?.Open(),
        Icone = (dc, p, r) => KeyboardActivity.Glyph(dc, p.G.Fabrica, r, Cor.Branco),
        Detalhe = () => Profiles?.ActiveName,
    });

    /// <summary>Reads the live Snap Tap off the interface thread; a change becomes a notice when `notify`.</summary>
    internal void ReadSnap(bool notify, bool profileChanged = false)
    {
        if (_stopped || _reading || !_present) return;
        _reading = true;
        _snapReadAt = DateTime.Now;
        _ = Task.Run(() =>
        {
            SnapTapState? s = null;
            try { s = RazerDevice.ReadSnapTap(); }
            catch (Exception e) { Log.Escrever("teclado: " + e.Message); }
            S.NaInterface(() =>
            {
                _reading = false;
                if (_stopped) return;
                var before = Snap;
                if (s == null) Problem = Lang.T("O teclado não respondeu.", "The keyboard did not answer.");
                else { Problem = null; Snap = s; }
                // a change Gear Hub reported in the last minute was already told by the peripherals module
                bool changed = s != null && before != null && before.Enabled != s.Enabled
                    && !global::Ilha.Peripherals.PeripheralsModule.SnapTapJustReported(s.Enabled);
                if (notify && s != null && (profileChanged ? _cfg.AvisarPerfil : changed && _cfg.AvisarSnapTap))
                    Notice(profileChanged);
                _activity?.Changed();
                CheckGuard();
            });
        });
    }

    /// <summary>The card's button: Snap Tap on or off in the live slot, pairs untouched.</summary>
    internal void Toggle()
    {
        if (Snap is not { } s) { ReadSnap(notify: false); return; }
        SetSnap(!s.Enabled, "pelo cartão");
    }

    private void SetSnap(bool on, string why)
    {
        if (Snap is not { } s || _reading) return;
        if (on && s.Used.Count == 0)
        {
            Problem = Lang.T("Esse perfil não tem par de teclas no Snap Tap (configure no Synapse).", "This profile has no Snap Tap key pair (set it in Synapse).");
            _activity?.Changed();
            return;
        }
        _reading = true;
        _activity?.Changed();
        Task.Run(() =>
        {
            bool ok = false;
            try { ok = RazerDevice.SetSnapTap(s, on); }
            catch (Exception e) { Log.Escrever("teclado: " + e.Message); }
            Log.Escrever($"teclado: Snap Tap {(on ? "ligado" : "desligado")} {why}: {(ok ? "ok" : "o teclado recusou")}");
            S.NaInterface(() =>
            {
                _reading = false;
                if (!ok) { Problem = Lang.T("O teclado não aceitou a mudança.", "The keyboard did not take the change."); _activity?.Changed(); return; }
                ReadSnap(notify: false);
            });
        });
    }

    private void Notice(bool profileChanged) => S.Ilha.Alertar(new Alerta
    {
        Tipo = "teclado", Som = "", Segundos = 2.5f, PermitidoNoCompetitivo = false,
        Criar = p => KeyboardActivity.NoticeCard(p, profileChanged ? Profiles?.ActiveName : null, Snap),
    });

    // ------------------------------------------------------------------ CS2 guard

    private static bool Competitive(Match m) => m.Game == "cs2" && m.RawMode is "competitive" or "premier" or "scrimcomp2v2";

    private void OnMatch(string game, Match? m)
    {
        if (_stopped || game != "cs2") return;
        if (m != null && m.Outcome == null && Competitive(m))
        {
            string key = $"{m.Start:O}";
            if (_guardedMatch == key) return;
            _guardedMatch = key;
            ReadSnap(notify: false);
            return;
        }
        // the match ended or left: put Snap Tap back if the island took it away
        if (_guardedMatch != null && (m == null || m.Outcome != null))
        {
            _guardedMatch = null;
            if (_turnedOff) { _turnedOff = false; SetSnap(true, "fim da competitiva do CS2"); }
        }
    }

    private bool _warned;

    private void CheckGuard()
    {
        if (_guardedMatch == null) { _warned = false; return; }
        if (Snap is not { Enabled: true }) return;
        if (_cfg.DesligarSnapTapNoCs2)
        {
            _turnedOff = true;
            SetSnap(false, "competitiva do CS2");
            S.Ilha.Alertar(new Alerta
            {
                Tipo = "teclado", Som = "", Segundos = 3, PermitidoNoCompetitivo = true,
                Criar = p => KeyboardActivity.GuardCard(p, turnedOff: true),
            });
        }
        else if (_cfg.AvisarSnapTapNoCs2 && !_warned)
        {
            _warned = true;
            S.Ilha.Alertar(new Alerta
            {
                Tipo = "teclado", Som = "alerta", Segundos = 5, Importante = true, PermitidoNoCompetitivo = true,
                Criar = p => KeyboardActivity.GuardCard(p, turnedOff: false),
            });
        }
    }

    // ------------------------------------------------------------------ self test

    public override string Autoteste()
    {
        var r = new StringBuilder();
        void Ok(string n, bool ok, string d = "") => r.AppendLine((ok ? "ok      " : "FALHOU  ") + n + (d.Length > 0 ? $" ({d})" : ""));

        var pkt = RazerProtocol.Build(7, RazerProtocol.GetSnapTap, new byte[] { 1 });
        byte crc = 0;
        for (int i = 3; i < 89; i++) crc ^= pkt[i];
        Ok("teclado: pacote no formato do Synapse (91 bytes, transação, tamanho, classe, comando, argumento, checksum)",
            pkt.Length == 91 && pkt[0] == 0 && pkt[2] == 7 && pkt[6] == 15 && pkt[7] == 0x02 && pkt[8] == 0xA7 && pkt[9] == 1 && pkt[89] == crc);

        var reply = new byte[91];
        reply[1] = RazerProtocol.StatusOk; reply[2] = 7; reply[6] = 15; reply[7] = 0x02; reply[8] = 0xA7;
        // slot 1, 4 pairs, off, A/D last input, then empty pairs (what the keyboard sent on 06/11/2026)
        new byte[] { 1, 4, 0, 31, 33, 0 }.CopyTo(reply, 9);
        var parsed = RazerProtocol.Parse(reply);
        var snap = RazerProtocol.ParseSnapTap(parsed.Data);
        Ok("teclado: resposta do Snap Tap (desligado, par A/D, pares vazios de enchimento)",
            RazerProtocol.IsOurs(parsed, 7, RazerProtocol.GetSnapTap) && !snap.Enabled && snap.Pairs.Count == 4 && snap.Used.Count == 1
            && RazerProtocol.PairText(snap.Used[0]) == "A ⇄ D", RazerProtocol.PairText(snap.Pairs[0]));
        Ok("teclado: resposta de outra transação ou ocupada não é nossa",
            !RazerProtocol.IsOurs(parsed, 8, RazerProtocol.GetSnapTap) && !RazerProtocol.IsOurs(parsed with { Status = RazerProtocol.StatusBusy }, 7, RazerProtocol.GetSnapTap));
        var args = RazerProtocol.SnapTapArgs(1, true, snap.Used);
        Ok("teclado: ligar grava só o par em uso, como o Synapse (slot, 1 par, ligado, A, D, modo)",
            args.Length == 6 && args[0] == 1 && args[1] == 1 && args[2] == 1 && args[3] == 31 && args[4] == 33 && args[5] == 0);
        Ok("teclado: lista de perfis do teclado", string.Join(",", RazerProtocol.ParseProfileList(new byte[] { 6, 1, 2, 3, 4, 5, 6, 0, 0 })) == "1,2,3,4,5,6");

        const string line = "[2026/11/06 14:08:22.729] info: devices: [{\"serialNumber\":\"X\",\"editionId\":0,\"layoutId\":1,\"activeProfile\":\"c53e67be-1b9f-4e2a-99ea-acabf50922a2\",\"productId\":746,\"profiles\":[{\"name\":\"Good One\",\"guid\":\"17ec12ee-fdef-4b0f-99ae-92db8f3d13e1\"},{\"name\":\"SnapTapLess\",\"guid\":\"c53e67be-1b9f-4e2a-99ea-acabf50922a2\"}]},{\"activeProfile\":\"af06d371-861f-4b98-8d78-bfaef5cfdebf\",\"productId\":999,\"profiles\":[]}]";
        var sp = SynapseLog.ParseLine(line, 746);
        Ok("teclado: log do Synapse dá o perfil em uso e os nomes, só do teclado", sp?.ActiveName == "SnapTapLess" && sp.Profiles.Count == 2, sp?.ActiveName ?? "nada");
        Ok("teclado: linha sem o teclado não muda nada", SynapseLog.ParseLine(line, 123) == null && SynapseLog.ParseLine("info: nada", 746) == null);
        Ok("teclado: competitiva do CS2 é guardada, casual não",
            Competitive(new Match { Game = "cs2", RawMode = "competitive" }) && Competitive(new Match { Game = "cs2", RawMode = "premier" })
            && !Competitive(new Match { Game = "cs2", RawMode = "casual" }) && !Competitive(new Match { Game = "rl", RawMode = "competitive" }));

        // the real keyboard, read only (no write in the self test)
        if (RazerDevice.Present())
        {
            var live = RazerDevice.ReadSnapTap();
            Ok("teclado: Snap Tap lido do teclado de verdade (só leitura)", live != null,
                live == null ? "sem resposta" : $"{(live.Enabled ? "ligado" : "desligado")}, {string.Join(" ", live.Used.Select(RazerProtocol.PairText))}");
            var list = RazerDevice.Command(RazerProtocol.GetProfileList, ReadOnlySpan<byte>.Empty);
            Ok("teclado: perfis guardados no teclado", list != null, list == null ? "" : string.Join(",", RazerProtocol.ParseProfileList(list)));
        }
        else Ok("teclado: teclado de verdade pulado (não está ligado)", true);
        var log = new SynapseLog().Poll();
        Ok("teclado: log de verdade do Synapse", true, log == null ? "sem Synapse ou sem linha" : $"perfil {log.ActiveName}, {log.Profiles.Count} perfis");
        return r.ToString().TrimEnd();
    }
}

public sealed class ConfigTeclado
{
    [Label("Avisar quando o perfil mudar", "Notify when the profile changes")]
    [JsonPropertyName("avisarPerfil")] public bool AvisarPerfil { get; set; } = true;

    [Label("Avisar quando o Snap Tap ligar ou desligar", "Notify when Snap Tap turns on or off")]
    [JsonPropertyName("avisarSnapTap")] public bool AvisarSnapTap { get; set; } = true;

    [Label("Avisar se o Snap Tap estiver ligado numa competitiva do CS2", "Warn if Snap Tap is on in a CS2 competitive match")]
    [Help("Competitivo, Premier e Wingman: a Valve expulsa da partida quem usa Snap Tap nos servidores oficiais.",
        "Competitive, Premier and Wingman: Valve kicks players using Snap Tap on official servers.")]
    [JsonPropertyName("avisarSnapTapNoCs2")] public bool AvisarSnapTapNoCs2 { get; set; } = true;

    [Label("Desligar o Snap Tap sozinho na competitiva do CS2", "Turn Snap Tap off by itself in CS2 competitive")]
    [Help("Volta a ligar quando a partida acaba.", "Turns it back on when the match ends.")]
    [JsonPropertyName("desligarSnapTapNoCs2")] public bool DesligarSnapTapNoCs2 { get; set; }
}
