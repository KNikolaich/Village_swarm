using System.Text.Json;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Hive.Infrastructure.Mqtt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hive.Modules.Devices;

public sealed record QueueOtaRequest(string Build);

public sealed record OtaPendingDto(string DeviceId, string Build, string Version, DateTimeOffset RequestedAt, string RequestedBy, int Attempts);

/// <summary>What is kept (as JSON in the modes table, key <see cref="OtaPendingStore.KeyPrefix"/> + device id).</summary>
public sealed record OtaPendingEntry(string Build, string Version, DateTimeOffset RequestedAt, string RequestedBy, int Attempts);

/// <summary>
/// An update waiting for a deep sleeper's next wake-up: it is awake for seconds only, and the command is not retained,
/// so the hive keeps the wish and sends the command when the hornet shows up (<see cref="OtaWakeDelivery"/>).
/// </summary>
public sealed class OtaPendingStore(HiveDbContext db, TimeProvider time)
{
    public const string KeyPrefix = "ota.pending.";

    /// <summary>Wake-ups the hive tries before it gives up (a hornet that cannot download would otherwise be tried for ever).</summary>
    public const int MaxAttempts = 5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<OtaPendingEntry?> GetAsync(string deviceId, CancellationToken ct)
    {
        var value = await db.Modes.AsNoTracking().Where(m => m.Key == KeyPrefix + deviceId).Select(m => m.Value).FirstOrDefaultAsync(ct);
        return value is null ? null : JsonSerializer.Deserialize<OtaPendingEntry>(value, Json);
    }

    public async Task SetAsync(string deviceId, OtaPendingEntry entry, string by, CancellationToken ct)
    {
        var key = KeyPrefix + deviceId;
        var value = JsonSerializer.Serialize(entry, Json);
        var mode = await db.Modes.FirstOrDefaultAsync(m => m.Key == key, ct);
        if (mode is null)
            db.Modes.Add(new Mode { Key = key, Value = value, UpdatedAt = time.GetUtcNow(), UpdatedBy = by });
        else
        {
            mode.Value = value;
            mode.UpdatedAt = time.GetUtcNow();
            mode.UpdatedBy = by;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> ClearAsync(string deviceId, CancellationToken ct) =>
        await db.Modes.Where(m => m.Key == KeyPrefix + deviceId).ExecuteDeleteAsync(ct) > 0;

    /// <summary>All waiting updates, except those whose hornet already runs the version (they are cleaned up when it next wakes).</summary>
    public async Task<IReadOnlyList<OtaPendingDto>> ListAsync(CancellationToken ct)
    {
        var rows = await db.Modes.AsNoTracking().Where(m => m.Key.StartsWith(KeyPrefix)).ToListAsync(ct);
        var versions = await db.Devices.AsNoTracking().Select(d => new { d.DeviceId, d.FwVersion }).ToDictionaryAsync(d => d.DeviceId, d => d.FwVersion, ct);
        var list = new List<OtaPendingDto>();
        foreach (var row in rows)
        {
            var entry = JsonSerializer.Deserialize<OtaPendingEntry>(row.Value, Json);
            var deviceId = row.Key[KeyPrefix.Length..];
            if (entry is null || (versions.TryGetValue(deviceId, out var fw) && fw == entry.Version))
                continue;
            list.Add(new OtaPendingDto(deviceId, entry.Build, entry.Version, entry.RequestedAt, entry.RequestedBy, entry.Attempts));
        }
        return list.OrderBy(p => p.RequestedAt).ToList();
    }
}

/// <summary>
/// At the first health of a wake-up (reset reason "deepsleep") sends the waiting update, if any. The hornet runs the
/// command at once and stays up until it has installed it. Tries on at most <see cref="OtaPendingStore.MaxAttempts"/> wake-ups.
/// </summary>
public sealed class OtaWakeDelivery(IServiceScopeFactory scopes, TimeProvider time, ILogger<OtaWakeDelivery> logger) : IMqttMessageHandler
{
    /// <summary>The hornet subscribes to its commands right after it connects; a moment keeps the command from arriving before that.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    public IReadOnlyList<string> TopicFilters => [$"{Topics.Prefix}/dev/+/health"];

    public ValueTask HandleAsync(string topic, ReadOnlyMemory<byte> payload, bool retained, DateTimeOffset receivedAt)
    {
        if (retained || !WokeFromSleep(payload))
            return ValueTask.CompletedTask;
        var parts = topic.Split('/');
        if (parts.Length != 5)
            return ValueTask.CompletedTask;
        var deviceId = parts[3];
        // On its own task: the receive path must stay fast, and the command goes out through the same connection.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Settle, time);
                await DeliverAsync(deviceId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sending the waiting update to {Device} failed", deviceId);
            }
        });
        return ValueTask.CompletedTask;
    }

