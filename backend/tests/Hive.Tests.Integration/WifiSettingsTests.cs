using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>Wi-Fi and the hive address are kept once in the hive; the password is encrypted and reaches only the wizard.</summary>
[Collection(HiveCollection.Name)]
public sealed class WifiSettingsTests(HiveFixture hive)
{
    [Fact]
    public async Task Wifi_is_saved_encrypted_and_only_the_wizard_gets_the_password()
    {
        var admin = await hive.LoginAsync();
        using var save = await admin.PutAsJsonAsync("/api/settings/wifi", new { ssid = "Dacha-IoT", password = "Очень-сложный-пароль-42", hiveUrl = "http://192.168.10.4/" });
        Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);

        var shown = await admin.GetFromJsonAsync<JsonElement>("/api/settings/wifi");
        Assert.Equal("Dacha-IoT", shown.GetProperty("ssid").GetString());
        Assert.True(shown.GetProperty("passwordSet").GetBoolean());
        Assert.DoesNotContain("Очень-сложный", shown.GetRawText());
        Assert.Equal("http://192.168.10.4", shown.GetProperty("hiveUrl").GetString());

        var stored = await hive.QueryAsync(db => db.Modes.SingleAsync(m => m.Key == "wifi.password"));
        Assert.DoesNotContain("Очень-сложный", stored.Value);

        var reveal = await admin.GetFromJsonAsync<JsonElement>("/api/settings/wifi/reveal");
        Assert.Equal("Очень-сложный-пароль-42", reveal.GetProperty("password").GetString());
    }

    [Fact]
    public async Task Empty_password_keeps_the_stored_one_and_viewers_get_nothing()
    {
        var admin = await hive.LoginAsync();
        (await admin.PutAsJsonAsync("/api/settings/wifi", new { ssid = "Dacha-IoT", password = "Очень-сложный-пароль-42", hiveUrl = "http://192.168.10.4" })).EnsureSuccessStatusCode();
        using var keep = await admin.PutAsJsonAsync("/api/settings/wifi", new { ssid = "Dacha-IoT-2", password = (string?)null, hiveUrl = "http://192.168.10.4" });
        Assert.Equal(HttpStatusCode.NoContent, keep.StatusCode);
        var reveal = await admin.GetFromJsonAsync<JsonElement>("/api/settings/wifi/reveal");
        Assert.Equal("Очень-сложный-пароль-42", reveal.GetProperty("password").GetString());

        await hive.CreateUserAsync("viewer-wifi", "viewer-wifi-password", HiveRoles.Viewer);
        var viewer = await hive.LoginAsync("viewer-wifi", "viewer-wifi-password");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/settings/wifi/reveal")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/settings/wifi", new { ssid = "x", password = "short", hiveUrl = "http://192.168.10.4" })).StatusCode);
    }
}
