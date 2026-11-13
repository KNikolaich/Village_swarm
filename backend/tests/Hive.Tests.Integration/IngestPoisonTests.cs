using System.Text.Json;
using Hive.Contracts;
using Hive.Domain;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests.Integration;

/// <summary>One item that cannot be stored must not hold up everything behind it (a dacha stopped recording for hours because of one).</summary>
[Collection(HiveCollection.Name)]
public sealed class IngestPoisonTests(HiveFixture hive)
{
    private static DeviceInfoReceived Info(string id, JsonDocument info)
    {
        var now = DateTimeOffset.UtcNow;
        return new DeviceInfoReceived(id, Ulid.New(now), now, now, "meteo", "esp32", "0.2.1", "", null, 0, info);
    }

    [Fact]
    public async Task A_bad_item_is_dropped_and_the_ones_with_it_are_stored()
    {
        var queue = hive.App.Services.GetRequiredService<IngestQueue>();
        var disposed = JsonDocument.Parse("{}");
        disposed.Dispose();   // what the write cannot serialize

        queue.Enqueue(Info("poison-bad1", disposed));
        queue.Enqueue(Info("poison-ok1", JsonDocument.Parse("{}")));
        queue.Enqueue(Info("poison-ok2", JsonDocument.Parse("{}")));

        await hive.WaitForAsync(async db => await db.Devices.CountAsync(d => d.DeviceId == "poison-ok1" || d.DeviceId == "poison-ok2") == 2, "good items stored after the bad one", timeoutS: 60);
        Assert.False(await hive.QueryAsync(db => db.Devices.AnyAsync(d => d.DeviceId == "poison-bad1")));
    }
}
