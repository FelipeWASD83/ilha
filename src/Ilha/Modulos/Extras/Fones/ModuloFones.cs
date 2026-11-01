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

    public override Type? TipoConfig => typeof(ConfigFones);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigFones>(cfg);
        _rajadas.Tick += (_, _) => Rajada();
        _varredura = new VarreduraApple(BateriaAppleChegou);
        _bt = new VigiaBluetooth(BluetoothMudou);
        _bt.Iniciar();
        if (_cfg.G733) IniciarG733();
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        bool g733Antes = _cfg.G733;
        _cfg = Ler<ConfigFones>(cfg);
        if (_cfg.G733 && !g733Antes) IniciarG733();
        else if (!_cfg.G733 && g733Antes) PararG733();
        ProgramarRajadas();
    }

    public override void Parar()
    {
        _parado = true;
        _rajadas.Stop();
        _rajadas.Dispose();
        _varredura?.Dispose();
        _varredura = null;
        _bt?.Dispose();
        _bt = null;
        PararG733();
        _apple.Clear();
        _vivos.Clear();
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
        return Fotos.Escolher(f.Nome, arquivos, _cfg.Fotos, _cfg.CorG733);
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
        }
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
        g.Leitor.Dispose();
        Log.Escrever("fones: receptor do G733 saiu");
    }

    private void G733Respondeu(string caminho, RespostaG733? r)
    {
        if (_parado || !_g733.TryGetValue(caminho, out var g)) return;
        var m = r == null ? g.Estado.SemResposta() : g.Estado.Receber(r.Value);
        if (g.Estado.Ultima is { } u)
        {
            g.Fone.Bateria = u.Porcentagem;
            g.Fone.Carregando = u.Carregando;
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
    }

    // ------------------------------------------------------------------ alerta

    private void Mostrar(Fone f)
    {
        S.Ilha.Alertar(new Alerta
        {
            Tipo = "fones",
            Som = "conectou",
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(L, A));
                var s = p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) => Desenhar(p, dc, t, f));
                _vivos.Add((s, f));
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

    private void Desenhar(Palco p, Vortice.Direct2D1.ID2D1DeviceContext dc, Vector2 t, Fone f)
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
            else Desenhos.Bluetooth(dc, F, centro, 24, Cor.Azul);
        }
        var partes = Partes(f.BateriaApple, f.Bateria, f.Carregando, f.G733);
        const float PASSO = 42;
        float larguraPartes = partes.Count * PASSO;
        float x0 = 64, largura = t.X - x0 - larguraPartes - 6;
        p.Texto.Desenhar(dc, f.G733 ? Lang.T("G733 conectado", "G733 connected") : f.Nome, new Vector2(x0, 9), 16, Cor.Branco, largura: largura);
        p.Texto.Desenhar(dc, Subtitulo(f.Apple, f.G733, f.BateriaApple, f.Carregando), new Vector2(x0, 33), 12.5f, Cor.Secundario, FontWeight.Normal, largura: largura);
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
}
