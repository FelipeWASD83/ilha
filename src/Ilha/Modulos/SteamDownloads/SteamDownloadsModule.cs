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
    private readonly Dictionary<string, DateTime> _pausedSince = new();
    private static readonly TimeSpan PausedOnPill = TimeSpan.FromMinutes(10);

    public override Type? TipoConfig => typeof(ConfigDownloads);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigDownloads>(cfg);
        _activity = new DownloadsActivity(S);
        S.Ilha.Adicionar(_activity);
        _soon.Tick += (_, _) => { _soon.Stop(); Look(); };
        _tick.Tick += (_, _) => Look();
        _tick.Start();
        // a photo of the pill (development): ILHA_FOTO_DOWNLOAD=1 shows a made-up download 5 s after opening
        if (Environment.GetEnvironmentVariable("ILHA_FOTO_DOWNLOAD") == "1")
        {
            var t = new System.Windows.Forms.Timer { Interval = 5000 };
            t.Tick += (_, _) =>
            {
                t.Stop();
                t.Dispose();
                _activity?.Show(new() { (new Download("app:730", 730, "Counter-Strike 2", 1_240_000_000, 4_500_000_000, false, false), 32e6) });
            };
            t.Start();
        }
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
            // Steam's own measured rate when the log has one (the count jumps every 5 minutes with its stats lines)
            if (d.Rate is double steamRate && !d.Paused) speed = steamRate;
            _rate[d.Key] = (d.Done, at, speed);
            items.Add((d, speed));
        }
        foreach (var k in _rate.Keys.Where(k => now.All(d => d.Key != k)).ToList()) _rate.Remove(k);

        // what left the list: only a game whose manifest says installed, with no update still required and every byte
        // downloaded, finished (4.0.1: a cancelled update stays "installed" with "update required" and was announced as
        // ready). Workshop folders vanish the same way whether done or cancelled: no notice for them.
        if (!_first)
            foreach (var (key, d) in _last)
            {
                if (now.Any(x => x.Key == key)) continue;
                bool done = !d.Workshop && manifests.TryGetValue(d.App, out var m) && SteamLibrary.Finished(m);
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
        // a paused download stays on the pill for 10 minutes, then leaves it (Steam can keep an update paused for days)
        foreach (var d in now)
            if (d.Paused) _pausedSince.TryAdd(d.Key, at); else _pausedSince.Remove(d.Key);
        foreach (var k in _pausedSince.Keys.Where(k => now.All(d => d.Key != k)).ToList()) _pausedSince.Remove(k);
        var onPill = items.Where(i => !i.Item1.Paused || at - _pausedSince[i.Item1.Key] < PausedOnPill).ToList();
        _activity?.Show(_cfg.MostrarNaPilula ? onPill : new());
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
        Ok("downloads: só conta como terminado o que baixou tudo (atualização cancelada não)",
            SteamLibrary.Finished(new SteamLibrary.Manifest(730, "x", 4, 100, 100)) && !SteamLibrary.Finished(new SteamLibrary.Manifest(730, "x", 6, 100, 40))
            && !SteamLibrary.Finished(new SteamLibrary.Manifest(730, "x", 4, 100, 40)) && !SteamLibrary.Finished(new SteamLibrary.Manifest(730, "x", 1026, 100, 40)));
        var d = new Download("app:730", 730, "Counter-Strike 2", 500_000_000, 2_000_000_000, false, false);
        Ok("downloads: detalhe com velocidade e tempo", DownloadsActivity.Detail(d, 25e6).Contains("/s") && DownloadsActivity.Detail(d, 25e6).Contains("min"),
            DownloadsActivity.Detail(d, 25e6));
        Ok("downloads: fração", d.Fraction is double f && Math.Abs(f - 0.25) < 1e-9);
        // 4.5.1: the content log (the manifest stays still during a download and the folder is full size from the start)
        var log = new[]
        {
            "[2026-11-15 13:23:46] AppID 1174180 update started : download 5429984/120936093232, store 0/0, reuse 0/0, delta 0/0, stage 7332642/128327186218 ",
            "[2026-11-15 13:28:46] stats: (SteamCache, 452) cache10-gru1.steamcontent.com: 1000 Bytes, 285 sec (5.31 Mbps). 5281 Hits / 0 Misses (100 %, 100 % bytes), score 0.9342",
            "[2026-11-15 13:28:46] stats: (Invalid, 0) : 3000000000 Bytes, 300 sec (80.00 Mbps). 41974 Hits / 12042 Misses (78 %, 79 % bytes), score 0.0000",
            "[2026-11-15 13:37:16] AppID 1174180 update canceled : Disabled (Suspended)",
            "[2026-11-15 13:40:00] stats: (Invalid, 0) : 999 Bytes, 300 sec (0.00 Mbps). 0 Hits / 0 Misses (0 %, 0 % bytes), score 0.0000",
            "[2026-11-15 13:46:52] AppID 1174180 update started : download 8341920208/120936093232, store 0/0, reuse 0/0, delta 0/0, stage 8486384745/128327186218 ",
            "[2026-11-15 13:51:52] stats: (Invalid, 0) : 2888789824 Bytes, 300 sec (76.93 Mbps). 41974 Hits / 12042 Misses (78 %, 79 % bytes), score 0.0000",
            "[2026-11-15 13:52:00] Current download rate: 80.000 Mbps",
            "[2026-11-15 13:56:52] stats: (Invalid, 0) : 3000000000 Bytes, 300 sec (80.00 Mbps). 41974 Hits / 12042 Misses (78 %, 79 % bytes), score 0.0000",
            "[2026-11-15 13:57:00] AppID 730 update started : download 0/500, store 0/0, reuse 0/0, delta 0/0, stage 0/600 ",
            "[2026-11-15 13:58:00] stats: (Invalid, 0) : 400 Bytes, 300 sec (0.01 Mbps). 1 Hits / 0 Misses (100 %, 100 % bytes), score 0.0000",
        };
        var runs = ContentLog.Parse(log);
        var rdr = runs.GetValueOrDefault(1174180);
        Ok("downloads: log da Steam, a última partida e as stats dela", rdr is { Start: 8341920208, Total: 120936093232, Since: 5888789824, Ended: true }
            && rdr.Downloaded(new DateTime(2026, 11, 15, 14, 30, 0)) == 8341920208 + 5888789824, $"{rdr?.Start} + {rdr?.Since}, {(rdr?.Ended == true ? "fechada" : "aberta")}");
        var cs = runs.GetValueOrDefault(730);
        Ok("downloads: log da Steam, outra partida não herda a taxa da anterior",
            cs is { Since: 400, Ended: false, Rate: null } && cs.Downloaded(new DateTime(2026, 11, 15, 13, 59, 0)) == 400, $"{cs?.Downloaded(new DateTime(2026, 11, 15, 13, 59, 0))}");
        var open = ContentLog.Parse(log[5..8]).GetValueOrDefault(1174180);
        Ok("downloads: log da Steam, 2 min depois da última stats a 80 Mbps",
            open != null && open.Downloaded(new DateTime(2026, 11, 15, 13, 53, 52)) == 8341920208 + 2888789824 + 1_200_000_000,
            $"{open?.Downloaded(new DateTime(2026, 11, 15, 13, 53, 52))}");
        Ok("downloads: log da Steam, taxa velha (Steam fechada) não soma nada e nada passa do total",
            open != null && open.Downloaded(new DateTime(2026, 11, 15, 14, 30, 0)) == 8341920208 + 2888789824
            && (open with { Since = 200_000_000_000 }).Downloaded(DateTime.Now) == 120936093232);
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
