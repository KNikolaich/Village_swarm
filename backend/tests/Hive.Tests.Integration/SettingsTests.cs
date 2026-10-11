using System.Net;
using System.Net.Http.Json;
using Hive.Infrastructure.Identity;

namespace Hive.Tests.Integration;

/// <summary>The interface language is one setting for the whole hive: everyone reads it, only admins change it.</summary>
[Collection(HiveCollection.Name)]
public sealed class SettingsTests(HiveFixture hive)
{
    private sealed record LanguageBody(string Language);

    [Fact]
    public async Task Language_is_shared_and_only_admins_change_it()
    {
        var admin = await hive.LoginAsync();
        var start = await admin.GetFromJsonAsync<LanguageBody>("/api/settings/language");
        Assert.Contains(start!.Language, new[] { "ru", "en" });

        await hive.CreateUserAsync("member-lang", "member-lang-password", HiveRoles.Member);
        var member = await hive.LoginAsync("member-lang", "member-lang-password");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/settings/language", new LanguageBody("en"))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/settings/language", new LanguageBody("en"))).StatusCode);
        Assert.Equal("en", (await member.GetFromJsonAsync<LanguageBody>("/api/settings/language"))!.Language);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/settings/language", new LanguageBody("de"))).StatusCode);
        await admin.PutAsJsonAsync("/api/settings/language", new LanguageBody("ru"));
    }
}
