using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace Ilha.Focus;

/// <summary>The focus modes. Edited in the settings window's "Focus" section (not the generated module form).</summary>
public sealed class ConfigFoco
{
    [JsonPropertyName("modos")] public List<FocusMode> Modos { get; set; } = FocusMode.Defaults();

    [Label("Aviso ao ligar e desligar", "Notice when a focus turns on and off")]
    [JsonPropertyName("aviso")] public bool Aviso { get; set; } = true;

    [Label("Marca do foco na pílula", "Focus mark on the pill")]
    [Help("O ícone do foco ligado na pílula quando não tem mais nada nela, como a lua do iPhone.",
        "The icon of the focus that is on, on the pill when nothing else is there, like the iPhone's moon.")]
    [JsonPropertyName("marca")] public bool Marca { get; set; } = true;
}

/// <summary>What the alerts do while a mode is on.</summary>
public static class FocusAlerts
{
    public const string All = "todos", Important = "importantes", None = "nenhum";

    public static string Normalize(string? v) => v switch { Important => Important, None => None, _ => All };
}

/// <summary>
/// One focus mode, like the iPhone's: a name, an icon and a color, what it changes while on (sounds, Windows Do Not
/// Disturb, alerts, wallpaper, volume, brightness) and when it turns on by itself (schedules and apps).
/// </summary>
public sealed class FocusMode
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("nome")] public string Nome { get; set; } = "";
    /// <summary>One of <see cref="FocusIcons.Names"/>.</summary>
    [JsonPropertyName("icone")] public string Icone { get; set; } = "lua";
    /// <summary>One of <see cref="FocusIcons.Colors"/>.</summary>
    [JsonPropertyName("cor")] public string Cor { get; set; } = "indigo";
    [JsonPropertyName("silenciar")] public bool Silenciar { get; set; }
    [JsonPropertyName("naoPerturbeWindows")] public bool NaoPerturbeWindows { get; set; }
    [JsonPropertyName("avisos")] public string Avisos { get; set; } = FocusAlerts.All;
    /// <summary>A Fundo Vivo library item id for the main monitor; "" = the wallpaper stays.</summary>
    [JsonPropertyName("wallpaper")] public string Wallpaper { get; set; } = "";
    /// <summary>0 to 100; -1 = the volume stays.</summary>
    [JsonPropertyName("volume")] public int Volume { get; set; } = -1;
    /// <summary>0 to 100; -1 = the brightness stays.</summary>
    [JsonPropertyName("brilho")] public int Brilho { get; set; } = -1;
    /// <summary>"seg-sex 09:00-18:00", one per line (see <see cref="Schedule.Parse"/>).</summary>
    [JsonPropertyName("horarios")] public List<string> Horarios { get; set; } = new();
    /// <summary>Process names ("bf6.exe"): the mode is on while one of them is open.</summary>
    [JsonPropertyName("apps")] public List<string> Apps { get; set; } = new();

    public string Name => Nome.Length > 0 ? Nome : Id;

    public const string DoNotDisturbId = "nao-perturbe";

    public static List<FocusMode> Defaults() => new()
    {
        new()
        {
            Id = DoNotDisturbId, Nome = Lang.T("Não Perturbe", "Do Not Disturb"), Icone = "lua", Cor = "indigo",
            Silenciar = true, NaoPerturbeWindows = true, Avisos = FocusAlerts.Important,
        },
        new()
        {
            Id = "trabalho", Nome = Lang.T("Trabalho", "Work"), Icone = "maleta", Cor = "azul",
            Avisos = FocusAlerts.Important,
        },
        new()
        {
            Id = "jogo", Nome = Lang.T("Jogo", "Gaming"), Icone = "controle", Cor = "verde",
            Silenciar = true, Avisos = FocusAlerts.Important,
        },
        new()
        {
            Id = "dormir", Nome = Lang.T("Dormir", "Sleep"), Icone = "cama", Cor = "laranja",
            Silenciar = true, NaoPerturbeWindows = true, Avisos = FocusAlerts.None, Brilho = 20, Volume = 15,
            Horarios = new List<string> { "todos 23:30-07:00" },
        },
    };

    /// <summary>A new id from a name ("Leitura" → "leitura", "leitura-2" when taken).</summary>
    public static string NewId(string name, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        string b = new string(Sports.Espn.Fold(name).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (b.Length == 0) b = "foco";
        string id = b;
        for (int i = 2; used.Contains(id); i++) id = $"{b}-{i}";
        return id;
    }
}