    private static bool WokeFromSleep(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("reset_reason", out var reason) && reason.GetString() == "deepsleep";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal async Task DeliverAsync(string deviceId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<OtaPendingStore>();
        if (await store.GetAsync(deviceId, ct) is not { } pending)
            return;
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var fw = await db.Devices.AsNoTracking().Where(d => d.DeviceId == deviceId).Select(d => d.FwVersion).FirstOrDefaultAsync(ct);
        if (fw == pending.Version)
        {
            await store.ClearAsync(deviceId, ct);
            logger.LogInformation("{Device} runs {Version}: the waiting update is done", deviceId, pending.Version);
            return;
        }
        if (pending.Attempts >= OtaPendingStore.MaxAttempts)
        {
            await store.ClearAsync(deviceId, ct);
            logger.LogWarning("{Device}: the waiting update to {Version} was sent {Attempts} times without success; dropped", deviceId, pending.Version, pending.Attempts);
            return;
        }
        var result = await scope.ServiceProvider.GetRequiredService<OtaService>().StartAsync(deviceId, pending.Build, pending.RequestedBy, ct, needOnline: false);
        if (result.Status == OtaStatus.Sent)
            await store.SetAsync(deviceId, pending with { Attempts = pending.Attempts + 1 }, pending.RequestedBy, ct);
        else
        {
            // The build is gone, or it no longer suits the board, or it is the version that runs: nothing to wait for.
            await store.ClearAsync(deviceId, ct);
            logger.LogWarning("{Device}: the waiting update could not be sent ({Status}: {Error}); dropped", deviceId, result.Status, result.Error);
        }
    }
}

public static class OtaPendingEndpoints
{
    public static IEndpointRouteBuilder MapOtaPendingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/devices/{deviceId}/ota/pending", async Task<Results<NoContent, NotFound, ValidationProblem>> (
                string deviceId, QueueOtaRequest request, System.Security.Claims.ClaimsPrincipal user,
                HiveDbContext db, OtaService ota, OtaPendingStore store, CancellationToken ct) =>
            {
                var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.Status != DeviceStatus.Disabled, ct);
                if (device is null)
                    return TypedResults.NotFound();
                if (device.Driver != Drivers.Hornet || DeviceConfigService.SleepS(device) is null || DeviceConfigService.SleepMode(device) != "deep")
                    return Refuse("only a hornet in deep sleep waits for its next wake-up: update the others when they are online");
                var check = await ota.StartAsync(deviceId, request.Build, $"user:{user.Identity?.Name}", ct, needOnline: false, checkOnly: true);
                if (check.Status is OtaStatus.UnknownDevice or OtaStatus.UnknownBuild)
                    return TypedResults.NotFound();
                if (check.Status != OtaStatus.Valid)
                    return Refuse(check.Error ?? "refused");
                var by = $"user:{user.Identity?.Name}";
                await store.SetAsync(deviceId, new OtaPendingEntry(request.Build, check.Version!, DateTimeOffset.UtcNow, by, 0), by, ct);
                return TypedResults.NoContent();

                static ValidationProblem Refuse(string reason) =>
                    TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["ota"] = [reason] });
            })
            .WithTags("devices")
            .RequireAuthorization(HiveRoles.AdminOnly);

        app.MapDelete("/api/devices/{deviceId}/ota/pending", async Task<NoContent> (string deviceId, OtaPendingStore store, CancellationToken ct) =>
            {
                await store.ClearAsync(deviceId, ct);
                return TypedResults.NoContent();
            })
            .WithTags("devices")
            .RequireAuthorization(HiveRoles.AdminOnly);

        app.MapGet("/api/ota/pending", async (OtaPendingStore store, CancellationToken ct) => TypedResults.Ok(await store.ListAsync(ct)))
            .WithTags("devices")
            .RequireAuthorization();
        return app;
    }
}
