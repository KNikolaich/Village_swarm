using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>A batch of hornets is one server-side queue: codes for each board, kept until used or expired.</summary>
[Collection(HiveCollection.Name)]
public sealed class BatchProvisioningTests(HiveFixture hive)
{
    [Fact]
    public async Task Batch_creates_one_code_per_board_and_the_queue_lists_them()
    {
        var admin = await hive.LoginAsync();
        using var response = await admin.PostAsJsonAsync("/api/provision/batches", new { type = "meteo", idPrefix = "meteo-batch", namePrefix = "Теплица", count = 3 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal(("meteo-batch1", "Теплица 1"), (items[0].GetProperty("deviceId").GetString(), items[0].GetProperty("name").GetString()));
        Assert.Equal(("meteo-batch3", "Теплица 3"), (items[2].GetProperty("deviceId").GetString(), items[2].GetProperty("name").GetString()));

        // A batch lives for hours, not the 30 minutes of a single code.
        var expires = items[0].GetProperty("expiresAt").GetDateTimeOffset();
        Assert.True(expires - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(120));

        var queue = await admin.GetFromJsonAsync<JsonElement>("/api/provision/codes");
        var ids = queue.EnumerateArray().Select(e => e.GetProperty("deviceId").GetString()).ToList();
        Assert.Contains("meteo-batch2", ids);
    }

    [Fact]
    public async Task Bad_batches_are_refused_and_viewers_cannot_create_them()
    {
        var admin = await hive.LoginAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/provision/batches", new { type = "meteo", idPrefix = "meteo-x", namePrefix = "x", count = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/provision/batches", new { type = "meteo", idPrefix = "Bad Prefix", namePrefix = "x", count = 2 })).StatusCode);

        await hive.CreateUserAsync("viewer-batch", "viewer-batch-password", HiveRoles.Viewer);
        var viewer = await hive.LoginAsync("viewer-batch", "viewer-batch-password");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/provision/batches", new { type = "meteo", idPrefix = "meteo-v", namePrefix = "v", count = 1 })).StatusCode);
    }
}
