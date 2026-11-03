using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Ilha.Games;

namespace Ilha.SteamDownloads;

/// <summary>
/// Steam downloads (3.9.0, his request): games, updates and Workshop items. A watcher on each library's steamapps folder
/// wakes a look (no polling while nothing downloads, besides one look a minute in case a watcher missed it); while
/// something downloads, a look every 2 s gives the progress and the speed. A game that finishes gets a notice.
/// </summary>
[Modulo("downloads-steam", "Downloads da Steam", English = "Steam downloads")]
internal sealed class SteamDownloadsModule : Modulo
{
    private ConfigDownloads _cfg = new();
    private DownloadsActivity? _activity;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly System.Windows.Forms.Timer _soon = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 60_000 };
    private List<string> _libraries = new();
    private Dictionary<string, Download> _last = new();
    private readonly Dictionary<string, (long Bytes, DateTime At, double? Speed)> _rate = new();
    private bool _stopped, _scanning, _first = true;

    public override Type? TipoConfig => typeof(ConfigDownloads);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigDownloads>(cfg);
        _activity = new DownloadsActivity(S);
        S.Ilha.Adicionar(_activity);
        _soon.Tick += (_, _) => { _soon.Stop(); Look(); };
        _tick.Tick += (_, _) => Look();
        _tick.Start();
        _ = Task.Run(() =>
        {
            var libs = GameSetup.SteamLibraries();
            S.NaInterface(() =>
            {
                if (_stopped) return;
                _libraries = libs;
                Watch();
                Look();
            });
        });
    }

    public override void ConfigMudou(JsonObject cfg) => _cfg = Ler<ConfigDownloads>(cfg);

    private void Watch()
    {
        foreach (var lib in _libraries)
        {
            string apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            try
            {
                var w = new FileSystemWatcher(apps)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024,
                };
                // the manifests, the downloading folder and the Workshop downloads; the game files themselves are not
                // looked at (an update rewriting thousands of files would wake this every time)
                FileSystemEventHandler h = (_, e) =>
                {
                    string rel = e.FullPath.Length > apps.Length ? e.FullPath[(apps.Length + 1)..] : "";
                    if (rel.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("downloading", StringComparison.OrdinalIgnoreCase)
                        || rel.StartsWith(Path.Combine("workshop", "downloads"), StringComparison.OrdinalIgnoreCase))
                        S.NaInterface(() => { if (!_stopped && !_soon.Enabled) _soon.Start(); });
                };
                w.Created += h;
                w.Changed += h;
                w.Deleted += h;
                w.Renamed += (s, e) => h(s, e);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception e) { Log.Escrever($"downloads da steam: não deu para vigiar {apps}: {e.Message}"); }
        }
    }

    private void Look()
    {
        if (_stopped || _scanning || _libraries.Count == 0) return;
        _scanning = true;
        var libs = _libraries;
        _ = Task.Run(() =>
        {
            var r = SteamLibrary.Scan(libs);
            S.NaInterface(() =>
            {
                _scanning = false;
                if (!_stopped) Apply(r.Now, r.Manifests);
            });
        });
    }

    private void Apply(List<Download> now, Dictionary<int, SteamLibrary.Manifest> manifests)
    {
        var at = DateTime.UtcNow;
        var items = new List<(Download, double?)>();
        foreach (var d in now)
        {
            double? speed = null;
            if (_rate.TryGetValue(d.Key, out var old) && !d.Paused)
            {
                double secs = (at - old.At).TotalSeconds;
                if (secs >= 1 && d.Done >= old.Bytes)
                {
                    double inst = (d.Done - old.Bytes) / secs;
                    // smoothed: Steam writes in bursts
                    speed = old.Speed is double s ? s * 0.6 + inst * 0.4 : inst;
                }
                else speed = old.Speed;
            }
            _rate[d.Key] = (d.Done, at, speed);
            items.Add((d, speed));
        }
        foreach (var k in _rate.Keys.Where(k => now.All(d => d.Key != k)).ToList()) _rate.Remove(k);

        // what left the list: a game whose manifest says installed finished; Workshop items count when the folder went away
        if (!_first)
            foreach (var (key, d) in _last)
            {
                if (now.Any(x => x.Key == key)) continue;
                bool done = d.Workshop || manifests.TryGetValue(d.App, out var m) && SteamLibrary.IsInstalled(m.Flags);
                if (done && !d.Paused && _cfg.AvisarAoTerminar)
                {
                    Log.Escrever($"downloads da steam: {d.Name} terminou");
                    S.Ilha.Alertar(new Alerta
                    {
                        Tipo = "download-steam", Som = "chegada", Importante = true, Segundos = 4,
                        Criar = p => DownloadsActivity.DoneCard(p, d),
                    });
                }
            }
        foreach (var d in now.Where(d => !_last.ContainsKey(d.Key))) Log.Escrever($"downloads da steam: baixando {d.Name}{(d.Workshop ? " (oficina)" : "")}");
        _first = false;
        _last = now.ToDictionary(d => d.Key);
        _activity?.Show(_cfg.MostrarNaPilula ? items : new());
        // a look every 2 s while something downloads, back to one a minute after
        _tick.Interval = now.Any(d => !d.Paused) ? 2000 : 60_000;
    }

    public override void Parar()
    {
        _stopped = true;
        foreach (var w in _watchers) { try { w.Dispose(); } catch (Exception) { } }
        _watchers.Clear();
        _soon.Stop();
        _soon.Dispose();
        _tick.Stop();
        _tick.Dispose();
    }

    public override string Autoteste()
    {
        var r = new StringBuilder();
        void Ok(string n, bool ok, string d = "") => r.AppendLine((ok ? "ok      " : "FALHOU  ") + n + (d.Length > 0 ? $" ({d})" : ""));
        string acf = "\"AppState\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"name\"\t\t\"Counter-Strike 2\"\n\t\"StateFlags\"\t\t\"1026\"\n" +
                     "\t\"BytesToDownload\"\t\t\"2000000000\"\n\t\"BytesDownloaded\"\t\t\"500000000\"\n\t\"UserConfig\"\n\t{\n\t\t\"name\"\t\t\"outro\"\n\t}\n}";
        var m = SteamLibrary.ReadManifest(acf);
        Ok("downloads: lê o manifesto (o primeiro nome, não o de dentro)", m is { App: 730, Name: "Counter-Strike 2", Flags: 1026, ToDownload: 2000000000, Downloaded: 500000000 });
        Ok("downloads: estados", SteamLibrary.IsDownloading(1026) && !SteamLibrary.IsDownloading(4) && !SteamLibrary.IsDownloading(6)
            && SteamLibrary.IsInstalled(4) && SteamLibrary.IsPaused(1538) && !SteamLibrary.IsPaused(1026));
        var d = new Download("app:730", 730, "Counter-Strike 2", 500_000_000, 2_000_000_000, false, false);
        Ok("downloads: detalhe com velocidade e tempo", DownloadsActivity.Detail(d, 25e6).Contains("/s") && DownloadsActivity.Detail(d, 25e6).Contains("min"),
            DownloadsActivity.Detail(d, 25e6));
        Ok("downloads: fração", d.Fraction is double f && Math.Abs(f - 0.25) < 1e-9);
        // the real libraries, read only
        var libs = GameSetup.SteamLibraries();
        var (now, manifests) = SteamLibrary.Scan(libs);
        Ok("downloads: bibliotecas da Steam lidas", manifests.Count > 0, $"{libs.Count} bibliotecas, {manifests.Count} jogos, {now.Count} baixando agora"
            + (now.Count > 0 ? ": " + string.Join(", ", now.Select(x => $"{x.Name} {x.Fraction:P0}")) : ""));
        return r.ToString().TrimEnd();
    }
}

public sealed class ConfigDownloads
{
    [Label("Mostrar na pílula", "Show on the pill")]
    [JsonPropertyName("mostrarNaPilula")] public bool MostrarNaPilula { get; set; } = true;

    [Label("Avisar quando terminar", "Notify when finished")]
    [JsonPropertyName("avisarAoTerminar")] public bool AvisarAoTerminar { get; set; } = true;
}
