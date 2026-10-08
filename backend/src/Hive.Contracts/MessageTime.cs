namespace Hive.Contracts;

/// <summary>Which clock to believe for a hornet timestamp (spec 4.3).</summary>
public static class MessageTime
{
    /// <summary>Hornet clocks this far ahead of the hive are not trusted.</summary>
    public static readonly TimeSpan MaxClockAhead = TimeSpan.FromMinutes(5);

    /// <summary>Older than this cannot come from a hornet outbox, so the clock must be wrong.</summary>
    public static readonly TimeSpan MaxBufferedAge = TimeSpan.FromDays(7);

    /// <summary>
    /// The hornet clock when plausible, otherwise receive time. Past timestamps are trusted up to
    /// <see cref="MaxBufferedAge"/>, because outbox replays are legitimately old.
    /// </summary>
    public static DateTimeOffset Effective(long ts, DateTimeOffset receivedAt)
    {
        if (ts <= 0)
            return receivedAt;
        var deviceTime = DateTimeOffset.FromUnixTimeMilliseconds(ts);
        return deviceTime > receivedAt + MaxClockAhead || deviceTime < receivedAt - MaxBufferedAge ? receivedAt : deviceTime;
    }

    /// <summary>Health is never buffered, so a gap over 5 minutes in either direction means a skewed clock.</summary>
    public static bool IsSkewed(long ts, DateTimeOffset receivedAt) =>
        ts <= 0 || (DateTimeOffset.FromUnixTimeMilliseconds(ts) - receivedAt).Duration() > MaxClockAhead;
}
