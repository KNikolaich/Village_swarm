using Hive.Domain;
using Hive.Modules.Retention;

namespace Hive.Tests.Unit.Retention;

public sealed class RetentionPolicyTests
{
    private static readonly RetentionOptions O = new();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");

    private static bool Goes(MediaKind kind, EventSeverity? severity, int ageDays, int level = 0, bool pinned = false) =>
        RetentionPolicy.ShouldDelete(pinned, kind, severity, Now.AddDays(-ageDays), Now, level, O);

    [Fact]
    public void Photos_follow_the_default_schedule()
    {
        Assert.False(Goes(MediaKind.Photo, EventSeverity.Warn, 29));
        Assert.True(Goes(MediaKind.Photo, EventSeverity.Warn, 31));
        Assert.True(Goes(MediaKind.Snapshot, null, 31));
        Assert.False(Goes(MediaKind.Photo, EventSeverity.Alarm, 200));
        Assert.True(Goes(MediaKind.Photo, EventSeverity.Critical, 366));
        Assert.False(Goes(MediaKind.Timelapse, null, 89));
        Assert.True(Goes(MediaKind.Timelapse, null, 91));
    }

    [Fact]
    public void Kept_forever_means_forever_even_on_a_full_disk()
    {
        foreach (var level in new[] { 0, 1, 2, 3 })
            Assert.False(Goes(MediaKind.Photo, EventSeverity.Info, 1000, level, pinned: true));
    }

    [Fact]
    public void A_full_disk_takes_ordinary_photos_first_and_alarm_photos_last()
    {
        // 10 days old: an ordinary photo goes on level 1, an alarm photo not before 90 days on level 3.
        Assert.True(Goes(MediaKind.Photo, EventSeverity.Info, 10, level: 1));
        Assert.False(Goes(MediaKind.Photo, EventSeverity.Alarm, 10, level: 3));
        Assert.False(Goes(MediaKind.Photo, EventSeverity.Alarm, 100, level: 1));
        Assert.True(Goes(MediaKind.Photo, EventSeverity.Alarm, 200, level: 2));
        Assert.False(Goes(MediaKind.Photo, EventSeverity.Alarm, 100, level: 2));
        Assert.True(Goes(MediaKind.Photo, EventSeverity.Alarm, 100, level: 3));
    }

    [Fact]
    public void Young_alarm_photos_are_safe_whatever_the_level()
    {
        foreach (var level in new[] { 0, 1, 2, 3 })
            Assert.False(Goes(MediaKind.Photo, EventSeverity.Critical, 29, level));
    }

    [Fact]
    public void Every_level_is_no_gentler_than_the_one_before()
    {
        foreach (var photo in Enum.GetValues<PhotoClass>())
            for (var level = 1; level <= RetentionPolicy.MaxPressureLevel; level++)
                Assert.True(RetentionPolicy.MaxAge(photo, level, O) <= RetentionPolicy.MaxAge(photo, level - 1, O), $"{photo} level {level}");
    }

    [Fact]
    public void The_query_bound_never_misses_a_deletable_photo()
    {
        for (var level = 0; level <= RetentionPolicy.MaxPressureLevel; level++)
        {
            var bound = RetentionPolicy.YoungestDeletable(level, O);
            foreach (var photo in Enum.GetValues<PhotoClass>())
                Assert.True(RetentionPolicy.MaxAge(photo, level, O) >= bound);
        }
    }
}
