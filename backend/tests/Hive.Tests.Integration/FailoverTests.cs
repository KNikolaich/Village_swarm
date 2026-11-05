using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Devices;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace Hive.Tests.Integration;

/// <summary>The second broker of the hornets (spec 4.5): home writes the city address and the hornet's login there into its config.</summary>
[Collection(HiveCollection.Name)]
public sealed class FailoverConfigTests(HiveFixture hive)
{
    private const string Secret = "shared-secret-of-home-and-city";

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
        // a hornet that was enrolled has a config; one that only said hello does not
        await hive.QueryAsync(async db =>
        {
            (await db.Devices.SingleAsync(d => d.DeviceId == id)).Config = JsonDocument.Parse($$"""{"v":1,"rev":1,"name":"{{id}}","health_interval_s":60}""");
            await db.SaveChangesAsync();
            return 0;
        });
    }

    private FailoverService Service(IServiceScope scope) => ActivatorUtilities.CreateInstance<FailoverService>(
        scope.ServiceProvider, Options.Create(new CityBrokerOptions { BridgePassword = Secret }));

    [Fact]
    public async Task The_city_address_goes_into_every_hornets_config_with_its_own_login_and_comes_out_again()
    {
        await RegisterAsync("meteo-fail1");
        using var scope = hive.App.Services.CreateScope();
        var failover = Service(scope);

        await failover.SetAsync(new FailoverDto("192.168.1.3", 1883), "user:test", CancellationToken.None);
        Assert.True(await failover.SyncAsync(CancellationToken.None) >= 1);

        var config = (await hive.QueryAsync(db => db.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == "meteo-fail1"))).Config!.RootElement;
        var city = config.GetProperty("mqtt_city");
        Assert.Equal(("192.168.1.3", 1883, "meteo-fail1"), (city.GetProperty("host").GetString(), city.GetProperty("port").GetInt32(), city.GetProperty("user").GetString()));
        Assert.Equal(CityBrokerLogin.Password(Secret, "meteo-fail1"), city.GetProperty("pass").GetString());
        Assert.NotEqual(CityBrokerLogin.Password(Secret, "meteo-fail2"), city.GetProperty("pass").GetString());   // each hornet has its own
        Assert.Equal(1, config.GetProperty("rev").GetInt32());   // the revision moved, so the hornet applies it

        // the same address again changes nothing; a new one is written
        Assert.Equal(0, await failover.SyncAsync(CancellationToken.None));
        await failover.SetAsync(new FailoverDto("city.example.org", 8883), "user:test", CancellationToken.None);
        await failover.SyncAsync(CancellationToken.None);
        var moved = (await hive.QueryAsync(db => db.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == "meteo-fail1"))).Config!.RootElement;
        Assert.Equal(("city.example.org", 8883, 2), (moved.GetProperty("mqtt_city").GetProperty("host").GetString(), moved.GetProperty("mqtt_city").GetProperty("port").GetInt32(), moved.GetProperty("rev").GetInt32()));

        // the retained config the hornet reads carries it
        using var client = new MqttClientFactory().CreateMqttClient();
        string? retained = null;
        client.ApplicationMessageReceivedAsync += m =>
        {
            retained = m.ApplicationMessage.ConvertPayloadToString();
            return Task.CompletedTask;
        };
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.MqttHost, hive.MqttPort).WithClientId("failover-watch").Build());
        await client.SubscribeAsync("vs/v1/dev/meteo-fail1/config");
        for (var i = 0; i < 50 && retained is null; i++)
            await Task.Delay(100);
        Assert.Contains("city.example.org", retained);

        // no address: the second broker is taken out of the config
        await failover.SetAsync(new FailoverDto(null, 1883), "user:test", CancellationToken.None);
        await failover.SyncAsync(CancellationToken.None);
        var cleared = (await hive.QueryAsync(db => db.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == "meteo-fail1"))).Config!.RootElement;
        Assert.False(cleared.TryGetProperty("mqtt_city", out _));
    }

    [Fact]
    public async Task Without_the_shared_secret_no_login_is_made_up_and_a_bad_address_is_refused()
    {
        var admin = await hive.LoginAsync();
        using var bad = await admin.PutAsJsonAsync("/api/failover", new { host = "not a host", port = 1883 });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
        using var badPort = await admin.PutAsJsonAsync("/api/failover", new { host = "192.168.1.3", port = 70000 });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, badPort.StatusCode);

        using var scope = hive.App.Services.CreateScope();
        var noSecret = ActivatorUtilities.CreateInstance<FailoverService>(scope.ServiceProvider, Options.Create(new CityBrokerOptions()));
        Assert.Null(noSecret.CityConfigFor("meteo-fail1", new FailoverDto("192.168.1.3", 1883)));
    }
}

/// <summary>On the city broker the hornet has a login of its own, made when missing and never remade under a connected hornet.</summary>
public sealed class CityLoginTests(DynSecFixture hive) : IClassFixture<DynSecFixture>
{
    [Fact]
    public async Task A_hornets_city_login_works_for_its_own_topics_only_and_a_second_call_does_not_drop_it()
    {
        var password = CityBrokerLogin.Password("shared-secret-of-home-and-city", "meteo-city1");
        var dynsec = hive.App.Services.GetRequiredService<DynamicSecurity>();
        await dynsec.ProvisionHornetIfMissingAsync("meteo-city1", password, CancellationToken.None);

        var admin = new MqttClientFactory().CreateMqttClient();
        var seen = new List<string>();
        admin.ApplicationMessageReceivedAsync += m =>
        {
            lock (seen) seen.Add(m.ApplicationMessage.Topic);
            return Task.CompletedTask;
        };
        await admin.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.Broker.Host, hive.Broker.Port)
            .WithCredentials("admin", DynSecFixture.AdminPassword).WithClientId("city-login-watch").Build());
        await admin.SubscribeAsync("vs/v1/dev/#");

        var hornet = new MqttClientFactory().CreateMqttClient();
        await hornet.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.Broker.Host, hive.Broker.Port)
            .WithCredentials("meteo-city1", password).WithClientId("meteo-city1").Build());
        await hornet.PublishStringAsync("vs/v1/dev/meteo-city1/log", "{}");
        await hornet.PublishStringAsync("vs/v1/dev/meteo-other/log", "{}");   // not its own
        for (var i = 0; i < 50; i++)
        {
            lock (seen)
                if (seen.Contains("vs/v1/dev/meteo-city1/log"))
                    break;
            await Task.Delay(100);
        }
        lock (seen)
        {
            Assert.Contains("vs/v1/dev/meteo-city1/log", seen);
            Assert.DoesNotContain("vs/v1/dev/meteo-other/log", seen);
        }

        // asking again must not remake the login: that would drop the connected hornet
        await dynsec.ProvisionHornetIfMissingAsync("meteo-city1", password, CancellationToken.None);
        await Task.Delay(1500);
        Assert.True(hornet.IsConnected);

        // and a wrong password is refused
        var intruder = new MqttClientFactory().CreateMqttClient();
        MqttClientConnectResultCode? code = null;
        try
        {
            code = (await intruder.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.Broker.Host, hive.Broker.Port)
                .WithCredentials("meteo-city1", "wrong-password").WithClientId("meteo-city1-x").Build())).ResultCode;
        }
        catch (Exception)
        {
            // refused with an exception: as good
        }
        Assert.True(code is null || code != MqttClientConnectResultCode.Success, "a wrong password must not connect");
        Assert.False(intruder.IsConnected);
    }
}
