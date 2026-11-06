using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;

namespace Ilha.Peripherals;

/// <summary>
/// Gear Hub, his peripherals app (4.3.0, his request on 06/11/2026: "compatibilidade com a ilha, fazer o perfil
/// aparecer na ilha"). Gear Hub keeps a named pipe open (`gear-hub/docs/canal-local.md`); the island connects as a
/// client while it exists, and:
/// - shows the profile Gear Hub applies ("Perfil cs2", "cs2 na frente") for 2 s, also over a competitive game, since
///   that is the moment a game comes to the front;
/// - tells Snap Tap, DPI, devices connecting, low battery and lighting effects (each one can be turned off);
/// - adds "Periféricos" to the menu while connected: the profile, the saved profiles as buttons, the devices and a
///   button that opens Gear Hub.
/// </summary>
[Modulo("perifericos", "Periféricos (Gear Hub)", English = "Peripherals (Gear Hub)")]
internal sealed class PeripheralsModule : Modulo
{
    private ConfigPerifericos _cfg = new();
    private PeripheralsActivity? _activity;
    private GearHubLink? _link;
    private readonly NoticeBatch _batch = new();
    private readonly System.Windows.Forms.Timer _batchTimer = new() { Interval = NoticeBatch.WindowMs };
    private readonly Dictionary<NoticeKind, Alerta> _shown = new();
    private bool _stopped, _menu;

    internal GearHubState State { get; } = new();
    internal string? Problem { get; private set; }

