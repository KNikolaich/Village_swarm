using System.Net.Http.Json;
using System.Text.Json;

namespace Hive.Tests.Integration;

/// <summary>The installed app and the tab are named after the hive: the manifest comes from the api, not from the build.</summary>
[Collection(HiveCollection.Name)]
public sealed class ManifestTests(HiveFixture hive)
{
    [Fact]
    public async Task The_manifest_carries_the_alias_and_is_open_and_never_cached()
    {
        using var admin = await hive.LoginAsync();
        using var set = await admin.PutAsJsonAsync("/api/settings/alias", new { alias = "Дача" });
        set.EnsureSuccessStatusCode();

        using var anonymous = hive.App.CreateClient();
        using var response = await anonymous.GetAsync("/api/manifest.webmanifest");
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore, "a rename must show at once");
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Улей — Дача", json.GetProperty("name").GetString());
        Assert.Equal("Дача", json.GetProperty("short_name").GetString());
        Assert.Equal(3, json.GetProperty("icons").GetArrayLength());

        (await admin.PutAsJsonAsync("/api/settings/alias", new { alias = "" })).EnsureSuccessStatusCode();
        var plain = JsonDocument.Parse(await anonymous.GetStringAsync("/api/manifest.webmanifest")).RootElement;
        Assert.Equal("Улей", plain.GetProperty("name").GetString());
        Assert.Equal("Улей", plain.GetProperty("short_name").GetString());
    }
}
