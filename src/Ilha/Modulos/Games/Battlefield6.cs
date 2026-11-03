using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Ilha.Games;

/// <summary>What one look at the BF6 screen found (null = not on screen or not read).</summary>
internal sealed record Bf6Frame(int? Us, int? Them, string? Mode, string? Map, List<Line> Feed, Outcome? EndScreen);

/// <summary>
/// Reads Battlefield 6's HUD off the screen (it has no game state feed and no match log). Calibrated on 03/11/2026 at
/// 3440x1440 in Conquest: the two ticket boxes at the top center, the kill feed at the top right, the loading screen's
/// mode and map at the top left. Other resolutions scale by the height, keeping each region's anchor.
/// </summary>
internal static class Bf6Reader
{
    private enum Anchor { Left, Center, Right }

    private const float RefW = 3440, RefH = 1440;
    private static readonly (Rectangle R, Anchor A) UsBox = (new(1352, 74, 110, 42), Anchor.Center);
    private static readonly (Rectangle R, Anchor A) ThemBox = (new(1980, 74, 104, 42), Anchor.Center);
    private static readonly (Rectangle R, Anchor A) ModeBox = (new(180, 60, 540, 50), Anchor.Left);
    private static readonly (Rectangle R, Anchor A) MapBox = (new(85, 280, 675, 120), Anchor.Left);
    private static readonly (Rectangle R, Anchor A) FeedBox = (new(2880, 55, 540, 205), Anchor.Right);
    private static readonly (Rectangle R, Anchor A) EndBox = (new(720, 160, 2000, 760), Anchor.Center);

    // Portuguese and English names, as the game writes them
    public static readonly string[] Modes =
    {
        "CONQUISTA", "AVANÇO", "CORRIDA", "MATA-MATA EM EQUIPE", "MATA-MATA DE ESQUADRÃO", "DOMINAÇÃO", "REI DA COLINA",
        "ESCALADA", "ESCARAMUÇA", "CONQUEST", "BREAKTHROUGH", "RUSH", "TEAM DEATHMATCH", "SQUAD DEATHMATCH", "DOMINATION",
        "KING OF THE HILL", "ESCALATION", "STRIKEPOINT",
    };

    /// <summary>
    /// What every look reads (both ticket boxes and the kill feed) in one rectangle: one screen copy instead of three,
    /// since each copy waits for the compositor.
    /// </summary>
    public static Rectangle Band(Rectangle screen) =>
        Rectangle.Union(Rectangle.Union(Place(UsBox, screen), Place(ThemBox, screen)), Place(FeedBox, screen));

    private static Rectangle Place((Rectangle R, Anchor A) region, Rectangle screen)
    {
        float k = screen.Height / RefH;
        var r = region.R;
        float x = region.A switch
        {
            Anchor.Left => r.X * k,
            Anchor.Right => screen.Width - (RefW - r.X) * k,
            _ => screen.Width / 2f + (r.X - RefW / 2) * k,
        };
        return new Rectangle(screen.X + (int)x, screen.Y + (int)(r.Y * k), Math.Max(1, (int)(r.Width * k)), Math.Max(1, (int)(r.Height * k)));
    }

    /// <summary>
    /// One look. `grab` copies a screen rectangle as BGRA; `slow` also reads what only shows between matches (loading
    /// screen, end screen), which costs more and is done every few looks.
    /// </summary>
    public static Bf6Frame Read(Func<Rectangle, byte[]?> grab, Rectangle screen, ScreenText ocr, bool slow)
    {
        int? us = Score(grab, Place(UsBox, screen), ocr, ours: true);
        int? them = Score(grab, Place(ThemBox, screen), ocr, ours: false);
        var feed = new List<Line>();
        var fr = Place(FeedBox, screen);
        if (grab(fr) is { } f) feed = ocr.Read(ScreenText.Scale(f, fr.Width, fr.Height, 2), fr.Width * 2, fr.Height * 2);
        string? mode = null, map = null;
        Outcome? end = null;
        if (slow && us == null && them == null)
        {
            var mr = Place(ModeBox, screen);
            if (grab(mr) is { } m) mode = ModeFrom(ocr.Read(ScreenText.Scale(m, mr.Width, mr.Height, 2), mr.Width * 2, mr.Height * 2));
            if (mode != null)
            {
                var pr = Place(MapBox, screen);
                if (grab(pr) is { } p) map = MapFrom(ocr.Read(p, pr.Width, pr.Height));
            }
            var er = Place(EndBox, screen);
            if (grab(er) is { } e) end = EndFrom(ocr.Read(e, er.Width, er.Height));
        }
        return new Bf6Frame(us, them, mode, map, feed, end);
    }