/// <summary>One schedule line: the days and a time window ("seg-sex 09:00-18:00"; the window may cross midnight).</summary>
public sealed record Schedule(bool[] Days, TimeSpan From, TimeSpan To)
{
    // Monday first, like the line is written; DayOfWeek is Sunday = 0
    private static readonly string[][] DayNames =
    {
        new[] { "seg", "mon" }, new[] { "ter", "tue" }, new[] { "qua", "wed" }, new[] { "qui", "thu" },
        new[] { "sex", "fri" }, new[] { "sab", "sáb", "sat" }, new[] { "dom", "sun" },
    };

    private static int Day(string s)
    {
        s = s.Trim().ToLowerInvariant();
        for (int i = 0; i < DayNames.Length; i++)
            if (DayNames[i].Any(n => s.StartsWith(n, StringComparison.Ordinal))) return i;
        return -1;
    }

    /// <summary>
    /// "todos 23:30-07:00", "seg-sex 09:00-18:00", "sab,dom 10:00-12:00", "qua 14:00-15:30" (English day names work
    /// too, and "all"/"every day"). Null when the line does not read.
    /// </summary>
    public static Schedule? Parse(string line)
    {
        var parts = (line ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        string times = parts[^1];
        string days = parts.Length > 1 ? string.Join("", parts[..^1]) : "todos";
        var tt = times.Split('-');
        if (tt.Length != 2 || !TimeSpan.TryParseExact(tt[0], @"h\:mm", CultureInfo.InvariantCulture, out var from)
            || !TimeSpan.TryParseExact(tt[1], @"h\:mm", CultureInfo.InvariantCulture, out var to) || from == to) return null;
        var on = new bool[7];
        string d = days.ToLowerInvariant();
        if (d is "todos" or "todososdias" or "all" or "everyday" or "diário" or "diario")
            Array.Fill(on, true);
        else
            foreach (var piece in d.Split(','))
            {
                var range = piece.Split('-');
                int a = Day(range[0]), b = range.Length == 2 ? Day(range[1]) : a;
                if (a < 0 || b < 0 || range.Length > 2) return null;
                for (int i = a; ; i = (i + 1) % 7) { on[i] = true; if (i == b) break; }
            }
        return new Schedule(on, from, to);
    }

    private static int Index(DayOfWeek d) => ((int)d + 6) % 7;

    /// <summary>Is `now` inside? A window that crosses midnight belongs to the day it starts on.</summary>
    public bool Contains(DateTime now)
    {
        var t = now.TimeOfDay;
        if (From < To) return Days[Index(now.DayOfWeek)] && t >= From && t < To;
        // crosses midnight: the evening part on its own day, the morning part on the day after
        return (Days[Index(now.DayOfWeek)] && t >= From) || (Days[Index(now.AddDays(-1).DayOfWeek)] && t < To);
    }
}

/// <summary>Which mode the automations want now: the first mode, in the list's order, with a schedule or an app on.</summary>
public static class FocusRules
{
    public static FocusMode? Wanted(IEnumerable<FocusMode> modes, DateTime now, ISet<string> running)
    {
        foreach (var m in modes)
        {
            if (m.Horarios.Select(Schedule.Parse).Any(s => s?.Contains(now) == true)) return m;
            if (m.Apps.Any(a => running.Contains(Normalize(a)))) return m;
        }
        return null;
    }

    /// <summary>"BF6.exe", "bf6" → "bf6".</summary>
    public static string Normalize(string app)
    {
        string a = app.Trim().ToLowerInvariant();
        return a.EndsWith(".exe") ? a[..^4] : a;
    }

    /// <summary>Does the mode let this alert through? Its own notices always pass.</summary>
    public static bool Allows(FocusMode m, string type, bool important) =>
        type == "foco" || FocusAlerts.Normalize(m.Avisos) switch
        {
            FocusAlerts.None => false,
            FocusAlerts.Important => important,
            _ => true,
        };
}
