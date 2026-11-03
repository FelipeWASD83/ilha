using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Ilha.Weather;

/// <summary>
/// Weather (3.9.0, his request): Open-Meteo for the city in the settings, read every 10 minutes. When the sky changes
/// group (clear, cloudy, fog, drizzle, rain, snow, storm) and the next reading agrees, the collapsed island gets a
/// notice ("Nublado → Chuva · 24°"). The menu has a "Clima" ball with the symbol and the temperature; it opens the card
/// with the next hours.
/// </summary>
[Modulo("clima", "Clima", English = "Weather")]
internal sealed class WeatherModule : Modulo
{
    private ConfigClima _cfg = new();
    private WeatherActivity? _activity;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 10 * 60_000 };
    private Place? _place;
    private bool _stopped, _busy, _again;
    // each change of city starts a new generation: a reading that left before the change is thrown away when it arrives
    // (4.0.1: it used to land after the change and keep the old city's position for good)
    private int _generation;
    private readonly SkyWatch _watch = new();

    internal WeatherNow? Now { get; private set; }
    internal string? Problem { get; private set; }
    internal string PlaceName => _place?.Name ?? _cfg.Cidade;

    private static string CacheFile => Path.Combine(Config.Pasta, "clima.json");

    public override Type? TipoConfig => typeof(ConfigClima);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigClima>(cfg);
        _activity = new WeatherActivity(S, this);
        S.Ilha.Adicionar(_activity);
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "clima", Nome = Lang.T("Clima", "Weather"), Ordem = 64,
            Abrir = () => _activity?.Open(),
            Icone = (dc, p, r) =>
            {
                if (Now is { } w) WeatherIcons.Draw(dc, p.G.Fabrica, r, w.Sky, w.Day, Cor.Branco);
                else WeatherIcons.Draw(dc, p.G.Fabrica, r, Sky.Cloudy, true, Cor.Branco);
            },
            Detalhe = () => Now is { } w ? WeatherActivity.Deg(w.Temp) : null,
        });
        _tick.Tick += (_, _) => Read();
        _tick.Start();
        Read();
        // a photo of the notice (development): ILHA_FOTO_CLIMA=1 shows a change 5 s after opening
        if (Environment.GetEnvironmentVariable("ILHA_FOTO_CLIMA") == "1")
        {
            var t = new System.Windows.Forms.Timer { Interval = 5000 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); Notice(Sky.Cloudy, Sky.Rain, true, 24); };
            t.Start();
        }
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        string old = _cfg.Cidade;
        _cfg = Ler<ConfigClima>(cfg);
        if (!string.Equals(old.Trim(), _cfg.Cidade.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            _place = null;
            Now = null;
            _watch.Reset();
            _generation++;
            if (_busy) _again = true; else Read();
        }
    }

    public override void Parar()
    {
        _stopped = true;
        _tick.Stop();
        _tick.Dispose();
    }

    internal void RefreshIfOld()
    {
        if (Now == null || DateTime.Now - Now.ReadAt > TimeSpan.FromMinutes(5)) Read();
    }

    private void Read()
    {
        if (_stopped || _busy) return;
        string city = _cfg.Cidade.Trim();
        if (city.Length == 0)
        {
            Problem = Lang.T("Escreva a cidade nas configurações (Clima).", "Type the city in the settings (Weather).");
            _activity?.Changed();
            return;
        }
        _busy = true;
        int gen = _generation;
        var place = _place;
        _ = Task.Run(async () =>
        {
            WeatherNow? now = null;
            string? problem = null;
            try
            {
                place ??= Cached(city) ?? await OpenMeteo.Find(city).ConfigureAwait(false);
                if (place == null) problem = Lang.T($"Não achei a cidade \"{city}\".", $"Couldn't find the city \"{city}\".");
                else
                {
                    Cache(city, place);
                    now = await OpenMeteo.Read(place).ConfigureAwait(false);
                    if (now == null) problem = Lang.T("O Open-Meteo respondeu sem o clima.", "Open-Meteo answered with no weather.");
                }
            }
            catch (Exception e) { problem = Lang.T("Sem resposta do Open-Meteo: ", "No answer from Open-Meteo: ") + e.Message; }
            S.NaInterface(() =>
            {
                _busy = false;
                if (_stopped) return;
                if (gen != _generation || _again)
                {
                    // the city changed while this one was on its way: read again for the new one
                    _again = false;
                    Read();
                    return;
                }
                if (place != null) _place = place;
                if (now == null)
                {
                    if (problem != Problem && problem != null) Log.Escrever("clima: " + problem);
                    Problem = problem;
                    _activity?.Changed();
                    return;
                }
                Problem = null;
                Now = now;
                if (_watch.Feed(now.Sky) is { } change && _cfg.AvisarMudanca)
                {
                    Log.Escrever($"clima: {change.From} -> {change.To} ({now.Temp:0.0} °C)");
                    Notice(change.From, change.To, now.Day, now.Temp);
                }
                _activity?.Changed();
                S.Menu.Redesenhar();
            });
        });
    }

    private void Notice(Sky from, Sky to, bool day, double temp) => S.Ilha.Alertar(new Alerta
    {
        Tipo = "clima", Som = "chegada", Importante = true, Segundos = 4,
        Criar = p => WeatherActivity.ChangeCard(p, from, to, day, temp),
    });

    private static Place? Cached(string city)
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            var n = JsonNode.Parse(File.ReadAllText(CacheFile));
            if (!string.Equals(n?["cidade"]?.GetValue<string>(), city, StringComparison.OrdinalIgnoreCase)) return null;
            return new Place(n!["nome"]!.GetValue<string>(), n["regiao"]?.GetValue<string>() ?? "", n["lat"]!.GetValue<double>(), n["lon"]!.GetValue<double>());
        }
        catch (Exception) { return null; }
    }

    private static void Cache(string city, Place p)
    {
        try
        {
            Directory.CreateDirectory(Config.Pasta);
            File.WriteAllText(CacheFile, new JsonObject { ["cidade"] = city, ["nome"] = p.Name, ["regiao"] = p.Region, ["lat"] = p.Lat, ["lon"] = p.Lon }.ToJsonString());
        }
        catch (Exception) { }
    }

    public override string Autoteste()
    {
        var r = new StringBuilder();
        void Ok(string n, bool ok, string d = "") => r.AppendLine((ok ? "ok      " : "FALHOU  ") + n + (d.Length > 0 ? $" ({d})" : ""));
        Ok("clima: grupos dos códigos WMO", SkyInfo.Of(0) == Sky.Clear && SkyInfo.Of(1) == Sky.Clear && SkyInfo.Of(3) == Sky.Cloudy && SkyInfo.Of(45) == Sky.Fog
            && SkyInfo.Of(53) == Sky.Drizzle && SkyInfo.Of(63) == Sky.Rain && SkyInfo.Of(81) == Sky.Rain && SkyInfo.Of(73) == Sky.Snow && SkyInfo.Of(95) == Sky.Storm);
        var w = new SkyWatch();
        var seen = new List<string>();
        foreach (var s in new[] { Sky.Cloudy, Sky.Cloudy, Sky.Rain, Sky.Cloudy, Sky.Rain, Sky.Rain, Sky.Rain, Sky.Clear, Sky.Clear })
            if (w.Feed(s) is { } ch) seen.Add($"{ch.From}>{ch.To}");
        Ok("clima: a primeira leitura só aprende, uma leitura sozinha não muda, duas seguidas avisam", string.Join(",", seen) == "Cloudy>Rain,Rain>Clear", string.Join(",", seen));
        const string json = """
        {"current":{"temperature_2m":25.4,"apparent_temperature":26.1,"weather_code":3,"is_day":1},
         "daily":{"temperature_2m_max":[28.0],"temperature_2m_min":[18.5]},
         "hourly":{"time":["2026-11-03T17:00","2026-11-03T18:00","2026-11-03T19:00"],"temperature_2m":[25,24,22],"weather_code":[3,61,61],
                   "precipitation_probability":[10,60,70],"is_day":[1,1,0]}}
        """;
        using var d = JsonDocument.Parse(json);
        var now = OpenMeteo.Parse(d.RootElement, new DateTime(2026, 11, 3, 17, 30, 0));
        Ok("clima: lê o tempo de agora, máxima e mínima e as próximas horas (só as que vêm)",
            now is { Temp: 25.4, Code: 3, Day: true, Max: 28.0, Min: 18.5 } && now.Hours.Count == 2 && now.Hours[0].Rain == 60 && !now.Hours[1].Day);
        using var g = JsonDocument.Parse("""{"results":[{"name":"Belo Horizonte","latitude":-19.92,"longitude":-43.94,"admin1":"Minas Gerais"}]}""");
        Ok("clima: lê a cidade do geocoding", OpenMeteo.ParsePlace(g.RootElement) is { Name: "Belo Horizonte", Lat: -19.92 });
        using var empty = JsonDocument.Parse("""{"generationtime_ms":0.5}""");
        Ok("clima: cidade não achada", OpenMeteo.ParsePlace(empty.RootElement) == null);
        try
        {
            var place = OpenMeteo.Find("Lisboa").GetAwaiter().GetResult();
            var live = place != null ? OpenMeteo.Read(place).GetAwaiter().GetResult() : null;
            Ok("clima: Open-Meteo de verdade (Lisboa)", live != null, live != null ? $"{live.Temp:0.0} °C, {SkyInfo.Name(live.Sky)}, {live.Hours.Count} horas" : "sem resposta");
        }
        catch (Exception e) { Ok("clima: Open-Meteo de verdade", false, e.Message); }
        return r.ToString().TrimEnd();
    }
}

/// <summary>The sky group confirmed by two readings in a row; the first reading only learns.</summary>
internal sealed class SkyWatch
{
    private Sky? _confirmed, _candidate;

    public void Reset() { _confirmed = null; _candidate = null; }

    public (Sky From, Sky To)? Feed(Sky now)
    {
        if (_confirmed is not Sky c) { _confirmed = now; return null; }
        if (now == c) { _candidate = null; return null; }
        if (_candidate != now) { _candidate = now; return null; }
        _confirmed = now;
        _candidate = null;
        return (c, now);
    }
}

public sealed class ConfigClima
{
    [Label("Cidade", "City")]
    [Help("O nome da cidade (o Open-Meteo acha a posição).", "The city's name (Open-Meteo finds its location).")]
    [JsonPropertyName("cidade")] public string Cidade { get; set; } = "";

    [Label("Avisar quando o tempo mudar", "Notify when the weather changes")]
    [JsonPropertyName("avisarMudanca")] public bool AvisarMudanca { get; set; } = true;
}
