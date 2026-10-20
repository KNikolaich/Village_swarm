using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Hive.Modules.Assistant;

/// <summary>An action the assistant understood and waits to be confirmed with a button: what, with which arguments, for whom.</summary>
public sealed record PendingAction(string Id, long ChatId, string Tool, JsonObject Args, string Text, DateTimeOffset ExpiresAt);

/// <summary>
/// What the assistant keeps between messages, in memory only: actions waiting for their button (lost on restart, they
/// expire in minutes anyway), the last few exchanges of each chat for follow-ups, and the day's request count.
/// </summary>
public sealed class AssistantState(TimeProvider time)
{
    private const int HistoryTurns = 6;
    private static readonly TimeSpan HistoryKeep = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, PendingAction> _pending = new();
    private readonly ConcurrentDictionary<long, List<(DateTimeOffset At, string User, string Assistant)>> _history = new();
    private readonly Lock _countLock = new();
    private DateOnly _day;
    private int _count;

    public PendingAction Propose(long chat, string tool, JsonObject args, string text, TimeSpan wait)
    {
        foreach (var (key, old) in _pending)
            if (old.ExpiresAt < time.GetUtcNow())
                _pending.TryRemove(key, out _);
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5));
        var action = new PendingAction(id, chat, tool, args, text, time.GetUtcNow() + wait);
        _pending[id] = action;
        return action;
    }

    /// <summary>Takes the action off the list: a second press of the same button finds nothing.</summary>
    public PendingAction? Take(string id, long chat)
    {
        if (!_pending.TryGetValue(id, out var action) || action.ChatId != chat)
            return null;
        _pending.TryRemove(id, out _);
        return action.ExpiresAt >= time.GetUtcNow() ? action : null;
    }

    public IReadOnlyList<(string User, string Assistant)> History(long chat)
    {
        if (!_history.TryGetValue(chat, out var turns))
            return [];
        lock (turns)
            return turns.Where(t => t.At > time.GetUtcNow() - HistoryKeep).Select(t => (t.User, t.Assistant)).ToList();
    }

    public void Remember(long chat, string user, string assistant)
    {
        var turns = _history.GetOrAdd(chat, _ => []);
        lock (turns)
        {
            turns.Add((time.GetUtcNow(), user, assistant));
            if (turns.Count > HistoryTurns)
                turns.RemoveRange(0, turns.Count - HistoryTurns);
        }
    }

    /// <summary>Counts a request against the daily limit; false once the limit is reached.</summary>
    public bool TryCount(int limit)
    {
        lock (_countLock)
        {
            var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
            if (today != _day)
                (_day, _count) = (today, 0);
            if (_count >= limit)
                return false;
            _count++;
            return true;
        }
    }
}
