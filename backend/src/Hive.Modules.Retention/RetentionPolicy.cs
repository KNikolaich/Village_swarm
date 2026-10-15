using Hive.Domain;

namespace Hive.Modules.Retention;

public sealed class RetentionOptions
{
    public const string Section = "Retention";

    /// <summary>Photos of events at alarm level and above (spec 7.4).</summary>
    public int AlarmPhotoDays { get; set; } = 365;

    /// <summary>Photos of info and warn events, periodic snapshots.</summary>
    public int OtherPhotoDays { get; set; } = 30;

    /// <summary>Raw time-lapse frames; the finished video (later) stays for good.</summary>
    public int TimelapseFrameDays { get; set; } = 90;

    /// <summary>Raw readings: older ones are kept as hourly aggregates, whole months at a time.</summary>
    public int RawTelemetryDays { get; set; } = 90;

    public int HealthDays { get; set; } = 14;
    public int LogDays { get; set; } = 30;

    /// <summary>Notification outbox, rule journal and command history older than this go.</summary>
    public int HousekeepingDays { get; set; } = 90;

    /// <summary>Disk use at which the cleanup turns aggressive, and where it stops (spec 7.4: 85 %).</summary>
    public double DiskHighPct { get; set; } = 85;

    public double DiskTargetPct { get; set; } = 80;

    /// <summary>Alarm photos are never deleted when younger than this, however full the disk is.</summary>
    public int AlarmFloorDays { get; set; } = 30;

    /// <summary>Hour of the daily cleanup, house time (after the nightly backup at 03:00).</summary>
    public int RunAtHour { get; set; } = 4;

    public int DiskCheckMinutes { get; set; } = 15;
}

public enum PhotoClass { Alarm, Other, Timelapse }

/// <summary>Which photos go when (spec 7.4), and which go first when the disk fills up. Pure.</summary>
public static class RetentionPolicy
{
    public const int MaxPressureLevel = 3;

    public static PhotoClass Classify(MediaKind kind, EventSeverity? severity) =>
        kind == MediaKind.Timelapse ? PhotoClass.Timelapse
        : severity >= EventSeverity.Alarm ? PhotoClass.Alarm
        : PhotoClass.Other;

    /// <summary>
    /// How old a photo of this class may be. <paramref name="level"/> 0 is the normal schedule; 1 to 3 are the steps
    /// taken while the disk stays above its limit: ordinary photos first, then time-lapse frames, and only at the
    /// end alarm photos, never those younger than the floor.
    /// </summary>
    public static TimeSpan MaxAge(PhotoClass photo, int level, RetentionOptions o)
    {
        var days = (photo, level) switch
        {
            (PhotoClass.Alarm, 0 or 1) => o.AlarmPhotoDays,
            (PhotoClass.Alarm, 2) => Math.Min(o.AlarmPhotoDays, 180),
            (PhotoClass.Alarm, _) => Math.Min(o.AlarmPhotoDays, 90),
            (PhotoClass.Other, 0) => o.OtherPhotoDays,
            (PhotoClass.Other, 1) => Math.Min(o.OtherPhotoDays, 7),
            (PhotoClass.Other, 2) => Math.Min(o.OtherPhotoDays, 1),
            (PhotoClass.Other, _) => 0,
            (PhotoClass.Timelapse, 0) => o.TimelapseFrameDays,
            (PhotoClass.Timelapse, 1) => Math.Min(o.TimelapseFrameDays, 30),
            (PhotoClass.Timelapse, 2) => Math.Min(o.TimelapseFrameDays, 7),
            _ => Math.Min(o.TimelapseFrameDays, 1),
        };
        if (photo == PhotoClass.Alarm)
            days = Math.Max(days, o.AlarmFloorDays);
        return TimeSpan.FromDays(days);
    }

    /// <summary>"Kept forever" photos never go, whatever the disk says.</summary>
    public static bool ShouldDelete(bool pinned, MediaKind kind, EventSeverity? severity, DateTimeOffset ts, DateTimeOffset now, int level, RetentionOptions o) =>
        !pinned && now - ts > MaxAge(Classify(kind, severity), level, o);

    /// <summary>The youngest age anything can be deleted at on this level: bounds the database query.</summary>
    public static TimeSpan YoungestDeletable(int level, RetentionOptions o) =>
        new[] { PhotoClass.Alarm, PhotoClass.Other, PhotoClass.Timelapse }.Min(c => MaxAge(c, level, o));
}
