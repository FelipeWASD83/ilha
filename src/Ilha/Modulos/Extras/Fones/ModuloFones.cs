using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;

namespace Ilha.Extras;

/// <summary>
/// Aparelho Bluetooth conectou: a ilha abre com a foto do fone (ou um ícone desenhado), o nome e a bateria. AirPods e
/// Beats: bateria do esquerdo, do direito e do estojo pelo anúncio BLE da Apple, lido em rajadas curtas enquanto
/// estão conectados. Outros: a bateria que o Windows guarda (quando o aparelho informa). G733 (receptor LIGHTSPEED,
/// não é Bluetooth): HID++ direto, "G733 conectado" quando o headset liga e a bateria estimada pela tensão.
/// </summary>
[Modulo("fones", "Fones e Bluetooth", English = "Headphones and Bluetooth")]
internal sealed class ModuloFones : Modulo
{
    private sealed class Fone
    {
        public required string Chave;
        public required string Nome;
        public TipoAparelho Tipo;
        public ulong Endereco;
        public ushort? Modelo;
        public bool Apple;
        public bool G733;
        public string? Foto;
        public Retrato? Retrato;
        public BateriaApple? BateriaApple;
        public int? Bateria;
        public bool Carregando;
        public bool Mouse;
        // the percentage comes from G HUB (exact), not from the voltage curve
        public bool PorGHub;
        // the small photo of its tile in the main menu
        public Retrato? RetratoMenu;
    }

    private const float L = 380, A = 60, CAIXA = 52;

    private ConfigFones _cfg = new();
    private VigiaBluetooth? _bt;
    private VarreduraApple? _varredura;
    private readonly System.Windows.Forms.Timer _rajadas = new();
    private readonly Dictionary<string, Fone> _apple = new();
    private readonly List<DeviceWatcher> _vigiasHid = new();
    private readonly Dictionary<string, (LeitorG733 Leitor, EstadoG733 Estado, Fone Fone)> _g733 = new();
    private readonly List<(Superficie S, Fone F)> _vivos = new();
    private bool _parado;

    // low battery alerts (levels from the config) and the G HUB reader (mouse, exact G733 percentage)
    private BatteryAlerts _alerts = new();
    private readonly System.Windows.Forms.Timer _ghubTimer = new();
    private bool _ghubBusy;
    private (int Percent, bool Charging, long At)? _ghubG733;
    private readonly Dictionary<string, Fone> _mice = new();
    private readonly HashSet<string> _tiles = new();

