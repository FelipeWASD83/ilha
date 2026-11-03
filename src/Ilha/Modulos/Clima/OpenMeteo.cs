using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Ilha.Weather;

internal sealed record Place(string Name, string Region, double Lat, double Lon);

internal sealed record HourAhead(DateTime Time, double Temp, int Code, int? Rain, bool Day);

internal sealed record WeatherNow(double Temp, double? Feels, int Code, bool Day, double? Max, double? Min, List<HourAhead> Hours, DateTime ReadAt)
{
    public Sky Sky => SkyInfo.Of(Code);
}

/// <summary>
/// Open-Meteo (open-meteo.com): no key, free for personal use. The city typed in the settings goes through its
/// geocoding API once (cached in ~/.ilha/clima.json); the forecast is read every 10 minutes.
/// </summary>
internal static class OpenMeteo
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task<Place?> Find(string city)
    {
        string lang = Lang.T("pt", "en");
        string json = await Http.GetStringAsync($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language={lang}&format=json").ConfigureAwait(false);
        using var d = JsonDocument.Parse(json);
        return ParsePlace(d.RootElement);
    }

    internal static Place? ParsePlace(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var rs) || rs.ValueKind != JsonValueKind.Array || rs.GetArrayLength() == 0) return null;
        var r = rs[0];
        string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        return new Place(S("name"), S("admin1"), r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble());
    }

    public static async Task<WeatherNow?> Read(Place p)
    {
        string url = "https://api.open-meteo.com/v1/forecast?latitude=" + p.Lat.ToString(Inv) + "&longitude=" + p.Lon.ToString(Inv) +
                     "&current=temperature_2m,apparent_temperature,weather_code,is_day" +
                     "&hourly=temperature_2m,weather_code,precipitation_probability,is_day&forecast_hours=13" +
                     "&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto";
        string json = await Http.GetStringAsync(url).ConfigureAwait(false);
        using var d = JsonDocument.Parse(json);
        return Parse(d.RootElement, DateTime.Now);
    }

    internal static WeatherNow? Parse(JsonElement root, DateTime now)
    {
        if (!root.TryGetProperty("current", out var c)) return null;
        double D(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
        double temp = D(c, "temperature_2m");
        if (double.IsNaN(temp) || !c.TryGetProperty("weather_code", out var wc) || !wc.TryGetInt32(out int code)) return null;
        double feels = D(c, "apparent_temperature");
        bool day = !c.TryGetProperty("is_day", out var id) || id.ValueKind != JsonValueKind.Number || id.GetInt32() == 1;
        double? max = null, min = null;
        if (root.TryGetProperty("daily", out var dl))
        {
            if (dl.TryGetProperty("temperature_2m_max", out var mx) && mx.ValueKind == JsonValueKind.Array && mx.GetArrayLength() > 0 && mx[0].ValueKind == JsonValueKind.Number) max = mx[0].GetDouble();
            if (dl.TryGetProperty("temperature_2m_min", out var mn) && mn.ValueKind == JsonValueKind.Array && mn.GetArrayLength() > 0 && mn[0].ValueKind == JsonValueKind.Number) min = mn[0].GetDouble();
        }
        var hours = new List<HourAhead>();
        if (root.TryGetProperty("hourly", out var h) && h.TryGetProperty("time", out var times) && times.ValueKind == JsonValueKind.Array)
        {
            var temps = h.GetProperty("temperature_2m");
            var codes = h.GetProperty("weather_code");
            h.TryGetProperty("precipitation_probability", out var rain);
            h.TryGetProperty("is_day", out var days);
            for (int i = 0; i < times.GetArrayLength(); i++)
            {
                if (!DateTime.TryParse(times[i].GetString(), Inv, DateTimeStyles.None, out var t)) continue;
                // from the next whole hour on
                if (t <= now) continue;
                if (temps[i].ValueKind != JsonValueKind.Number || codes[i].ValueKind != JsonValueKind.Number) continue;
                int? rp = rain.ValueKind == JsonValueKind.Array && rain[i].ValueKind == JsonValueKind.Number ? rain[i].GetInt32() : null;
                bool hd = days.ValueKind != JsonValueKind.Array || days[i].ValueKind != JsonValueKind.Number || days[i].GetInt32() == 1;
                hours.Add(new HourAhead(t, temps[i].GetDouble(), codes[i].GetInt32(), rp, hd));
            }
        }
        return new WeatherNow(temp, double.IsNaN(feels) ? null : feels, code, day, max, min, hours, now);
    }
}