    /// <summary>
    /// One ticket box. Light digits on a see-through colored box: tried as it is, then as masks of the light pixels
    /// (the sky behind changes everything); the first that gives a number wins.
    /// </summary>
    private static int? Score(Func<Rectangle, byte[]?> grab, Rectangle r, ScreenText ocr, bool ours)
    {
        if (grab(r) is not { } img) return null;
        int w = r.Width, h = r.Height;
        var big = ScreenText.Scale(img, w, h, 4);
        if (ScreenText.Number(ocr.Read(big, w * 4, h * 4)) is int a) return a;
        Func<float, float, float, bool>[] masks = ours
            ? new Func<float, float, float, bool>[] { (_, s, v) => v > 0.85f && s < 0.35f, (_, _, v) => v > 0.92f }
            : new Func<float, float, float, bool>[] { (_, s, v) => v > 0.8f && s > 0.2f, (_, _, v) => v > 0.7f };
        foreach (var keep in masks)
        {
            var bw = ScreenText.Scale(ScreenText.Mask(img, keep), w, h, 4, smooth: false);
            var (pd, pw, ph) = ScreenText.Pad(bw, w * 4, h * 4, 24);
            if (ScreenText.Number(ocr.Read(pd, pw, ph)) is int b) return b;
        }
        return null;
    }

    public static string? ModeFrom(IEnumerable<Line> lines)
    {
        foreach (var l in lines)
        {
            string t = Normalize(l.Text);
            foreach (var m in Modes)
                if (t.Contains(Normalize(m))) return Title(m);
        }
        return null;
    }

    /// <summary>The map name is the tallest all-caps line in its box.</summary>
    public static string? MapFrom(IEnumerable<Line> lines)
    {
        var best = lines.Where(l => l.Text.Count(char.IsLetter) >= 3 && l.Text.Where(char.IsLetter).All(char.IsUpper))
            .OrderByDescending(l => l.Words.Max(w => w.H)).FirstOrDefault();
        return best == null ? null : Title(best.Text);
    }

    public static Outcome? EndFrom(IEnumerable<Line> lines)
    {
        foreach (var l in lines)
        {
            string t = Normalize(l.Text);
            if (t.Contains("VITORIA") || t.Contains("VICTORY")) return Outcome.Win;
            if (t.Contains("DERROTA") || t.Contains("DEFEAT")) return Outcome.Loss;
        }
        return null;
    }

