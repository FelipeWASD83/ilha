using System;
using System.Collections.Generic;
using System.Linq;

namespace Ilha.Extras;

/// <summary>
/// Decides when a falling battery crosses one of the alert levels (50, 25, 10, 5 and 1 by default), once per level and
/// per discharge. No reading is invented: it only looks at what the device reported.
/// <list type="bullet">
/// <item>A level fires when the percentage goes from above it to at or below it. A big drop (60 to 8) fires only the
/// lowest level it crossed, since the alert shows the real number anyway.</item>
/// <item>A device that reports in steps of 10 (AirPods) gets the levels rounded down to a multiple of 10 (50, 20, 10, 0):
/// 25 becomes 20, and 5 and 1 become 0, which for AirPods means "under 10%".</item>
/// <item>The first reading never fires on a level above 10: opening the island with the battery at 40% is not news. At 10%
/// or less it does fire, once.</item>
/// <item>Charging resets everything, so the next discharge alerts again. A level fires again only after the battery went
/// back up by a margin (5 points, 10 for the coarse devices), so 25 and 24 jittering does not repeat it.</item>
/// </list>
/// </summary>
internal sealed class BatteryAlerts
{
    public const int DefaultMargin = 5;
    public const int FirstReadingCeiling = 10;

    public static readonly int[] DefaultLevels = { 50, 25, 10, 5, 1 };

    private sealed class Track
    {
        public int? Previous;
        public readonly HashSet<int> Fired = new();
    }

    private readonly int[] _levels;
    private readonly Dictionary<string, Track> _tracks = new();

    public BatteryAlerts(IEnumerable<int>? levels = null)
    {
        _levels = (levels ?? DefaultLevels).Where(l => l is >= 0 and <= 100).Distinct().OrderByDescending(l => l).ToArray();
        if (_levels.Length == 0) _levels = DefaultLevels;
    }

    /// <summary>"50, 25, 10, 5, 1" from the settings window into levels (anything that is not a number from 0 to 100 is dropped).</summary>
    public static int[] ParseLevels(string? text)
    {
        var r = (text ?? "").Split(new[] { ',', ';', ' ', '%' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out int n) ? n : -1).Where(n => n is >= 0 and <= 100).Distinct().ToArray();
        return r.Length > 0 ? r : DefaultLevels;
    }

    /// <summary>The levels as they apply to a device that reports in steps of 10.</summary>
    public static int[] Coarse(IEnumerable<int> levels) =>
        levels.Select(l => l / 10 * 10).Distinct().OrderByDescending(l => l).ToArray();

    public IReadOnlyList<int> Levels => _levels;

    /// <summary>
    /// A new reading. Returns the level to alert about (the lowest one just crossed) or null for nothing to say.
    /// </summary>
    /// <param name="key">One battery (a device, or one bud).</param>
    /// <param name="coarse">Reports in steps of 10 (AirPods).</param>
    public int? Feed(string key, int percent, bool charging, bool coarse = false)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (!_tracks.TryGetValue(key, out var tr)) _tracks[key] = tr = new Track();
        var levels = coarse ? Coarse(_levels) : _levels;
        int margin = coarse ? 10 : DefaultMargin;

        if (charging)
        {
            tr.Fired.Clear();
            tr.Previous = percent;
            return null;
        }
        // back up by the margin: the level can fire again on the next fall
        tr.Fired.RemoveWhere(l => percent >= l + margin);

        int? fire = null;
        if (tr.Previous is int prev)
        {
            foreach (var l in levels)
                if (prev > l && percent <= l && !tr.Fired.Contains(l)) fire = fire is int f ? Math.Min(f, l) : l;
            foreach (var l in levels)
                if (prev > l && percent <= l) tr.Fired.Add(l);
        }
        else if (percent <= FirstReadingCeiling)
        {
            // first time we see this battery and it is already low: tell once, about the lowest level it is under
            foreach (var l in levels)
                if (percent <= l) { tr.Fired.Add(l); fire = fire is int f ? Math.Min(f, l) : l; }
        }
        tr.Previous = percent;
        return fire;
    }

    /// <summary>The device went away (switched off, disconnected): the next reading counts as a first one.</summary>
    public void Forget(string key) => _tracks.Remove(key);
}