    public override Type? TipoConfig => typeof(ConfigFones);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigFones>(cfg);
        _rajadas.Tick += (_, _) => Rajada();
        _ghubTimer.Tick += (_, _) => PollGHub();
        _alerts = new BatteryAlerts(BatteryAlerts.ParseLevels(_cfg.LimiaresBateria));
        _varredura = new VarreduraApple(BateriaAppleChegou);
        _bt = new VigiaBluetooth(BluetoothMudou);
        _bt.Iniciar();
        if (_cfg.G733) IniciarG733();
        ProgramGHub();
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        bool g733Antes = _cfg.G733;
        string niveisAntes = _cfg.LimiaresBateria;
        _cfg = Ler<ConfigFones>(cfg);
        if (_cfg.G733 && !g733Antes) IniciarG733();
        else if (!_cfg.G733 && g733Antes) PararG733();
        if (_cfg.LimiaresBateria != niveisAntes) _alerts = new BatteryAlerts(BatteryAlerts.ParseLevels(_cfg.LimiaresBateria));
        ProgramarRajadas();
        ProgramGHub();
    }

    public override void Parar()
    {
        _parado = true;
        _rajadas.Stop();
        _rajadas.Dispose();
        _ghubTimer.Stop();
        _ghubTimer.Dispose();
        _varredura?.Dispose();
        _varredura = null;
        _bt?.Dispose();
        _bt = null;
        PararG733();
        _apple.Clear();
        _mice.Clear();
        _vivos.Clear();
        // the core removes the tile with the module (it carries its id as owner)
        _tiles.Clear();
        _shown.Clear();
    }

    private bool Ignorado(string nome) => _cfg.Ignorar.Any(i => !string.IsNullOrWhiteSpace(i) && nome.Contains(i.Trim(), StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ Bluetooth

    private void BluetoothMudou(AparelhoBluetooth a, bool conectado, bool inicial)
    {
        if (_parado) return;
        Log.Escrever($"fones: {(conectado ? "conectou" : "desconectou")} {a.Tipo}{(inicial ? " (já estava)" : "")}");
        bool apple = ModelosApple.EhApple(a.Fabricante, a.Nome);
        if (!conectado)
        {
            if (_apple.TryGetValue(a.Id, out var saiu)) HideTile(saiu);
            _apple.Remove(a.Id);
            ProgramarRajadas();
            return;
        }
        var f = new Fone
        {
            Chave = a.Id, Nome = Fotos.NomeLimpo(a.Nome), Tipo = a.Tipo, Endereco = a.Endereco, Apple = apple,
            Modelo = apple && a.Produto is ushort p && ModelosApple.Conhecido(p) ? p : null,
        };
        if (apple)
        {
            _apple[a.Id] = f;
            ProgramarRajadas();
            if (_cfg.BateriaAirPods) _varredura?.Rajada(Math.Max(2, _cfg.RajadaSegundos) + 3, f.Modelo, (short)_cfg.SinalMinimo);
        }
        if (inicial || Ignorado(a.Nome)) return;
        f.Foto = EscolherFoto(f);
        PrepararEMostrar(f, lerBateriaDoWindows: !apple);
    }

    private string? EscolherFoto(Fone f)
    {
        var arquivos = Fotos.Arquivos();
        if (f.Modelo is ushort m && ModelosApple.Foto(m) is string pelaApple && arquivos.Contains(pelaApple, StringComparer.OrdinalIgnoreCase))
            return pelaApple;
        return Fotos.Escolher(f.Nome, arquivos, _cfg.Fotos, f.Mouse ? _cfg.CorMouse : _cfg.CorG733);
    }

    /// <summary>Foto (e bateria do Windows) fora da interface; o alerta aparece quando ela fica pronta.</summary>
    private void PrepararEMostrar(Fone f, bool lerBateriaDoWindows)
    {
        int px = Math.Max(1, (int)MathF.Round(CAIXA * S.Palco.Escala));
        string? foto = f.Foto;
        ulong endereco = f.Endereco;
        Task.Run(() =>
        {
            Retrato? r = foto == null ? null : Retrato.DeArquivo(Path.Combine(Fotos.Pasta, foto), px, px, recortar: true);
            int? bat = lerBateriaDoWindows && endereco != 0 ? NosPnp.BateriaDoAparelho(endereco) : null;
            S.NaInterface(() =>
            {
                if (_parado) return;
                f.Retrato = r;
                if (bat != null) f.Bateria = bat;
                Mostrar(f);
                if (lerBateriaDoWindows && f.Bateria == null) ReverBateriaDepois(f);
            });
        });
    }

    /// <summary>O Windows só grava a bateria uns segundos depois de conectar: confere de novo uma vez.</summary>
    private void ReverBateriaDepois(Fone f)
    {
        var t = new System.Windows.Forms.Timer { Interval = 8000 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            if (_parado) return;
            ulong end = f.Endereco;
            Task.Run(() =>
            {
                var b = NosPnp.BateriaDoAparelho(end);
                if (b != null) S.NaInterface(() => { f.Bateria = b; Atualizar(f); });
            });
        };
        t.Start();
    }

    private void ProgramarRajadas()
    {
        int s = _cfg.IntervaloAirPodsSegundos;
        if (_apple.Count > 0 && _cfg.BateriaAirPods && s > 0)
        {
            int ms = Math.Max(15, s) * 1000;
            if (!_rajadas.Enabled || _rajadas.Interval != ms) { _rajadas.Interval = ms; _rajadas.Start(); }
        }
        else
        {
            _rajadas.Stop();
            if (_apple.Count == 0) _varredura?.Parar();
        }
    }

    private void Rajada()
    {
        if (_apple.Count == 0 || !_cfg.BateriaAirPods) { _rajadas.Stop(); return; }
        // um modelo só conectado: casa por ele; mais de um, aceita qualquer modelo conhecido
        var modelos = _apple.Values.Select(f => f.Modelo).Distinct().ToList();
        _varredura?.Rajada(Math.Max(2, _cfg.RajadaSegundos), modelos.Count == 1 ? modelos[0] : null, (short)_cfg.SinalMinimo);
    }

    private void BateriaAppleChegou(BateriaApple b)
    {
        foreach (var f in _apple.Values)
        {
            if (f.Modelo != null && f.Modelo != b.Modelo) continue;
            f.BateriaApple = b;
            if (f.Modelo == null && ModelosApple.Conhecido(b.Modelo))
            {
                // o PnP não disse o modelo: o anúncio diz, e com ele a foto certa
                f.Modelo = b.Modelo;
                var nova = EscolherFoto(f);
                if (nova != null && nova != f.Foto) TrocarFoto(f, nova);
            }
            Atualizar(f);
            ShowTile(f);
            FeedApple(f, b);
        }
    }

    /// <summary>
    /// AirPods report in steps of 10. Each bud counts on its own and a bud charging in the case does not: the one that runs
    /// out first is the one the alert names (no name when both are at the same level).
    /// </summary>
    private void FeedApple(Fone f, BateriaApple b)
    {
        var buds = new List<(string Side, int Pct)>();
        if (b.Esquerdo is int l && !b.CarregandoEsquerdo) buds.Add((Lang.T("esquerdo", "left"), l));
        if (b.Direito is int r && !b.CarregandoDireito) buds.Add((Lang.T("direito", "right"), r));
        if (buds.Count == 0)
        {
            // both in the case charging (or nothing read): this only resets the levels
            if (b.MenorFone is int any) _alerts.Feed(f.Chave, any, charging: true, coarse: true);
            return;
        }
        var low = buds.OrderBy(x => x.Pct).First();
        string? side = buds.Count == 2 && buds[0].Pct != buds[1].Pct ? low.Side : null;
        FeedAlert(f, f.Chave, low.Pct, false, coarse: true, part: side);
    }

    private void TrocarFoto(Fone f, string foto)
    {
        f.Foto = foto;
        int px = Math.Max(1, (int)MathF.Round(CAIXA * S.Palco.Escala));
        Task.Run(() =>
        {
            var r = Retrato.DeArquivo(Path.Combine(Fotos.Pasta, foto), px, px, recortar: true);
            if (r != null) S.NaInterface(() => { f.Retrato = r; Atualizar(f); });
        });
    }

    // ------------------------------------------------------------------ G733

    private void IniciarG733()
    {
        foreach (var pid in HidPP.ProdutosG733)
        {
            try
            {
                var w = DeviceInformation.CreateWatcher(HidDevice.GetDeviceSelector(HidPP.PaginaHidPP, HidPP.UsoHidPP, HidPP.Logitech, pid));
                bool enumerou = false;
                w.Added += (_, d) => { bool inicial = !enumerou; S.NaInterface(() => G733Apareceu(d.Id, inicial)); };
                w.Removed += (_, d) => S.NaInterface(() => G733Sumiu(d.Id));
                w.EnumerationCompleted += (_, _) => enumerou = true;
                w.Start();
                _vigiasHid.Add(w);
            }
            catch (Exception e) { Log.Escrever("fones: vigia do G733 não começou: " + e.Message); }
        }
    }

    private void PararG733()
    {
        foreach (var w in _vigiasHid)
            try { if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop(); } catch { }
        _vigiasHid.Clear();
        foreach (var g in _g733.Values) g.Leitor.Dispose();
        _g733.Clear();
    }

    private void G733Apareceu(string caminho, bool inicial)
    {
        if (_parado || !_cfg.G733 || _g733.ContainsKey(caminho)) return;
        var f = new Fone { Chave = caminho, Nome = "G733", Tipo = TipoAparelho.Fone, G733 = true };
        var leitor = new LeitorG733(caminho, TimeSpan.FromMinutes(Math.Max(1, _cfg.IntervaloG733Minutos)), r => S.NaInterface(() => G733Respondeu(caminho, r)));
        // a ilha abrindo com o headset já ligado não é "conectou"
        _g733[caminho] = (leitor, new EstadoG733(silenciarPrimeira: inicial), f);
        leitor.Iniciar();
        Log.Escrever("fones: receptor do G733 presente");
    }

    private void G733Sumiu(string caminho)
    {
        if (!_g733.Remove(caminho, out var g)) return;
        HideTile(g.Fone);
        g.Leitor.Dispose();
        Log.Escrever("fones: receptor do G733 saiu");
    }

    private void G733Respondeu(string caminho, RespostaG733? r)
    {
        if (_parado || !_g733.TryGetValue(caminho, out var g)) return;
        var m = r == null ? g.Estado.SemResposta() : g.Estado.Receber(r.Value);
        if (g.Estado.Ultima is { } u)
        {
            // G HUB gives the exact number (the voltage curve is an estimate that can be a few points off)
            var gh = _ghubG733;
            bool fresh = gh is { } x && Environment.TickCount64 - x.At < 3L * Math.Max(30, _cfg.IntervaloGHubSegundos) * 1000 + 10_000;
            g.Fone.Bateria = fresh ? gh!.Value.Percent : u.Porcentagem;
            g.Fone.Carregando = fresh ? gh!.Value.Charging : u.Carregando;
            g.Fone.PorGHub = fresh;
        }
        if (m == MudancaG733.Ligou)
        {
            Log.Escrever($"fones: G733 ligou ({g.Estado.Ultima?.Milivolts} mV)");
            if (Ignorado("G733")) return;
            g.Fone.Foto = EscolherFoto(g.Fone);
            PrepararEMostrar(g.Fone, lerBateriaDoWindows: false);
        }
        else if (m == MudancaG733.Desligou) Log.Escrever("fones: G733 desligou");
        else if (m == MudancaG733.Atualizou) Atualizar(g.Fone);

        if (g.Estado.Ligado == true && g.Fone.Bateria is int pct)
        {
            ShowTile(g.Fone);
            FeedAlert(g.Fone, "g733", pct, g.Fone.Carregando, coarse: false);
        }
        else if (g.Estado.Ligado != true)
        {
            HideTile(g.Fone);
            _alerts.Forget("g733");
        }
    }

    // ------------------------------------------------------------------ G HUB (mouse and exact G733 percentage)

    private void ProgramGHub()
    {
        if (_parado) return;
        if (!_cfg.GHub || !(_cfg.Mouse || _cfg.G733)) { _ghubTimer.Stop(); _ghubG733 = null; return; }
        int ms = Math.Max(30, _cfg.IntervaloGHubSegundos) * 1000;
        if (_ghubTimer.Enabled && _ghubTimer.Interval == ms) return;
        _ghubTimer.Interval = ms;
        _ghubTimer.Start();
        PollGHub();
    }

    private void PollGHub()
    {
        if (_parado || _ghubBusy || !_cfg.GHub) return;
        _ghubBusy = true;
        Task.Run(async () =>
        {
            var devices = await GHubClient.ReadAll().ConfigureAwait(false);
            S.NaInterface(() =>
            {
                _ghubBusy = false;
                if (!_parado) GHubRead(devices);
            });
        });
    }

    private void GHubRead(List<GHubDevice>? devices)
    {
        // G HUB closed (or no G733 in its list): the G733 goes back to the voltage curve
        _ghubG733 = null;
        var seen = new HashSet<string>();
        foreach (var d in devices ?? new List<GHubDevice>())
        {
            if (d.IsG733 && _cfg.G733) G733FromGHub(d);
            else if (d.IsMouse && _cfg.Mouse) { MouseRead(d); seen.Add(d.Id); }
        }
        // a mouse G HUB no longer reports (or G HUB closed): no number to show
        foreach (var id in _mice.Keys.Where(k => !seen.Contains(k)).ToList()) HideTile(_mice[id]);
    }

    private void G733FromGHub(GHubDevice d)
    {
        _ghubG733 = (d.Percent, d.Charging, Environment.TickCount64);
        // only while the headset is on (the HID++ side says so); G HUB can keep the last value of a headset that is off
        var on = _g733.Values.FirstOrDefault(x => x.Estado.Ligado == true);
        if (on.Fone == null) return;
        on.Fone.Bateria = d.Percent;
        on.Fone.Carregando = d.Charging;
        on.Fone.PorGHub = true;
        Atualizar(on.Fone);
        ShowTile(on.Fone);
        FeedAlert(on.Fone, "g733", d.Percent, d.Charging, coarse: false);
    }

    private void MouseRead(GHubDevice d)
    {
        if (!_mice.TryGetValue(d.Id, out var f))
        {
            _mice[d.Id] = f = new Fone
            {
                Chave = "ghub:" + d.Id, Nome = d.Name.Length > 0 ? d.Name : "G502 X PLUS", Tipo = TipoAparelho.Mouse, Mouse = true, PorGHub = true,
            };
            Log.Escrever($"fones: mouse {f.Nome} no G HUB, {d.Percent}%{(d.Charging ? " (carregando)" : "")}");
        }
        f.Bateria = d.Percent;
        f.Carregando = d.Charging;
        ShowTile(f);
        FeedAlert(f, f.Chave, d.Percent, d.Charging, coarse: false);
    }

    // ------------------------------------------------------------------ the batteries tile in the main menu

    private const string TILE_ID = "baterias";
    /// <summary>The devices in the batteries tile (on, connected or listed by G HUB), mouse first.</summary>
    private readonly List<Fone> _shown = new();

    /// <summary>The device enters the menu's batteries tile (photo and iPhone-style battery); a later call only redraws.</summary>
    private void ShowTile(Fone f)
    {
        if (!_cfg.BateriaNoMenu || _parado) return;
        if (_shown.Contains(f)) { S.Menu.Redesenhar(); return; }
        _shown.Add(f);
        _tiles.Add(f.Chave);
        UpdateTile();
        if (f.RetratoMenu != null) return;
        f.Foto ??= EscolherFoto(f);
        if (f.Foto == null) return;
        int px = Math.Max(1, (int)MathF.Round(PHOTO * S.Palco.Escala));
        string foto = f.Foto;
        Task.Run(() =>
        {
            var r = Retrato.DeArquivo(Path.Combine(Fotos.Pasta, foto), px, px, recortar: true);
            if (r != null) S.NaInterface(() => { f.RetratoMenu = r; S.Menu.Redesenhar(); });
        });
    }

    private void HideTile(Fone f)
    {
        if (!_shown.Remove(f)) return;
        _tiles.Remove(f.Chave);
        UpdateTile();
    }

    /// <summary>One tile for all the devices; its size follows how many there are (the menu repacks when it changes).</summary>
    private void UpdateTile()
    {
        _shown.Sort((a, b) => a.Mouse != b.Mouse ? (a.Mouse ? -1 : 1) : string.Compare(MenuName(a), MenuName(b), StringComparison.CurrentCulture));
        if (_shown.Count == 0) { S.Menu.Remover(TILE_ID); return; }
        var (cols, rows) = TileSize(_shown.Count);
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = TILE_ID, Dono = "fones", Nome = Lang.T("Baterias", "Batteries"), Ordem = 90, Cols = cols, Rows = rows,
            Abrir = () => { if (_shown.Count > 0) Mostrar(_shown[0], doMenu: true); },
            Draw = DrawTile,
            Parts = size => TileSlots(_shown.Count, size).Select((slot, i) =>
            {
                var f = _shown[i];
                return (slot, (Action)(() => Mostrar(f, doMenu: true)));
            }).ToList(),
        });
    }

    /// <summary>The photo's box (DIP).</summary>
    private const float PHOTO = 32;

    /// <summary>
    /// One device: a 1x1 tile; two or more: 2x2, one per line. (2x1 with two side by side left three empty cells next
    /// to the tall sound slider; 2x2 leaves one.)
    /// </summary>
    internal static (int Cols, int Rows) TileSize(int devices) => devices <= 1 ? (1, 1) : (2, 2);

    /// <summary>Where each device goes inside the tile (relative to it): the whole tile, or lines from the top (up to 4).</summary>
    internal static List<System.Drawing.RectangleF> TileSlots(int devices, System.Drawing.SizeF size)
    {
        var slots = new List<System.Drawing.RectangleF>();
        if (devices <= 0) return slots;
        if (devices == 1)
        {
            slots.Add(new(0, 0, size.Width, size.Height));
            return slots;
        }
        const float pad = 8;
        int n = Math.Min(devices, 4);
        float h = (size.Height - 2 * pad) / n;
        for (int i = 0; i < n; i++) slots.Add(new(0, pad + i * h, size.Width, h));
        return slots;
    }

    /// <summary>One number for the tile's battery: the emptier AirPod (the case when no bud was read), else the battery.</summary>
    internal static int? FooterLevel(BateriaApple? apple, int? bateria) =>
        apple != null ? apple.MenorFone ?? apple.Estojo : bateria;

    private void DrawTile(Vortice.Direct2D1.ID2D1DeviceContext dc, Palco p, System.Drawing.RectangleF r)
    {
        var slots = TileSlots(_shown.Count, r.Size);
        for (int i = 0; i < slots.Count; i++)
        {
            var f = _shown[i];
            var s = slots[i];
            s.Offset(r.X, r.Y);
            int? level = FooterLevel(f.BateriaApple, f.Bateria);
            if (_shown.Count == 1)
            {
                // photo on top, battery under it
                DrawPhoto(dc, p, new Vector2(s.X + s.Width / 2, s.Y + 5 + PHOTO / 2), f);
                BatteryPill.Draw(dc, p, s.X + (s.Width - BatteryPill.W) / 2, s.Y + 43, level, f.Carregando);
            }
            else
            {
                // a line: photo, name, battery on the right
                const float side = 12;
                DrawPhoto(dc, p, new Vector2(s.X + side + PHOTO / 2, s.Y + s.Height / 2), f);
                float px = s.Right - side - BatteryPill.Total;
                BatteryPill.Draw(dc, p, px, s.Y + (s.Height - BatteryPill.H) / 2, level, f.Carregando);
                float nx = s.X + side + PHOTO + 8;
                var m = p.Texto.Medir(MenuName(f), 12, Vortice.DirectWrite.FontWeight.Medium);
                p.Texto.Desenhar(dc, MenuName(f), new Vector2(nx, s.Y + (s.Height - m.Y) / 2), 12, Cor.Branco, Vortice.DirectWrite.FontWeight.Medium, largura: px - nx - 6);
            }
        }
    }

    internal static string MenuName(bool mouse, bool g733, bool apple, ushort? modelo, string nome) =>
        mouse ? "G502 X Plus" : g733 ? "G733" : apple ? "AirPods" : nome;

    private static string MenuName(Fone f) => MenuName(f.Mouse, f.G733, f.Apple, f.Modelo, f.Nome);

    /// <summary>The small line under the device name: "48%", or the two AirPods ("E 80 · D 90"). Null when nothing was read.</summary>
    internal static string? MenuDetail(BateriaApple? apple, int? bateria, bool carregando)
    {
        if (apple != null)
        {
            var partes = new List<string>();
            if (apple.Esquerdo is int e) partes.Add(Lang.T("E ", "L ") + e);
            if (apple.Direito is int d) partes.Add(Lang.T("D ", "R ") + d);
            if (partes.Count == 0 && apple.Estojo is int c) partes.Add(Lang.T("estojo ", "case ") + c);
            return partes.Count > 0 ? string.Join(" · ", partes) : null;
        }
        return bateria is int b ? b + "%" : null;
    }

    /// <summary>
    /// The photo centered on the point, over a faint round backdrop (a black device still reads on the dark tile); a
    /// drawing while the photo loads.
    /// </summary>
    private static void DrawPhoto(Vortice.Direct2D1.ID2D1DeviceContext dc, Palco p, Vector2 centro, Fone f)
    {
        Icone.Circulo(dc, centro, PHOTO / 2 + 2, new Vortice.Mathematics.Color4(1, 1, 1, 0.07f));
        if (f.RetratoMenu != null)
        {
            var tam = f.RetratoMenu.TamanhoDip(p.Escala);
            f.RetratoMenu.Desenhar(dc, p.Escala, centro.X - tam.X / 2, centro.Y - tam.Y / 2);
        }
        else if (f.Tipo == TipoAparelho.Mouse) Desenhos.Mouse(dc, p.G.Fabrica, centro, 24, Cor.Branco);
        else Desenhos.Fone(dc, p.G.Fabrica, centro, 24, Cor.Branco);
    }

    // ------------------------------------------------------------------ low battery alerts

    private void FeedAlert(Fone f, string key, int percent, bool charging, bool coarse, string? part = null)
    {
        if (!_cfg.AlertasBateria) return;
        if (_alerts.Feed(key, percent, charging, coarse) is not int level) return;
        Log.Escrever($"fones: bateria de {f.Nome} em {percent}% (nível {level})");
        if (Ignorado(f.Nome)) return;
        ShowLowBattery(f, percent, level, part, coarse);
    }

    /// <summary>The photo (if the device has none loaded yet) comes first, out of the interface thread.</summary>
    private void ShowLowBattery(Fone f, int percent, int level, string? part, bool coarse)
    {
        void Show() { if (!_parado) S.Ilha.Alertar(LowBatteryAlert(f, percent, level, part, coarse)); }
        if (f.Retrato != null) { Show(); return; }
        f.Foto ??= EscolherFoto(f);
        if (f.Foto == null) { Show(); return; }
        int px = Math.Max(1, (int)MathF.Round(CAIXA * S.Palco.Escala));
        string foto = f.Foto;
        Task.Run(() =>
        {
            var r = Retrato.DeArquivo(Path.Combine(Fotos.Pasta, foto), px, px, recortar: true);
            S.NaInterface(() => { if (r != null) f.Retrato = r; Show(); });
        });
    }

    private Alerta LowBatteryAlert(Fone f, int percent, int level, string? part, bool coarse) => new()
    {
        Tipo = "bateria",
        Som = "alerta",
        // he asked for these warnings, so they go through Do Not Disturb (it reads "alarms only" all the time on his PC)
        // and over full screen content; competitive games still silence them
        Importante = true,
        Segundos = level <= 10 ? 7f : (float?)null,
        Criar = p =>
        {
            var c = p.NovaCena(new Vector2(L, A));
            p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) => DesenharBateriaBaixa(p, dc, t, f, percent, level, part, coarse));
            return c;
        },
    };

    /// <summary>The line under the name: "Battery at 25%", "Low battery: 10%", "Battery almost empty: 1%".</summary>
    internal static string TextoBateriaBaixa(int percent, int level, string? part, bool coarse)
    {
        string n = coarse && percent == 0 ? "<10%" : percent + "%";
        string quem = part == null ? "" : Lang.T($"Fone {part} · ", $"{char.ToUpperInvariant(part[0])}{part[1..]} earbud · ");
        if (level <= 5) return quem + Lang.T("Bateria quase no fim: ", "Battery almost empty: ") + n;
        if (level <= 10) return quem + Lang.T("Bateria baixa: ", "Low battery: ") + n;
        return quem + Lang.T("Bateria em ", "Battery at ") + n;
    }

    private void DesenharBateriaBaixa(Palco p, Vortice.Direct2D1.ID2D1DeviceContext dc, Vector2 t, Fone f, int percent, int level, string? part, bool coarse)
    {
        var F = p.G.Fabrica;
        DesenharImagem(p, dc, t, f);
        var cor = percent <= 10 ? Cor.Vermelho : Cor.Laranja;
        const float PASSO = 42;
        float x0 = 64, largura = t.X - x0 - PASSO - 6;
        p.Texto.Desenhar(dc, f.G733 ? "G733" : f.Nome, new Vector2(x0, 9), 16, Cor.Branco, largura: largura);
        p.Texto.Desenhar(dc, TextoBateriaBaixa(percent, level, part, coarse), new Vector2(x0, 33), 12.5f, cor, FontWeight.Normal, largura: largura);
        var c = new Vector2(t.X - PASSO / 2, t.Y / 2);
        Icone.Anel(dc, F, c, 15, 3.4f, percent / 100f, cor);
        string n = coarse && percent == 0 ? "<10" : percent.ToString();
        var tn = p.Texto.Medir(n, 11, FontWeight.SemiBold, tabular: true);
        p.Texto.Desenhar(dc, n, new Vector2(c.X - tn.X / 2, c.Y - tn.Y / 2), 11, Cor.Branco, tabular: true);
    }

    // ------------------------------------------------------------------ alerta

    private void Mostrar(Fone f, bool doMenu = false)
    {
        S.Ilha.Alertar(new Alerta
        {
            Tipo = "fones",
            Som = doMenu ? "" : "conectou",
            Importante = doMenu,
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(L, A));
                var s = p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) => Desenhar(p, dc, t, f));
                _vivos.Add((s, f));
                if (f.Retrato == null && (f.Foto ??= EscolherFoto(f)) is string foto)
                {
                    int px = Math.Max(1, (int)MathF.Round(CAIXA * S.Palco.Escala));
                    Task.Run(() =>
                    {
                        var r = Retrato.DeArquivo(Path.Combine(Fotos.Pasta, foto), px, px, recortar: true);
                        if (r != null) S.NaInterface(() => { f.Retrato = r; Atualizar(f); });
                    });
                }
                c.AoSoltar += () => _vivos.RemoveAll(x => x.S == s);
                return c;
            },
        });
    }

    /// <summary>A bateria chegou ou mudou: redesenha o alerta dele se ainda estiver na tela.</summary>
    private void Atualizar(Fone f)
    {
        foreach (var (s, x) in _vivos.ToArray()) if (x == f) s.Redesenhar();
    }

    internal static List<(string Rotulo, int Pct, bool Carregando)> Partes(BateriaApple? apple, int? bateria, bool carregando, bool g733)
    {
        var r = new List<(string, int, bool)>();
        if (apple != null)
        {
            if (apple.Esquerdo is int e) r.Add((Lang.T("E", "L"), e, apple.CarregandoEsquerdo));
            if (apple.Direito is int d) r.Add((Lang.T("D", "R"), d, apple.CarregandoDireito));
            if (apple.Estojo is int c) r.Add((Lang.T("estojo", "case"), c, apple.CarregandoEstojo));
        }
        else if (bateria is int b) r.Add((g733 ? Lang.T("estimada", "estimate") : Lang.T("bateria", "battery"), b, carregando));
        return r;
    }

    internal static string Subtitulo(bool apple, bool g733, BateriaApple? ba, bool carregando)
    {
        if (g733) return carregando ? Lang.T("Ligado · carregando", "On · charging") : Lang.T("Ligado · bateria estimada pela tensão", "On · battery estimated from voltage");
        if (apple && ba == null) return Lang.T("Conectado · lendo a bateria", "Connected · reading the battery");
        return carregando ? Lang.T("Conectado · carregando", "Connected · charging") : Lang.T("Conectado", "Connected");
    }

    /// <summary>The photo of the device, or a drawn icon when there is no photo.</summary>
    private void DesenharImagem(Palco p, Vortice.Direct2D1.ID2D1DeviceContext dc, Vector2 t, Fone f)
    {
        var F = p.G.Fabrica;
        if (f.Retrato != null)
        {
            var tam = f.Retrato.TamanhoDip(p.Escala);
            f.Retrato.Desenhar(dc, p.Escala, (CAIXA - tam.X) / 2, (t.Y - tam.Y) / 2);
        }
        else
        {
            Icone.Retangulo(dc, 4, (t.Y - 44) / 2, 44, 44, 13, Cor.Fraco);
            var centro = new Vector2(26, t.Y / 2);
            if (f.Tipo == TipoAparelho.Controle) Desenhos.Controle(dc, F, centro, 28, Cor.Branco);
            else if (f.Tipo == TipoAparelho.Fone) Desenhos.Fone(dc, F, centro, 28, Cor.Branco);
            else if (f.Tipo == TipoAparelho.Mouse) Desenhos.Mouse(dc, F, centro, 28, Cor.Branco);
            else Desenhos.Bluetooth(dc, F, centro, 24, Cor.Azul);
        }
    }

    private void Desenhar(Palco p, Vortice.Direct2D1.ID2D1DeviceContext dc, Vector2 t, Fone f)
    {
        var F = p.G.Fabrica;
        DesenharImagem(p, dc, t, f);
        var partes = Partes(f.BateriaApple, f.Bateria, f.Carregando, f.G733 && !f.PorGHub);
        const float PASSO = 42;
        float larguraPartes = partes.Count * PASSO;
        float x0 = 64, largura = t.X - x0 - larguraPartes - 6;
        p.Texto.Desenhar(dc, f.G733 ? Lang.T("G733 conectado", "G733 connected") : f.Nome, new Vector2(x0, 9), 16, Cor.Branco, largura: largura);
        p.Texto.Desenhar(dc, Subtitulo(f.Apple, f.G733 && !f.PorGHub, f.BateriaApple, f.Carregando), new Vector2(x0, 33), 12.5f, Cor.Secundario, FontWeight.Normal, largura: largura);
        for (int i = 0; i < partes.Count; i++)
        {
            var (rotulo, pct, carregando) = partes[i];
            var cor = carregando ? Cor.Verde : pct <= 20 ? Cor.Vermelho : Cor.Branco;
            var c = new Vector2(t.X - larguraPartes + i * PASSO + PASSO / 2, 22);
            Icone.Anel(dc, F, c, 13, 3, pct / 100f, cor);
            string n = pct.ToString();
            var tn = p.Texto.Medir(n, 10, FontWeight.SemiBold, tabular: true);
            p.Texto.Desenhar(dc, n, new Vector2(c.X - tn.X / 2, c.Y - tn.Y / 2), 10, Cor.Branco, tabular: true);
            var tr = p.Texto.Medir(rotulo, 9.5f, FontWeight.Normal);
            p.Texto.Desenhar(dc, rotulo, new Vector2(c.X - tr.X / 2, 40), 9.5f, Cor.Secundario, FontWeight.Normal);
        }
    }

    // ------------------------------------------------------------------ autoteste

    public override string Autoteste() => TestesFones.Rodar();
}

