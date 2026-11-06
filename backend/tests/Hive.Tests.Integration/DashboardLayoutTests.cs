using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Hive.Tests.Integration;

/// <summary>The overview is arranged once for the family, apart for a wide screen and for a phone.</summary>
[Collection(HiveCollection.Name)]
public sealed class DashboardLayoutTests(HiveFixture hive)
{
    [Fact]
    public async Task The_desktop_and_the_phone_keep_their_own_order_until_reset()
    {
        var admin = await hive.LoginAsync();
        using var desktop = await admin.PutAsJsonAsync("/api/dashboard/layout/desktop", new
        {
            items = new object[] { new { id = "zone:2", w = 8 }, new { id = "guard", w = 4 }, new { id = "events", w = 12, hidden = true } },
        });
        Assert.Equal(HttpStatusCode.NoContent, desktop.StatusCode);
        using var phone = await admin.PutAsJsonAsync("/api/dashboard/layout/phone", new { items = new object[] { new { id = "guard" }, new { id = "cam:guard-hall" } } });
        Assert.Equal(HttpStatusCode.NoContent, phone.StatusCode);

        var layouts = await admin.GetFromJsonAsync<JsonElement>("/api/dashboard/layout");
        var wide = layouts.GetProperty("desktop").EnumerateArray().ToList();
        Assert.Equal(["zone:2", "guard", "events"], wide.Select(w => w.GetProperty("id").GetString()));
        Assert.Equal(8, wide[0].GetProperty("w").GetInt32());
        Assert.True(wide[2].GetProperty("hidden").GetBoolean());
        Assert.Equal(["guard", "cam:guard-hall"], layouts.GetProperty("phone").EnumerateArray().Select(w => w.GetProperty("id").GetString()));

        using var reset = await admin.PutAsJsonAsync("/api/dashboard/layout/phone", new { items = (object[]?)null });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await admin.GetFromJsonAsync<JsonElement>("/api/dashboard/layout")).GetProperty("phone").ValueKind);

        using var badWidth = await admin.PutAsJsonAsync("/api/dashboard/layout/desktop", new { items = new object[] { new { id = "guard", w = 5 } } });
        Assert.Equal(HttpStatusCode.BadRequest, badWidth.StatusCode);
        using var twice = await admin.PutAsJsonAsync("/api/dashboard/layout/desktop", new { items = new object[] { new { id = "guard" }, new { id = "guard" } } });
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
        using var tv = await admin.PutAsJsonAsync("/api/dashboard/layout/tv", new { items = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NotFound, tv.StatusCode);
    }

    [Fact]
    public async Task Everyone_reads_the_layout_and_only_admins_change_it()
    {
        await hive.CreateUserAsync("member-layout", "member-layout-password", Hive.Infrastructure.Identity.HiveRoles.Member);
        var member = await hive.LoginAsync("member-layout", "member-layout-password");
        using var read = await member.GetAsync("/api/dashboard/layout");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var write = await member.PutAsJsonAsync("/api/dashboard/layout/desktop", new { items = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }
}
