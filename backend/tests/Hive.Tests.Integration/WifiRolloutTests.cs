using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;
using MQTTnet;

namespace Hive.Tests.Integration;

/// <summary>Switching the hornets to the Wi-Fi saved in Settings: who gets the order, and that the password goes nowhere else.</summary>
[Collection(HiveCollection.Name)]
public sealed class WifiRolloutTests(HiveFixture hive) : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var link in _links)
            await link.DisposeAsync();
    }

    private const string Secret = "s3cret-pass-phrase-for-tests";

    /// <summary>The links stay open for the test: closing one would fire its last will and mark the hornet offline.</summary>
    private readonly List<MqttHornetLink> _links = [];

    private async Task RegisterAsync(string id)
    {
        var spec = new HornetSpec { Id = id, Type = "meteo" };
        var hornet = new VirtualHornet(spec, HornetRole.Create(spec, "no-photos"));
        hornet.Start(DateTimeOffset.UtcNow.AddMinutes(-1));
        var link = new MqttHornetLink(new BrokerOptions { Host = hive.MqttHost, Port = hive.MqttPort });
        _links.Add(link);
        await link.ConnectAsync(id, CancellationToken.None);
        foreach (var m in hornet.OnConnected(DateTimeOffset.UtcNow))
            await link.PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == id && d.Status == DeviceStatus.Online), "device online");
    }

    private Task ChangeAsync(string id, Action<Device> change) => hive.QueryAsync(async db =>
    {
        var device = await db.Devices.SingleAsync(d => d.DeviceId == id);
        change(device);
        await db.SaveChangesAsync();
        return 0;
    });

    [Fact]
    public async Task The_order_goes_to_awake_hornets_only_and_the_password_is_never_stored_or_shown()
    {
        var admin = await hive.LoginAsync();
        using var saved = await admin.PutAsJsonAsync("/api/settings/wifi", new { ssid = "village-iot", password = Secret, hiveUrl = "http://hive.test" });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        await RegisterAsync("meteo-wf-a");
        await RegisterAsync("meteo-wf-b");
        await RegisterAsync("meteo-wf-c");
        await RegisterAsync("meteo-wf-d");
        await ChangeAsync("meteo-wf-b", d => d.Status = DeviceStatus.Offline);
        await ChangeAsync("meteo-wf-c", d =>
        {
            d.Config = JsonDocument.Parse("""{"sleep_s":600}""");
            d.LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        });
        await ChangeAsync("meteo-wf-d", d => d.Info = JsonDocument.Parse("""{"commands":["reboot"]}"""));

        using var client = new MqttClientFactory().CreateMqttClient();
        string? delivered = null;
        client.ApplicationMessageReceivedAsync += m =>
        {
            delivered = m.ApplicationMessage.ConvertPayloadToString();
            return Task.CompletedTask;
        };
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.MqttHost, hive.MqttPort).WithClientId("wifi-watch").Build());
        await client.SubscribeAsync("vs/v1/dev/meteo-wf-a/cmd/wifi");

        using var response = await admin.PostAsJsonAsync("/api/settings/wifi/apply", new { deviceIds = new[] { "meteo-wf-a", "meteo-wf-b", "meteo-wf-c", "meteo-wf-d" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Where(r => r.GetProperty("deviceId").GetString()!.StartsWith("meteo-wf-", StringComparison.Ordinal))
            .ToDictionary(r => r.GetProperty("deviceId").GetString()!, r => r.GetProperty("result").GetString());
        Assert.Equal(new Dictionary<string, string?>
        {
            ["meteo-wf-a"] = "sent", ["meteo-wf-b"] = "offline", ["meteo-wf-c"] = "sleeping", ["meteo-wf-d"] = "old_firmware",
        }, results);

        for (var i = 0; i < 50 && delivered is null; i++)
            await Task.Delay(100);
        using var cmd = JsonDocument.Parse(delivered ?? throw new TimeoutException("the command did not arrive"));
        Assert.Equal(("village-iot", Secret), (cmd.RootElement.GetProperty("ssid").GetString(), cmd.RootElement.GetProperty("pass").GetString()));
        Assert.Equal(120, cmd.RootElement.GetProperty("ttl_s").GetInt32());
        Assert.Equal("switch", cmd.RootElement.GetProperty("mode").GetString());

        // The journal keeps the command but not the secret.
        var stored = await hive.QueryAsync(db => db.Commands.SingleAsync(c => c.DeviceId == "meteo-wf-a" && c.Name == "wifi"));
        Assert.Equal("***", stored.Payload.RootElement.GetProperty("pass").GetString());
        var everyCommand = await hive.QueryAsync(db => db.Commands.ToListAsync());
        Assert.DoesNotContain(everyCommand, c => c.Payload.RootElement.GetRawText().Contains(Secret, StringComparison.Ordinal));
        var audit = (await hive.QueryAsync(db => db.Audit.Where(a => a.Action == "wifi_apply").ToListAsync())).Select(a => a.Details?.RootElement.GetRawText() ?? "").ToList();
        Assert.NotEmpty(audit);
        Assert.DoesNotContain(audit, a => a.Contains(Secret, StringComparison.Ordinal));

        // The MQTT monitor shows the command with the passphrase blanked out.
        string monitor = "";
        for (var i = 0; i < 30 && !monitor.Contains("/cmd/wifi", StringComparison.Ordinal); i++)
        {
            monitor = await admin.GetStringAsync("/api/monitor");
            await Task.Delay(100);
        }
        Assert.Contains("/cmd/wifi", monitor, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, monitor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_spare_network_is_only_offered_to_firmware_that_knows_how_to_keep_one()
    {
        var admin = await hive.LoginAsync();
        using var saved = await admin.PutAsJsonAsync("/api/settings/wifi", new { ssid = "village-iot", password = Secret, hiveUrl = "http://hive.test" });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        await RegisterAsync("meteo-wf-g");
        await RegisterAsync("meteo-wf-h");
        // h runs firmware from before spare networks: it knows the wifi command (a hard switch) but not wifi.stage.
        await ChangeAsync("meteo-wf-h", d => d.Info = JsonDocument.Parse("""{"commands":["reboot","wifi"],"caps":[]}"""));

        using var client = new MqttClientFactory().CreateMqttClient();
        string? delivered = null;
        client.ApplicationMessageReceivedAsync += m =>
        {
            delivered = m.ApplicationMessage.ConvertPayloadToString();
            return Task.CompletedTask;
        };
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.MqttHost, hive.MqttPort).WithClientId("wifi-stage-watch").Build());
        await client.SubscribeAsync("vs/v1/dev/meteo-wf-g/cmd/wifi");

        using var response = await admin.PostAsJsonAsync("/api/settings/wifi/apply", new { deviceIds = new[] { "meteo-wf-g", "meteo-wf-h" }, mode = "stage" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .ToDictionary(r => r.GetProperty("deviceId").GetString()!, r => r.GetProperty("result").GetString());
        Assert.Equal("sent", results["meteo-wf-g"]);
        Assert.Equal("old_firmware", results["meteo-wf-h"]);

        for (var i = 0; i < 50 && delivered is null; i++)
            await Task.Delay(100);
        using var cmd = JsonDocument.Parse(delivered ?? throw new TimeoutException("the command did not arrive"));
        Assert.Equal("stage", cmd.RootElement.GetProperty("mode").GetString());

        using var bad = await admin.PostAsJsonAsync("/api/settings/wifi/apply", new { deviceIds = new[] { "meteo-wf-g" }, mode = "yolo" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Only_the_chosen_hornets_are_switched_and_choosing_none_is_refused()
    {
        var admin = await hive.LoginAsync();
        using var saved = await admin.PutAsJsonAsync("/api/settings/wifi", new { ssid = "village-iot", password = Secret, hiveUrl = "http://hive.test" });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        await RegisterAsync("meteo-wf-e");
        await RegisterAsync("meteo-wf-f");

        using var none = await admin.PostAsJsonAsync("/api/settings/wifi/apply", new { deviceIds = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);

        using var one = await admin.PostAsJsonAsync("/api/settings/wifi/apply", new { deviceIds = new[] { "meteo-wf-e" } });
        var items = (await one.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Select(r => r.GetProperty("deviceId").GetString()).ToList();
        Assert.Equal(["meteo-wf-e"], items);
        Assert.False(await hive.QueryAsync(db => db.Commands.AnyAsync(c => c.DeviceId == "meteo-wf-f" && c.Name == "wifi")));
    }

    [Fact]
    public async Task Only_admins_can_switch_the_hornets()
    {
        await hive.CreateUserAsync("viewer-wifirollout", "viewer-wifi-password", Hive.Infrastructure.Identity.HiveRoles.Viewer);
        var viewer = await hive.LoginAsync("viewer-wifirollout", "viewer-wifi-password");
        using var refused = await viewer.PostAsJsonAsync("/api/settings/wifi/apply", new { deviceIds = new[] { "meteo-wf-a" } });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }
}
