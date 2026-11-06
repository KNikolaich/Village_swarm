using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>Pins are set by hand but never on top of each other: the hive refuses conflicting wiring.</summary>
[Collection(HiveCollection.Name)]
public sealed class PinsTests(HiveFixture hive)
{
    private async Task RegisterAsync(string id, string type = "meteo")
    {
        var spec = new HornetSpec { Id = id, Type = type };
        var hornet = new VirtualHornet(spec, HornetRole.Create(spec, "no-photos"));
        hornet.Start(DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var link = new MqttHornetLink(new BrokerOptions { Host = hive.MqttHost, Port = hive.MqttPort });
        await link.ConnectAsync(id, CancellationToken.None);
        foreach (var m in hornet.OnConnected(DateTimeOffset.UtcNow))
            await link.PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == id && d.Status == DeviceStatus.Online), "device online");
    }

    [Fact]
    public async Task Conflicting_pins_are_refused_and_free_pins_are_kept()
    {
        await RegisterAsync("meteo-pins1");
        var admin = await hive.LoginAsync();

        using var clash = await admin.PutAsJsonAsync("/api/devices/meteo-pins1/pins", new { onewirePin = 4, sdaPin = 4 });
        Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);

        using var bad = await admin.PutAsJsonAsync("/api/devices/meteo-pins1/pins", new { onewirePin = 99 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var ok = await admin.PutAsJsonAsync("/api/devices/meteo-pins1/pins", new { onewirePin = 3, sdaPin = 8, sclPin = 9 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        using var view = await admin.GetAsync("/api/devices/meteo-pins1/pins");
        var pins = (await view.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pins");
        var onewire = pins.EnumerateArray().Single(p => p.GetProperty("function").GetString() == "onewire");
        Assert.Equal((3, "config"), (onewire.GetProperty("pin").GetInt32(), onewire.GetProperty("source").GetString()));

        // Removing the override (null) goes back to the board default; the config keeps no stale hw key.
        using var reset = await admin.PutAsJsonAsync("/api/devices/meteo-pins1/pins", new { onewirePin = (int?)null, sdaPin = (int?)null, sclPin = (int?)null });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var config = await hive.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == "meteo-pins1"));
        Assert.False(config.Config!.RootElement.TryGetProperty("hw", out _));
    }

    [Fact]
    public async Task Battery_divider_ratio_is_kept_in_the_config_and_shown_with_the_pins()
    {
        await RegisterAsync("meteo-pins3");
        var admin = await hive.LoginAsync();

        using var tooHigh = await admin.PutAsJsonAsync("/api/devices/meteo-pins3/pins", new { vbatPin = 17, vbatRatio = 1000.0 });
        Assert.Equal(HttpStatusCode.BadRequest, tooHigh.StatusCode);
        using var zero = await admin.PutAsJsonAsync("/api/devices/meteo-pins3/pins", new { vbatPin = 17, vbatRatio = 0.0 });
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);

        using var ok = await admin.PutAsJsonAsync("/api/devices/meteo-pins3/pins", new { vbatPin = 17, vbatRatio = 5.5 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var device = await hive.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == "meteo-pins3"));
        var hw = device.Config!.RootElement.GetProperty("hw");
        Assert.Equal((17, 5.5), (hw.GetProperty("vbat_pin").GetInt32(), hw.GetProperty("vbat_ratio").GetDouble()));

        using var view = await admin.GetAsync("/api/devices/meteo-pins3/pins");
        var body = await view.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5.5, body.GetProperty("vbatRatio").GetDouble());
        Assert.Contains(body.GetProperty("pins").EnumerateArray(), p => p.GetProperty("function").GetString() == "vbat" && p.GetProperty("pin").GetInt32() == 17);

        // Without a ratio the firmware default (1.0) applies and the config keeps no vbat_ratio.
        using var reset = await admin.PutAsJsonAsync("/api/devices/meteo-pins3/pins", new { vbatPin = 17 });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        device = await hive.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == "meteo-pins3"));
        Assert.False(device.Config!.RootElement.GetProperty("hw").TryGetProperty("vbat_ratio", out _));
    }

    [Fact]
    public async Task Thermistor_pin_and_calibration_go_to_the_config()
    {
        await RegisterAsync("meteo-pins5");
        var admin = await hive.LoginAsync();

        using var badBeta = await admin.PutAsJsonAsync("/api/devices/meteo-pins5/pins", new { ntcPin = 0, ntc = new { beta = 20.0 } });
        Assert.Equal(HttpStatusCode.BadRequest, badBeta.StatusCode);
        using var badSide = await admin.PutAsJsonAsync("/api/devices/meteo-pins5/pins", new { ntcPin = 0, ntc = new { side = "middle" } });
        Assert.Equal(HttpStatusCode.BadRequest, badSide.StatusCode);

        using var ok = await admin.PutAsJsonAsync("/api/devices/meteo-pins5/pins",
            new { ntcPin = 0, ntc = new { side = "low", rSeries = 9960.0, rNominal = 10200.0, beta = 3435.0, offset = -0.4 } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(body.GetProperty("pins").EnumerateArray(), p => p.GetProperty("function").GetString() == "ntc" && p.GetProperty("pin").GetInt32() == 0);
        Assert.Equal(-0.4, body.GetProperty("ntc").GetProperty("offset").GetDouble());

        var device = await hive.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == "meteo-pins5"));
        var hw = device.Config!.RootElement.GetProperty("hw");
        Assert.Equal((0, "low", 9960.0, 10200.0, 3435.0, -0.4),
            (hw.GetProperty("ntc_pin").GetInt32(), hw.GetProperty("ntc_side").GetString(), hw.GetProperty("ntc_r_series").GetDouble(),
             hw.GetProperty("ntc_r_nominal").GetDouble(), hw.GetProperty("ntc_beta").GetDouble(), hw.GetProperty("ntc_offset").GetDouble()));
        Assert.False(hw.TryGetProperty("ntc_vcc", out _));  // not given: the firmware default 3.3 V

        // Without the thermistor nothing of it stays in the config.
        using var reset = await admin.PutAsJsonAsync("/api/devices/meteo-pins5/pins", new { });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        device = await hive.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == "meteo-pins5"));
        Assert.False(device.Config!.RootElement.TryGetProperty("hw", out _));
    }

    [Fact]
    public async Task Ntc_thermometer_reads_a0_and_keeps_its_calibration()
    {
        await RegisterAsync("meteo-pins-ntc");
        var admin = await hive.LoginAsync();

        // The pin is left to the board (GPIO0 = A0 on the C3); the settings go with it even so.
        using var ok = await admin.PutAsJsonAsync("/api/devices/meteo-pins-ntc/pins",
            new { thermometer = "ntc", ntc = new { side = "low", offset = 0.6 } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ntc", body.GetProperty("thermometer").GetString());
        var pins = body.GetProperty("pins").EnumerateArray().ToList();
        Assert.Contains(pins, p => p.GetProperty("function").GetString() == "ntc" && p.GetProperty("pin").GetInt32() == 0 && p.GetProperty("source").GetString() == "default");
        Assert.DoesNotContain(pins, p => p.GetProperty("function").GetString() == "onewire");

        var device = await hive.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == "meteo-pins-ntc"));
        var hw = device.Config!.RootElement.GetProperty("hw");
        Assert.Equal(("ntc", "low", 0.6), (hw.GetProperty("thermometer").GetString(), hw.GetProperty("ntc_side").GetString(), hw.GetProperty("ntc_offset").GetDouble()));
        Assert.False(hw.TryGetProperty("ntc_pin", out _));
    }

    [Fact]
    public async Task Heat_firmware_cannot_take_an_ntc_thermometer()
    {
        await RegisterAsync("heat-pins-ntc", "heat");
        var admin = await hive.LoginAsync();
        using var bad = await admin.PutAsJsonAsync("/api/devices/heat-pins-ntc/pins", new { thermometer = "ntc" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task The_thermometer_can_be_a_dht_on_its_own_pin()
    {
        await RegisterAsync("meteo-pins4");
        var admin = await hive.LoginAsync();

        using var unknown = await admin.PutAsJsonAsync("/api/devices/meteo-pins4/pins", new { thermometer = "dht21" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        using var ok = await admin.PutAsJsonAsync("/api/devices/meteo-pins4/pins", new { thermometer = "dht22", dhtPin = 7 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("dht22", true), (body.GetProperty("thermometer").GetString(), body.GetProperty("thermometerByHand").GetBoolean()));
        var functions = body.GetProperty("pins").EnumerateArray().Select(p => p.GetProperty("function").GetString()).ToList();
        Assert.Contains("dht", functions);
        Assert.DoesNotContain("onewire", functions);  // a DHT thermometer leaves the 1-Wire pin alone

        var device = await hive.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == "meteo-pins4"));
        var hw = device.Config!.RootElement.GetProperty("hw");
        Assert.Equal(("dht22", 7), (hw.GetProperty("thermometer").GetString(), hw.GetProperty("dht_pin").GetInt32()));
        var dto = await admin.GetFromJsonAsync<JsonElement>("/api/devices/meteo-pins4");
        Assert.Equal("dht22", dto.GetProperty("thermometer").GetString());

        // Back to the board's sensor: no thermometer key is left behind.
        using var reset = await admin.PutAsJsonAsync("/api/devices/meteo-pins4/pins", new { });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.False((await reset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("thermometerByHand").GetBoolean());
    }

    [Fact]
    public async Task Viewers_cannot_move_pins()
    {
        await RegisterAsync("meteo-pins2");
        await hive.CreateUserAsync("viewer-pins", "viewer-pins-password", Hive.Infrastructure.Identity.HiveRoles.Viewer);
        var viewer = await hive.LoginAsync("viewer-pins", "viewer-pins-password");
        using var response = await viewer.PutAsJsonAsync("/api/devices/meteo-pins2/pins", new { onewirePin = 5 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
