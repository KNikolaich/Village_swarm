using System.Text.Json;
using System.Text.RegularExpressions;
using Hive.Domain;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Hive.Modules.Devices;

/// <summary>The normal range of one reading of one device: below it the number is shown light blue, above it pink, inside it white. Either end may be absent.</summary>
public sealed record ValueRangeDto(string DeviceId, string Metric, double? Min, double? Max);

public sealed record ValueRangeRequest(double? Min, double? Max);

/// <summary>Kept in the modes table, key <see cref="KeyPrefix"/> + device id + "/" + metric, so no table of its own is needed.</summary>
public sealed class ValueRangeStore(HiveDbContext db, TimeProvider time)
{
    public const string KeyPrefix = "range.";

    private static readonly Regex Segment = new("^[a-z0-9_]+$");
    private static readonly Regex DeviceId = new("^[a-z][a-z0-9]*(-[a-z0-9]+)*$");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string? Validate(string deviceId, string metric, ValueRangeRequest r)
    {
        if (deviceId.Length > 32 || !DeviceId.IsMatch(deviceId))
            return "device: неверный id устройства";
        if (metric.Length > 64 || !Segment.IsMatch(metric))
            return "metric: латиница, цифры и «_»";
        if (r.Min is null && r.Max is null)
            return "Укажите хотя бы одну границу (или уберите диапазон)";
        if (r.Min is { } lo && r.Max is { } hi && lo >= hi)
            return "Нижняя граница должна быть меньше верхней";
        if (r.Min is { } a && !double.IsFinite(a) || r.Max is { } b && !double.IsFinite(b))
            return "Границы должны быть числами";
        return null;
    }

    public async Task<IReadOnlyList<ValueRangeDto>> ListAsync(CancellationToken ct)
    {
        var rows = await db.Modes.AsNoTracking().Where(m => m.Key.StartsWith(KeyPrefix)).ToListAsync(ct);
        var list = new List<ValueRangeDto>();
        foreach (var row in rows)
        {
            var name = row.Key[KeyPrefix.Length..];
            var slash = name.IndexOf('/');
            if (slash <= 0 || JsonSerializer.Deserialize<ValueRangeRequest>(row.Value, Json) is not { } r)
                continue;
            list.Add(new ValueRangeDto(name[..slash], name[(slash + 1)..], r.Min, r.Max));
        }
        return list.OrderBy(r => r.DeviceId).ThenBy(r => r.Metric).ToList();
    }

    public async Task SetAsync(string deviceId, string metric, ValueRangeRequest r, string by, CancellationToken ct)
    {
        var key = $"{KeyPrefix}{deviceId}/{metric}";
        var value = JsonSerializer.Serialize(r, Json);
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

    public async Task ClearAsync(string deviceId, string metric, CancellationToken ct) =>
        await db.Modes.Where(m => m.Key == $"{KeyPrefix}{deviceId}/{metric}").ExecuteDeleteAsync(ct);
}

public static class ValueRangeEndpoints
{
    public static IEndpointRouteBuilder MapValueRangeEndpoints(this IEndpointRouteBuilder app)
    {
        var ranges = app.MapGroup("/api/ranges").WithTags("ranges");
        ranges.MapGet("", async (ValueRangeStore store, CancellationToken ct) => TypedResults.Ok(await store.ListAsync(ct)))
            .RequireAuthorization();
        ranges.MapPut("/{deviceId}/{metric}", async Task<Results<NoContent, NotFound, ValidationProblem>> (
                string deviceId, string metric, ValueRangeRequest request, System.Security.Claims.ClaimsPrincipal user,
                HiveDbContext db, ValueRangeStore store, CancellationToken ct) =>
            {
                if (ValueRangeStore.Validate(deviceId, metric, request) is { } error)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["range"] = [error] });
                if (!await db.Devices.AnyAsync(d => d.DeviceId == deviceId, ct))
                    return TypedResults.NotFound();
                await store.SetAsync(deviceId, metric, request, $"user:{user.Identity?.Name}", ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(HiveRoles.CanControl);
        ranges.MapDelete("/{deviceId}/{metric}", async Task<NoContent> (string deviceId, string metric, ValueRangeStore store, CancellationToken ct) =>
            {
                await store.ClearAsync(deviceId, metric, ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(HiveRoles.CanControl);
        return app;
    }
}
