using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>Zones are the user's: the first start offers the usual ones, then they are created, renamed and assigned in the UI.</summary>
[Collection(HiveCollection.Name)]
public sealed class ZonesTests(HiveFixture hive)
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

    [Fact]
    public async Task A_new_hive_starts_with_the_usual_zones()
    {
        var admin = await hive.LoginAsync();
        var names = (await admin.GetFromJsonAsync<JsonElement>("/api/zones")).EnumerateArray().Select(z => z.GetProperty("name").GetString()).ToList();
        foreach (var expected in new[] { "Улица", "Гостиная", "Спальня", "Детская" })
            Assert.Contains(expected, names);
    }

    [Fact]
    public async Task Zones_can_be_created_renamed_and_removed_with_their_hornets_left_unassigned()
    {
        await RegisterAsync("meteo-zone-a");
        var admin = await hive.LoginAsync();

        using var created = await admin.PostAsJsonAsync("/api/zones", new { name = "Теплица", kind = "garden" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        using var duplicate = await admin.PostAsJsonAsync("/api/zones", new { name = "Теплица", kind = "garden" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var empty = await admin.PostAsJsonAsync("/api/zones", new { name = "  ", kind = "house" });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        using var assign = await admin.PutAsJsonAsync("/api/devices/meteo-zone-a/zone", new { zoneId = id });
        Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);
        var device = await admin.GetFromJsonAsync<JsonElement>("/api/devices/meteo-zone-a");
        Assert.Equal("Теплица", device.GetProperty("zone").GetString());
        var listed = (await admin.GetFromJsonAsync<JsonElement>("/api/zones")).EnumerateArray().Single(z => z.GetProperty("id").GetInt32() == id);
        Assert.Equal(1, listed.GetProperty("deviceCount").GetInt32());

        using var rename = await admin.PutAsJsonAsync($"/api/zones/{id}", new { name = "Парник", kind = "garden" });
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);

        using var missing = await admin.PutAsJsonAsync("/api/devices/meteo-zone-a/zone", new { zoneId = 99999 });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        using var removed = await admin.DeleteAsync($"/api/zones/{id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var after = await admin.GetFromJsonAsync<JsonElement>("/api/devices/meteo-zone-a");
        Assert.Equal(JsonValueKind.Null, after.GetProperty("zoneId").ValueKind);
    }

    [Fact]
    public async Task Zones_and_hornets_keep_the_place_set_for_them_in_lists()
    {
        await RegisterAsync("meteo-sort-a");
        var admin = await hive.LoginAsync();

        using var first = await admin.PostAsJsonAsync("/api/zones", new { name = "Сортировка-1", kind = "house" });
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        using var second = await admin.PostAsJsonAsync("/api/zones", new { name = "Сортировка-2", kind = "house", sort = 0 });
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var order = (await admin.GetFromJsonAsync<JsonElement>("/api/zones")).EnumerateArray().Select(z => z.GetProperty("id").GetInt32()).ToList();
        Assert.True(order.IndexOf(secondId) < order.IndexOf(firstId), "a zone placed at 0 comes before one added last");

        // A rename without a sort keeps the place.
        using var rename = await admin.PutAsJsonAsync($"/api/zones/{secondId}", new { name = "Сортировка-2б", kind = "house" });
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        var zone = (await admin.GetFromJsonAsync<JsonElement>("/api/zones")).EnumerateArray().Single(z => z.GetProperty("id").GetInt32() == secondId);
        Assert.Equal(0, zone.GetProperty("sort").GetInt32());
        using var badZone = await admin.PutAsJsonAsync($"/api/zones/{secondId}", new { name = "Сортировка-2б", kind = "house", sort = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, badZone.StatusCode);

        Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>("/api/devices/meteo-sort-a")).GetProperty("sort").GetInt32());
        using var sorted = await admin.PutAsJsonAsync("/api/devices/meteo-sort-a/sort", new { sort = 7 });
        Assert.Equal(HttpStatusCode.NoContent, sorted.StatusCode);
        Assert.Equal(7, (await admin.GetFromJsonAsync<JsonElement>("/api/devices/meteo-sort-a")).GetProperty("sort").GetInt32());
        using var tooFar = await admin.PutAsJsonAsync("/api/devices/meteo-sort-a/sort", new { sort = 10000 });
        Assert.Equal(HttpStatusCode.BadRequest, tooFar.StatusCode);
        using var missing = await admin.PutAsJsonAsync("/api/devices/nobody/sort", new { sort = 1 });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Only_admins_change_zones()
    {
        await hive.CreateUserAsync("viewer-zones", "viewer-zones-password", Hive.Infrastructure.Identity.HiveRoles.Viewer);
        var viewer = await hive.LoginAsync("viewer-zones", "viewer-zones-password");
        using var read = await viewer.GetAsync("/api/zones");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var create = await viewer.PostAsJsonAsync("/api/zones", new { name = "Чердак", kind = "tech" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        using var assign = await viewer.PutAsJsonAsync("/api/devices/anything/zone", new { zoneId = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, assign.StatusCode);
        using var sort = await viewer.PutAsJsonAsync("/api/devices/anything/sort", new { sort = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, sort.StatusCode);
    }
}
