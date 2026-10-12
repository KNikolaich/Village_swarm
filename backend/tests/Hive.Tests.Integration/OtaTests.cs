using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Hive.Domain;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;
using MQTTnet;

namespace Hive.Tests.Integration;

/// <summary>Over-the-air updates from the hive side: what is sent, what is refused, who may download.</summary>
[Collection(HiveCollection.Name)]
public sealed class OtaTests(HiveFixture hive)
{
    private const string Board = "esp32c3-supermini"; // what the simulator meteo hornet reports as hw

    private byte[] AddBuild(string name, string role, string board, string version, int size = 4096)
    {
        var dir = Path.Combine(hive.FirmwareRoot, name);
        Directory.CreateDirectory(dir);
        var image = RandomNumberGenerator.GetBytes(size);
        File.WriteAllBytes(Path.Combine(dir, "firmware.bin"), image);
        File.WriteAllText(Path.Combine(dir, "build.json"), JsonSerializer.Serialize(new
        {
            env = name, role, board, version, chipFamily = "ESP32-C3",
            parts = new[] { new { file = "firmware.bin", offset = 0 } },
        }));
        return image;
    }

    private async Task RegisterAsync(string id)
    {
        var spec = new HornetSpec { Id = id, Type = "meteo", Fw = "0.1.0" };
        var hornet = new VirtualHornet(spec, HornetRole.Create(spec, "no-photos"));
        hornet.Start(DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var link = new MqttHornetLink(new BrokerOptions { Host = hive.MqttHost, Port = hive.MqttPort });
        await link.ConnectAsync(id, CancellationToken.None);
        foreach (var m in hornet.OnConnected(DateTimeOffset.UtcNow))
            await link.PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == id && d.Status == DeviceStatus.Online), "device online");
    }

    [Fact]
    public async Task Update_command_carries_the_size_md5_version_and_download_address()
    {
        var image = AddBuild("meteo-ota1", "meteo", Board, "9.9.9+test");
        await RegisterAsync("meteo-ota-a");

        using var client = new MqttClientFactory().CreateMqttClient();
        string? seen = null;
        client.ApplicationMessageReceivedAsync += m =>
        {
            seen = m.ApplicationMessage.ConvertPayloadToString();
            return Task.CompletedTask;
        };
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.MqttHost, hive.MqttPort).WithClientId("ota-watch").Build());
        await client.SubscribeAsync("vs/v1/dev/meteo-ota-a/cmd/ota");

        var admin = await hive.LoginAsync();
        using var response = await admin.PostAsJsonAsync("/api/devices/meteo-ota-a/ota", new { build = "meteo-ota1" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        for (var i = 0; i < 50 && seen is null; i++)
            await Task.Delay(100);

        using var cmd = JsonDocument.Parse(seen ?? throw new TimeoutException("no ota command"));
        // The hive address is the one from Settings when set (other tests may have set it), else the configured one.
        var url = cmd.RootElement.GetProperty("url").GetString()!;
        Assert.StartsWith("http://", url, StringComparison.Ordinal);
        Assert.EndsWith("/api/ota/meteo-ota1/firmware.bin", url, StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(image)), cmd.RootElement.GetProperty("md5").GetString());
        Assert.Equal(image.Length, cmd.RootElement.GetProperty("size").GetInt32());
        Assert.Equal("9.9.9+test", cmd.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Wrong_board_wrong_role_and_same_version_are_refused()
    {
        AddBuild("relay-ota1", "relay", Board, "9.9.9+test");
        AddBuild("meteo-ota-other-board", "meteo", "esp-01", "9.9.9+test");
        AddBuild("meteo-ota-same", "meteo", Board, "0.1.0");
        AddBuild("meteo-ota-huge", "meteo", Board, "9.9.9+huge", size: 2_000_000);
        await RegisterAsync("meteo-ota-b");
        var admin = await hive.LoginAsync();

        foreach (var build in new[] { "relay-ota1", "meteo-ota-other-board", "meteo-ota-same", "meteo-ota-huge" })
        {
            using var response = await admin.PostAsJsonAsync("/api/devices/meteo-ota-b/ota", new { build });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var unknown = await admin.PostAsJsonAsync("/api/devices/meteo-ota-b/ota", new { build = "no-such-build" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Only_an_enrolled_hornet_with_its_token_can_download_the_image()
    {
        var image = AddBuild("meteo-ota-dl", "meteo", Board, "9.9.9+dl");
        var admin = await hive.LoginAsync();
        using var codeResponse = await admin.PostAsJsonAsync("/api/provision/codes", new { deviceId = "meteo-ota-c", type = "meteo", name = "OTA" });
        var code = (await codeResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
        using var enroll = await hive.App.CreateClient().PostAsJsonAsync("/api/provision/enroll", new { code, mac = "02:00:00:00:0A:01", hw = Board, fw = "0.1.0" });
        var token = (await enroll.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadToken").GetString();

        var anonymous = hive.App.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/ota/meteo-ota-dl/firmware.bin")).StatusCode);

        var hornet = hive.App.CreateClient();
        hornet.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await hornet.GetAsync("/api/ota/meteo-ota-dl/firmware.bin")).StatusCode);

        hornet.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var ok = await hornet.GetAsync("/api/ota/meteo-ota-dl/firmware.bin");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(image, await ok.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await hornet.GetAsync("/api/ota/no-such-build/firmware.bin")).StatusCode);
    }
}
