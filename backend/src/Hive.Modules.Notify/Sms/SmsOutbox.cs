using Hive.Domain;
using Hive.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Notify.Sms;

/// <summary>
/// Sends the outbox rows with the channel "sms" through the modem: retries with a growing pause while the modem or the
/// network is unavailable, and drops a message that is too old to matter (an SMS about a leak an hour ago is noise).
/// </summary>
public sealed class SmsOutbox(
    IServiceScopeFactory scopes, ISmsModem modem, IOptions<SmsOptions> options, TimeProvider time, ILogger<SmsOutbox> logger) : BackgroundService
{
    internal const int MaxAttempts = 6;
    internal static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("SMS is off (set the modem port and the phone numbers in Settings → SMS)");
            return;
        }
        logger.LogInformation("SMS on {Port} to {Count} number(s), at most {Max} per hour", options.Value.Port, options.Value.Numbers.Count, options.Value.MaxPerHour);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await RunOnceAsync(scope.ServiceProvider.GetRequiredService<HiveDbContext>(), stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "SMS outbox pass failed");
            }
        }
    }

    internal async Task RunOnceAsync(HiveDbContext db, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var due = await db.Notifications
            .Where(n => n.Channel == SmsPlanner.Channel && n.Status == NotificationStatus.Pending && n.NextAttemptAt <= now)
            .OrderBy(n => n.Id)
            .Take(5)
            .ToListAsync(ct);
        foreach (var n in due)
        {
            if (now - n.CreatedAt > GiveUpAfter)
            {
                n.Status = NotificationStatus.Failed;
                n.Error ??= "too old to send";
                continue;
            }
            try
            {
                await modem.SendAsync(n.Target, n.Text, ct);
                n.Status = NotificationStatus.Sent;
                n.SentAt = now;
                n.Error = null;
            }
            catch (SmsModemException ex)
            {
                n.Attempts++;
                n.Error = ex.Message;
                if (n.Attempts >= MaxAttempts)
                {
                    n.Status = NotificationStatus.Failed;
                    logger.LogWarning("SMS to {Number} failed for good after {Attempts} attempts: {Error}", n.Target, n.Attempts, ex.Message);
                }
                else
                {
                    n.NextAttemptAt = now.AddSeconds(Math.Min(600, 30 * Math.Pow(2, n.Attempts - 1)));
                    logger.LogWarning("SMS to {Number} not sent (attempt {Attempts}): {Error}", n.Target, n.Attempts, ex.Message);
                }
            }
            // One modem: when it fails, the next number would fail the same way.
            if (n.Status != NotificationStatus.Sent)
                break;
        }
        await db.SaveChangesAsync(ct);
    }
}
