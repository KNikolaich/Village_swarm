using System.Text.Json;
using Hive.Contracts;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Mqtt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Devices;

/// <summary>What the city says to the dacha about one of the dacha's hornets (contracts/cluster.md): which hive it should prefer.</summary>
public sealed record PreferRequestMessage(int V, string Id, long Ts, string Node, string DeviceId, string Prefer);

public static class PreferRequest
{
    public const string TopicPrefix = $"{Topics.Prefix}/sync/city/prefer";

    public static string Topic(string deviceId) => $"{TopicPrefix}/{deviceId}";

    /// <summary>Where the dacha remembers the last request it applied for a hornet.</summary>
    public static string AppliedKey(string deviceId) => $"prefer.city.{deviceId}";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(PreferRequestMessage message) => JsonSerializer.Serialize(message, Json);

    public static PreferRequestMessage? Parse(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<PreferRequestMessage>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The dacha: the city says that one of the dacha's hornets should prefer this or the other hive (it was chosen on the city's copy of the hornet).
/// The choice is written the way the dacha's own switch writes it. Requests are retained, so one sent while the dacha was down arrives when it is back;
/// what was applied is remembered by its time, so it is not applied twice and never undoes a later choice made here.
/// </summary>
public sealed class PreferRequestReceiver(
    IOptions<HiveOptions> hive,
    IOptions<CityBrokerOptions> city,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<PreferRequestReceiver> logger) : IMqttMessageHandler
{
    public IReadOnlyList<string> TopicFilters => hive.Value.Node == "home" ? [$"{PreferRequest.TopicPrefix}/+"] : [];

    public ValueTask HandleAsync(string topic, ReadOnlyMemory<byte> payload, bool retained, DateTimeOffset receivedAt)
    {
        if (!topic.StartsWith(PreferRequest.TopicPrefix + "/", StringComparison.Ordinal) || payload.IsEmpty)
            return ValueTask.CompletedTask;
        var message = PreferRequest.Parse(payload.Span);
        if (message is not { V: 1, Node: "city" } || message.Prefer is not ("this" or "other")
            || !Topics.IsValidDeviceId(message.DeviceId) || topic != PreferRequest.Topic(message.DeviceId))
            return ValueTask.CompletedTask;
        // On its own task: the writes go through the same broker connection that delivers this message.
        _ = Task.Run(async () =>
        {
            try
            {
                await ApplyAsync(message, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Applying the city's choice of the preferred hive for {Device} failed", message.DeviceId);
            }
        });
        return ValueTask.CompletedTask;
    }

    /// <summary>true when the hornet's choice was written (or was already what the city asked); false when the request was not for this hive to apply.</summary>
    public async Task<bool> ApplyAsync(PreferRequestMessage message, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var appliedRow = await db.Modes.FirstOrDefaultAsync(m => m.Key == PreferRequest.AppliedKey(message.DeviceId), ct);
        if (appliedRow is not null && long.TryParse(appliedRow.Value, out var last) && message.Ts <= last)
            return false;   // this one (or a newer one) was applied before

        // Only a hornet made here: a guest of the city is the city's own business.
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == message.DeviceId && d.Driver == Drivers.Hornet && !d.Visitor && d.Status != DeviceStatus.Disabled, ct);
        if (device is null)
        {
            logger.LogInformation("The city asked to move {Device}, which is not a hornet made here: ignored", message.DeviceId);
            return false;
        }

        var wantsOther = message.Prefer == "other";
        var hasOther = device.Config?.RootElement.TryGetProperty("prefer_hive", out var current) == true && current.GetString() == "other";
        if (wantsOther != hasOther)
        {
            var status = await ActivatorUtilities.CreateInstance<FailoverService>(scope.ServiceProvider, city).SetPreferAsync(message.DeviceId, message.Prefer, "city", ct);
            if (status != ConfigUpdateStatus.Updated)
            {
                // No address of the city here (or no secret to make the hornet's login from): nothing was written, so the request stays unapplied for the next time.
                logger.LogWarning("The city asked {Device} to prefer {Prefer}, but it could not be written here ({Status}); set the city broker address in Settings", message.DeviceId, message.Prefer, status);
                return false;
            }
            logger.LogInformation("{Device} now prefers {Prefer}, as the city asked", message.DeviceId, message.Prefer);
        }

        var now = time.GetUtcNow();
        if (appliedRow is null)
            db.Modes.Add(new Mode { Key = PreferRequest.AppliedKey(message.DeviceId), Value = message.Ts.ToString(System.Globalization.CultureInfo.InvariantCulture), UpdatedAt = now, UpdatedBy = "city" });
        else
        {
            appliedRow.Value = message.Ts.ToString(System.Globalization.CultureInfo.InvariantCulture);
            appliedRow.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }
}
