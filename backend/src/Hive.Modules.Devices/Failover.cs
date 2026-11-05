using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Hive.Infrastructure.Mqtt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Devices;

/// <summary>Where hornets find the city broker when home does not answer (the address as the HORNETS reach it, e.g. through the WireGuard tunnel of the router). No host: no second broker.</summary>
public sealed record FailoverDto(string? Host, int Port, string? ReplicaUrl = null);

/// <summary>What the Settings card shows: the address, and whether this node has the shared secret the hornets' logins on the city broker are made from.</summary>
public sealed record FailoverStateDto(string? Host, int Port, bool CanLogin, string? ReplicaUrl);

/// <summary>
/// The second broker of the hornets (spec 4.5): the city address is kept in the modes table and written, with the hornet's own login there,
/// into every hornet's config as <c>mqtt_city</c>. The hornet connects to it when home has not answered for three minutes.
/// </summary>
public sealed class FailoverService(HiveDbContext db, MqttGateway mqtt, IOptions<CityBrokerOptions> options, NodeState node, TimeProvider time)
{
    public const string Key = "failover.city";
    public const int DefaultPort = 1883;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly Regex HostName = new("^[A-Za-z0-9]([A-Za-z0-9.-]{0,126}[A-Za-z0-9])?$");

    public static string? Validate(FailoverDto r) =>
        !string.IsNullOrWhiteSpace(r.Host) && !HostName.IsMatch(r.Host.Trim()) ? "host: адрес городского брокера (имя или IP)"
        : r.Port is < 1 or > 65535 ? "port: от 1 до 65535"
        : !string.IsNullOrWhiteSpace(r.ReplicaUrl) && !(Uri.TryCreate(r.ReplicaUrl.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https") ? "replicaUrl: адрес вида https://192.168.1.3"
        : null;

    /// <summary>The shared secret is what the logins on the city broker come from: without it there is nothing to give a hornet.</summary>
    public bool CanLogin => !string.IsNullOrEmpty(options.Value.BridgePassword);

    public async Task<FailoverDto> GetAsync(CancellationToken ct)
    {
        var value = await db.Modes.AsNoTracking().Where(m => m.Key == Key).Select(m => m.Value).FirstOrDefaultAsync(ct);
        return value is null ? new FailoverDto(null, DefaultPort) : JsonSerializer.Deserialize<FailoverDto>(value, Web) ?? new FailoverDto(null, DefaultPort);
    }

    public async Task<FailoverStateDto> StateAsync(CancellationToken ct)
    {
        var city = await GetAsync(ct);
        return new FailoverStateDto(city.Host, city.Port, CanLogin, city.ReplicaUrl);
    }

    public async Task SetAsync(FailoverDto dto, string by, CancellationToken ct)
    {
        var host = dto.Host?.Trim();
        var replicaUrl = string.IsNullOrWhiteSpace(dto.ReplicaUrl) ? null : dto.ReplicaUrl.Trim().TrimEnd('/');
        var mode = await db.Modes.FirstOrDefaultAsync(m => m.Key == Key, ct);
        if (string.IsNullOrEmpty(host) && replicaUrl is null)
        {
            if (mode is not null)
                db.Modes.Remove(mode);
        }
        else
        {
            var value = JsonSerializer.Serialize(new FailoverDto(host, dto.Port, replicaUrl), Web);
            if (mode is null)
                db.Modes.Add(new Mode { Key = Key, Value = value, UpdatedAt = time.GetUtcNow(), UpdatedBy = by });
            else
            {
                mode.Value = value;
                mode.UpdatedAt = time.GetUtcNow();
                mode.UpdatedBy = by;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The "mqtt_city" a hornet should have, or null (no address set, or no shared secret to make its password from).</summary>
    public JsonObject? CityConfigFor(string deviceId, FailoverDto city, bool preferOther = false)
    {
        if (string.IsNullOrEmpty(city.Host) || !CanLogin)
            return null;
        var config = new JsonObject
        {
            ["host"] = city.Host,
            ["port"] = city.Port,
            ["user"] = DynamicSecurity.ClientName(deviceId),
            ["pass"] = CityBrokerLogin.Password(options.Value.BridgePassword!, deviceId),
        };
        if (preferOther)
            config["prefer_other"] = true;
        // On the city the "other broker" is the dacha's, for a hornet that is carried between the two: it settles on one within seconds.
        if (node.Node == "city")
        {
            config["after_s"] = 20;
            config["probe_s"] = 900;
        }
        return config;
    }

    public static bool PreferOther(JsonObject config) => config["prefer_hive"]?.GetValue<string>() == "other";

    /// <summary>
    /// The hive the hornet prefers to reach: "this" (the one it was enrolled with) or "other". Kept in its config as prefer_hive and written
    /// to the firmware inside mqtt_city. NotFound when there is no such hornet; BadRequest when "other" is asked and no other hive is set.
    /// </summary>
    public async Task<ConfigUpdateStatus> SetPreferAsync(string deviceId, string prefer, string by, CancellationToken ct)
    {
        if (prefer is not ("this" or "other"))
            return ConfigUpdateStatus.BadRequest;
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.Driver == Drivers.Hornet && d.Status != DeviceStatus.Disabled, ct);
        if (device is null)
            return ConfigUpdateStatus.NotFound;
        var city = await GetAsync(ct);
        if (prefer == "other" && CityConfigFor(deviceId, city) is null)
            return ConfigUpdateStatus.BadRequest;
        var config = device.Config is { } current
            ? JsonNode.Parse(current.RootElement.GetRawText())!.AsObject()
            : new JsonObject { ["v"] = 1, ["name"] = device.Name, ["health_interval_s"] = 60 };
        if (prefer == "other")
            config["prefer_hive"] = "other";
        else
            config.Remove("prefer_hive");
        var want = CityConfigFor(deviceId, city, prefer == "other");
        if (want is null)
            config.Remove("mqtt_city");
        else
            config["mqtt_city"] = want;
        device.ConfigRev++;
        config["rev"] = device.ConfigRev;
        device.Config = JsonDocument.Parse(config.ToJsonString());
        Audit.Record(db, time.GetUtcNow(), by, "device_prefer_hive", deviceId, new { prefer });
        await db.SaveChangesAsync(ct);
        if (mqtt.IsConnected)
            await mqtt.PublishAsync(Topics.Config(deviceId), config.ToJsonString(), retain: true, ct);
        return ConfigUpdateStatus.Updated;
    }

    /// <summary>Brings every hornet's config in line with the city address (a new config revision only where it differs). Returns how many changed.</summary>
    public async Task<int> SyncAsync(CancellationToken ct)
    {
        var city = await GetAsync(ct);
        var changed = 0;
        // The city only writes into the configs of its own hornets: the others are copies of home's.
        var hornets = await db.Devices.Where(d => d.Driver == Drivers.Hornet && d.Status != DeviceStatus.Disabled && d.Config != null && (d.Local || !node.ReadOnly)).ToListAsync(ct);
        foreach (var device in hornets)
        {
            var config = JsonNode.Parse(device.Config!.RootElement.GetRawText())!.AsObject();
            var want = CityConfigFor(device.DeviceId, city, PreferOther(config));
            if (JsonNode.DeepEquals(config["mqtt_city"], want))
                continue;
            if (want is null)
                config.Remove("mqtt_city");
            else
                config["mqtt_city"] = want;
            device.ConfigRev++;
            config["rev"] = device.ConfigRev;
            device.Config = JsonDocument.Parse(config.ToJsonString());
            await db.SaveChangesAsync(ct);
            if (mqtt.IsConnected)
                await mqtt.PublishAsync(Topics.Config(device.DeviceId), config.ToJsonString(), retain: true, ct);
            changed++;
        }
        return changed;
    }
}

/// <summary>Keeps the hornets' configs up to date with the city address: at start, every few minutes (new hornets, a changed address).</summary>
public sealed class FailoverSync(IServiceScopeFactory scopes, MqttGateway mqtt, TimeProvider time, ILogger<FailoverSync> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The build runs the app to write the OpenAPI document; it must not need a database.
        if (Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider")
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (!mqtt.IsConnected)
                        continue;
                    using var scope = scopes.CreateScope();
                    var changed = await scope.ServiceProvider.GetRequiredService<FailoverService>().SyncAsync(stoppingToken);
                    if (changed > 0)
                        logger.LogInformation("The city broker address went into {Count} hornet configs", changed);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Writing the city broker into hornet configs failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public sealed record PreferHiveRequest(string Prefer);

public static class FailoverEndpoints
{
    public static IEndpointRouteBuilder MapFailoverEndpoints(this IEndpointRouteBuilder app)
    {
        // Which hive a hornet prefers to reach: the one it was enrolled with ("this") or the other ("other").
        app.MapPut("/api/devices/{deviceId}/prefer", async Task<Results<NoContent, NotFound, ValidationProblem>> (
                string deviceId, PreferHiveRequest request, System.Security.Claims.ClaimsPrincipal user, FailoverService failover, CancellationToken ct) =>
                await failover.SetPreferAsync(deviceId, request.Prefer, $"user:{user.Identity?.Name}", ct) switch
                {
                    ConfigUpdateStatus.NotFound => TypedResults.NotFound(),
                    ConfigUpdateStatus.BadRequest => TypedResults.ValidationProblem(new Dictionary<string, string[]>
                        { ["prefer"] = ["this или other; для other сначала укажите адрес другого Улья в Настройках → Запасной брокер"] }),
                    _ => TypedResults.NoContent(),
                })
            .WithTags("devices")
            .RequireAuthorization(HiveRoles.CanControl);

        var group = app.MapGroup("/api/failover").WithTags("failover");
        group.MapGet("", async (FailoverService failover, CancellationToken ct) => TypedResults.Ok(await failover.StateAsync(ct)))
            .RequireAuthorization(HiveRoles.AdminOnly);
        group.MapPut("", async Task<Results<Ok<FailoverStateDto>, ValidationProblem>> (FailoverDto request, System.Security.Claims.ClaimsPrincipal user, FailoverService failover, CancellationToken ct) =>
            {
                // An empty host and address switch the second broker and the photo copy off.
                if (FailoverService.Validate(request) is { } error)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["host"] = [error] });
                await failover.SetAsync(request, $"user:{user.Identity?.Name}", ct);
                await failover.SyncAsync(ct);
                return TypedResults.Ok(await failover.StateAsync(ct));
            })
            .RequireAuthorization(HiveRoles.AdminOnly);
        return app;
    }
}
