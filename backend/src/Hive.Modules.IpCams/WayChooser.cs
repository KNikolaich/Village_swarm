using Hive.Domain;

namespace Hive.Modules.IpCams;

/// <summary>A change of the way a channel's frames are taken first, and why (for the log and the stored setting).</summary>
public sealed record WaySwitch(string From, string To, string Reason);

/// <summary>
/// Which way (snapshot.cgi or RTSP) a channel in auto tries first. One failure does not move it: a busy NVR misses a snapshot
/// now and then. The preferred way changes after three failures in a row, or when the other way gives its first frame clearly
/// faster; a way that was left is tried first again every ten minutes, and taken back when it works and is faster.
/// Not thread-safe on its own: <see cref="Snapshots"/> asks it inside the NVR's queue, one request at a time.
/// </summary>
public sealed class WayChooser(string? preferred)
{
    public const int FailuresToSwitch = 3;
    public static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(10);
    private const int Samples = 5;

    private sealed class Stats
    {
        public readonly Queue<int> Ms = new();
        public int FailStreak;
        public DateTimeOffset? LastTry;

        public double? Median => Ms.Count == 0 ? null : Ms.Order().ElementAt(Ms.Count / 2);
    }

    private readonly Dictionary<string, Stats> _stats = [];

    /// <summary>The way tried first when nothing says otherwise.</summary>
    public string? Preferred { get; private set; } = preferred;

    private Stats Of(string way) => _stats.TryGetValue(way, out var s) ? s : _stats[way] = new Stats();

    /// <summary>The ways in the order to try them now: the preferred first, unless the other is due for its retry.</summary>
    public IReadOnlyList<string> Order(IReadOnlyList<string> ways, DateTimeOffset now)
    {
        if (ways.Count < 2)
            return ways;
        var first = Preferred is { } p && ways.Contains(p) ? p : ways[0];
        var other = ways.First(w => w != first);
        var o = Of(other);
        // A way left behind because it failed (the NVR may have been busy only for a while), or one that was faster, gets another
        // chance now and then; a way that works but is slower is not tried for nothing.
        // The way the caller lists first (snapshot.cgi, normally the quick one) is tried once if it never was: a channel that was
        // stored as "rtsp" after a passing failure gets its chance back.
        var retry = (o.LastTry is null && ways[0] == other)
                    || (o.LastTry is { } last && now - last >= RetryEvery
                        && (o.FailStreak > 0 || (o.Median is { } om && Of(first).Median is { } fm && om < fm)));
        return retry ? [other, first] : [first, other];
    }

    /// <summary>The outcome of one way (ms: until its first frame); a switch when the preferred way changes because of it.</summary>
    public WaySwitch? Record(string way, bool ok, int ms, DateTimeOffset now)
    {
        var s = Of(way);
        s.LastTry = now;
        if (ok)
        {
            s.FailStreak = 0;
            s.Ms.Enqueue(ms);
            while (s.Ms.Count > Samples)
                s.Ms.Dequeue();
        }
        else
            s.FailStreak++;

        var current = Preferred;
        if (current is null)
        {
            // Nothing chosen yet: the first way that works.
            if (!ok)
                return null;
            Preferred = way;
            return null;
        }
        if (way == current)
        {
            if (ok || s.FailStreak < FailuresToSwitch)
                return null;
            var other = _stats.Keys.FirstOrDefault(k => k != current && _stats[k].Ms.Count > 0 && _stats[k].FailStreak == 0);
            if (other is null)
                return null;
            Preferred = other;
            return new WaySwitch(current, other, $"{current} failed {s.FailStreak} times in a row");
        }
        // The other way worked: taken when it gives its first frame clearly faster, or the preferred one keeps failing.
        if (!ok)
            return null;
        var mine = Of(current);
        if (mine.FailStreak >= FailuresToSwitch)
        {
            Preferred = way;
            return new WaySwitch(current, way, $"{current} failed {mine.FailStreak} times in a row");
        }
        if (mine.Median is { } slow && s.Median is { } fast && fast < slow * 0.8)
        {
            Preferred = way;
            return new WaySwitch(current, way, $"{way} gives its first frame in {fast:0} ms, {current} in {slow:0} ms");
        }
        return null;
    }
}
