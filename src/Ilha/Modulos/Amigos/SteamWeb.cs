using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Ilha.Games;

namespace Ilha.Friends;

/// <summary>A friend as the Steam Web API says it now.</summary>
internal sealed record Friend(string Id, string Name, int State, string? GameId, string? Game, string Avatar)
{
    public bool Online => State > 0;
    public bool Playing => Game is { Length: > 0 };

    /// <summary>"Jogando CS2", "Online", "Ausente"... for the card.</summary>
    public string Status => Playing ? Lang.T("Jogando ", "Playing ") + Game : State switch
    {
        0 => Lang.T("Offline", "Offline"),
        2 => Lang.T("Ocupado", "Busy"),
        3 => Lang.T("Ausente", "Away"),
        4 => Lang.T("Soneca", "Snooze"),
        5 => Lang.T("Procurando troca", "Looking to trade"),
        6 => Lang.T("Procurando jogo", "Looking to play"),
        _ => Lang.T("Online", "Online"),
    };
}

/// <summary>
/// The Steam Web API with the key he types on the island (Credential Manager, `Ilha/steam`): the friend list
/// (ISteamUser/GetFriendList) and their state (GetPlayerSummaries, up to 100 per call). The key travels in the query
/// string, as the API requires, and the URL never goes to the log.
/// </summary>
internal static class SteamWeb
{
    public const string Credential = "steam";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public sealed class ApiException : Exception
    {
        public HttpStatusCode? Status { get; }
        public ApiException(string m, HttpStatusCode? s) : base(m) => Status = s;
    }

    /// <summary>The account signed in to Steam: the running client's ActiveUser, else the most recent in loginusers.vdf.</summary>
    public static string? MySteamId()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (k?.GetValue("ActiveUser") is int account && account != 0) return (76561197960265728L + (uint)account).ToString();
        }
        catch (Exception) { }
        try
        {
            if (GameSetup.SteamFolder() is not { } steam) return null;
            string vdf = Path.Combine(steam, "config", "loginusers.vdf");
            if (!File.Exists(vdf)) return null;
            return FromLoginUsers(File.ReadAllText(vdf));
        }
        catch (Exception) { return null; }
    }

    internal static string? FromLoginUsers(string vdf)
    {
        string? first = null;
        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(vdf, "\"(7656\\d{13})\"\\s*\\{([^}]*)\\}"))
        {
            first ??= m.Groups[1].Value;
            if (Regex.IsMatch(m.Groups[2].Value, "\"(MostRecent|mostrecent)\"\\s*\"1\"")) return m.Groups[1].Value;
        }
        return first;
    }

    private static async Task<JsonDocument> Get(string url)
    {
        using var r = await Http.GetAsync(url).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new ApiException($"HTTP {(int)r.StatusCode}", r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    public static async Task<List<string>> FriendIds(string key, string me)
    {
        using var d = await Get($"https://api.steampowered.com/ISteamUser/GetFriendList/v1/?key={Uri.EscapeDataString(key)}&steamid={me}&relationship=friend").ConfigureAwait(false);
        return ParseFriendIds(d.RootElement);
    }

    internal static List<string> ParseFriendIds(JsonElement root)
    {
        var r = new List<string>();
        if (root.TryGetProperty("friendslist", out var fl) && fl.TryGetProperty("friends", out var fs) && fs.ValueKind == JsonValueKind.Array)
            foreach (var f in fs.EnumerateArray())
                if (f.TryGetProperty("steamid", out var id) && id.GetString() is { Length: > 0 } s) r.Add(s);
        return r;
    }

    public static async Task<List<Friend>> Summaries(string key, IReadOnlyList<string> ids)
    {
        var all = new List<Friend>();
        for (int i = 0; i < ids.Count; i += 100)
        {
            string batch = string.Join(",", ids.Skip(i).Take(100));
            using var d = await Get($"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(key)}&steamids={batch}").ConfigureAwait(false);
            all.AddRange(ParseSummaries(d.RootElement));
        }
        return all;
    }

    internal static List<Friend> ParseSummaries(JsonElement root)
    {
        var r = new List<Friend>();
        if (!root.TryGetProperty("response", out var resp) || !resp.TryGetProperty("players", out var ps) || ps.ValueKind != JsonValueKind.Array) return r;
        foreach (var p in ps.EnumerateArray())
        {
            string S(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int state = p.TryGetProperty("personastate", out var st) && st.TryGetInt32(out int x) ? x : 0;
            string game = S("gameextrainfo"), gid = S("gameid");
            r.Add(new Friend(S("steamid"), S("personaname"), state, gid.Length > 0 ? gid : null, game.Length > 0 ? game : null, S("avatarmedium")));
        }
        return r;
    }
}