public sealed class ConfigFones
{
    /// <summary>Pedaços de nome de aparelho que não abrem a ilha (ex.: "Teclado").</summary>
    [Label("Aparelhos ignorados", "Ignored devices")]
    [Help("Pedaços de nome de aparelho que não abrem a ilha (ex.: \"Teclado\")", "Parts of a device name that don't open the island (e.g. \"Keyboard\")")]
    [JsonPropertyName("ignorar")] public List<string> Ignorar { get; set; } = new();
    /// <summary>Pedaço do nome do aparelho -> arquivo de imagens/fones (quando o casamento automático erra).</summary>
    [Label("Fotos por aparelho", "Photos per device")]
    [Help("Pedaço do nome do aparelho -> arquivo de imagens/fones (quando o casamento automático erra)", "Part of the device name -> file in imagens/fones (when the automatic match is wrong)")]
    [JsonPropertyName("fotos")] public Dictionary<string, string> Fotos { get; set; } = new();
    [Label("Cor do G733 (foto)", "G733 color (photo)")]
    [Help("Palavra do nome do arquivo da foto, como \"preto\"", "Word from the photo file name, such as \"preto\"")]
    [JsonPropertyName("corG733")] public string CorG733 { get; set; } = "preto";
    [Label("Ler a bateria dos AirPods", "Read AirPods battery")]
    [JsonPropertyName("bateriaAirPods")] public bool BateriaAirPods { get; set; } = true;
    /// <summary>De quanto em quanto tempo uma rajada de varredura BLE relê a bateria dos AirPods (0 = só ao conectar).</summary>
    [Label("Intervalo da bateria dos AirPods (segundos)", "AirPods battery interval (seconds)")]
    [Help("De quanto em quanto tempo uma varredura BLE relê a bateria (0 = só ao conectar)", "How often a BLE scan rereads the battery (0 = only when connecting)")]
    [JsonPropertyName("intervaloAirPodsSegundos")] public int IntervaloAirPodsSegundos { get; set; } = 60;
    [Label("Duração da varredura (segundos)", "Scan duration (seconds)")]
    [JsonPropertyName("rajadaSegundos")] public int RajadaSegundos { get; set; } = 5;
    /// <summary>Sinal mínimo (dBm) para aceitar um anúncio: AirPods de outra pessoa na sala chegam mais fracos.</summary>
    [Label("Sinal mínimo (dBm)", "Minimum signal (dBm)")]
    [Help("AirPods de outra pessoa na sala chegam mais fracos", "Someone else's AirPods in the room arrive weaker")]
    [JsonPropertyName("sinalMinimo")] public int SinalMinimo { get; set; } = -75;
    [Label("Headset Logitech G733", "Logitech G733 headset")]
    [JsonPropertyName("g733")] public bool G733 { get; set; } = true;
    [Label("Intervalo da bateria do G733 (minutos)", "G733 battery interval (minutes)")]
    [JsonPropertyName("intervaloG733Minutos")] public int IntervaloG733Minutos { get; set; } = 5;
    [Label("Alertas de bateria baixa", "Low battery alerts")]
    [Help("Avisa quando a bateria cai por cada nível. Os AirPods informam de 10 em 10: lá vira 50, 20, 10 e abaixo de 10",
        "Warns when the battery falls past each level. AirPods report in steps of 10: there it becomes 50, 20, 10 and under 10")]
    [JsonPropertyName("alertasBateria")] public bool AlertasBateria { get; set; } = true;
    [Label("Níveis do alerta (%)", "Alert levels (%)")]
    [Help("Separados por vírgula", "Separated by commas")]
    [JsonPropertyName("limiaresBateria")] public string LimiaresBateria { get; set; } = "50, 25, 10, 5, 1";
    [Label("Ler a bateria pelo G HUB", "Read the battery from G HUB")]
    [Help("O mouse G502 X Plus e a porcentagem exata do G733 vêm do G HUB, que precisa estar aberto",
        "The G502 X Plus mouse and the exact G733 percentage come from G HUB, which has to be running")]
    [JsonPropertyName("ghub")] public bool GHub { get; set; } = true;
    [Label("Mouse G502 X Plus", "G502 X Plus mouse")]
    [JsonPropertyName("mouse")] public bool Mouse { get; set; } = true;
    [Label("Cor do mouse (foto)", "Mouse color (photo)")]
    [Help("preto, roxo, branco ou menta", "preto, roxo, branco or menta (black, purple, white or mint)")]
    [JsonPropertyName("corMouse")] public string CorMouse { get; set; } = "preto";
    [Label("Bateria no menu", "Battery in the menu")]
    [Help("Cada aparelho ligado aparece no menu principal com a bateria", "Each connected device shows up in the main menu with its battery")]
    [JsonPropertyName("bateriaNoMenu")] public bool BateriaNoMenu { get; set; } = true;
    [Label("Intervalo do G HUB (segundos)", "G HUB interval (seconds)")]
    [JsonPropertyName("intervaloGHubSegundos")] public int IntervaloGHubSegundos { get; set; } = 60;
}
