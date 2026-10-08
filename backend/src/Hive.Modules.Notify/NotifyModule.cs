using System.Security.Claims;
using System.Security.Cryptography;
using Hive.Domain;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Hive.Modules.Notify.Telegram;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Modules.Notify;

public sealed record TelegramStatusDto(bool Enabled, string? BotUsername, IReadOnlyList<TelegramLinkDto> Links);

public sealed record TelegramLinkDto(int Id, long ChatId, string? Username, DateTimeOffset LinkedAt, string NotifyLevel, DateTimeOffset? MutedUntil);

public sealed record LinkCodeDto(string Code, DateTimeOffset ExpiresAt, string? BotUsername);

/// <summary>Outbox of notifications and the Telegram bot (spec 6.3, 6.7, 9).</summary>
public static class NotifyModule
{
    public const string Name = "notify";

    // No 0/O, 1/I: the code is read from a screen and typed on a phone.
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static IServiceCollection AddNotifyModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.Section));
        services.AddSingleton<TelegramGateway>();
        services.AddSingleton<IIngestListener, NotificationPlanner>();
        services.AddSingleton<NotificationOutbox>();
        services.AddHostedService(sp => sp.GetRequiredService<NotificationOutbox>());
        services.AddSingleton<TelegramBotHost>();
        services.AddHostedService(sp => sp.GetRequiredService<TelegramBotHost>());
        services.AddScoped<BotCommands>();
        return services;
    }

    public static IEndpointRouteBuilder MapNotifyEndpoints(this IEndpointRouteBuilder app)
    {
        var tg = app.MapGroup("/api/me/telegram").WithTags("telegram");

        tg.MapGet("", async (ClaimsPrincipal user, UserManager<HiveUser> users, HiveDbContext db, TelegramGateway gateway, TelegramBotHost host, CancellationToken ct) =>
        {
            var userId = int.Parse(users.GetUserId(user)!, System.Globalization.CultureInfo.InvariantCulture);
            var links = await db.TgLinks.AsNoTracking().Where(l => l.UserId == userId).OrderBy(l => l.LinkedAt).ToListAsync(ct);
            return new TelegramStatusDto(gateway.Enabled, host.BotUsername,
                links.Select(l => new TelegramLinkDto(l.Id, l.ChatId, l.Username, l.LinkedAt, l.NotifyLevel.ToString().ToLowerInvariant(), l.MutedUntil)).ToList());
        });

        // One-time code for "/link CODE" (spec 9.4): 8 characters, 10 minutes.
        tg.MapPost("/code", async (ClaimsPrincipal user, UserManager<HiveUser> users, HiveDbContext db, TelegramBotHost host, TimeProvider time, CancellationToken ct) =>
        {
            var userId = int.Parse(users.GetUserId(user)!, System.Globalization.CultureInfo.InvariantCulture);
            var code = RandomNumberGenerator.GetString(CodeAlphabet, 8);
            var expires = time.GetUtcNow().AddMinutes(10);
            db.TgLinkCodes.Add(new TgLinkCode { Code = code, UserId = userId, ExpiresAt = expires });
            await db.SaveChangesAsync(ct);
            return new LinkCodeDto(code, expires, host.BotUsername);
        });

        tg.MapDelete("/{id:int}", async Task<Results<NoContent, NotFound>> (int id, ClaimsPrincipal user, UserManager<HiveUser> users, HiveDbContext db, CancellationToken ct) =>
        {
            var userId = int.Parse(users.GetUserId(user)!, System.Globalization.CultureInfo.InvariantCulture);
            return await db.TgLinks.Where(l => l.Id == id && l.UserId == userId).ExecuteDeleteAsync(ct) > 0
                ? TypedResults.NoContent()
                : TypedResults.NotFound();
        });
        return app;
    }
}
