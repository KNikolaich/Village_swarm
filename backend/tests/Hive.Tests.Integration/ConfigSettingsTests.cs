using System.Net;
using System.Net.Http.Json;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests.Integration;

/// <summary>Plain parameters entered in Settings: admins only, validated, kept in the database, applied over .env at start.</summary>
[Collection(HiveCollection.Name)]
public sealed class ConfigSettingsTests(HiveFixture hive)
{
    private sealed record Item(string Key, string Section, string Value, string? Stored, string Source, bool RestartPending, string Apply);
    private sealed record Page(string[] Sections, List<Item> Items);
    private sealed record Body(string? Value);

    [Fact]
    public async Task Value_is_saved_validated_flagged_for_restart_and_cleared()
    {
        var admin = await hive.LoginAsync();
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/settings/config/assistant.model", new Body(" my-model "))).StatusCode);
            var item = (await admin.GetFromJsonAsync<Page>("/api/settings/config"))!.Items.Single(i => i.Key == "assistant.model");
            Assert.Equal("my-model", item.Stored);
            Assert.Equal("db", item.Source);
            Assert.True(item.RestartPending); // the running Hive was started without it

            // Bad values are refused and do not replace the stored one.
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/settings/config/sensors.air_warn_c", new Body("hot"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/settings/config/timezone", new Body("Mars/Olympus"))).StatusCode);

            // The next start puts it over .env.
            using (var scope = hive.App.Services.CreateScope())
            {
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assistant:Model"] = "from-env" });
                await ConfigSettings.ApplyAsync(scope.ServiceProvider, config);
                Assert.Equal("my-model", config.Build()["Assistant:Model"]);
            }
        }
        finally
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/settings/config/assistant.model", new Body(""))).StatusCode);
        }
        var after = (await admin.GetFromJsonAsync<Page>("/api/settings/config"))!.Items.Single(i => i.Key == "assistant.model");
        Assert.Null(after.Stored);
        Assert.NotEqual("db", after.Source);
    }

    [Fact]
    public async Task Auto_parameters_are_stored_but_never_wait_for_a_restart()
    {
        var admin = await hive.LoginAsync();
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/settings/config/backup.at", new Body("04:15"))).StatusCode);
            var item = (await admin.GetFromJsonAsync<Page>("/api/settings/config"))!.Items.Single(i => i.Key == "backup.at");
            Assert.Equal("04:15", item.Value);
            Assert.Equal("auto", item.Apply);
            Assert.False(item.RestartPending);
            using var scope = hive.App.Services.CreateScope();
            Assert.Equal("04:15", (await scope.ServiceProvider.GetRequiredService<HiveDbContext>().Modes.AsNoTracking().SingleAsync(m => m.Key == "cfg.backup.at")).Value);
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/settings/config/backup.at", new Body(""));
        }
    }

    [Fact]
    public async Task Only_admins_and_only_known_keys()
    {
        await hive.CreateUserAsync("member-config", "member-config-password", HiveRoles.Member);
        var member = await hive.LoginAsync("member-config", "member-config-password");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/settings/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/settings/config/timezone", new Body("Europe/Moscow"))).StatusCode);

        var admin = await hive.LoginAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/settings/config/pg.password", new Body("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await hive.App.CreateClient().GetAsync("/api/settings/config")).StatusCode);
        var page = await admin.GetFromJsonAsync<Page>("/api/settings/config");
        Assert.Equal(ConfigSettings.All.Count, page!.Items.Count);
    }
}
