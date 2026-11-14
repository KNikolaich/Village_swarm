namespace Hive.Modules.Notify.Sms;

/// <summary>
/// Keeps SMS rare and paid for only once: the same alarm within a window is dropped, and no more than N go out
/// in an hour whatever happens (spec 9.6). In memory: after a restart it starts empty, which is the safe side.
/// </summary>
public sealed class SmsThrottle(int maxPerHour, TimeSpan dedupe)
{
    private readonly Dictionary<string, DateTimeOffset> _seen = [];
    private readonly Queue<DateTimeOffset> _sent = new();
    private readonly Lock _gate = new();

    /// <summary>True when this alarm may go out now; the answer counts it.</summary>
    public bool TryTake(string key, DateTimeOffset now)
    {
        lock (_gate)
        {
            while (_sent.Count > 0 && now - _sent.Peek() >= TimeSpan.FromHours(1))
                _sent.Dequeue();
            foreach (var old in _seen.Where(x => now - x.Value >= dedupe).Select(x => x.Key).ToList())
                _seen.Remove(old);

            if (_seen.ContainsKey(key) || _sent.Count >= maxPerHour)
                return false;
            _seen[key] = now;
            _sent.Enqueue(now);
            return true;
        }
    }
}
