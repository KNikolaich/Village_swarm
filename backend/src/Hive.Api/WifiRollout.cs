using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Hive.Modules.Devices;
using Hive.Modules.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Hive.Api;

/// <summary>How one hornet took the order to switch Wi-Fi: sent, or why not.</summary>
public sealed record WifiRolloutItem(string DeviceId, string Name, string Result);

/// <summary>The hornets to switch, named one by one: there is deliberately no "all", the first switch is tried on one board.</summary>
public sealed record WifiApplyRequest(IReadOnlyList<string> DeviceIds);

/// <summary>
/// Sends the Wi-Fi network saved in Settings to every hornet that can take it right now (spec: change the router or the
/// password without climbing to every board). Hornets that sleep, are offline or run old firmware are listed as skipped:
/// press it again when they are awake. Each hornet tries the new network and falls back to the old one on its own.
/// </summary>
public sealed class WifiRollout(HiveWifiService wifi, CommandService commands, HiveDbContext db, TimeProvider time)
{
    /// <summary>The command lives only this long: a hornet that wakes later must not switch on an old order.</summary>
    public const int TtlS = 120;

    private static bool Asleep(Device d, DateTimeOffset now) =>
        DeviceConfigService.SleepS(d) is { } sleep && DeviceConfigService.SleepMode(d) == "deep"
        && d.LastSeenAt is { } seen && now - seen > TimeSpan.FromSeconds(90) && sleep > 0;

    private static bool Knows(Device d) =>
        d.Info?.RootElement.TryGetProperty("commands", out var list) == true && list.ValueKind == JsonValueKind.Array
        && list.EnumerateArray().Any(c => c.GetString() == "wifi");

    public async Task<IReadOnlyList<WifiRolloutItem>?> ApplyAsync(IReadOnlyCollection<string> deviceIds, string by, CancellationToken ct)
    {
        var secrets = await wifi.RevealAsync(ct);
        if (secrets is null)
            return null;
        var now = time.GetUtcNow();
        var hornets = await db.Devices.AsNoTracking().Where(d => d.Status != DeviceStatus.Disabled && d.Status != DeviceStatus.Pending && d.TypeCode != "hive" && deviceIds.Contains(d.DeviceId))
            .OrderBy(d => d.DeviceId).ToListAsync(ct);

        var items = new List<WifiRolloutItem>();
        foreach (var d in hornets)
        {
            string result;
            if (d.Status != DeviceStatus.Online)
                result = "offline";
            else if (Asleep(d, now))
                result = "sleeping";
            else if (!Knows(d))
                result = "old_firmware";
            else
            {
                var (status, _) = await commands.SendAsync(d.DeviceId, "wifi", new JsonObject { ["ssid"] = secrets.Ssid, ["pass"] = secrets.Password }, by, TtlS, ct);
                result = status == SendStatus.Sent ? "sent" : "failed";
            }
            items.Add(new WifiRolloutItem(d.DeviceId, d.Name, result));
        }

        // The network name and the counts are journalled; the password never is.
        Audit.Record(db, now, by, "wifi_apply", null, new { ssid = secrets.Ssid, sent = items.Count(i => i.Result == "sent"), skipped = items.Count(i => i.Result is not "sent") });
        await db.SaveChangesAsync(ct);
        return items;
    }
}

public static class WifiRolloutEndpoints
{
    public static IEndpointRouteBuilder MapWifiRolloutEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/settings/wifi/apply", async Task<Results<Ok<IReadOnlyList<WifiRolloutItem>>, ValidationProblem>> (
                WifiApplyRequest request, ClaimsPrincipal user, WifiRollout rollout, CancellationToken ct) =>
            {
                if (request.DeviceIds is not { Count: > 0 })
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["deviceIds"] = ["Choose the hornets to switch"] });
                return await rollout.ApplyAsync(request.DeviceIds.ToHashSet(), $"user:{user.Identity?.Name}", ct) is { } items
                    ? TypedResults.Ok(items)
                    : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["wifi"] = ["Save the Wi-Fi network and password first"] });
            })
            .WithTags("settings")
            .RequireAuthorization(HiveRoles.AdminOnly);
        return app;
    }
}
