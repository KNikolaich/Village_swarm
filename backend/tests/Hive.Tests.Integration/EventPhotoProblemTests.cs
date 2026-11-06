using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;

namespace Hive.Tests.Integration;

/// <summary>An event without photos tells "the camera took no frame" (its journal or a brownout reboot) from a lost upload.</summary>
[Collection(HiveCollection.Name)]
public sealed class EventPhotoProblemTests(HiveFixture hive)
{
    [Fact]
    public async Task Camera_frame_failure_or_brownout_around_the_event_marks_it_no_frame()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        DeviceEvent Motion(string id, string device) => new()
        {
            Id = id, DeviceId = device, Type = "motion", Severity = EventSeverity.Alarm, Ts = now, ReceivedAt = now,
            Payload = JsonDocument.Parse("""{"photos":["p1"]}"""),
        };
        DeviceHealth Health(string device, TimeSpan at, long uptimeS) => new()
        {
            DeviceId = device, Ts = now + at, UptimeS = uptimeS, ResetReason = "brownout",
        };
        await hive.QueryAsync(async db =>
        {
            db.Events.AddRange(
                Motion("01NOFRAME000000000000000A", "cam-noframe1"),
                Motion("01NOFRAME000000000000000B", "cam-noframe2"),
                Motion("01NOFRAME000000000000000C", "cam-noframe3"),
                Motion("01NOFRAME000000000000000D", "cam-noframe4"));
            db.DeviceLogs.AddRange(
                new DeviceLog { DeviceId = "cam-noframe1", Ts = now.AddSeconds(2), Level = "warn", Msg = "camera frame failed" },
                // A failure an hour later is not this event's.
                new DeviceLog { DeviceId = "cam-noframe4", Ts = now.AddHours(1), Level = "warn", Msg = "camera frame failed" });
            db.DeviceHealth.AddRange(
                Health("cam-noframe2", TimeSpan.FromSeconds(90), 60), // rebooted 30 s after the event
                Health("cam-noframe3", TimeSpan.FromSeconds(90), 3600)); // still the brownout boot of an hour ago
            await db.SaveChangesAsync();
            return 0;
        });

        var member = await hive.LoginAsync();
        async Task<string?> Problem(string id)
        {
            var e = await member.GetFromJsonAsync<JsonElement>($"/api/events/{id}");
            return e.TryGetProperty("photoProblem", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }
        Assert.Equal("no_frame", await Problem("01NOFRAME000000000000000A"));
        Assert.Equal("no_frame", await Problem("01NOFRAME000000000000000B"));
        Assert.Null(await Problem("01NOFRAME000000000000000C"));
        Assert.Null(await Problem("01NOFRAME000000000000000D"));
    }
}
