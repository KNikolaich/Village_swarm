using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Modules.Backup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests.Integration;

/// <summary>The backup journal the container writes, what the API says about it, and the warning when backups go wrong.</summary>
[Collection(HiveCollection.Name)]
public sealed class BackupTests(HiveFixture hive)
{
    private async Task AddRunAsync(string kind, bool ok, TimeSpan ago, string? detail = null)
    {
        var at = DateTimeOffset.UtcNow - ago;
        await hive.QueryAsync(async db =>
        {
            db.BackupRuns.Add(new BackupRun { Kind = kind, StartedAt = at, FinishedAt = at.AddSeconds(5), Ok = ok, Bytes = 4096, Detail = detail });
            await db.SaveChangesAsync();
            return 0;
        });
    }

    /// <summary>One story in order (the journal is shared state): stale, well, failed and warned once, well again.</summary>
    [Fact]
    public async Task The_journal_drives_the_status_and_the_warnings()
    {
        var admin = await hive.LoginAsync();
        var watch = hive.App.Services.GetServices<IHostedService>().OfType<BackupWatch>().Single();

        await AddRunAsync("dump", true, TimeSpan.FromHours(50));
        var stale = await admin.GetFromJsonAsync<JsonElement>("/api/backups");
        Assert.False(stale.GetProperty("ok").GetBoolean());
        Assert.Equal("dump_stale", stale.GetProperty("problems")[0].GetString());

        await AddRunAsync("dump", true, TimeSpan.FromHours(1));
        await AddRunAsync("verify", true, TimeSpan.FromHours(30));
        var good = await admin.GetFromJsonAsync<JsonElement>("/api/backups");
        Assert.True(good.GetProperty("ok").GetBoolean(), good.ToString());
        Assert.Equal(4096, good.GetProperty("dump").GetProperty("bytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, good.GetProperty("offsite").ValueKind); // none configured
        Assert.True(good.GetProperty("recent").GetArrayLength() >= 3);

        await AddRunAsync("dump", false, TimeSpan.FromMinutes(1), "pg_dump: connection refused");
        await watch.CheckAsync(CancellationToken.None);
        await watch.CheckAsync(CancellationToken.None); // the same problem again: no second warning
        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.Type == "backup" && e.Severity == EventSeverity.Warn), "backup warning");
        Assert.Equal(1, await hive.QueryAsync(db => db.Events.CountAsync(e => e.Type == "backup")));
        var warning = await hive.QueryAsync(db => db.Events.FirstAsync(e => e.Type == "backup"));
        Assert.Equal("dump_failed", warning.Payload.RootElement.GetProperty("problems")[0].GetString());

        await AddRunAsync("dump", true, TimeSpan.Zero);
        await watch.CheckAsync(CancellationToken.None);
        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.Type == "backup_cleared"), "all-clear");
    }

    [Fact]
    public async Task Only_admins_see_the_backups()
    {
        await hive.CreateUserAsync("viewer-backup", "viewer-backup-password", Hive.Infrastructure.Identity.HiveRoles.Viewer);
        var viewer = await hive.LoginAsync("viewer-backup", "viewer-backup-password");
        using var refused = await viewer.GetAsync("/api/backups");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }
}
