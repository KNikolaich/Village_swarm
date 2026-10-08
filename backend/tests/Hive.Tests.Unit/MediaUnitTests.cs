using Hive.Infrastructure;
using Hive.Modules.Media;

namespace Hive.Tests.Unit;

public class MediaUnitTests
{
    private static readonly string Photo = Path.Combine(AppContext.BaseDirectory, "scenarios", "photos", "frame0.jpg");

    [Fact]
    public void Photo_path_follows_spec_layout()
    {
        var local = new DateTimeOffset(2026, 10, 2, 23, 15, 7, TimeSpan.FromHours(3));
        Assert.Equal("photos/guard-gate1/2026/10/02/231507_motion_01J9Z3K7Q8M4N5P6R7S8T9V130-0.jpg",
            MediaStore.PhotoPath("guard-gate1", local, "motion", "01J9Z3K7Q8M4N5P6R7S8T9V130-0"));
        Assert.Equal("thumbs/guard-gate1/2026/10/02/01J9Z3K7Q8M4N5P6R7S8T9V130-0.webp",
            MediaStore.ThumbPath("guard-gate1", local, "01J9Z3K7Q8M4N5P6R7S8T9V130-0"));
    }

    [Fact]
    public void Day_range_uses_house_time_zone()
    {
        var (from, to) = new HiveOptions { TimeZone = "Europe/Moscow" }.DayRange(new DateOnly(2026, 10, 2));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 21, 0, 0, TimeSpan.Zero), from.ToUniversalTime());
        Assert.Equal(TimeSpan.FromDays(1), to - from);
    }

    [Fact]
    public void Cursor_round_trips_and_garbage_is_ignored()
    {
        var c = new TsCursor(new DateTimeOffset(2026, 10, 2, 12, 0, 0, 123, TimeSpan.Zero), "01J9Z3K7Q8M4N5P6R7S8T9V130");
        Assert.Equal(c, TsCursor.Decode(c.Encode()));
        Assert.Null(TsCursor.Decode("%%%"));
        Assert.Null(TsCursor.Decode(""));
        Assert.Equal(200, TsCursor.ClampLimit(10_000));
    }

    [Fact]
    public void Jpeg_signature_and_dimensions()
    {
        var bytes = File.ReadAllBytes(Photo);
        Assert.True(PhotoIngestService.IsJpeg(bytes));
        Assert.False(PhotoIngestService.IsJpeg("GIF89a"u8));
        Assert.Equal((640, 480), ImageTools.Identify(bytes));
        Assert.Null(ImageTools.Identify(new byte[100]));
    }

    [Fact]
    public void Thumbnail_is_320px_webp()
    {
        var webp = ImageTools.Thumbnail(Photo, 320);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(webp, 0, 4));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(webp, 8, 4));
        Assert.Equal((320, 240), ImageTools.Identify(webp));
    }
}
