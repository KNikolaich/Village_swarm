using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Rules;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Cluster;

public sealed record CatalogZone(string Name, string Kind, int Sort);

/// <summary>What the city needs to know of a device by name. Runtime state (status, readings, firmware) comes through the bridge, not from here.</summary>
public sealed record CatalogDevice(string DeviceId, string Type, string Name, string? Zone, string Driver, int Sort, bool Disabled, JsonElement? Info);

public sealed record CatalogRule(string Name, bool Enabled, JsonElement Definition);

/// <summary>A person who may sign in: the same login and password at home and in the city (the password only as the hash the hive stores).</summary>
public sealed record CatalogUser(string UserName, string? DisplayName, string? PasswordHash, IReadOnlyList<string> Roles);

/// <summary>The reference data of the home node (spec 11.2): the master is at home, the city applies it and only reads.</summary>
public sealed record CatalogSnapshot(
    int V, long Ts, string Node,
    IReadOnlyList<CatalogZone> Zones, IReadOnlyList<CatalogDevice> Devices, IReadOnlyList<CatalogRule> Rules, IReadOnlyList<CatalogUser> Users);

public sealed record CatalogCounts(int Zones, int Devices, int Rules, int Users);

/// <summary>When the city last applied a snapshot, and how big it was: shown in the cluster banner.</summary>
public sealed class CatalogState
{
    public DateTimeOffset? AppliedAt { get; private set; }

    public CatalogCounts? Counts { get; private set; }

    public void Applied(DateTimeOffset at, CatalogCounts counts)
    {
        AppliedAt = at;
        Counts = counts;
    }
}

public static class CatalogCodec
{
    public const string Topic = $"{Topics.Prefix}/sync/home/catalog";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] Pack(CatalogSnapshot snapshot)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gzip, snapshot, Json);
        return buffer.ToArray();
    }

    public static CatalogSnapshot? Unpack(ReadOnlyMemory<byte> payload)
    {
        using var gzip = new GZipStream(new MemoryStream(payload.ToArray()), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<CatalogSnapshot>(gzip, Json);
    }

    /// <summary>A fingerprint of the content without the time, to publish only when something changed.</summary>
    public static string Fingerprint(CatalogSnapshot snapshot) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot with { Ts = 0 }, Json)));
}

/// <summary>Reads the catalog of this node out of the database.</summary>
public sealed class CatalogBuilder(HiveDbContext db, UserManager<HiveUser> users, IOptions<HiveOptions> hive, TimeProvider time)
{
    public async Task<CatalogSnapshot> BuildAsync(CancellationToken ct)
    {
        var zones = await db.Zones.AsNoTracking().OrderBy(z => z.Id).ToListAsync(ct);
        var zoneName = zones.ToDictionary(z => z.Id, z => z.Name);
        var devices = await db.Devices.AsNoTracking().OrderBy(d => d.DeviceId).ToListAsync(ct);
        var rules = await db.Rules.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
        var people = new List<CatalogUser>();
        foreach (var user in await users.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(ct))
            people.Add(new CatalogUser(user.UserName!, user.DisplayName, user.PasswordHash, (await users.GetRolesAsync(user)).OrderBy(r => r).ToList()));

        return new CatalogSnapshot(1, time.GetUtcNow().ToUnixTimeMilliseconds(), hive.Value.Node,
            zones.Select(z => new CatalogZone(z.Name, z.Kind.ToString(), z.Sort)).ToList(),
            devices.Select(d => new CatalogDevice(d.DeviceId, d.TypeCode, d.Name, d.ZoneId is { } z && zoneName.TryGetValue(z, out var n) ? n : null,
                d.Driver, d.Sort, d.Status == DeviceStatus.Disabled,
                d.Driver != Drivers.Hornet && d.Info is { } info ? info.RootElement.Clone() : null)).ToList(),
            rules.Select(r => new CatalogRule(r.Name, r.Enabled, r.Definition.RootElement.Clone())).ToList(),
            people);
    }
}

/// <summary>Applies the home catalog in the city. Names are the keys (ids differ between the two databases); nothing of the city's own is removed except what a snapshot put there.</summary>
public sealed class CatalogApplier(HiveDbContext db, UserManager<HiveUser> users, RuleStore rules, TimeProvider time)
{
    public const string SyncedBy = "sync:home";

