using System.Collections.Concurrent;
using System.Reflection;
using Hive.Domain;
using Hive.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hive.Modules.Devices;

/// <summary>
/// The picture of the tab a monitored panel asked for last, drawn a little before it asks again: while a panel is monitored
/// (display.monitor) it reads every few minutes, and the hive keeps the frame warm at that pace so the read is answered at
/// once. Nothing is kept or drawn for a panel that is not monitored, and the entry goes when the monitoring ends.
/// </summary>
public sealed class FrameCache
{
    /// <summary>How long before the next expected read the frame is drawn.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromSeconds(15);

    public sealed record Entry(string Basis, DateTimeOffset At, byte[] Bits, string ETag);

    /// <summary>What a monitored panel asked for last: that frame is drawn again at <see cref="WarmAt"/>, until <see cref="Until"/>.</summary>
    public sealed record Watch(int Tab, bool Sleep, TimeSpan Every, DateTimeOffset WarmAt, DateTimeOffset Until);

    private readonly ConcurrentDictionary<string, Entry> frames = new();
    private readonly ConcurrentDictionary<string, Watch> watches = new();
    private long hits;

    /// <summary>Reads answered from here (for the tests).</summary>
    public long Hits => Interlocked.Read(ref hits);

    private static string Key(string device, int tab, bool sleep) => $"{device}/{tab}/{(sleep ? "s" : "t")}";

    /// <summary>The frame kept for this tab when it was drawn from the same basis (items, background, turn, fonts) less than <paramref name="ttl"/> ago.</summary>
    public Entry? Find(string device, int tab, bool sleep, string basis, DateTimeOffset now, TimeSpan ttl)
    {
        if (!frames.TryGetValue(Key(device, tab, sleep), out var entry) || entry.Basis != basis || now - entry.At >= ttl || now < entry.At)
            return null;
        Interlocked.Increment(ref hits);
        return entry;
    }

    public void Put(string device, int tab, bool sleep, string basis, DateTimeOffset now, byte[] bits, string etag) =>
        frames[Key(device, tab, sleep)] = new Entry(basis, now, bits, etag);

    /// <summary>
    /// A read of a monitored panel was answered: the same frame is drawn again <see cref="Lead"/> before the panel reads next.
    /// (Several reads before that only move the plan to the newest tab.)
    /// </summary>
    public void Plan(string device, int tab, bool sleep, DateTimeOffset now, TimeSpan every, DateTimeOffset until)
    {
        var warm = now + every - Lead;
        if (warm <= now)
            warm = now + every / 2;
        watches[device] = new Watch(tab, sleep, every, warm, until);
    }

    /// <summary>The panels whose frame is to be drawn now; one that is no longer monitored is forgotten.</summary>
    public IReadOnlyList<(string Device, Watch Watch)> Due(DateTimeOffset now)
    {
        var due = new List<(string, Watch)>();
        foreach (var (device, watch) in watches)
        {
            if (watch.Until <= now)
                Forget(device);
            else if (watch.WarmAt <= now)
                due.Add((device, watch));
        }
        return due;
    }

    /// <summary>The frame was drawn: the next one is due after the same pause.</summary>
    public void Rearm(string device, Watch watch) => watches[device] = watch with { WarmAt = watch.WarmAt + watch.Every };

    public void Forget(string device)
    {
        watches.TryRemove(device, out _);
        foreach (var key in frames.Keys.Where(k => k.StartsWith(device + "/", StringComparison.Ordinal)))
            frames.TryRemove(key, out _);
    }
}

/// <summary>Draws the frames of <see cref="FrameCache"/> when they are due: looks every 5 s, one at a time.</summary>
public sealed class FrameWarmer(IServiceScopeFactory scopes, FrameCache cache, TimeProvider time, ILogger<FrameWarmer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The build runs the app to write the OpenAPI document; it must not need a database.
        if (Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider")
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var (device, watch) in cache.Due(time.GetUtcNow()))
                {
                    try
                    {
                        using var scope = scopes.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<ScreenAccessService>().WarmAsync(device, watch, stoppingToken);
                        cache.Rearm(device, watch);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Warming the frame of {Device} failed", device);
                        cache.Rearm(device, watch);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
