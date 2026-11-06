using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Hive.Contracts.Messages;
using Hive.Domain;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>"Update all outdated": one hornet at a time, the next only after the previous came back new, and a stop on the first failure.</summary>
[Collection(HiveCollection.Name)]
public sealed class OtaRolloutTests(HiveFixture hive) : IAsyncLifetime
{
    private const string Board = "esp32c3-supermini"; // what the simulator meteo hornet reports as hw

    /// <summary>The links stay open for the test: closing one would fire its last will and mark the hornet offline.</summary>
    private readonly Dictionary<string, MqttHornetLink> _links = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var link in _links.Values)
            await link.DisposeAsync();
    }

    private void AddBuild(string name, string version, string chipFamily = "ESP32-C3", int size = 4096)
    {
        var dir = Path.Combine(hive.FirmwareRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "firmware.bin"), RandomNumberGenerator.GetBytes(size));
        File.WriteAllText(Path.Combine(dir, "build.json"), JsonSerializer.Serialize(new
        {
            env = name, role = "meteo", board = Board, version, chipFamily,
            parts = new[] { new { file = "firmware.bin", offset = 0 } },
        }));
    }

    private static VirtualHornet Hornet(string id, string fw, int boot = 1)
    {
        var spec = new HornetSpec { Id = id, Type = "meteo", Fw = fw };
        var hornet = new VirtualHornet(spec, HornetRole.Create(spec, "no-photos"), boot);
        hornet.Start(DateTimeOffset.UtcNow.AddMinutes(-1));
        return hornet;
    }

    private async Task RegisterAsync(string id, string fw = "0.1.0")
    {
        var link = new MqttHornetLink(new BrokerOptions { Host = hive.MqttHost, Port = hive.MqttPort });
        _links[id] = link;
        await link.ConnectAsync(id, CancellationToken.None);
        foreach (var m in Hornet(id, fw).OnConnected(DateTimeOffset.UtcNow))
            await link.PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == id && d.Status == DeviceStatus.Online), "device online");
    }

    /// <summary>The hornet restarted on the new image and says so in its info.</summary>
    private async Task ComeBackAsync(string id, string fw)
    {
        foreach (var m in Hornet(id, fw, boot: 2).OnConnected(DateTimeOffset.UtcNow))
            await _links[id].PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
    }

    private async Task FailAsync(string id, string version)
    {
        var hornet = Hornet(id, "0.1.0");
        var e = hornet.NextEnvelope(DateTimeOffset.UtcNow);
        hornet.EmitEvent("ota", new OtaEvent { Id = e.Id, Ts = e.Ts, Seq = e.Seq, Boot = e.Boot, Phase = OtaPhase.Failed, Version = version, Error = "md5 mismatch" });
        foreach (var m in hornet.TakePending(connected: true))
            await _links[id].PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
    }

    private static async Task<JsonElement> RolloutAsync(HttpClient admin) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/firmware/rollout")).GetProperty("rollout");

    private static async Task<JsonElement> WaitRolloutAsync(HttpClient admin, Func<JsonElement, bool> condition, string what)
    {
        for (var i = 0; i < 100; i++)
        {
            var r = await RolloutAsync(admin);
            if (r.ValueKind == JsonValueKind.Object && condition(r))
                return r;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Timed out waiting for {what}");
    }

    private static string StateOf(JsonElement rollout, string deviceId) =>
        rollout.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("deviceId").GetString() == deviceId).GetProperty("state").GetString()!;

    [Fact]
    public async Task Hornets_update_one_by_one_and_the_first_failure_stops_the_rest()
    {
        const string version = "9.9.9+roll";
        AddBuild("meteo-roll", version);
        foreach (var id in new[] { "roll-a", "roll-b", "roll-c" })
            await RegisterAsync(id);

        var admin = await hive.LoginAsync();
        using (var start = await admin.PostAsJsonAsync("/api/firmware/rollout", new { deviceIds = new[] { "roll-c", "roll-a", "roll-b" } }))
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        var r = await RolloutAsync(admin);
        Assert.Equal("running", r.GetProperty("state").GetString());
        Assert.Equal("updating", StateOf(r, "roll-a"));
        Assert.Equal("pending", StateOf(r, "roll-b"));

        // A second rollout waits for this one.
        using (var again = await admin.PostAsJsonAsync("/api/firmware/rollout", new { deviceIds = new[] { "roll-a" } }))
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        await ComeBackAsync("roll-a", version);
        r = await WaitRolloutAsync(admin, x => StateOf(x, "roll-b") == "updating", "roll-b to start");
        Assert.Equal("done", StateOf(r, "roll-a"));

        await FailAsync("roll-b", version);
        r = await WaitRolloutAsync(admin, x => x.GetProperty("state").GetString() == "failed", "the rollout to stop");
        Assert.Equal("failed", StateOf(r, "roll-b"));
        Assert.Equal("skipped", StateOf(r, "roll-c"));
        Assert.Equal("stopped", r.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("deviceId").GetString() == "roll-c").GetProperty("error").GetString());

        // roll-c never got an order, and the failure is in Events as a warning (so Telegram hears of it).
        Assert.False(await hive.QueryAsync(db => db.Commands.AnyAsync(c => c.DeviceId == "roll-c" && c.Name == "ota")));
        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.Type == "ota_rollout" && e.Severity == EventSeverity.Warn), "ota_rollout event");
    }

    [Fact]
    public async Task Esp8266_boards_need_a_confirmation_and_stop_lets_the_current_one_finish()
    {
        const string version = "9.9.9+esp8266";
        AddBuild("meteo-roll-8266", version, chipFamily: "ESP8266");
        foreach (var id in new[] { "roll8266-a", "roll8266-b" })
            await RegisterAsync(id);
        var admin = await hive.LoginAsync();

        using (var unconfirmed = await admin.PostAsJsonAsync("/api/firmware/rollout", new { deviceIds = new[] { "roll8266-a", "roll8266-b" } }))
            Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);

        using (var confirmed = await admin.PostAsJsonAsync("/api/firmware/rollout", new { deviceIds = new[] { "roll8266-a", "roll8266-b" }, confirmNoRollback = true }))
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        using (var stop = await admin.PostAsync("/api/firmware/rollout/stop", null))
            Assert.Equal(HttpStatusCode.OK, stop.StatusCode);

        var r = await RolloutAsync(admin);
        Assert.Equal("stopping", r.GetProperty("state").GetString());
        Assert.True(r.GetProperty("items").EnumerateArray().All(i => i.GetProperty("noRollback").GetBoolean()));

        await ComeBackAsync("roll8266-a", version);
        r = await WaitRolloutAsync(admin, x => x.GetProperty("state").GetString() == "stopped", "the rollout to stop");
        Assert.Equal("done", StateOf(r, "roll8266-a"));
        Assert.Equal("skipped", StateOf(r, "roll8266-b"));
    }

    [Fact]
    public async Task A_refusal_by_the_hive_stops_the_rollout_instead_of_skipping_everyone()
    {
        // Bigger than any board takes over the air: the hive refuses it for every hornet alike.
        AddBuild("meteo-roll-huge", "9.9.9+huge", size: 2_000_000);
        foreach (var id in new[] { "rollbig-a", "rollbig-b" })
            await RegisterAsync(id);
        var admin = await hive.LoginAsync();

        using (var start = await admin.PostAsJsonAsync("/api/firmware/rollout", new { deviceIds = new[] { "rollbig-a", "rollbig-b" } }))
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        var r = await RolloutAsync(admin);
        Assert.Equal("failed", r.GetProperty("state").GetString());
        Assert.Equal("failed", StateOf(r, "rollbig-a"));
        Assert.Equal("skipped", StateOf(r, "rollbig-b"));
    }
}
