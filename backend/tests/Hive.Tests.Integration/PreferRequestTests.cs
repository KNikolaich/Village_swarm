using System.Text.Json;
using Hive.Contracts;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hive.Tests.Integration;

/// <summary>The dacha applies the city's choice of the preferred hive for the dacha's own hornets, once, and never over a later choice made here.</summary>
[Collection(HiveCollection.Name)]
public sealed class PreferRequestTests(HiveFixture hive)
{
    private const string Secret = "shared-secret-of-home-and-city";

    private PreferRequestReceiver Receiver() => new(
        Options.Create(new HiveOptions { Node = "home" }), Options.Create(new CityBrokerOptions { BridgePassword = Secret }),
        hive.App.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<PreferRequestReceiver>.Instance);

    private async Task MakeHornetAsync(string id, bool visitor = false)
    {
        await hive.QueryAsync(async db =>
        {
            if (!await db.Devices.AnyAsync(d => d.DeviceId == id))
                db.Devices.Add(new Device
                {
                    DeviceId = id, TypeCode = "meteo", Name = id, Status = DeviceStatus.Online, CreatedAt = DateTimeOffset.UtcNow, Visitor = visitor,
                    Config = JsonDocument.Parse($$"""{"v":1,"rev":1,"name":"{{id}}","health_interval_s":60}"""),
                });
            await db.SaveChangesAsync();
            return 0;
        });
    }

    private async Task SetCityAsync(string? host)
    {
        using var scope = hive.App.Services.CreateScope();
        await ActivatorUtilities.CreateInstance<FailoverService>(scope.ServiceProvider, Options.Create(new CityBrokerOptions { BridgePassword = Secret }))
            .SetAsync(new FailoverDto(host, 1883), "user:test", CancellationToken.None);
    }

    private static PreferRequestMessage Ask(string id, string prefer, long ts) => new(1, Ulid.New(DateTimeOffset.UtcNow), ts, "city", id, prefer);

    private async Task<JsonElement> ConfigOfAsync(string id) => (await hive.QueryAsync(db => db.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == id))).Config!.RootElement;

    [Fact]
    public async Task The_city_choice_is_written_into_the_hornets_config_like_a_choice_made_here()
    {
        await MakeHornetAsync("meteo-pref-a");
        await SetCityAsync("192.168.1.3");

        Assert.True(await Receiver().ApplyAsync(Ask("meteo-pref-a", "other", 1000), CancellationToken.None));

        var config = await ConfigOfAsync("meteo-pref-a");
        Assert.Equal("other", config.GetProperty("prefer_hive").GetString());
        Assert.True(config.GetProperty("mqtt_city").GetProperty("prefer_other").GetBoolean());
        Assert.Equal(1, config.GetProperty("rev").GetInt32());   // a new revision (the first one), so the hornet applies it
    }

    [Fact]
    public async Task A_request_is_applied_once_and_never_over_a_later_choice_made_here()
    {
        await MakeHornetAsync("meteo-pref-b");
        await SetCityAsync("192.168.1.3");
        var receiver = Receiver();
        Assert.True(await receiver.ApplyAsync(Ask("meteo-pref-b", "other", 2000), CancellationToken.None));

        // the same retained request arrives again after a restart: nothing changes
        Assert.False(await receiver.ApplyAsync(Ask("meteo-pref-b", "other", 2000), CancellationToken.None));
        Assert.Equal(1, (await ConfigOfAsync("meteo-pref-b")).GetProperty("rev").GetInt32());

        // somebody at the dacha chooses the dacha; the old retained request of the city must not undo it
        using (var scope = hive.App.Services.CreateScope())
            await ActivatorUtilities.CreateInstance<FailoverService>(scope.ServiceProvider, Options.Create(new CityBrokerOptions { BridgePassword = Secret }))
                .SetPreferAsync("meteo-pref-b", "this", "user:test", CancellationToken.None);
        Assert.False(await receiver.ApplyAsync(Ask("meteo-pref-b", "other", 2000), CancellationToken.None));
        Assert.False((await ConfigOfAsync("meteo-pref-b")).TryGetProperty("prefer_hive", out _));

        // a newer request from the city does apply
        Assert.True(await receiver.ApplyAsync(Ask("meteo-pref-b", "other", 3000), CancellationToken.None));
        Assert.Equal("other", (await ConfigOfAsync("meteo-pref-b")).GetProperty("prefer_hive").GetString());
    }

    [Fact]
    public async Task A_guest_of_the_city_an_unknown_hornet_and_a_missing_city_address_are_not_applied()
    {
        await MakeHornetAsync("meteo-pref-guest", visitor: true);
        await MakeHornetAsync("meteo-pref-noaddr");
        var receiver = Receiver();

        await SetCityAsync("192.168.1.3");
        Assert.False(await receiver.ApplyAsync(Ask("meteo-pref-guest", "other", 1000), CancellationToken.None));   // the city's own hornet: its business
        Assert.False(await receiver.ApplyAsync(Ask("nobody-here", "other", 1000), CancellationToken.None));

        await SetCityAsync(null);
        Assert.False(await receiver.ApplyAsync(Ask("meteo-pref-noaddr", "other", 1000), CancellationToken.None));
        Assert.False((await ConfigOfAsync("meteo-pref-noaddr")).TryGetProperty("prefer_hive", out _));

        // not remembered as applied: with the address set, the same request goes through
        await SetCityAsync("192.168.1.3");
        Assert.True(await receiver.ApplyAsync(Ask("meteo-pref-noaddr", "other", 1000), CancellationToken.None));
    }

    [Fact]
    public async Task Asking_for_what_the_hornet_already_has_changes_nothing_but_is_remembered()
    {
        await MakeHornetAsync("meteo-pref-same");
        await SetCityAsync("192.168.1.3");
        var receiver = Receiver();

        Assert.True(await receiver.ApplyAsync(Ask("meteo-pref-same", "this", 1000), CancellationToken.None));

        Assert.Equal(1, (await ConfigOfAsync("meteo-pref-same")).GetProperty("rev").GetInt32());   // as it was seeded: no new revision
        Assert.False(await receiver.ApplyAsync(Ask("meteo-pref-same", "this", 1000), CancellationToken.None));
    }

    [Fact]
    public void Only_the_dacha_listens_and_only_for_a_well_formed_request()
    {
        Assert.Equal(["vs/v1/sync/city/prefer/+"], Receiver().TopicFilters);
        var city = new PreferRequestReceiver(Options.Create(new HiveOptions { Node = "city" }), Options.Create(new CityBrokerOptions()),
            hive.App.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<PreferRequestReceiver>.Instance);
        Assert.Empty(city.TopicFilters);

        Assert.Null(PreferRequest.Parse(System.Text.Encoding.UTF8.GetBytes("not json")));
        var roundTrip = PreferRequest.Parse(System.Text.Encoding.UTF8.GetBytes(PreferRequest.Serialize(Ask("meteo-x", "other", 5))));
        Assert.Equal(("meteo-x", "other", 5L), (roundTrip!.DeviceId, roundTrip.Prefer, roundTrip.Ts));
    }
}
