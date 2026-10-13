using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>What the device page draws: bucketed readings and health, log lines, and the command history.</summary>
[Collection(HiveCollection.Name)]
public sealed class HistoryTests(HiveFixture hive)
{
    private async Task RegisterAsync(string id)
    {
        var spec = new HornetSpec { Id = id, Type = "meteo" };
        var hornet = new VirtualHornet(spec, HornetRole.Create(spec, "no-photos"));
        hornet.Start(DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var link = new MqttHornetLink(new BrokerOptions { Host = hive.MqttHost, Port = hive.MqttPort });
        await link.ConnectAsync(id, CancellationToken.None);
        foreach (var m in hornet.OnConnected(DateTimeOffset.UtcNow))
            await link.PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == id && d.Status == DeviceStatus.Online), "device online");
    }

    private static string Iso(DateTimeOffset t) => Uri.EscapeDataString(t.ToString("O"));

    [Fact]
    public async Task Series_come_back_in_buckets_with_mean_and_extremes()
    {
        await RegisterAsync("meteo-hist-a");
        var now = DateTimeOffset.UtcNow;
        // Buckets are whole hours here (4 h window, 4 points): two points in one hour 3 h back, one in the hour 1 h back.
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var points = new[] { (hour.AddHours(-3).AddMinutes(10), 10.0), (hour.AddHours(-3).AddMinutes(20), 20.0), (hour.AddHours(-1).AddMinutes(5), 30.0) };
        await hive.QueryAsync(async db =>
        {
            foreach (var (ts, value) in points)
                await db.Database.ExecuteSqlAsync($"INSERT INTO telemetry (device_id, metric, ts, value, received_at) VALUES ('meteo-hist-a', 'temperature', {ts}, {value}, {ts})");
            return 0;
        });
        var admin = await hive.LoginAsync();

        var url = $"/api/telemetry/series?device=meteo-hist-a&metric=temperature&from={Iso(now.AddHours(-4))}&to={Iso(now)}&points=4";
        var series = (await admin.GetFromJsonAsync<JsonElement>(url)).EnumerateArray().ToList();

        Assert.InRange(series.Count, 2, 3);
        Assert.Equal(series.OrderBy(p => p.GetProperty("ts").GetDateTimeOffset()).Select(p => p.GetProperty("ts").GetDateTimeOffset()),
                     series.Select(p => p.GetProperty("ts").GetDateTimeOffset()));
        Assert.Equal(10.0, series.Min(p => p.GetProperty("min").GetDouble()));
        Assert.Equal(30.0, series.Max(p => p.GetProperty("max").GetDouble()));
        var first = series[0];
        Assert.Equal(15.0, first.GetProperty("avg").GetDouble(), 3);
    }

    [Fact]
    public async Task Health_is_bucketed_and_empty_ranges_give_an_empty_list()
    {
        await RegisterAsync("meteo-hist-b");
        var now = DateTimeOffset.UtcNow;
        await hive.QueryAsync(async db =>
        {
            db.DeviceHealth.AddRange(
                new DeviceHealth { DeviceId = "meteo-hist-b", Ts = now.AddMinutes(-30), UptimeS = 100, Rssi = -60, HeapFree = 20000, Vbat = 3.9, ResetReason = "power" },
                new DeviceHealth { DeviceId = "meteo-hist-b", Ts = now.AddMinutes(-29), UptimeS = 160, Rssi = -70, HeapFree = 18000, Vbat = null, ResetReason = "power" });
            await db.SaveChangesAsync();
            return 0;
        });
        var admin = await hive.LoginAsync();

        var health = (await admin.GetFromJsonAsync<JsonElement>($"/api/telemetry/health?device=meteo-hist-b&from={Iso(now.AddHours(-1))}&to={Iso(now)}&points=2")).EnumerateArray().ToList();
        Assert.NotEmpty(health);
        Assert.Equal(-65.0, health.Average(p => p.GetProperty("rssi").GetDouble()), 3);

        var none = await admin.GetFromJsonAsync<JsonElement>($"/api/telemetry/health?device=meteo-hist-b&from={Iso(now.AddDays(-30))}&to={Iso(now.AddDays(-29))}");
        Assert.Equal(0, none.GetArrayLength());
    }

    [Fact]
    public async Task Logs_are_newest_first_and_can_be_narrowed_by_level()
    {
        await RegisterAsync("meteo-hist-c");
        var now = DateTimeOffset.UtcNow;
        await hive.QueryAsync(async db =>
        {
            db.DeviceLogs.AddRange(
                new DeviceLog { DeviceId = "meteo-hist-c", Ts = now.AddMinutes(-3), Level = "info", Msg = "first" },
                new DeviceLog { DeviceId = "meteo-hist-c", Ts = now.AddMinutes(-2), Level = "error", Msg = "second" },
                new DeviceLog { DeviceId = "meteo-hist-c", Ts = now.AddMinutes(-1), Level = "info", Msg = "third" });
            await db.SaveChangesAsync();
            return 0;
        });
        var admin = await hive.LoginAsync();

        var all = (await admin.GetFromJsonAsync<JsonElement>("/api/telemetry/logs?device=meteo-hist-c")).EnumerateArray().Select(l => l.GetProperty("msg").GetString()).ToList();
        Assert.Equal(["third", "second", "first"], all);

        var errors = (await admin.GetFromJsonAsync<JsonElement>("/api/telemetry/logs?device=meteo-hist-c&level=error")).EnumerateArray().Select(l => l.GetProperty("msg").GetString()).ToList();
        Assert.Equal(["second"], errors);
    }

    [Fact]
    public async Task Command_history_lists_newest_first_and_is_closed_to_viewers()
    {
        await RegisterAsync("meteo-hist-d");
        var admin = await hive.LoginAsync();
        foreach (var name in new[] { "reboot", "config_reload" })
        {
            using var sent = await admin.PostAsJsonAsync("/api/devices/meteo-hist-d/commands", new { name });
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
            await Task.Delay(20);
        }

        var history = (await admin.GetFromJsonAsync<JsonElement>("/api/devices/meteo-hist-d/commands")).EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Equal(["config_reload", "reboot"], history);

        await hive.CreateUserAsync("viewer-hist", "viewer-hist-password", Hive.Infrastructure.Identity.HiveRoles.Viewer);
        var viewer = await hive.LoginAsync("viewer-hist", "viewer-hist-password");
        using var refused = await viewer.GetAsync("/api/devices/meteo-hist-d/commands");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }
}
