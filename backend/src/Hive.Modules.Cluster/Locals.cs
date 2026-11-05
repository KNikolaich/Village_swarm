using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Mqtt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Cluster;

/// <summary>A hornet the city made itself (its own hornet): what home needs to let it in when it is carried to the dacha.</summary>
public sealed record LocalHornet(string DeviceId, string Type, string Name, string? Hw);

public sealed record LocalsSnapshot(int V, long Ts, string Node, IReadOnlyList<LocalHornet> Devices);

public static class LocalsCodec
{
    public const string Topic = $"{Topics.Prefix}/sync/city/locals";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] Pack(LocalsSnapshot snapshot)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gzip, snapshot, Json);
        return buffer.ToArray();
    }

    public static LocalsSnapshot? Unpack(ReadOnlyMemory<byte> payload)
    {
        using var gzip = new GZipStream(new MemoryStream(payload.ToArray()), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<LocalsSnapshot>(gzip, Json);
    }

    /// <summary>What changed is the list, not the time.</summary>
    public static string Fingerprint(LocalsSnapshot snapshot) => JsonSerializer.Serialize(snapshot with { Ts = 0 }, Json);
}

/// <summary>
/// City: tells home which hornets the city made itself (retained, over the bridge). A hornet of the city hive that is carried to the dacha
/// has the dacha's broker as its second one; home lets it in because it knows its id from here.
/// </summary>
public sealed class LocalsPublisher(
    IOptions<ClusterOptions> options,
    IOptions<HiveOptions> hive,
    IServiceScopeFactory scopes,
    MqttGateway mqtt,
    TimeProvider time,
    ILogger<LocalsPublisher> logger) : BackgroundService
{
    private string? _last;
    private DateTimeOffset _lastAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider" || !options.Value.Enabled || hive.Value.Node != "city")
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.CatalogCheckS)), time);
        try
        {
            do
            {
                try
                {
                    await PublishIfNeededAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Publishing the city's own hornets failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task PublishIfNeededAsync(CancellationToken ct)
    {
        if (!mqtt.IsConnected)
            return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var own = await db.Devices.AsNoTracking()
            .Where(d => d.Local && d.Driver == Drivers.Hornet && d.Status != DeviceStatus.Disabled)
            .OrderBy(d => d.DeviceId).Select(d => new LocalHornet(d.DeviceId, d.TypeCode, d.Name, d.Hw)).ToListAsync(ct);
        var now = time.GetUtcNow();
        var snapshot = new LocalsSnapshot(1, now.ToUnixTimeMilliseconds(), "city", own);
        var fingerprint = LocalsCodec.Fingerprint(snapshot);
        if (fingerprint == _last && now - _lastAt < TimeSpan.FromSeconds(options.Value.CatalogEveryS))
            return;
        await mqtt.PublishAsync(LocalsCodec.Topic, LocalsCodec.Pack(snapshot), retain: true, ct);
        _last = fingerprint;
        _lastAt = now;
    }
}

/// <summary>
/// Home: for each hornet the city made itself, a login on the home broker (made from the shared secret and the id, as the hornet has it in its
/// config) and a row in the swarm as a guest, so that the hornet is let in when it is carried to the dacha and found when it arrives.
/// A guest that is away is not reported as missing.
/// </summary>
public sealed class LocalsReceiver(
    IOptions<ClusterOptions> options,
    IOptions<HiveOptions> hive,
    IServiceScopeFactory scopes,
    IServiceProvider services,
    TimeProvider time,
    ILogger<LocalsReceiver> logger) : IMqttMessageHandler
{
    // Resolved when needed: the broker's connection is built from the list of message handlers, this class among them.
    private DynamicSecurity Dynsec => services.GetRequiredService<DynamicSecurity>();

    public IReadOnlyList<string> TopicFilters => options.Value.Enabled && hive.Value.Node == "home" ? [LocalsCodec.Topic] : [];

    public ValueTask HandleAsync(string topic, ReadOnlyMemory<byte> payload, bool retained, DateTimeOffset receivedAt)
    {
        if (topic != LocalsCodec.Topic || payload.IsEmpty || string.IsNullOrEmpty(options.Value.BridgePassword))
            return ValueTask.CompletedTask;
        LocalsSnapshot? snapshot;
        try
        {
            snapshot = LocalsCodec.Unpack(payload);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return ValueTask.CompletedTask;
        }
        if (snapshot is not { V: 1, Node: "city" })
            return ValueTask.CompletedTask;
        // On its own task: the broker's answers come back through the loop that delivers this message.
        _ = Task.Run(async () =>
        {
            try
            {
                await ApplyAsync(snapshot, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Letting the city's own hornets in failed");
            }
        });
        return ValueTask.CompletedTask;
    }

    internal async Task<int> ApplyAsync(LocalsSnapshot snapshot, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var now = time.GetUtcNow();
        var added = 0;
        foreach (var hornet in snapshot.Devices.Where(h => Topics.IsValidDeviceId(h.DeviceId)))
        {
            await Dynsec.ProvisionHornetIfMissingAsync(hornet.DeviceId, CityBrokerLogin.Password(options.Value.BridgePassword!, hornet.DeviceId), ct);
            if (await db.Devices.AnyAsync(d => d.DeviceId == hornet.DeviceId, ct) || !await db.DeviceTypes.AnyAsync(t => t.Code == hornet.Type, ct))
                continue;
            db.Devices.Add(new Device
            {
                DeviceId = hornet.DeviceId, TypeCode = hornet.Type, Name = hornet.Name, Hw = hornet.Hw, Visitor = true,
                Status = DeviceStatus.Offline, CreatedAt = now,
            });
            added++;
        }
        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("{Count} hornets of the city are known here as guests", added);
        }
        return added;
    }
}