    public async Task<CatalogCounts> ApplyAsync(CatalogSnapshot s, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        var zones = await db.Zones.ToListAsync(ct);
        foreach (var z in s.Zones)
        {
            var kind = Enum.TryParse<ZoneKind>(z.Kind, out var k) ? k : ZoneKind.House;
            var row = zones.FirstOrDefault(x => x.Name == z.Name);
            if (row is null)
            {
                row = new Zone { Name = z.Name, Kind = kind, Sort = z.Sort };
                db.Zones.Add(row);
                zones.Add(row);
            }
            else
            {
                row.Kind = kind;
                row.Sort = z.Sort;
            }
        }
        await db.SaveChangesAsync(ct);
        var zoneId = zones.ToDictionary(z => z.Name, z => z.Id);

        var existing = await db.Devices.ToDictionaryAsync(d => d.DeviceId, ct);
        foreach (var d in s.Devices)
        {
            if (!existing.TryGetValue(d.DeviceId, out var device))
            {
                device = new Device { DeviceId = d.DeviceId, TypeCode = d.Type, Name = d.Name, CreatedAt = now, Status = DeviceStatus.Pending };
                db.Devices.Add(device);
            }
            device.TypeCode = d.Type;
            device.Name = d.Name;
            device.ZoneId = d.Zone is not null && zoneId.TryGetValue(d.Zone, out var id) ? id : null;
            device.Driver = d.Driver;
            device.Sort = d.Sort;
            if (d.Disabled)
                device.Status = DeviceStatus.Disabled;
            else if (device.Status == DeviceStatus.Disabled)
                device.Status = DeviceStatus.Offline;
            if (d.Info is { } info && d.Driver != Drivers.Hornet)
                device.Info = JsonDocument.Parse(info.GetRawText());
        }

        var ruleRows = await db.Rules.ToListAsync(ct);
        foreach (var r in s.Rules)
        {
            var row = ruleRows.FirstOrDefault(x => x.Name == r.Name);
            if (row is null)
                db.Rules.Add(new Rule { Name = r.Name, Enabled = r.Enabled, Definition = JsonDocument.Parse(r.Definition.GetRawText()), UpdatedAt = now, UpdatedBy = SyncedBy });
            else if (row.Enabled != r.Enabled || row.Definition.RootElement.GetRawText() != r.Definition.GetRawText())
            {
                row.Enabled = r.Enabled;
                row.Definition = JsonDocument.Parse(r.Definition.GetRawText());
                row.UpdatedAt = now;
                row.UpdatedBy = SyncedBy;
                row.State = null;
            }
        }
        // A rule home no longer has goes, if it came from home in the first place.
        var names = s.Rules.Select(r => r.Name).ToHashSet();
        db.Rules.RemoveRange(ruleRows.Where(r => r.UpdatedBy == SyncedBy && !names.Contains(r.Name)));
        await db.SaveChangesAsync(ct);
        rules.Invalidate();

        foreach (var u in s.Users)
        {
            var user = await users.FindByNameAsync(u.UserName);
            if (user is null)
            {
                user = new HiveUser { UserName = u.UserName, DisplayName = u.DisplayName, PasswordHash = u.PasswordHash, CreatedAt = now };
                if (!(await users.CreateAsync(user)).Succeeded)
                    continue;
            }
            else if (user.PasswordHash != u.PasswordHash || user.DisplayName != u.DisplayName)
            {
                user.PasswordHash = u.PasswordHash;
                user.DisplayName = u.DisplayName;
                await users.UpdateAsync(user);
            }
            var have = await users.GetRolesAsync(user);
            var want = u.Roles.Where(HiveRoles.All.Contains).ToList();
            var add = want.Except(have).ToList();
            var remove = have.Except(want).ToList();
            if (add.Count > 0)
                await users.AddToRolesAsync(user, add);
            if (remove.Count > 0)
                await users.RemoveFromRolesAsync(user, remove);
        }
        return new CatalogCounts(s.Zones.Count, s.Devices.Count, s.Rules.Count, s.Users.Count);
    }
}

/// <summary>Home: publishes the catalog when it changed (looked at once a minute) and at least every five minutes (spec 11.2).</summary>
public sealed class CatalogPublisher(
    IOptions<ClusterOptions> options,
    IOptions<HiveOptions> hive,
    MqttGateway mqtt,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<CatalogPublisher> logger) : BackgroundService
{
    internal string? LastFingerprint { get; private set; }

    private DateTimeOffset _lastPublished = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The build runs the app to write the OpenAPI document; it must not need a database.
        if (Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider" || !options.Value.Enabled || hive.Value.Node != "home")
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
                    logger.LogWarning(ex, "Publishing the catalog failed");
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
        var snapshot = await scope.ServiceProvider.GetRequiredService<CatalogBuilder>().BuildAsync(ct);
        var fingerprint = CatalogCodec.Fingerprint(snapshot);
        var now = time.GetUtcNow();
        if (fingerprint == LastFingerprint && now - _lastPublished < TimeSpan.FromSeconds(options.Value.CatalogEveryS))
            return;
        await mqtt.PublishAsync(CatalogCodec.Topic, CatalogCodec.Pack(snapshot), retain: true, ct);
        LastFingerprint = fingerprint;
        _lastPublished = now;
        logger.LogInformation("Catalog published: {Zones} zones, {Devices} devices, {Rules} rules, {Users} users",
            snapshot.Zones.Count, snapshot.Devices.Count, snapshot.Rules.Count, snapshot.Users.Count);
    }
}

/// <summary>City: takes the retained catalog from the bridge and applies it.</summary>
public sealed class CatalogReceiver(
    IOptions<ClusterOptions> options,
    IOptions<HiveOptions> hive,
    IServiceScopeFactory scopes,
    CatalogState state,
    TimeProvider time,
    ILogger<CatalogReceiver> logger) : IMqttMessageHandler
{
    private long _lastTs;

    public IReadOnlyList<string> TopicFilters => options.Value.Enabled && hive.Value.Node == "city" ? [CatalogCodec.Topic] : [];

    public async ValueTask HandleAsync(string topic, ReadOnlyMemory<byte> payload, bool retained, DateTimeOffset receivedAt)
    {
        if (topic != CatalogCodec.Topic || !options.Value.Enabled || hive.Value.Node != "city" || payload.IsEmpty)
            return;
        try
        {
            var snapshot = CatalogCodec.Unpack(payload);
            if (snapshot is not { V: 1, Node: "home" } || snapshot.Ts <= _lastTs)
                return;
            using var scope = scopes.CreateScope();
            var counts = await scope.ServiceProvider.GetRequiredService<CatalogApplier>().ApplyAsync(snapshot, CancellationToken.None);
            _lastTs = snapshot.Ts;
            state.Applied(time.GetUtcNow(), counts);
            logger.LogInformation("Catalog from home applied: {Zones} zones, {Devices} devices, {Rules} rules, {Users} users", counts.Zones, counts.Devices, counts.Rules, counts.Users);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or DbUpdateException or InvalidOperationException)
        {
            logger.LogWarning(ex, "A catalog from home could not be applied");
        }
    }
}
