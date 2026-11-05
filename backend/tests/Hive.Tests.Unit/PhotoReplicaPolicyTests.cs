using Hive.Domain;
using Hive.Modules.Cluster;

namespace Hive.Tests.Unit;

/// <summary>Which photos go to the city first, how long they wait, and how long a failed one is left alone (spec 7.3).</summary>
public class PhotoReplicaPolicyTests
{
    [Fact]
    public void Alarm_photos_come_first_then_other_events_then_periodic_frames()
    {
        Assert.Equal(PhotoReplicaPolicy.Alarm, PhotoReplicaPolicy.Priority(MediaKind.Photo, "01J9Z3K7Q8M4N5P6R7S8T9V170", EventSeverity.Alarm));
        Assert.Equal(PhotoReplicaPolicy.Alarm, PhotoReplicaPolicy.Priority(MediaKind.Photo, "01J9Z3K7Q8M4N5P6R7S8T9V170", EventSeverity.Critical));
        Assert.Equal(PhotoReplicaPolicy.Event, PhotoReplicaPolicy.Priority(MediaKind.Photo, "01J9Z3K7Q8M4N5P6R7S8T9V170", EventSeverity.Info));
        Assert.Equal(PhotoReplicaPolicy.Event, PhotoReplicaPolicy.Priority(MediaKind.Photo, "01J9Z3K7Q8M4N5P6R7S8T9V170", EventSeverity.Warn));
        Assert.Equal(PhotoReplicaPolicy.Periodic, PhotoReplicaPolicy.Priority(MediaKind.Snapshot, null, null));
        Assert.Equal(PhotoReplicaPolicy.Periodic, PhotoReplicaPolicy.Priority(MediaKind.Timelapse, null, null));
        Assert.Equal(PhotoReplicaPolicy.Periodic, PhotoReplicaPolicy.Priority(MediaKind.Photo, null, null));   // a photo no event owns
    }

    [Fact]
    public void An_alarm_waits_a_week_an_event_a_day_and_a_periodic_frame_six_hours()
    {
        Assert.Equal(TimeSpan.FromDays(7), PhotoReplicaPolicy.Ttl(PhotoReplicaPolicy.Alarm));
        Assert.Equal(TimeSpan.FromHours(24), PhotoReplicaPolicy.Ttl(PhotoReplicaPolicy.Event));
        Assert.Equal(TimeSpan.FromHours(6), PhotoReplicaPolicy.Ttl(PhotoReplicaPolicy.Periodic));
    }

    [Fact]
    public void A_failed_photo_is_left_alone_for_longer_each_time_but_never_more_than_an_hour()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), PhotoReplicaPolicy.Pause(1));
        Assert.Equal(TimeSpan.FromMinutes(2), PhotoReplicaPolicy.Pause(2));
        Assert.Equal(TimeSpan.FromMinutes(8), PhotoReplicaPolicy.Pause(4));
        Assert.Equal(TimeSpan.FromMinutes(60), PhotoReplicaPolicy.Pause(7));
        Assert.Equal(TimeSpan.FromMinutes(60), PhotoReplicaPolicy.Pause(50));
    }

    [Fact]
    public void The_key_is_made_from_the_shared_secret_and_differs_with_it()
    {
        Assert.Equal(PhotoReplicaPolicy.Key("secret"), PhotoReplicaPolicy.Key("secret"));
        Assert.NotEqual(PhotoReplicaPolicy.Key("secret"), PhotoReplicaPolicy.Key("other"));
    }

    [Theory]
    [InlineData("{\"host\":\"192.168.1.3\",\"port\":1883}", "https://192.168.1.3")]
    [InlineData("{\"host\":\"192.168.1.3\",\"port\":1883,\"replicaUrl\":\"https://city.example.org:8443/\"}", "https://city.example.org:8443")]
    [InlineData("{\"host\":null,\"port\":1883,\"replicaUrl\":\"http://10.0.0.5\"}", "http://10.0.0.5")]
    [InlineData("{\"host\":null,\"port\":1883}", null)]
    [InlineData(null, null)]
    public void The_city_address_is_the_one_set_for_photos_else_https_and_the_broker_address(string? setting, string? url) =>
        Assert.Equal(url, PhotoReplicator.CityUrl(setting));
}
