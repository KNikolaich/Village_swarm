using Hive.Domain;
using Hive.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hive.Modules.Telemetry.Ingest;

/// <summary>
/// Drains <see cref="IngestQueue"/> in batches (every second or 200 items), drops duplicates by message id
/// and writes them with <see cref="IngestWriter"/>. A failed batch is retried until the database is back.
/// </summary>
public sealed class IngestPipeline(
    IngestQueue queue,
    IServiceScopeFactory scopes,
    IEnumerable<IIngestListener> listeners,
    TimeProvider time,
    ILogger<IngestPipeline> logger) : BackgroundService
{
    public const int MaxBatch = 200;
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>A batch that fails this many times while the database answers holds a poison item: it is written item by item and the bad one is dropped.</summary>
    private const int PoisonAfter = 3;

    private readonly RecentIds _seen = new(20_000);

    /// <summary>Items written since start; used by tests and the system page.</summary>
    public long Written { get; private set; }

    public long Duplicates { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnsurePartitionsAsync(stoppingToken);
        var partitionsCheckedOn = time.GetUtcNow().Date;

        var batch = new List<IngestItem>(MaxBatch);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await queue.Reader.WaitToReadAsync(stoppingToken))
                return;

            var deadline = time.GetUtcNow() + MaxWait;
            while (batch.Count < MaxBatch)
            {
                if (queue.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                    continue;
                }
                var left = deadline - time.GetUtcNow();
                if (left <= TimeSpan.Zero)
                    break;
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(left);
                try
                {
                    await queue.Reader.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }

            if (time.GetUtcNow().Date != partitionsCheckedOn)
            {
                await EnsurePartitionsAsync(stoppingToken);
                partitionsCheckedOn = time.GetUtcNow().Date;
            }

            await FlushAsync(batch, stoppingToken);
            batch.Clear();
        }
    }

    private async Task FlushAsync(List<IngestItem> batch, CancellationToken ct)
    {
        var unique = Deduplicate(batch);
        if (unique.Count == 0)
            return;
        unique = await ArrivingAsync(unique, ct);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IngestWriter>().WriteAsync(unique, ct);
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Ingest batch of {Count} failed (try {Attempt}), retrying in {Delay}s ({Queued} queued)",
                    unique.Count, attempt, RetryDelay.TotalSeconds, queue.Count);
                // While the database is down the batch waits as long as it takes: nothing is lost. When the database answers and the batch
                // still fails, one item in it is bad: the rest must not wait behind it for ever.
                if (attempt >= PoisonAfter && await DatabaseAnswersAsync(ct))
                {
                    unique = await WriteEachAsync(unique, ct);
                    break;
                }
                await Task.Delay(RetryDelay, time, ct);
            }
        }

        foreach (var item in unique)
            if (item.MessageId is not null)
                _seen.Add(item.MessageId);
        Written += unique.Count;

        foreach (var listener in listeners)
        {
            try
            {
                await listener.OnIngestedAsync(unique, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Ingest listener {Listener} failed", listener.GetType().Name);
            }
        }
    }

    private async Task<bool> DatabaseAnswersAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<HiveDbContext>().Database.CanConnectAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Writes the items of a failed batch one at a time; the ones that still fail are dropped with an error in the log. Returns what was written.</summary>
    private async Task<List<IngestItem>> WriteEachAsync(List<IngestItem> items, CancellationToken ct)
    {
        var written = new List<IngestItem>(items.Count);
        foreach (var item in items)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IngestWriter>().WriteAsync([item], ct);
                written.Add(item);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Ingest item dropped: {Kind} of {Device} cannot be stored", item.GetType().Name, item.DeviceId);
            }
        }
        return written;
    }

    /// <summary>
    /// While the family is on its way (presence arriving), an alarm from an armed camera becomes a quiet warning with its photos
    /// (<see cref="ArrivingMode"/>): stored so, and seen so by the rules, the feed and Telegram. Guard-cams report alarm whenever
    /// they are armed; the mode is the hive's to know.
    /// </summary>
    private async Task<List<IngestItem>> ArrivingAsync(List<IngestItem> items, CancellationToken ct)
    {
        static bool Candidate(IngestItem i) => i is DeviceEventReceived { Severity: EventSeverity.Alarm } e && ArrivingMode.Concerns(e.Type);
        if (!items.Any(Candidate))
            return items;
        string? presence;
        try
        {
            using var scope = scopes.CreateScope();
            presence = await scope.ServiceProvider.GetRequiredService<HiveDbContext>().Modes.AsNoTracking()
                .Where(m => m.Key == Mode.Presence).Select(m => m.Value).FirstOrDefaultAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Guard mode not read; alarms stay alarms");
            return items;
        }
        return presence == Mode.PresenceArriving
            ? items.Select(i => Candidate(i) ? ArrivingMode.Quieten((DeviceEventReceived)i) : i).ToList()
            : items;
    }

    private List<IngestItem> Deduplicate(List<IngestItem> batch)
    {
        var inBatch = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<IngestItem>(batch.Count);
        foreach (var item in batch)
        {
            if (item.MessageId is { } id && (_seen.Contains(id) || !inBatch.Add(id)))
            {
                Duplicates++;
                continue;
            }
            unique.Add(item);
        }
        return unique;
    }

    private async Task EnsurePartitionsAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await TelemetryPartitions.EnsureAsync(scope.ServiceProvider.GetRequiredService<HiveDbContext>(), time.GetUtcNow(), ct: ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Creating telemetry partitions failed, retrying in {Delay}s", RetryDelay.TotalSeconds);
                await Task.Delay(RetryDelay, time, ct);
            }
        }
    }
}

/// <summary>Fixed-size set of recently written message ids (events are also unique in the database).</summary>
internal sealed class RecentIds(int capacity)
{
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public bool Contains(string id) => _set.Contains(id);

    public void Add(string id)
    {
        if (!_set.Add(id))
            return;
        _order.Enqueue(id);
        if (_order.Count > capacity)
            _set.Remove(_order.Dequeue());
    }
}
