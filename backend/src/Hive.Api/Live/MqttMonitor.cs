using System.Text;
using Hive.Infrastructure.Mqtt;

namespace Hive.Api.Live;

public sealed record MonitorEntry(long Seq, DateTimeOffset At, string Topic, bool Retained, string Payload);

public sealed record MonitorPage(long Last, IReadOnlyList<MonitorEntry> Entries);

/// <summary>
/// Read-only window on the hive's MQTT traffic for administrators: the last messages of vs/v1/# in memory, nothing
/// stored and nothing sent. Payloads are cut to 1000 characters; the broker password is never needed to look.
/// </summary>
public sealed class MqttMonitor : IMqttMessageHandler
{
    public const int Capacity = 500;
    public const int MaxPayload = 1000;

    private readonly object _lock = new();
    private readonly LinkedList<MonitorEntry> _entries = new();
    private long _seq;

    public IReadOnlyList<string> TopicFilters { get; } = ["vs/v1/#"];

    public ValueTask HandleAsync(string topic, ReadOnlyMemory<byte> payload, bool retained, DateTimeOffset receivedAt)
    {
        var text = IsText(payload.Span)
            ? Encoding.UTF8.GetString(payload.Span)
            : $"<binary {payload.Length} bytes>";
        if (text.Length > MaxPayload)
            text = text[..MaxPayload] + "…";
        lock (_lock)
        {
            _entries.AddLast(new MonitorEntry(++_seq, receivedAt, topic, retained, text));
            while (_entries.Count > Capacity)
                _entries.RemoveFirst();
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Entries newer than <paramref name="since"/>, oldest first, with the cursor for the next call.</summary>
    public MonitorPage Since(long since)
    {
        lock (_lock)
        {
            var fresh = _entries.Where(e => e.Seq > since).ToList();
            return new MonitorPage(_seq, fresh);
        }
    }

    private static bool IsText(ReadOnlySpan<byte> bytes)
    {
        try
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
