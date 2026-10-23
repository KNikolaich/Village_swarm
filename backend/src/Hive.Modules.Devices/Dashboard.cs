using System.Security.Claims;
using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Hive.Modules.Devices;

/// <summary>One widget of the overview: its id (guard, zone:3, cam:guard-hall...), width in twelfths, hidden or not.</summary>
public sealed record DashboardWidgetDto(string Id, int W = 12, bool Hidden = false);

/// <summary>The overview on a wide screen and on a phone; null: not arranged yet, the UI's default order.</summary>
public sealed record DashboardLayoutsDto(IReadOnlyList<DashboardWidgetDto>? Desktop, IReadOnlyList<DashboardWidgetDto>? Phone);

/// <summary>Items null: back to the default order.</summary>
public sealed record SaveDashboardLayoutRequest(IReadOnlyList<DashboardWidgetDto>? Items);

/// <summary>How the overview is arranged (spec 8.2 screen 1): only the screen uses it.</summary>
public sealed class DashboardLayoutService(HiveDbContext db, TimeProvider time)
{
    public static readonly string[] Screens = ["desktop", "phone"];
    public static readonly int[] Widths = [3, 4, 6, 8, 12];
    public const int MaxWidgets = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static bool IdOk(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.');

    public static string? Validate(IReadOnlyList<DashboardWidgetDto> items)
    {
        if (items.Count > MaxWidgets)
            return $"Up to {MaxWidgets} widgets";
        if (items.Any(i => i.Id is null || !IdOk(i.Id)))
            return "A widget id is 1..64 characters of letters, digits, - _ : .";
        if (items.Any(i => !Widths.Contains(i.W)))
            return "A width is one of " + string.Join(", ", Widths);
        if (items.Select(i => i.Id).Distinct().Count() != items.Count)
            return "Each widget once";
        return null;
    }

    public async Task<DashboardLayoutsDto> GetAsync(CancellationToken ct)
    {
        var rows = await db.DashboardLayouts.AsNoTracking().ToListAsync(ct);
        IReadOnlyList<DashboardWidgetDto>? Of(string screen) =>
            rows.FirstOrDefault(r => r.Screen == screen)?.Items.Deserialize<List<DashboardWidgetDto>>(Json);
        return new DashboardLayoutsDto(Of("desktop"), Of("phone"));
    }

    public async Task SaveAsync(string screen, IReadOnlyList<DashboardWidgetDto>? items, string by, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var row = await db.DashboardLayouts.FirstOrDefaultAsync(r => r.Screen == screen, ct);
        if (items is null)
        {
            if (row is not null)
                db.DashboardLayouts.Remove(row);
        }
        else
        {
            var json = JsonSerializer.SerializeToDocument(items, Json);
            if (row is null)
                db.DashboardLayouts.Add(new DashboardLayout { Screen = screen, Items = json, UpdatedAt = now, UpdatedBy = by });
            else
            {
                row.Items = json;
                row.UpdatedAt = now;
                row.UpdatedBy = by;
            }
        }
        Audit.Record(db, now, by, items is null ? "dashboard_reset" : "dashboard_layout", screen);
        await db.SaveChangesAsync(ct);
    }
}

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var dashboard = app.MapGroup("/api/dashboard").WithTags("dashboard");
        dashboard.MapGet("/layout", (DashboardLayoutService s, CancellationToken ct) => s.GetAsync(ct));
        dashboard.MapPut("/layout/{screen}", async Task<Results<NoContent, NotFound, ValidationProblem>> (
                string screen, SaveDashboardLayoutRequest request, ClaimsPrincipal user, DashboardLayoutService s, CancellationToken ct) =>
            {
                if (!DashboardLayoutService.Screens.Contains(screen))
                    return TypedResults.NotFound();
                if (request.Items is { } items && DashboardLayoutService.Validate(items) is { } error)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["items"] = [error] });
                await s.SaveAsync(screen, request.Items, $"user:{user.Identity?.Name}", ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(HiveRoles.AdminOnly);
        return app;
    }
}
