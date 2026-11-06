using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ilha.Keyboard;

/// <summary>What Synapse knows about the keyboard: its profiles (names) and the one in use.</summary>
internal sealed record SynapseProfiles(string Active, IReadOnlyList<(string Guid, string Name)> Profiles)
{
    public string? ActiveName => Profiles.FirstOrDefault(p => p.Guid == Active).Name;
}

/// <summary>
/// Synapse 4 has no API for profiles, but its tray process logs the device list on every change, with the profile in
/// use and every profile's name (systray_systrayv*.log in %LOCALAPPDATA%\Razer\RazerAppEngine\User Data\Logs, seen on
/// 06/11/2026). The island follows the newest of those files from where it stopped; only the keyboard's product id
/// counts.
/// </summary>
internal sealed class SynapseLog
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Razer", "RazerAppEngine", "User Data", "Logs");

    private static readonly Regex Device = new(@"""activeProfile"":""(?<a>[0-9a-fA-F-]{36})"",""productId"":(?<p>\d+),""profiles"":\[(?<list>[^\]]*)\]", RegexOptions.Compiled);
    private static readonly Regex Profile = new(@"\{""name"":""(?<n>(?:[^""\\]|\\.)*)"",""guid"":""(?<g>[0-9a-fA-F-]{36})""", RegexOptions.Compiled);

    private readonly int _product;
    private string? _file;
    private long _pos;

    public SynapseLog(int product = RazerDevice.Product) => _product = product;

    /// <summary>The keyboard's profiles in one log line, or null when the line has none of it.</summary>
    public static SynapseProfiles? ParseLine(string line, int product)
    {
        foreach (System.Text.RegularExpressions.Match m in Device.Matches(line))
        {
            if (!int.TryParse(m.Groups["p"].Value, out int p) || p != product) continue;
            var list = Profile.Matches(m.Groups["list"].Value)
                .Select(x => (x.Groups["g"].Value, Unescape(x.Groups["n"].Value))).ToList();
            return new SynapseProfiles(m.Groups["a"].Value, list);
        }
        return null;
    }

    private static string Unescape(string s)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<string>("\"" + s + "\"") ?? s; }
        catch (Exception) { return s; }
    }

    /// <summary>The newest state since the last call (null when nothing new). The first call looks back 2 MB.</summary>
    public SynapseProfiles? Poll()
    {
        try
        {
            var dir = new DirectoryInfo(Folder);
            if (!dir.Exists) return null;
            var newest = dir.GetFiles("systray_systrayv*.log").Where(f => f.Length > 0).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest == null) return null;
            if (newest.FullName != _file)
            {
                // the first file seen: from near its end; a newer file after a rotation: from its start
                _pos = _file == null ? Math.Max(0, newest.Length - 2_000_000) : 0;
                _file = newest.FullName;
            }
            if (newest.Length < _pos) _pos = 0;
            if (newest.Length == _pos) return null;
            using var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(_pos, SeekOrigin.Begin);
            var bytes = new byte[newest.Length - _pos];
            int n = fs.Read(bytes, 0, bytes.Length);
            // only whole lines: a line still being written is read again next time
            int end = Array.LastIndexOf(bytes, (byte)'\n', Math.Max(0, n - 1));
            if (end < 0) return null;
            _pos += end + 1;
            SynapseProfiles? last = null;
            foreach (var line in Encoding.UTF8.GetString(bytes, 0, end + 1).Split('\n'))
                if (line.Contains("activeProfile", StringComparison.Ordinal) && ParseLine(line, _product) is { } s) last = s;
            return last;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
