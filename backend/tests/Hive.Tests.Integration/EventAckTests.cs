using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>"Просмотрено" for events: one action marks a filtered set, and the viewer cannot do it.</summary>
[Collection(HiveCollection.Name)]
public sealed class EventAckTests(HiveFixture hive)
{
    [Fact]
    public async Task Mark_all_viewed_changes_only_unseen_events_in_the_filter()
    {
        var now = DateTimeOffset.UtcNow;
        await hive.QueryAsync(async db =>
        {
            db.Events.AddRange(
                new DeviceEvent { Id = "01ACKTEST0000000000000000A", DeviceId = "guard-ack1", Type = "motion", Severity = EventSeverity.Alarm, Ts = now, ReceivedAt = now, Payload = JsonDocument.Parse("{}") },
                new DeviceEvent { Id = "01ACKTEST0000000000000000B", DeviceId = "guard-ack1", Type = "motion", Severity = EventSeverity.Alarm, Ts = now, ReceivedAt = now, Payload = JsonDocument.Parse("{}") },
                new DeviceEvent { Id = "01ACKTEST0000000000000000C", DeviceId = "meteo-ack1", Type = "motion", Severity = EventSeverity.Alarm, Ts = now, ReceivedAt = now, Payload = JsonDocument.Parse("{}") });
            await db.SaveChangesAsync();
            return 0;
        });

        var member = await hive.LoginAsync();
        using var response = await member.PostAsJsonAsync("/api/events/ack", new { device = "guard-ack1" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("acknowledged").GetInt32());

        var other = await hive.QueryAsync(db => db.Events.SingleAsync(e => e.Id == "01ACKTEST0000000000000000C"));
        Assert.Null(other.AcknowledgedAt);

        // Already viewed events are not counted again.
        using var again = await member.PostAsJsonAsync("/api/events/ack", new { device = "guard-ack1" });
        Assert.Equal(0, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("acknowledged").GetInt32());
    }

    [Fact]
    public async Task Viewer_cannot_mark_events_viewed()
    {
        await hive.CreateUserAsync("viewer-ack", "viewer-ack-password", HiveRoles.Viewer);
        var viewer = await hive.LoginAsync("viewer-ack", "viewer-ack-password");
        using var response = await viewer.PostAsJsonAsync("/api/events/ack", new { });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
