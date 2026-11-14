using System.Text.Json;
using Hive.Contracts;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Modules.Notify.Sms;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hive.Tests.Integration;

/// <summary>SMS about critical events (docs/sms-keenetic.md): what is planned, what is held back, and how the outbox deals with a modem that fails.</summary>
[Collection(HiveCollection.Name)]
public sealed class SmsTests(HiveFixture hive)
{
    private const string Alice = "+79160000001";
    private const string Bob = "+79160000002";

    private sealed class FakeModem : ISmsModem
    {
        public List<(string Number, string Text)> Sent { get; } = [];
        public string? FailWith { get; set; }

        public Task SendAsync(string number, string text, CancellationToken ct)
        {
            if (FailWith is not null)
                throw new SmsModemException(FailWith);
            Sent.Add((number, text));
            return Task.CompletedTask;
        }
    }

    private SmsPlanner Planner(SmsOptions options) => new(
        hive.App.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(options),
        hive.App.Services.GetRequiredService<IOptions<HiveOptions>>(), hive.App.Services.GetRequiredService<NodeState>(), TimeProvider.System);

    private static SmsOutbox Outbox(ISmsModem modem) =>
        new(null!, modem, Options.Create(new SmsOptions()), TimeProvider.System, NullLogger<SmsOutbox>.Instance);

