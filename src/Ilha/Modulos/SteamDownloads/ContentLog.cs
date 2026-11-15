using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Ilha.Games;

namespace Ilha.SteamDownloads;

/// <summary>
/// The live progress of a Steam download, from Steam's own logs\content_log.txt (4.5.1). The manifest is only rewritten
/// when a download pauses or stops, and the downloading folder is allocated at full size the moment it starts (128 GB of
/// Red Dead 2 on the first second), so neither moves while it downloads. The log does: "update started : download
/// A/B" gives where this run began, a "stats: (Invalid, 0) : N Bytes" line every 5 minutes gives what came down in
/// them (the sum of every server's line), and "Current download rate: X Mbps" every minute fills the gap between two
/// stats lines.
/// </summary>
internal static class ContentLog
{
    /// <summary>One run of an app's download: where it started, what came down since, the last rate Steam saw.</summary>
    public sealed record Run(int App, long Start, long Total, DateTime StartedAt, long Since, DateTime LastStats, double? Rate, DateTime RateAt, bool Ended)
    {
        /// <summary>The bytes downloaded at <paramref name="now"/>: the start, the stats since, and the rate since the last
        /// stats line (at most 5 minutes of it, and only while the rate is fresh).</summary>
        public long Downloaded(DateTime now)
        {
            double extra = 0;
            if (!Ended && Rate is double r && (now - RateAt).TotalMinutes < 3)
                extra = r * Math.Clamp((now - LastStats).TotalSeconds, 0, 300);
            return (long)Math.Min(Start + Since + extra, Total);
        }
    }

    private static readonly Regex Line = new(@"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\] (.*)$", RegexOptions.Compiled);
    private static readonly Regex Started = new(@"^AppID (\d+) update started : download (\d+)/(\d+)", RegexOptions.Compiled);
    private static readonly Regex Stopped = new(@"^AppID (\d+) (?:update canceled|App update changed : None)", RegexOptions.Compiled);
    private static readonly Regex Stats = new(@"^stats: \(Invalid, 0\) : (\d+) Bytes", RegexOptions.Compiled);
    private static readonly Regex RateLine = new(@"^Current download rate: ([\d.]+) Mbps", RegexOptions.Compiled);

    /// <summary>The last run of each app in the log. Steam downloads one app at a time, so the stats and the rate belong
    /// to the run that is open when they are written.</summary>
    public static Dictionary<int, Run> Parse(IEnumerable<string> lines)
    {
        var runs = new Dictionary<int, Run>();
        Run? cur = null;
        foreach (var raw in lines)
        {
            var m = Line.Match(raw);
            if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                continue;
            string text = m.Groups[2].Value;
            System.Text.RegularExpressions.Match x;
            if ((x = Started.Match(text)).Success)
            {
                if (cur != null) runs[cur.App] = cur with { Ended = true };
                cur = new Run(int.Parse(x.Groups[1].Value), long.Parse(x.Groups[2].Value), long.Parse(x.Groups[3].Value), at, 0, at, null, at, false);
                runs[cur.App] = cur;
            }
            else if (cur == null) continue;
            else if ((x = Stopped.Match(text)).Success)
            {
                if (int.Parse(x.Groups[1].Value) != cur.App) continue;
                runs[cur.App] = cur with { Ended = true };
                cur = null;
            }
            else if ((x = Stats.Match(text)).Success)
            {
                cur = cur with { Since = cur.Since + long.Parse(x.Groups[1].Value), LastStats = at };
                runs[cur.App] = cur;
            }
            else if ((x = RateLine.Match(text)).Success)
            {
                if (!double.TryParse(x.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double mbps)) continue;
                cur = cur with { Rate = mbps * 1e6 / 8, RateAt = at };
                runs[cur.App] = cur;
            }
        }
        return runs;
    }

    private static readonly object Gate = new();
    private static (long Length, DateTime Write) _read;
    private static Dictionary<int, Run> _runs = new();

    /// <summary>The runs in the log now (read again only when the file changed; it changes about once a minute).</summary>
    public static Dictionary<int, Run> Current()
    {
        lock (Gate)
        {
            try
            {
                if (GameSetup.SteamFolder() is not { } steam) return _runs;
                var file = new FileInfo(Path.Combine(steam, "logs", "content_log.txt"));
                if (!file.Exists) return _runs = new();
                var key = (file.Length, file.LastWriteTimeUtc);
                if (key == _read) return _runs;
                // the run can have started before Steam rotated the log: the previous file first
                var lines = new List<string>();
                foreach (var f in new[] { Path.Combine(steam, "logs", "content_log.previous.txt"), file.FullName })
                {
                    if (!File.Exists(f)) continue;
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var rd = new StreamReader(fs);
                    while (rd.ReadLine() is { } l) lines.Add(l);
                }
                _runs = Parse(lines);
                _read = key;
            }
            catch (Exception e) { Log.Escrever("downloads da steam: não deu para ler o content_log: " + e.Message); }
            return _runs;
        }
    }
}
