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
public sealed record FailoverDto(string? Host, int Port);

/// <summary>What the Settings card shows: the address, and whether this node has the shared secret the hornets' logins on the city broker are made from.</summary>
public sealed record FailoverStateDto(string? Host, int Port, bool CanLogin);

/// <summary>
/// The second broker of the hornets (spec 4.5): the city address is kept in the modes table and written, with the hornet's own login there,
/// into every hornet's config as <c>mqtt_city</c>. The hornet connects to it when home has not answered for three minutes.
/// </summary>
public sealed class FailoverService(HiveDbContext db, MqttGateway mqtt, IOptions<CityBrokerOptions> options, TimeProvider time)
{
    public const string Key = "failover.city";
    public const int DefaultPort = 1883;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly Regex HostName = new("^[A-Za-z0-9]([A-Za-z0-9.-]{0,126}[A-Za-z0-9])?$");

    public static string? Validate(FailoverDto r) =>
        string.IsNullOrWhiteSpace(r.Host) || !HostName.IsMatch(r.Host.Trim()) ? "host: адрес городского брокера (имя или IP)"
        : r.Port is < 1 or > 65535 ? "port: от 1 до 65535"
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
        return new FailoverStateDto(city.Host, city.Port, CanLogin);
    }

    public async Task SetAsync(FailoverDto dto, string by, CancellationToken ct)
    {
        var host = dto.Host?.Trim();
        var mode = await db.Modes.FirstOrDefaultAsync(m => m.Key == Key, ct);
        if (string.IsNullOrEmpty(host))
        {
            if (mode is not null)
                db.Modes.Remove(mode);
        }
        else
        {
            var value = JsonSerializer.Serialize(new FailoverDto(host, dto.Port), Web);
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
    public JsonObject? CityConfigFor(string deviceId, FailoverDto city) =>
        string.IsNullOrEmpty(city.Host) || !CanLogin ? null : new JsonObject
        {
            ["host"] = city.Host,
            ["port"] = city.Port,
            ["user"] = DynamicSecurity.ClientName(deviceId),
            ["pass"] = CityBrokerLogin.Password(options.Value.BridgePassword!, deviceId),
        };

    /// <summary>Brings every hornet's config in line with the city address (a new config revision only where it differs). Returns how many changed.</summary>
    public async Task<int> SyncAsync(CancellationToken ct)
    {
        var city = await GetAsync(ct);
        var changed = 0;
        var hornets = await db.Devices.Where(d => d.Driver == Drivers.Hornet && d.Status != DeviceStatus.Disabled && d.Config != null).ToListAsync(ct);
        foreach (var device in hornets)
        {
            var config = JsonNode.Parse(device.Config!.RootElement.GetRawText())!.AsObject();
            var want = CityConfigFor(device.DeviceId, city);
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
public sealed class FailoverSync(IServiceScopeFactory scopes, NodeState node, MqttGateway mqtt, TimeProvider time, ILogger<FailoverSync> logger) : BackgroundService
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
                    // The city only watches: the hornets' configs are written by the node that commands them.
                    if (!mqtt.IsConnected || node.ReadOnly)
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

public static class FailoverEndpoints
{
    public static IEndpointRouteBuilder MapFailoverEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/failover").WithTags("failover");
        group.MapGet("", async (FailoverService failover, CancellationToken ct) => TypedResults.Ok(await failover.StateAsync(ct)))
            .RequireAuthorization(HiveRoles.AdminOnly);
        group.MapPut("", async Task<Results<Ok<FailoverStateDto>, ValidationProblem>> (FailoverDto request, System.Security.Claims.ClaimsPrincipal user, FailoverService failover, CancellationToken ct) =>
            {
                // An empty host switches the second broker off.
                if (!string.IsNullOrWhiteSpace(request.Host) && FailoverService.Validate(request) is { } error)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["host"] = [error] });
                await failover.SetAsync(request, $"user:{user.Identity?.Name}", ct);
                await failover.SyncAsync(ct);
                return TypedResults.Ok(await failover.StateAsync(ct));
            })
            .RequireAuthorization(HiveRoles.AdminOnly);
        return app;
    }
}
