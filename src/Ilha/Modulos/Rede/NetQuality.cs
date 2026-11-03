using System;
using System.Collections.Generic;
using System.Linq;

namespace Ilha.Network;

/// <summary>One look at the connection: the typical ping to the internet host, the loss over the window and whether it is bad.</summary>
internal sealed record NetReading(int? Ms, double Loss, bool Bad, bool LocalBad, int Samples)
{
    public static readonly NetReading None = new(null, 0, false, false, 0);

    /// <summary>The mark on the score pill: "ping 180 · perda 3%" (only the parts that are bad, the ping always).</summary>
    public string Mark(double lossLimit)
    {
        var parts = new List<string>();
        if (Ms is int ms) parts.Add($"ping {ms}");
        if (Loss * 100 >= lossLimit) parts.Add(Lang.T($"perda {Loss * 100:0}%", $"loss {Loss * 100:0}%"));
        if (parts.Count == 0) parts.Add(Lang.T("sem resposta", "no reply"));
        return (LocalBad ? Lang.T("rede local · ", "local network · ") : "") + string.Join(" · ", parts);
    }
}

/// <summary>
/// The connection during a competitive game (3.9.0, his request). Windows does not let a program without admin see the
/// game server's address (and a driver would be the kernel, which is off limits), so this measures his own connection:
/// the router and an internet host, once a second. The score pill gets a mark only when it is bad.
/// </summary>
internal static class NetQuality
{
    public const int Window = 30;

    private static NetReading _current = NetReading.None;

    /// <summary>The newest reading; `Changed` runs (on the interface thread) when the mark should change.</summary>
    public static NetReading Current => _current;
    public static event Action? Changed;
    public static double LossLimit { get; set; } = 2;

    public static void Publish(NetReading r)
    {
        var old = _current;
        _current = r;
        if (old.Bad != r.Bad || r.Bad && (old.Ms != r.Ms || Math.Abs(old.Loss - r.Loss) > 0.001 || old.LocalBad != r.LocalBad)) Changed?.Invoke();
    }

    /// <summary>
    /// The reading from the last samples (null = no reply, oldest first). The ping is the median of the last five replies
    /// (one slow packet is not a bad connection); the loss counts the whole window and only after ten samples. It turns bad
    /// above the limit or at the loss limit, and good again only below 80% of the limit (no blinking at the edge).
    /// </summary>
    public static NetReading Evaluate(IReadOnlyList<int?> host, IReadOnlyList<int?> router, int limitMs, double lossPct, bool wasBad)
    {
        if (host.Count == 0) return NetReading.None;
        var replies = host.Where(x => x != null).Select(x => x!.Value).ToList();
        int? ms = replies.Count == 0 ? null : Median(replies.Skip(Math.Max(0, replies.Count - 5)).ToList());
        double loss = host.Count >= 10 ? host.Count(x => x == null) / (double)host.Count : 0;
        bool lossBad = loss * 100 >= lossPct;
        // the last three with no reply at all: down, whatever the window says
        bool silent = host.Count >= 3 && host.Skip(host.Count - 3).All(x => x == null);
        bool msBad = ms is int m && (wasBad ? m >= limitMs * 0.8 : m > limitMs);
        bool bad = msBad || lossBad || silent;
        bool localBad = false;
        if (bad && router.Count > 0)
        {
            var rr = router.Where(x => x != null).Select(x => x!.Value).ToList();
            double rloss = router.Count >= 10 ? router.Count(x => x == null) / (double)router.Count : 0;
            // the router itself slow or dropping: the problem is in the house, not out on the internet
            localBad = rr.Count == 0 || Median(rr.Skip(Math.Max(0, rr.Count - 5)).ToList()) > 20 || rloss * 100 >= lossPct;
        }
        return new NetReading(ms, loss, bad, localBad, host.Count);
    }

    private static int Median(List<int> v)
    {
        v.Sort();
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }
}