    public override Type? TipoConfig => typeof(ConfigPerifericos);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigPerifericos>(cfg);
        _activity = new PeripheralsActivity(S, this);
        S.Ilha.Adicionar(_activity);
        _batchTimer.Tick += (_, _) => { _batchTimer.Stop(); FlushBatch(); };
        _link = new GearHubLink(GearHubProtocol.PipeName,
            line => S.NaInterface(() => OnLine(line)),
            on => S.NaInterface(() => OnConnection(on)),
            IsGearHub);
        _link.Start();
    }

    public override void ConfigMudou(JsonObject cfg) => _cfg = Ler<ConfigPerifericos>(cfg);

    public override void Parar()
    {
        _stopped = true;
        _batchTimer.Stop();
        _batchTimer.Dispose();
        _link?.Dispose();
        _link = null;
    }

    private static bool IsGearHub(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName.Equals("GearHub", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }

    private void OnConnection(bool on)
    {
        if (_stopped || on) return;
        if (State.Connected) Log.Escrever("periféricos: Gear Hub desconectou");
        State.Reset();
        Problem = null;
        ShowMenu(false);
        _activity?.Changed();
    }

    private void OnLine(string line)
    {
        if (_stopped || GearHubProtocol.Parse(line) is not { } m) return;
        string type = GearHubProtocol.Str(m, "type") ?? "";
        var notices = State.Apply(m, Rules());
        if (type == "hello")
        {
            Log.Escrever($"periféricos: Gear Hub {State.Version} conectado ({State.Devices.Count} aparelho(s), perfil {State.Profile})");
            ShowMenu(true);
        }
        if (type == "snaptap" && State.SnapTap is bool snap) RecordSnapTap(snap);
        if (type == "profile") Problem = null;
        foreach (var n in notices)
        {
            if (_batch.Take(n)) { if (!_batchTimer.Enabled) _batchTimer.Start(); }
            else Show(n);
        }
        _activity?.Changed();
        S.Menu.Redesenhar();
    }

    private void FlushBatch()
    {
        if (!_stopped && _batch.Flush() is { } n) Show(n);
    }

    private NoticeRules Rules() => new()
    {
        Profile = _cfg.AvisarPerfil, SnapTap = _cfg.AvisarSnapTap, Dpi = _cfg.AvisarDpi, Devices = _cfg.AvisarAparelhos,
        Battery = _cfg.AvisarBateria, Effects = _cfg.AvisarEfeitos,
        CoveredElsewhere = (kind, key, name, battery) =>
            CoveredByHeadphones(S.Config().Modulos["fones"] as JsonObject, kind, key, name, battery, () => Games.GamesModule.Running("lghub_agent")),
    };

    private void Show(GearNotice n)
    {
        var spec = NoticeSpec.For(n, _cfg.PassarNaoPerturbe);
        // a newer profile, Snap Tap, DPI or effect replaces the one still on screen (cycling profiles by hotkey)
        if (n.Kind is not (NoticeKind.Device or NoticeKind.Battery) && _shown.TryGetValue(n.Kind, out var old)) S.Ilha.FecharAlerta(old);
        var a = new Alerta
        {
            Tipo = "perifericos", Som = spec.Sound, Segundos = spec.Seconds, Importante = spec.Important,
            PermitidoNoCompetitivo = spec.Competitive,
            Criar = p => PeripheralsActivity.NoticeCard(p, n),
        };
        _shown[n.Kind] = a;
        S.Ilha.Alertar(a);
    }

    private void ShowMenu(bool on)
    {
        if (on == _menu) return;
        _menu = on;
        if (!on) { S.Menu.Remover("perifericos"); return; }
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "perifericos", Nome = Lang.T("Periféricos", "Peripherals"), Ordem = 68,
            Abrir = () => _activity?.Open(),
            Icone = (dc, p, r) => PeripheralsActivity.Sliders(dc, new System.Numerics.Vector2(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width, Cor.Branco, coloredKnobs: false),
            Detalhe = () => State.ProfileDisplay,
        });
    }

    // ------------------------------------------------------------------ requests from the card

    internal void ApplyProfile(string name) => Request(GearHubProtocol.Profile(name));

    internal void ShowGearHub() => Request(GearHubProtocol.Show());

    /// <summary>
    /// The Snap Tap chip: the keyboard module already has the Snap Tap card (state, key pairs, the switch), so with its
    /// menu entry there the chip opens it; without it (keyboard module off) the chip asks Gear Hub to switch.
    /// </summary>
    internal void SnapTapPressed()
    {
        if (S.Menu.Itens.FirstOrDefault(i => i.Id == "teclado") is { } kb) { kb.Abrir(); return; }
        if (State.SnapTap is bool on) Request(GearHubProtocol.SnapTap(!on));
    }

    private void Request(string json)
    {
        bool ok = _link?.Send(json) == true;
        Problem = ok ? null : Lang.T("O Gear Hub não respondeu.", "Gear Hub did not answer.");
        if (!ok) Log.Escrever("periféricos: pedido não foi para o Gear Hub");
        _activity?.Changed();
    }

    // ------------------------------------------------------------------ the keyboard module asks this

    private static long _snapAt = long.MinValue / 2;
    private static bool _snapOn;

    private static void RecordSnapTap(bool on)
    {
        _snapOn = on;
        _snapAt = Environment.TickCount64;
    }

    /// <summary>
    /// Gear Hub said Snap Tap is `on` in the last minute: the keyboard module, which reads the keyboard every 30 s,
    /// skips its own notice for that change (it would be the second one).
    /// </summary>
    internal static bool SnapTapJustReported(bool on) => _snapOn == on && Environment.TickCount64 - _snapAt < 60_000;

    /// <summary>
    /// The headphones module already tells this one: its connected card for Razer keyboards (`teclados`), DualSense
    /// (`controles`) and the G733 (`g733`); its low battery warning for the G733 (HID++, always) and for the Logitech
    /// mouse (only through G HUB, so only while G HUB runs). Its levels are 50, 25, 10, 5, 1; Gear Hub's are 20, 10, 5.
    /// Everything else (memory, camera, lights, a mouse with G HUB closed, controller battery) is told here.
    /// </summary>
    internal static bool CoveredByHeadphones(JsonObject? fones, string kind, string key, string name, bool battery, Func<bool> ghubRunning)
    {
        fones ??= new JsonObject();
        bool F(string k) => GearHubProtocol.Bool(fones, k) ?? true;
        if (!F("ligado")) return false;
        bool logi = key.StartsWith("logi-", StringComparison.OrdinalIgnoreCase);
        bool g733 = logi && kind == "headset" && name.Contains("G733", StringComparison.OrdinalIgnoreCase);
        if (battery)
        {
            if (!F("alertasBateria")) return false;
            if (g733) return F("g733");
            if (logi && kind == "mouse") return F("mouse") && F("ghub") && ghubRunning();
            return false;
        }
        return kind switch
        {
            "keyboard" => key.StartsWith("razer-", StringComparison.OrdinalIgnoreCase) && F("teclados"),
            "controller" => key.StartsWith("dualsense", StringComparison.OrdinalIgnoreCase) && F("controles"),
            "headset" => g733 && F("g733"),
            _ => false,
        };
    }

    // ------------------------------------------------------------------ self test

    public override string Autoteste()
    {
        var r = new StringBuilder();
        void Ok(string n, bool ok, string d = "") => r.AppendLine((ok ? "ok      " : "FALHOU  ") + n + (d.Length > 0 ? $" ({d})" : ""));

        const string hello = """{"type":"hello","version":"1.1.0","profile":"cs2","profileDisplay":"cs2","profiles":[{"name":"default","display":"Padrão","apps":[],"saved":true},{"name":"cs2","display":"cs2","apps":["cs2.exe"],"saved":true},{"name":"vazio","display":"Vazio","apps":[],"saved":false}],"devices":[{"key":"razer-02ea","name":"Razer Huntsman V3 HE TKL","kind":"keyboard","status":"Snap Tap ligado","battery":null},{"key":"logi-4a1b2c3d","name":"G502 X PLUS","kind":"mouse","status":"1600 DPI","battery":64}],"snapTap":true,"novoCampo":{"x":1}}""";
        var none = new NoticeRules();
        var st = new GearHubState();
        Ok("periféricos: evento antes do hello não conta", st.Apply(GearHubProtocol.Parse("""{"type":"profile","name":"cs2","reason":"app"}""")!, none).Count == 0 && !st.Connected);
        var n0 = st.Apply(GearHubProtocol.Parse(hello)!, none);
        Ok("periféricos: hello dá versão, perfil, perfis salvos, aparelhos e Snap Tap (campo novo ignorado)",
            n0.Count == 0 && st.Connected && st.Version == "1.1.0" && st.Profile == "cs2" && st.Profiles.Count == 3 && st.Saved.Count() == 2
            && st.Profiles[0].Display == "Padrão" && st.Devices.Count == 2 && st.Devices[0].Battery == null && st.Devices[1].Battery == 64 && st.SnapTap == true,
            $"{st.Profiles.Count} perfis, {st.Devices.Count} aparelhos");
        Ok("periféricos: linha estragada, sem type ou vazia é ignorada",
            GearHubProtocol.Parse("{nada") == null && GearHubProtocol.Parse("""{"x":1}""") == null && GearHubProtocol.Parse("[1,2]") == null);

        List<GearNotice> Feed(string json, NoticeRules? rules = null) => st.Apply(GearHubProtocol.Parse(json)!, rules ?? none);

        var p = Feed("""{"type":"profile","name":"default","display":"Padrão","reason":"app","app":"cs2.exe"}""");
        Ok("periféricos: perfil aplicado pelo jogo vira \"Perfil Padrão\" com o jogo embaixo",
            p.Count == 1 && p[0].Kind == NoticeKind.Profile && p[0].Title == Lang.T("Perfil Padrão", "Profile Padrão") && p[0].Sub == GearHubProtocol.ReasonText("app", "cs2.exe")
            && p[0].Sub.StartsWith("cs2 ") && st.Profile == "default" && st.ProfileDisplay == "Padrão", p.FirstOrDefault()?.Title + " / " + p.FirstOrDefault()?.Sub);
        Ok("periféricos: motivos do perfil (manual, bandeja, atalho, desconhecido)",
            GearHubProtocol.ReasonText("manual", null) == Lang.T("manual", "manual") && GearHubProtocol.ReasonText("hotkey", null) == Lang.T("atalho", "hotkey")
            && GearHubProtocol.ReasonText("tray", null) == Lang.T("pela bandeja", "from the tray") && GearHubProtocol.ReasonText("outro", null) == "outro");
        Ok("periféricos: perfil escolhido no cartão da ilha não vira aviso (o cartão já mostra)",
            Feed("""{"type":"profile","name":"cs2","display":"cs2","reason":"ilha"}""").Count == 0 && st.Profile == "cs2");
        Ok("periféricos: aviso de perfil desligado na config", Feed("""{"type":"profile","name":"default","reason":"hotkey"}""", new NoticeRules { Profile = false }).Count == 0 && st.ProfileDisplay == "Padrão");
        var sp = NoticeSpec.For(p[0], throughDnd: true);
        Ok("periféricos: aviso de perfil é curto, sem som, passa no competitivo e no Não Perturbe",
            sp is { Seconds: 2f, Sound: "", Important: true, Competitive: true });

        Feed("""{"type":"profiles","profiles":[{"name":"default","display":"Padrão 2","apps":[],"saved":true}]}""");
        Ok("periféricos: lista de perfis nova troca a lista e o nome do perfil ativo", st.Profiles.Count == 1 && st.ProfileDisplay == "Padrão 2");
        Feed("""{"type":"devices","devices":[{"key":"dualsense-std","name":"DualSense","kind":"controller","status":"USB","battery":80}]}""");
        Ok("periféricos: lista de aparelhos nova", st.Devices.Count == 1 && st.Devices[0].Kind == "controller" && st.Devices[0].Battery == 80);

        var cam = Feed("""{"type":"device","key":"cam-c920","name":"C920","kind":"camera","connected":true}""");
        Ok("periféricos: aparelho conectou (câmera) vira aviso e entra na lista",
            cam.Count == 1 && cam[0] is { Kind: NoticeKind.Device, Title: "C920", On: true, Icon: "camera" } && st.Devices.Any(d => d.Key == "cam-c920"));
        Ok("periféricos: aparelho conectado não é furado no competitivo e toca o som de conectar",
            NoticeSpec.For(cam[0], true) is { Competitive: false, Sound: "conectou", Important: true } && NoticeSpec.For(cam[0], false).Important == false);
        var off = Feed("""{"type":"device","key":"cam-c920","name":"C920","kind":"camera","connected":false}""");
        Ok("periféricos: aparelho desconectou vira aviso e sai da lista", off.Count == 1 && off[0].On == false && !st.Devices.Any(d => d.Key == "cam-c920"));
        var covers = new NoticeRules { CoveredElsewhere = (k, key, name, bat) => CoveredByHeadphones(null, k, key, name, bat, () => false) };
        Ok("periféricos: teclado Razer conectando fica com o módulo de fones (sem aviso duplicado), desconectando avisa",
            Feed("""{"type":"device","key":"razer-02ea","name":"Razer Huntsman V3 HE TKL","kind":"keyboard","connected":true}""", covers).Count == 0
            && Feed("""{"type":"device","key":"razer-02ea","name":"Razer Huntsman V3 HE TKL","kind":"keyboard","connected":false}""", covers).Count == 1);
        Ok("periféricos: aviso de aparelho desligado na config", Feed("""{"type":"device","key":"cam-x","name":"X","kind":"camera","connected":true}""", new NoticeRules { Devices = false }).Count == 0);

        var bat = Feed("""{"type":"battery","key":"dualsense-std","name":"DualSense","kind":"controller","percent":10,"charging":false}""", covers);
        Ok("periféricos: bateria baixa do controle vira aviso importante (7 s, com som) e muda a lista",
            bat.Count == 1 && bat[0].Percent == 10 && bat[0].Sub == global::Ilha.Extras.ModuloFones.TextoBateriaBaixa(10, 10, null, false)
            && NoticeSpec.For(bat[0], false) is { Important: true, Seconds: 7f, Sound: "alerta", Competitive: false } && st.Devices[0].Battery == 10,
            bat.FirstOrDefault()?.Sub ?? "nada");
        Ok("periféricos: bateria carregando não avisa", Feed("""{"type":"battery","key":"dualsense-std","name":"DualSense","kind":"controller","percent":5,"charging":true}""").Count == 0);
        Ok("periféricos: bateria do G733 fica com o módulo de fones",
            Feed("""{"type":"battery","key":"logi-0afe","name":"G733","kind":"headset","percent":20,"charging":false}""", covers).Count == 0);
        Ok("periféricos: bateria do mouse Logitech avisa aqui com o G HUB fechado e fica com os fones com ele aberto",
            !CoveredByHeadphones(null, "mouse", "logi-4a1b", "G502 X PLUS", true, () => false) && CoveredByHeadphones(null, "mouse", "logi-4a1b", "G502 X PLUS", true, () => true)
            && !CoveredByHeadphones(new JsonObject { ["alertasBateria"] = false }, "mouse", "logi-4a1b", "G502", true, () => true)
            && !CoveredByHeadphones(new JsonObject { ["ligado"] = false }, "headset", "logi-0afe", "G733", true, () => true)
            && !CoveredByHeadphones(null, "memory", "corsair-dram", "Vengeance", false, () => true));

        var s1 = Feed("""{"type":"snaptap","on":false}""");
        Ok("periféricos: Snap Tap desligou vira aviso; o mesmo estado de novo não",
            s1.Count == 1 && s1[0].On == false && s1[0].Sub == GearHubState.SnapText(false) && Feed("""{"type":"snaptap","on":false}""").Count == 0 && st.SnapTap == false);
        var dpi = Feed("""{"type":"dpi","key":"logi-4a1b2c3d","name":"G502 X PLUS","value":1600}""");
        Ok("periféricos: DPI vira \"DPI 1600\" com o mouse embaixo, curto e sem som",
            dpi.Count == 1 && dpi[0].Title == "DPI 1600" && dpi[0].Sub == "G502 X PLUS" && NoticeSpec.For(dpi[0], true) is { Seconds: 1.8f, Sound: "", Competitive: false });
        var fx = Feed("""{"type":"effect","name":"Onda","targets":["G502 X PLUS","Vengeance RGB"]}""");
        Ok("periféricos: efeito de luz vira aviso discreto com os aparelhos", fx.Count == 1 && fx[0].Title == "Onda" && fx[0].Sub == "G502 X PLUS, Vengeance RGB");
        Ok("periféricos: aviso de efeito desligado na config", Feed("""{"type":"effect","name":"Onda","targets":[]}""", new NoticeRules { Effects = false }).Count == 0);
        Ok("periféricos: tipo desconhecido não faz nada", Feed("""{"type":"novidade","x":1}""").Count == 0);

        var batch = new NoticeBatch();
        batch.Take(new GearNotice(NoticeKind.Profile, "Perfil cs2", "cs2 na frente", "gear"));
        batch.Take(new GearNotice(NoticeKind.SnapTap, "Snap Tap", GearHubState.SnapText(false), "keyboard") { On = false });
        var merged = batch.Flush();
        Ok("periféricos: perfil e Snap Tap juntos viram um aviso só",
            merged is { Kind: NoticeKind.Profile, On: false } && merged.Sub == "cs2 na frente · " + GearHubState.SnapText(false) && batch.Empty, merged?.Sub ?? "nada");
        Ok("periféricos: aparelho não espera o lote", !batch.Take(cam[0]) && batch.Flush() == null);

        Ok("periféricos: pedidos no formato do canal",
            JsonNode.Parse(GearHubProtocol.Profile("cs2 \"x\""))!["name"]!.GetValue<string>() == "cs2 \"x\"" && GearHubProtocol.Profile("a") == """{"cmd":"profile","name":"a"}"""
            && GearHubProtocol.SnapTap(true) == """{"cmd":"snaptap","on":true}""" && GearHubProtocol.Show() == """{"cmd":"show"}"""
            && GearHubProtocol.Show("profiles") == """{"cmd":"show","page":"profiles"}""" && GearHubProtocol.State() == """{"cmd":"state"}""");

        RecordSnapTap(true);
        Ok("periféricos: o teclado pula o aviso que o Gear Hub já deu", SnapTapJustReported(true) && !SnapTapJustReported(false));
        _snapAt = long.MinValue / 2;

        st.Reset();
        Ok("periféricos: desconectar limpa o estado", !st.Connected && st.Profile == null && st.Devices.Count == 0 && st.SnapTap == null);

        PipeTests(Ok);
        return r.ToString().TrimEnd();
    }

    /// <summary>A fake Gear Hub on a pipe of its own: hello, events, a request back, a drop and the reconnection.</summary>
    private static void PipeTests(Action<string, bool, string> Ok)
    {
        string name = "ilha-teste-gearhub-" + Guid.NewGuid().ToString("N")[..8];
        var lines = new BlockingCollection<string>();
        var conns = new BlockingCollection<bool>();
        string? request = null;
        var requestIn = new ManualResetEventSlim();
        var dropNow = new ManualResetEventSlim();
        int accepted = 0;
        var server = new Thread(() =>
        {
            try
            {
                for (int round = 0; round < 2; round++)
                {
                    using var s = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    var wait = s.WaitForConnectionAsync();
                    if (!wait.Wait(5000)) return;
                    accepted++;
                    var w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
                    w.WriteLine("""{"type":"hello","version":"1.1.0","profile":"default","profileDisplay":"Padrão","profiles":[{"name":"cs2","display":"cs2","apps":["cs2.exe"],"saved":true}],"devices":[],"snapTap":false}""");
                    if (round == 1) { Thread.Sleep(300); return; }
                    w.WriteLine("lixo que não é json");
                    w.WriteLine("""{"type":"profile","name":"cs2","display":"cs2","reason":"app","app":"cs2.exe"}""");
                    var rd = new StreamReader(s, new UTF8Encoding(false));
                    var readTask = rd.ReadLineAsync();
                    if (readTask.Wait(5000)) request = readTask.Result;
                    requestIn.Set();
                    dropNow.Wait(5000);
                }
            }
            catch (Exception) { }
        }) { IsBackground = true };
        server.Start();

        var link = new GearHubLink(name, l => lines.Add(l), c => conns.Add(c), pid => pid == Environment.ProcessId) { RetryMs = 200 };
        link.Start();
        bool connected = conns.TryTake(out bool c1, 4000) && c1;
        var st = new GearHubState();
        var got = new List<string>();
        for (int i = 0; i < 3 && lines.TryTake(out var l, 3000); i++) got.Add(l);
        var parsed = got.Select(GearHubProtocol.Parse).Where(x => x != null).ToList();
        var notices = parsed.SelectMany(x => st.Apply(x!, new NoticeRules())).ToList();
        Ok("periféricos: pipe de mentira: conecta, recebe o hello e o perfil, pula a linha estragada", connected && got.Count == 3 && parsed.Count == 2
            && st.Connected && st.Profile == "cs2" && notices.Count == 1 && notices[0].Kind == NoticeKind.Profile, $"{got.Count} linhas, {notices.Count} aviso(s)");
        bool sent = link.Send(GearHubProtocol.Profile("cs2"));
        requestIn.Wait(5000);
        Ok("periféricos: pipe de mentira: o pedido do cartão chega ao Gear Hub", sent && request == """{"cmd":"profile","name":"cs2"}""", request ?? "nada chegou");
        dropNow.Set();
        bool dropped = conns.TryTake(out bool c2, 4000) && !c2;
        bool back = conns.TryTake(out bool c3, 5000) && c3 && lines.TryTake(out var again, 3000) && GearHubProtocol.Parse(again)?["type"]?.GetValue<string>() == "hello";
        Ok("periféricos: pipe de mentira: caiu, avisa, e reconecta sozinho com hello novo", dropped && back && accepted == 2, $"caiu {dropped}, voltou {back}");
        var sw = Stopwatch.StartNew();
        link.Dispose();
        Ok("periféricos: fechar o cliente não trava", sw.ElapsedMilliseconds < 1500, sw.ElapsedMilliseconds + " ms");
        server.Join(2000);

        // nobody on the pipe: no connection, no exception, closes at once
        var quiet = new BlockingCollection<bool>();
        var none = new GearHubLink("ilha-teste-sem-gearhub-" + Guid.NewGuid().ToString("N")[..8], _ => { }, x => quiet.Add(x)) { RetryMs = 100 };
        none.Start();
        Thread.Sleep(450);
        sw.Restart();
        none.Dispose();
        Ok("periféricos: sem o pipe só espera, sem conectar", quiet.Count == 0 && !none.Send("{}") && sw.ElapsedMilliseconds < 500, sw.ElapsedMilliseconds + " ms");

        // a pipe with the right name served by another program: refused
        string other = "ilha-teste-outro-" + Guid.NewGuid().ToString("N")[..8];
        using (var fake = new NamedPipeServerStream(other, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            _ = fake.WaitForConnectionAsync();
            var refused = new BlockingCollection<bool>();
            var picky = new GearHubLink(other, _ => { }, x => refused.Add(x), _ => false) { RetryMs = 100 };
            picky.Start();
            Thread.Sleep(400);
            picky.Dispose();
            Ok("periféricos: pipe de outro programa é recusado", refused.Count == 0, "");
        }

        // the island closing while Gear Hub is connected and quiet: the pending read must not hold it
        string held = "ilha-teste-aberto-" + Guid.NewGuid().ToString("N")[..8];
        using (var open = new NamedPipeServerStream(held, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            var accept = open.WaitForConnectionAsync();
            var up = new BlockingCollection<bool>();
            var holder = new GearHubLink(held, _ => { }, x => up.Add(x)) { RetryMs = 100 };
            holder.Start();
            bool linked = up.TryTake(out bool u, 3000) && u && accept.Wait(1000);
            sw.Restart();
            holder.Dispose();
            Ok("periféricos: fechar com o Gear Hub conectado e calado não trava", linked && sw.ElapsedMilliseconds < 1000, sw.ElapsedMilliseconds + " ms");
        }

        // the real Gear Hub, read only: only the hello (no request is sent)
        var real = new BlockingCollection<string>();
        var live = new GearHubLink(GearHubProtocol.PipeName, l => real.Add(l), _ => { }, IsGearHub) { RetryMs = 60_000 };
        live.Start();
        if (real.TryTake(out var h, 2500) && GearHubProtocol.Parse(h) is { } hm)
        {
            var ls = new GearHubState();
            ls.Apply(hm, new NoticeRules());
            Ok("periféricos: Gear Hub de verdade (só o hello)", ls.Connected, $"versão {ls.Version}, perfil {ls.ProfileDisplay}, {ls.Profiles.Count} perfis, {ls.Devices.Count} aparelhos");
        }
        else Ok("periféricos: Gear Hub de verdade pulado (sem o pipe)", true, "");
        live.Dispose();
    }
}

public sealed class ConfigPerifericos
{
    [Label("Avisar quando o perfil mudar", "Notify when the profile changes")]
    [Help("Aparece por 2 s quando o Gear Hub aplica um perfil, também por cima de jogo competitivo.",
        "Shows for 2 s when Gear Hub applies a profile, also over a competitive game.")]
    [JsonPropertyName("avisarPerfil")] public bool AvisarPerfil { get; set; } = true;

    [Label("Avisar quando o Snap Tap ligar ou desligar", "Notify when Snap Tap turns on or off")]
    [JsonPropertyName("avisarSnapTap")] public bool AvisarSnapTap { get; set; } = true;

    [Label("Avisar quando o DPI mudar", "Notify when the DPI changes")]
    [JsonPropertyName("avisarDpi")] public bool AvisarDpi { get; set; } = true;

    [Label("Avisar quando um aparelho conectar ou desconectar", "Notify when a device connects or disconnects")]
    [Help("O que o módulo de fones já avisa (teclado Razer, DualSense, G733) não aparece duas vezes.",
        "What the headphones module already tells (Razer keyboard, DualSense, G733) does not show twice.")]
    [JsonPropertyName("avisarAparelhos")] public bool AvisarAparelhos { get; set; } = true;

    [Label("Avisar bateria baixa", "Warn about low battery")]
    [Help("Em 20, 10 e 5%. A do G733, e a do mouse com o G HUB aberto, ficam com o módulo de fones.",
        "At 20, 10 and 5%. The G733's, and the mouse's while G HUB is open, stay with the headphones module.")]
    [JsonPropertyName("avisarBateria")] public bool AvisarBateria { get; set; } = true;

    [Label("Avisar quando um efeito de luz começar", "Notify when a lighting effect starts")]
    [JsonPropertyName("avisarEfeitos")] public bool AvisarEfeitos { get; set; } = true;

    [Label("Avisos passam pelo Não Perturbe", "Notices go through Do Not Disturb")]
    [Help("Snap Tap, DPI, aparelhos e efeitos. O perfil e a bateria baixa passam sempre; jogo competitivo silencia todos menos o perfil.",
        "Snap Tap, DPI, devices and effects. The profile and low battery always go through; a competitive game silences all but the profile.")]
    [JsonPropertyName("passarNaoPerturbe")] public bool PassarNaoPerturbe { get; set; } = true;
}
