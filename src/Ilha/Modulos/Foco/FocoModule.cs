using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ilha.Brilho;
using Ilha.LiveWallpaper;
using Ilha.Midia;

namespace Ilha.Focus;

/// <summary>
/// Focus, like the iPhone's (his request on 03/11/2026): modes turned on from the menu or by themselves (a schedule, an
/// app that is open), each changing what it was set to while it is on: the island's sounds, Windows' Do Not Disturb,
/// which alerts show, the wallpaper (Fundo Vivo), the volume and the brightness. Everything a mode changed goes back
/// when it goes off. One mode at a time; the menu also has a Do Not Disturb toggle (the bell with the red slash).
/// <para>
/// Automations: every 30 s and when the window in front changes, the first mode whose schedule is on or whose app is open
/// is the one wanted. When that changes, the wanted mode turns on (over a manual one, like on the iPhone), and the mode
/// an automation turned on turns off when it is no longer wanted. Turning a mode off by hand while its schedule is on
/// keeps it off until the next change.
/// </para>
/// </summary>
[Modulo("foco", "Foco", English = "Focus")]
internal sealed class FocoModule : Modulo
{
    private ConfigFoco _cfg = new();
    private FocusActivity? _activity;
    private FocusMode? _active;
    private bool _byAuto, _stopped;
    private string? _lastAuto;
    private bool _firstLook = true;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 30_000 };

    // what to put back when the mode goes off
    private string? _wallpaperBefore;
    private (float Before, float Set)? _volume, _brightness;

    // the calls to the shell for Windows' Do Not Disturb, one after the other (a quick off and on keep their order)
    private Task _dnd = Task.CompletedTask;

    private void Dnd(Action a) => _dnd = _dnd.ContinueWith(_ =>
    {
        try { a(); }
        catch (Exception e) { Log.Escrever("foco: Não Perturbe do Windows: " + e.Message); }
    }, TaskScheduler.Default);

    private static string StateFile => Path.Combine(Config.Pasta, "foco.json");

    public override Type? TipoConfig => typeof(ConfigFoco);

    internal FocusMode? Active => _active;
    internal IReadOnlyList<FocusMode> Modes => _cfg.Modos;
    internal bool ShowMark => _cfg.Marca;

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Read(cfg);
        Sons.Silencio = () => _active?.Silenciar == true;
        Ilha.FiltroDoFoco = a => _active is { } m && !FocusRules.Allows(m, a.Tipo, a.Importante);
        _activity = new FocusActivity(S, this);
        S.Ilha.Adicionar(_activity);
        S.Menu.Adicionar(new ItemDoMenu
        {
            // a 2x1 block right after the batteries (3.7.0, his request): under them while they are 2x1, beside them
            // (under the sliders) when they grow to 2x2, since the blocks are packed in menu order
            Id = "foco", Nome = Lang.T("Foco", "Focus"), Ordem = 91, Cols = 2, Follows = "baterias",
            Abrir = () => _activity?.Open(),
            Icone = (dc, p, r) => FocusIcons.Draw(dc, p.G.Fabrica, r, _active?.Icone ?? "lua", Cor.Branco),
            BodyColor = () => _active is { } m ? FocusIcons.ColorOf(m.Cor) : null,
            Draw = DrawTile,
        });
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "nao-perturbe", Nome = Lang.T("Não Perturbe", "Do Not Disturb"), Ordem = 7,
            Abrir = () => Toggle(DoNotDisturb()),
            Icone = (dc, p, r) => FocusIcons.BellOff(dc, p.G.Fabrica, r, Cor.Branco),
            BodyColor = () => _active?.Id == FocusMode.DoNotDisturbId ? FocusIcons.ColorOf(DoNotDisturb()?.Cor ?? "indigo") : null,
            Detalhe = () => _active?.Id == FocusMode.DoNotDisturbId ? Lang.T("ligado", "on") : null,
        });
        // a mode turned on by hand stays on after the island restarts
        if (ReadState() is { } id && Find(id) is { } kept) Activate(kept, byAuto: false, announce: false);
        _tick.Tick += (_, _) => Evaluate();
        _tick.Start();
        S.NaInterface(Evaluate);
    }

    /// <summary>The 2x1 block: the symbol in a round badge, "Focus" and the mode on (or "Off").</summary>
    private void DrawTile(Vortice.Direct2D1.ID2D1DeviceContext dc, Palco p, System.Drawing.RectangleF cell)
    {
        var body = Menu.Corpo(cell);
        var c = new System.Numerics.Vector2(body.X + 8 + 19, body.Y + body.Height / 2);
        bool on = _active != null;
        Icone.Circulo(dc, c, 19, on ? new Vortice.Mathematics.Color4(1, 1, 1, 0.25f) : new Vortice.Mathematics.Color4(1, 1, 1, 0.1f));
        FocusIcons.Draw(dc, p.G.Fabrica, new System.Drawing.RectangleF(c.X - 11, c.Y - 11, 22, 22), _active?.Icone ?? "lua", Cor.Branco);
        float x = c.X + 27, w = body.Right - x - 8;
        p.Texto.Desenhar(dc, Lang.T("Foco", "Focus"), new System.Numerics.Vector2(x, body.Y + 9), 13.5f, Cor.Branco, largura: w);
        p.Texto.Desenhar(dc, _active?.Name ?? Lang.T("Desligado", "Off"), new System.Numerics.Vector2(x, body.Y + 28), 11.5f,
            on ? Cor.Branco : Cor.Secundario, Vortice.DirectWrite.FontWeight.Normal, largura: w);
    }

    private static ConfigFoco Read(JsonObject cfg)
    {
        var c = Ler<ConfigFoco>(cfg);
        if (c.Modos.Count == 0) c.Modos = FocusMode.Defaults();
        foreach (var m in c.Modos.Where(m => m.Id.Length == 0)) m.Id = FocusMode.NewId(m.Name, c.Modos.Select(x => x.Id));
        return c;
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Read(cfg);
        // the mode on was edited (or removed): its new settings apply from a fresh start
        if (_active is { } on)
        {
            bool auto = _byAuto;
            Deactivate(announce: false);
            if (Find(on.Id) is { } again) Activate(again, auto, announce: false);
        }
        _lastAuto = null;
        _firstLook = true;
        Evaluate();
        _activity?.Changed();
        S.Menu.Redesenhar();
    }

    public override void Contexto(Contexto ctx)
    {
        // an app trigger: re-check when the window in front changes (an app just opened, most of the time)
        if (_cfg.Modos.Any(m => m.Apps.Count > 0)) Evaluate();
    }

    private FocusMode? Find(string id) => _cfg.Modos.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    private FocusMode? DoNotDisturb() => Find(FocusMode.DoNotDisturbId) ?? FocusMode.Defaults()[0];

    private static HashSet<string> Running()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try { set.Add(p.ProcessName.ToLowerInvariant()); } catch (Exception) { }
            p.Dispose();
        }
        return set;
    }

    private void Evaluate()
    {
        if (_stopped) return;
        var running = _cfg.Modos.Any(m => m.Apps.Count > 0) ? Running() : new HashSet<string>();
        var wanted = FocusRules.Wanted(_cfg.Modos, DateTime.Now, running);
        string? id = wanted?.Id;
        if (id == _lastAuto && !_firstLook) return;
        string? before = _lastAuto;
        _lastAuto = id;
        bool first = _firstLook;
        _firstLook = false;
        if (wanted != null)
        {
            // at start a mode kept on by hand is not replaced by one that was already scheduled (it was his choice)
            if (!(first && _active != null && !_byAuto)) Activate(wanted, byAuto: true, announce: !first);
        }
        else if (_active != null && _byAuto && (_active.Id == before || first)) Deactivate(announce: true);
    }

    /// <summary>The menu and the card: the mode on goes off, any other turns on (by hand).</summary>
    internal void Toggle(FocusMode? m)
    {
        if (m == null) return;
        if (_active?.Id == m.Id) Deactivate(announce: true);
        else Activate(m, byAuto: false, announce: true);
    }

    private void Activate(FocusMode m, bool byAuto, bool announce)
    {
        if (_active?.Id == m.Id) { _byAuto = byAuto; WriteState(); return; }
        if (_active != null) Undo();
        _active = m;
        _byAuto = byAuto;
        Apply(m);
        WriteState();
        Log.Escrever($"foco: {m.Name} ligado{(byAuto ? " (automação)" : "")}");
        if (announce && _cfg.Aviso) Notice(m, on: true);
        Refresh();
    }

    private void Deactivate(bool announce)
    {
        if (_active is not { } m) return;
        Undo();
        _active = null;
        _byAuto = false;
        WriteState();
        Log.Escrever($"foco: {m.Name} desligado");
        if (announce && _cfg.Aviso) Notice(m, on: false);
        Refresh();
    }

    private void Refresh()
    {
        _activity?.Changed();
        S.Menu.Redesenhar();
    }

    // ------------------------------------------------------------------ what a mode changes

    private void Apply(FocusMode m)
    {
        if (m.NaoPerturbeWindows)
            Dnd(() =>
            {
                bool ok = WindowsDnd.Start(out string detail);
                Log.Escrever($"foco: Não Perturbe do Windows: {detail}");
                if (!ok) S.NaInterface(() => S.Avisar(Lang.T("O Windows não deixou ligar o Não Perturbe: ", "Windows did not let Do Not Disturb turn on: ") + detail));
            });
        if (m.Volume >= 0 && VolumeDoSistema.Ler() is { } v)
        {
            float target = m.Volume / 100f;
            VolumeDoSistema.Somar(target - v.Nivel);
            _volume = (v.Nivel, target);
        }
        if (m.Brilho >= 0 && ModuloBrilho.NivelAtual is float b)
        {
            float target = m.Brilho / 100f;
            if (ModuloBrilho.DefinirDeFora(target)) _brightness = (b, target);
        }
        if (m.Wallpaper.Length > 0)
        {
            string item = m.Wallpaper;
            Task.Run(() =>
            {
                try { return PutWallpaper(item); }
                catch (Exception e) { Log.Escrever("foco: wallpaper não trocou: " + e.Message); return null; }
            }).ContinueWith(t => S.NaInterface(() => { if (_active == m && t.Result is { } before) _wallpaperBefore = before; }));
        }
    }

    private void Undo()
    {
        Dnd(WindowsDnd.Stop);
        // put back only what is still as the mode left it: a change he made in the meantime stays
        if (_volume is { } vol && VolumeDoSistema.Ler() is { } now && MathF.Abs(now.Nivel - vol.Set) < 0.02f)
            VolumeDoSistema.Somar(vol.Before - now.Nivel);
        _volume = null;
        if (_brightness is { } bri && ModuloBrilho.NivelAtual is float b && MathF.Abs(b - bri.Set) < 0.02f)
            ModuloBrilho.DefinirDeFora(bri.Before);
        _brightness = null;
        if (_wallpaperBefore is { } back)
        {
            _wallpaperBefore = null;
            Task.Run(() =>
            {
                try { PutWallpaper(back); }
                catch (Exception e) { Log.Escrever("foco: wallpaper anterior não voltou: " + e.Message); }
            });
        }
    }

    /// <summary>Puts a Fundo Vivo library item on the main monitor. Returns the item that was there before (null: none).</summary>
    private static string? PutWallpaper(string itemId)
    {
        var cfg = FundoVivoBridge.ReadConfig();
        var main = FundoVivoBridge.Monitors().FirstOrDefault(x => x.Primary) ?? throw new InvalidOperationException("sem monitor principal");
        string? before = FundoVivoBridge.Assigned(cfg).TryGetValue(main.Id, out var b) ? b : null;
        if (string.Equals(before, itemId, StringComparison.OrdinalIgnoreCase)) return null;
        var item = FundoVivoBridge.Scan(cfg).FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("wallpaper não está mais na biblioteca do Fundo Vivo");
        FundoVivoBridge.Assign(main, item);
        if (!FundoVivoBridge.Reload() && FundoVivoBridge.FindExe(null) is { } exe) FundoVivoBridge.StartEngine(exe);
        Log.Escrever($"foco: wallpaper {item.Title}");
        return before;
    }

    // ------------------------------------------------------------------ notice and state

    private void Notice(FocusMode m, bool on) => S.Ilha.Alertar(new Alerta
    {
        Tipo = "foco",
        Som = "",
        // he turned it on: it shows through Do Not Disturb (always on in his Windows)
        Importante = true,
        Segundos = 2.2f,
        Criar = p => FocusActivity.NoticeCard(p, m, on),
    });

    private void WriteState()
    {
        try
        {
            var o = new JsonObject { ["ativo"] = _active != null && !_byAuto ? _active.Id : "" };
            File.WriteAllText(StateFile, o.ToJsonString());
        }
        catch (Exception e) { Log.Escrever("foco: estado não gravou: " + e.Message); }
    }

    private static string? ReadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return null;
            var id = JsonNode.Parse(File.ReadAllText(StateFile))?["ativo"]?.GetValue<string>();
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch (Exception) { return null; }
    }

    public override void Parar()
    {
        _stopped = true;
        _tick.Stop();
        _tick.Dispose();
        // leaving: what the mode changed goes back, but the choice stays in foco.json for the next start
        if (_active != null) Undo();
        try { _dnd.Wait(3000); } catch (Exception) { }
        Sons.Silencio = null;
        Ilha.FiltroDoFoco = null;
    }

    public override string Autoteste() => FocusTests.Run();
}