    /// <summary>Upper case, no accents, only letters, digits and spaces.</summary>
    public static string Normalize(string s)
    {
        var d = s.ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var b = new StringBuilder();
        foreach (char c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            b.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return string.Join(" ", b.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Title(string s) => CultureInfo.GetCultureInfo("pt-BR").TextInfo.ToTitleCase(s.ToLowerInvariant());

    /// <summary>A name as OCR tends to read it: lower case, letters and digits, the usual look-alikes folded.</summary>
    public static string Fold(string s)
    {
        var b = new StringBuilder();
        foreach (char c in s.ToLowerInvariant())
        {
            char x = c switch { '0' => 'o', '1' or 'l' or '|' or '!' => 'i', '5' => 's', '8' => 'b', 'ü' => 'u', _ => c };
            if (char.IsLetterOrDigit(x)) b.Append(x);
        }
        return b.ToString();
    }

    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    public static bool SameName(string read, string name)
    {
        string a = Fold(read), b = Fold(name);
        if (a.Length == 0 || b.Length == 0) return false;
        return a == b || (b.Length >= 6 && Distance(a, b) <= 2);
    }
}

/// <summary>
/// The BF6 match out of a stream of looks. Tickets only count after two looks in a row agree (a misread digit never
/// reaches the pill); the feed lines with our name first are our kills, with our name after the first word our deaths,
/// each counted once even while it stays on screen and OCR reads it a little differently.
/// </summary>
internal sealed class Bf6State
{
    private Match? _match;
    private int? _usSeen, _themSeen;
    private DateTime _hudSeen, _hudFirst;
    private string? _mode, _map;
    private readonly List<(string Key, DateTime At)> _feed = new();
    private bool _ended;

    public Match? Current => _match;

    public List<Moment> Apply(Bf6Frame f, DateTime now, string myName)
    {
        var moments = new List<Moment>();
        if (f.Mode != null) { _mode = f.Mode; _map = f.Map ?? _map; }
        bool hud = f.Us != null || f.Them != null;
        if (hud)
        {
            if (_match == null || _ended)
            {
                _match = new Match { Game = "bf6", Start = now, Mode = _mode ?? "", Map = _map ?? "", Kills = 0, Deaths = 0 };
                _match.UsLabel = Lang.T("Nós", "Us");
                _match.ThemLabel = Lang.T("Eles", "Them");
                _match.UsColor = TeamColors.Blue;
                _match.ThemColor = TeamColors.Red;
                _usSeen = _themSeen = null;
                _hudFirst = now;
                _ended = false;
                _feed.Clear();
            }
            if (f.Us is int u) { if (_usSeen == u) _match.Us = u; _usSeen = u; }
            if (f.Them is int t) { if (_themSeen == t) _match.Them = t; _themSeen = t; }
            _hudSeen = now;
        }
        if (_match == null) return moments;
        var m = _match;
        m.Clock = (now - m.Start).TotalSeconds;
        m.ClockRead = now;
        m.ClockUp = true;

        if (!_ended && myName.Length > 0)
        {
            _feed.RemoveAll(x => now - x.At > TimeSpan.FromSeconds(12));
            foreach (var line in f.Feed)
            {
                var words = line.Words.Select(w => w.Text.Trim('>', '▶', '►', '.', ',')).Where(w => w.Length > 0).ToList();
                if (words.Count < 2) continue;
                bool killer = Bf6Reader.SameName(words[0], myName);
                bool victim = !killer && words.Skip(1).Any(w => Bf6Reader.SameName(w, myName));
                if (!killer && !victim) continue;
                string key = (killer ? "k:" : "d:") + Bf6Reader.Fold(string.Join("", killer ? words.Skip(1) : words.Take(1)));
                if (_feed.Any(x => x.Key[..2] == key[..2] && Bf6Reader.Distance(x.Key, key) <= 3)) continue;
                _feed.Add((key, now));
                if (killer) { m.Kills = (m.Kills ?? 0) + 1; moments.Add(new Moment(MomentKind.Kill, Who: string.Join(" ", words.Skip(1)))); }
                else { m.Deaths = (m.Deaths ?? 0) + 1; moments.Add(new Moment(MomentKind.Death)); }
            }
        }

        if (!_ended && f.EndScreen is Outcome o)
        {
            _ended = true;
            m.Outcome = o;
            m.Stage = Lang.T("Fim de jogo", "Game over");
            moments.Add(new Moment(MomentKind.End, Outcome: o));
        }

        // the HUD gone for a while: the match ended (or he left). Near the end of the tickets, the side ahead won
        if (!hud && now - _hudSeen > TimeSpan.FromSeconds(25))
        {
            if (!_ended && _hudSeen - _hudFirst > TimeSpan.FromSeconds(60) && m.Us is int a && m.Them is int b && Math.Min(a, b) <= 40 && a != b)
            {
                m.Outcome = a > b ? Outcome.Win : Outcome.Loss;
                moments.Add(new Moment(MomentKind.End, Outcome: m.Outcome));
            }
            _match = null;
            _ended = false;
            _mode = _map = null;
        }
        return moments;
    }

    /// <summary>The game left the front for good (closed or minimized for minutes): forget the match.</summary>
    public void Drop()
    {
        _match = null;
        _ended = false;
    }
}

/// <summary>Looks at the screen about every 0.7 s while bf6.exe is in front; nothing at all otherwise.</summary>
internal sealed class Bf6Source : GameSource
{
    private readonly Bf6State _state = new();
    private readonly Func<string> _myName;
    private readonly Action<bool> _reading;
    private readonly System.Threading.Timer _tick, _forget;
    private ScreenText? _ocr;
    private int _busy, _count;
    private volatile bool _front, _stop;

    public override string Game => "bf6";

    /// <param name="reading">Called with true while looking at the game (the island leaves screen captures then).</param>
    public Bf6Source(Func<string> myName, Action<bool> reading, Action<Action> dispatch) : base(dispatch)
    {
        _myName = myName;
        _reading = reading;
        _tick = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        _forget = new System.Threading.Timer(_ => Forget(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public override void Foreground(string process)
    {
        bool front = string.Equals(process, "bf6.exe", StringComparison.OrdinalIgnoreCase);
        if (front == _front || _stop) return;
        _front = front;
        _reading(front);
        if (front)
        {
            _forget.Change(Timeout.Infinite, Timeout.Infinite);
            _tick.Change(0, 700);
        }
        else
        {
            _tick.Change(Timeout.Infinite, Timeout.Infinite);
            // alt-tabbed out: the match stays on the pill; minutes away and it is gone
            _forget.Change(180_000, Timeout.Infinite);
        }
    }

    private void Forget()
    {
        if (_front) return;
        bool had;
        lock (_state) { had = _state.Current != null; _state.Drop(); }
        if (had) Publish(null, Array.Empty<Moment>());
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr m, ref MONITORINFO mi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static Rectangle? GameScreen()
    {
        var mon = MonitorFromWindow(GetForegroundWindow(), 2);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfoW(mon, ref mi)) return null;
        var r = mi.rcMonitor;
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    private void Tick()
    {
        if (_stop || !_front || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            _ocr ??= new ScreenText();
            if (GameScreen() is not { } screen) return;
            var band = Bf6Reader.Band(screen);
            var copy = ScreenText.Grab(band);
            byte[]? Grab(Rectangle r) => r.IntersectsWith(band) && band.Contains(r)
                ? copy == null ? null : ScreenText.Crop(copy, band.Width, new Rectangle(r.X - band.X, r.Y - band.Y, r.Width, r.Height))
                : ScreenText.Grab(r);
            var frame = Bf6Reader.Read(Grab, screen, _ocr, slow: _count++ % 3 == 0);
            List<Moment> moments;
            Match? m;
            lock (_state)
            {
                moments = _state.Apply(frame, DateTime.Now, _myName());
                m = _state.Current;
            }
            Publish(m, moments);
        }
        catch (Exception e) { Log.Escrever("jogos: bf6: " + e.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public override void Dispose()
    {
        _stop = true;
        _tick.Dispose();
        _forget.Dispose();
        if (_front) _reading(false);
    }
}
