using System.Net;
using System.Net.Http.Json;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests.Integration;

/// <summary>Secrets entered in Settings: admins only, write-only, encrypted in the database, applied over .env at start.</summary>
[Collection(HiveCollection.Name)]
public sealed class SecretsTests(HiveFixture hive)
{
    private sealed record Item(string Key, string Group, string Source);
    private sealed record Page(List<Item> Secrets, string KeysPath);
    private sealed record Body(string? Value);

    private const string Plain = "sk-test-very-secret-value";

    [Fact]
    public async Task Secret_is_stored_encrypted_and_never_returned()
    {
        var admin = await hive.LoginAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/settings/secrets/assistant.apikey", new Body(Plain))).StatusCode);

        var text = await (await admin.GetAsync("/api/settings/secrets")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(Plain, text);
        var page = System.Text.Json.JsonSerializer.Deserialize<Page>(text, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal("db", page.Secrets.Single(s => s.Key == "assistant.apikey").Source);

        using (var scope = hive.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
            var stored = await db.Modes.AsNoTracking().SingleAsync(m => m.Key == "secret.assistant.apikey");
            Assert.DoesNotContain(Plain, stored.Value);
            Assert.Equal(Plain, scope.ServiceProvider.GetRequiredService<SecretStore>().TryUnprotect(stored.Value));
            // The audit trail names the secret, not its value.
            Assert.DoesNotContain((await db.Audit.AsNoTracking().ToListAsync()).Select(a => a.Details?.RootElement.ToString() ?? ""), d => d.Contains(Plain));

            // At the next start it goes over the configuration.
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assistant:ApiKey"] = "from-env" });
            await Secrets.ApplyAsync(scope.ServiceProvider, config);
            Assert.Equal(Plain, config.Build()["Assistant:ApiKey"]);
        }

        // Clearing returns to .env.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/settings/secrets/assistant.apikey", new Body(""))).StatusCode);
        var after = await admin.GetFromJsonAsync<Page>("/api/settings/secrets");
        Assert.NotEqual("db", after!.Secrets.Single(s => s.Key == "assistant.apikey").Source);
    }

    [Fact]
    public async Task Only_admins_and_only_known_keys()
    {
        await hive.CreateUserAsync("member-secrets", "member-secrets-password", HiveRoles.Member);
        var member = await hive.LoginAsync("member-secrets", "member-secrets-password");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/settings/secrets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/settings/secrets/smtp.password", new Body("x"))).StatusCode);

        var admin = await hive.LoginAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/settings/secrets/pg.password", new Body("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await hive.App.CreateClient().GetAsync("/api/settings/secrets")).StatusCode);
    }
}
