using System.Threading.Channels;
using Hive.Domain;

namespace Hive.Modules.Telemetry.Ingest;

/// <summary>
/// Buffer between MQTT receive and the database writer. Bounded so a long DB outage cannot eat
/// the RPi memory; when full, the oldest items are dropped (QoS 1 events are still retried by hornets).
/// </summary>
public sealed class IngestQueue
{
    public const int Capacity = 50_000;

    private readonly Channel<IngestItem> _channel = Channel.CreateBounded<IngestItem>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Enqueue(IngestItem item) => _channel.Writer.TryWrite(item);

    public ChannelReader<IngestItem> Reader => _channel.Reader;

    public int Count => _channel.Reader.Count;
}

/// <summary>Notified after a batch is committed: SignalR, rules and notifications hook in here later.</summary>
public interface IIngestListener
{
    Task OnIngestedAsync(IReadOnlyList<IngestItem> items, CancellationToken ct);
}