    private async Task<DeviceEventReceived> EventAsync(string device, string type, EventSeverity severity)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Ulid.New();
        await hive.QueryAsync(async db =>
        {
            db.Events.Add(new DeviceEvent { Id = id, DeviceId = device, Type = type, Severity = severity, Ts = now, ReceivedAt = now, Payload = JsonDocument.Parse("{}") });
            await db.SaveChangesAsync();
            return 0;
        });
        return new DeviceEventReceived(device, id, now, now, type, severity, null, JsonDocument.Parse("{}"));
    }

    private Task<List<Notification>> SmsRowsAsync() =>
        hive.QueryAsync(db => db.Notifications.AsNoTracking().Where(n => n.Channel == "sms").OrderBy(n => n.Id).ToListAsync());

    private Task ClearAsync() => hive.QueryAsync(async db => await db.Notifications.Where(n => n.Channel == "sms").ExecuteDeleteAsync());

    [Fact]
    public async Task Only_critical_events_are_planned_one_sms_per_number()
    {
        await ClearAsync();
        var planner = Planner(new SmsOptions { Port = "/dev/ttyUSB2", Recipients = $"{Alice}, {Bob}" });
        var warn = await EventAsync("sms-dev1", "motion", EventSeverity.Warn);
        var alarm = await EventAsync("sms-dev1", "motion", EventSeverity.Alarm);
        var critical = await EventAsync("sms-dev1", "leak", EventSeverity.Critical);

        await planner.OnIngestedAsync([warn, alarm, critical], CancellationToken.None);

        var rows = await SmsRowsAsync();
        Assert.Equal([Alice, Bob], rows.Select(r => r.Target).OrderBy(t => t));
        Assert.All(rows, r =>
        {
            Assert.Equal(NotificationStatus.Pending, r.Status);
            Assert.Equal(critical.MessageId, r.EventId);
            Assert.StartsWith("Улей: Протечка: sms-dev1", r.Text);
        });
    }

    [Fact]
    public async Task Nothing_is_planned_when_sms_is_not_configured()
    {
        await ClearAsync();
        var critical = await EventAsync("sms-dev2", "leak", EventSeverity.Critical);
        await Planner(new SmsOptions { Recipients = Alice }).OnIngestedAsync([critical], CancellationToken.None);
        await Planner(new SmsOptions { Port = "/dev/ttyUSB2" }).OnIngestedAsync([critical], CancellationToken.None);
        Assert.Empty(await SmsRowsAsync());
    }

    [Fact]
    public async Task The_same_event_again_is_held_back_and_the_hourly_limit_skips_the_rest()
    {
        await ClearAsync();
        var planner = Planner(new SmsOptions { Port = "/dev/ttyUSB2", Recipients = Alice, MaxPerHour = 2 });

        await planner.OnIngestedAsync([await EventAsync("sms-dev3", "leak", EventSeverity.Critical)], CancellationToken.None);
        await planner.OnIngestedAsync([await EventAsync("sms-dev3", "leak", EventSeverity.Critical)], CancellationToken.None); // same device, same type: grouped
        await planner.OnIngestedAsync([await EventAsync("sms-dev4", "leak", EventSeverity.Critical)], CancellationToken.None);
        await planner.OnIngestedAsync([await EventAsync("sms-dev5", "leak", EventSeverity.Critical)], CancellationToken.None); // the third: over the limit

        var rows = await SmsRowsAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal([NotificationStatus.Pending, NotificationStatus.Pending, NotificationStatus.Skipped], rows.Select(r => r.Status));
        Assert.Contains("2 SMS per hour", rows[2].Error);
    }

    [Fact]
    public async Task The_telegram_outbox_leaves_sms_rows_alone()
    {
        await ClearAsync();
        await Planner(new SmsOptions { Port = "/dev/ttyUSB2", Recipients = Alice })
            .OnIngestedAsync([await EventAsync("sms-dev6", "leak", EventSeverity.Critical)], CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(3)); // the running Telegram outbox ticks every second

        var row = Assert.Single(await SmsRowsAsync());
        Assert.Equal(NotificationStatus.Pending, row.Status);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.Error);
    }

    [Fact]
    public async Task A_pending_sms_goes_to_the_modem_once()
    {
        await ClearAsync();
        await Planner(new SmsOptions { Port = "/dev/ttyUSB2", Recipients = Alice })
            .OnIngestedAsync([await EventAsync("sms-dev7", "leak", EventSeverity.Critical)], CancellationToken.None);
        var modem = new FakeModem();

        await hive.QueryAsync(async db => { await Outbox(modem).RunOnceAsync(db, CancellationToken.None); return 0; });
        await hive.QueryAsync(async db => { await Outbox(modem).RunOnceAsync(db, CancellationToken.None); return 0; });

        Assert.Equal([(Alice, "Улей: Протечка: sms-dev7, ")], modem.Sent.Select(s => (s.Number, s.Text[..^5])));
        var row = Assert.Single(await SmsRowsAsync());
        Assert.Equal(NotificationStatus.Sent, row.Status);
        Assert.NotNull(row.SentAt);
    }

    [Fact]
    public async Task A_failing_modem_is_retried_with_a_pause_and_given_up_after_the_attempts()
    {
        await ClearAsync();
        await Planner(new SmsOptions { Port = "/dev/ttyUSB2", Recipients = Alice })
            .OnIngestedAsync([await EventAsync("sms-dev8", "leak", EventSeverity.Critical)], CancellationToken.None);
        var modem = new FakeModem { FailWith = "the modem is not registered in the network" };

        await hive.QueryAsync(async db => { await Outbox(modem).RunOnceAsync(db, CancellationToken.None); return 0; });
        var row = Assert.Single(await SmsRowsAsync());
        Assert.Equal(NotificationStatus.Pending, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Contains("not registered", row.Error);
        Assert.True(row.NextAttemptAt > DateTimeOffset.UtcNow);

        for (var attempt = 2; attempt <= SmsOutbox.MaxAttempts; attempt++)
        {
            await hive.QueryAsync(async db =>
            {
                await db.Notifications.Where(n => n.Channel == "sms").ExecuteUpdateAsync(s => s.SetProperty(n => n.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
                await Outbox(modem).RunOnceAsync(db, CancellationToken.None);
                return 0;
            });
        }

        row = Assert.Single(await SmsRowsAsync());
        Assert.Equal(NotificationStatus.Failed, row.Status);
        Assert.Equal(SmsOutbox.MaxAttempts, row.Attempts);
        Assert.Empty(modem.Sent);
    }

    [Fact]
    public async Task An_sms_that_waited_too_long_is_dropped_not_sent()
    {
        await ClearAsync();
        await Planner(new SmsOptions { Port = "/dev/ttyUSB2", Recipients = Alice })
            .OnIngestedAsync([await EventAsync("sms-dev9", "leak", EventSeverity.Critical)], CancellationToken.None);
        await hive.QueryAsync(async db =>
            await db.Notifications.Where(n => n.Channel == "sms").ExecuteUpdateAsync(s => s.SetProperty(n => n.CreatedAt, DateTimeOffset.UtcNow.AddHours(-3))));
        var modem = new FakeModem();

        await hive.QueryAsync(async db => { await Outbox(modem).RunOnceAsync(db, CancellationToken.None); return 0; });

        Assert.Empty(modem.Sent);
        Assert.Equal(NotificationStatus.Failed, Assert.Single(await SmsRowsAsync()).Status);
    }
}
