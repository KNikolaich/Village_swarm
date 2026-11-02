using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Contracts.Messages;
using Hive.Domain;
using Hive.Infrastructure.Identity;
using Hive.Modules.IpCams;
using Microsoft.EntityFrameworkCore;
using MQTTnet;

namespace Hive.Tests.Integration;

/// <summary>The car (contracts/mqtt-topics.md "Car"): the hive turns a held control into drive messages and shows its camera.</summary>
[Collection(HiveCollection.Name)]
public sealed class CarTests(HiveFixture hive)
{
    private const string StreamKey = "k3J9q2mX7vPz4Lw8aB5c";

    private Task AddCarAsync(string id) => hive.QueryAsync(async db =>
    {
        if (!await db.Devices.AnyAsync(d => d.DeviceId == id))
        {
            db.Devices.Add(new Device
            {
                DeviceId = id, TypeCode = Cars.Type, Name = "Машинка", Status = DeviceStatus.Online, Ip = "192.168.1.77",
                CreatedAt = DateTimeOffset.UtcNow, Config = JsonDocument.Parse($$$"""{"v":1,"rev":1,"name":"Машинка","health_interval_s":60,"car":{"stream_key":"{{{StreamKey}}}"}}"""),
            });
            await db.SaveChangesAsync();
        }
        return 0;
    });

    [Fact]
    public async Task A_held_control_becomes_drive_messages_and_a_start_is_audited()
    {
        await AddCarAsync("car-drive1");
        using var admin = await hive.LoginAsync();

        using var car = new MqttClientFactory().CreateMqttClient();
        var received = new ConcurrentQueue<DriveMessage>();
        car.ApplicationMessageReceivedAsync += m =>
        {
            received.Enqueue(ContractJson.Deserialize<DriveMessage>(m.ApplicationMessage.ConvertPayloadToString()));
            return Task.CompletedTask;
        };
        await car.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.MqttHost, hive.MqttPort).WithClientId("car-drive1-radio").Build());
        await car.SubscribeAsync("vs/v1/hive/drive/car-drive1");

        async Task<HttpStatusCode> DriveAsync(object body, HttpClient? as_ = null, string id = "car-drive1")
        {
            using var response = await (as_ ?? admin).PostAsJsonAsync($"/api/cars/{id}/drive", body);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.NoContent, await DriveAsync(new { left = 60, right = 60 }));
        Assert.Equal(HttpStatusCode.NoContent, await DriveAsync(new { left = 60, right = 60 })); // the repeat of a held arrow
        Assert.Equal(HttpStatusCode.NoContent, await DriveAsync(new { left = 0, right = 0 }));
        var until = DateTime.UtcNow.AddSeconds(5);
        while (received.Count < 3 && DateTime.UtcNow < until)
            await Task.Delay(50);
        Assert.Equal([(60, 60), (60, 60), (0, 0)], received.Select(m => (m.Left, m.Right)));
        Assert.All(received, m => Assert.True(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - m.Ts < 10_000));
        Assert.Equal(1, await hive.QueryAsync(db => db.Audit.CountAsync(a => a.Action == "car.drive" && a.Target == "car-drive1")));

        Assert.Equal(HttpStatusCode.BadRequest, await DriveAsync(new { left = 150, right = 0 }));
        Assert.Equal(HttpStatusCode.NotFound, await DriveAsync(new { left = 10, right = 10 }, id: "no-such-car"));

        await hive.CreateUserAsync("viewer-car", "viewer-car-password", HiveRoles.Viewer);
        using var viewer = await hive.LoginAsync("viewer-car", "viewer-car-password");
        Assert.Equal(HttpStatusCode.Forbidden, await DriveAsync(new { left = 10, right = 10 }, viewer));
    }

    [Fact]
    public async Task The_wiring_and_limits_go_to_the_config_and_only_an_administrator_changes_them()
    {
        await AddCarAsync("car-setup1");
        using var admin = await hive.LoginAsync();
        var defaults = (await admin.GetFromJsonAsync<Hive.Modules.Devices.CarSetupDto>("/api/cars/car-setup1/setup"))!;
        Assert.Null(defaults.MotorPins);
        Assert.Equal(100, defaults.MaxSpeed);

        using var saved = await admin.PutAsJsonAsync("/api/cars/car-setup1/setup",
            new Hive.Modules.Devices.CarSetupDto([12, 13, 14, 15], MotorPwm: false, InvertRight: true, MaxSpeed: 70, MaxRunS: 30, LightPin: 4));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var config = (await hive.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == "car-setup1"))).Config!.RootElement;
        Assert.Equal([12, 13, 14, 15], config.GetProperty("hw").GetProperty("motor_pins").EnumerateArray().Select(p => p.GetInt32()));
        Assert.False(config.GetProperty("hw").GetProperty("motor_pwm").GetBoolean());
        Assert.True(config.GetProperty("car").GetProperty("invert_right").GetBoolean());
        Assert.Equal(StreamKey, config.GetProperty("car").GetProperty("stream_key").GetString()); // the key stays
        Assert.Equal(70, config.GetProperty("car").GetProperty("max_speed").GetInt32());

        using var clash = await admin.PutAsJsonAsync("/api/cars/car-setup1/setup", new Hive.Modules.Devices.CarSetupDto([12, 13, 14, 4]));
        Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);

        await hive.CreateUserAsync("member-car", "member-car-password", HiveRoles.Member);
        using var member = await hive.LoginAsync("member-car", "member-car-password");
        using var refused = await member.PutAsJsonAsync("/api/cars/car-setup1/setup", new Hive.Modules.Devices.CarSetupDto());
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task The_cars_camera_is_a_stream_of_the_video_widget_opened_with_its_key()
    {
        await AddCarAsync("car-video1");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!hive.Go2Rtc.Streams.ContainsKey("car-video1-sub") && DateTime.UtcNow < deadline)
            await Task.Delay(200);
        Assert.Equal($"http://hive:{StreamKey}@192.168.1.77:81/stream", hive.Go2Rtc.Streams["car-video1-sub"]);
        Assert.Equal(hive.Go2Rtc.Streams["car-video1-sub"], hive.Go2Rtc.Streams["car-video1-main"]);

        using var admin = await hive.LoginAsync();
        var list = (await admin.GetFromJsonAsync<VideoStreamsDto>("/api/video/streams"))!;
        var camera = Assert.Single(list.Cameras, c => c.DeviceId == "car-video1");
        Assert.Equal(Cars.Type, camera.Kind);
        Assert.DoesNotContain(StreamKey, JsonSerializer.Serialize(list)); // the key stays between the hive and go2rtc

        await hive.QueryAsync(async db =>
        {
            await db.Devices.Where(d => d.DeviceId == "car-video1").ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeviceStatus.Disabled));
            return 0;
        });
        deadline = DateTime.UtcNow.AddSeconds(15);
        while (hive.Go2Rtc.Streams.ContainsKey("car-video1-sub") && DateTime.UtcNow < deadline)
            await Task.Delay(200);
        Assert.False(hive.Go2Rtc.Streams.ContainsKey("car-video1-sub"), "the stream goes with the car");
    }
}
