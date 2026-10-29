using Hive.Modules.Devices;

namespace Hive.Tests.Unit;

/// <summary>The frame a monitored panel is about to ask for again is drawn a little before: kept only for that, and only on the same basis.</summary>
public class FrameCacheTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(2);

    [Fact]
    public void A_frame_is_found_while_it_is_younger_than_the_pause_and_drawn_from_the_same_basis()
    {
        var cache = new FrameCache();
        cache.Put("monitor-1", 1, false, "items-a", T0, [1, 2, 3], "\"t1abc\"");
        Assert.Equal("\"t1abc\"", cache.Find("monitor-1", 1, false, "items-a", T0.AddSeconds(100), Every)?.ETag);
        Assert.Equal(1, cache.Hits);
        Assert.Null(cache.Find("monitor-1", 1, false, "items-b", T0.AddSeconds(100), Every));   // the person changed the screen
        Assert.Null(cache.Find("monitor-1", 1, false, "items-a", T0.AddSeconds(121), Every));   // too old
        Assert.Null(cache.Find("monitor-1", 2, false, "items-a", T0.AddSeconds(10), Every));    // another tab
        Assert.Null(cache.Find("monitor-1", 1, true, "items-a", T0.AddSeconds(10), Every));     // the sleeping picture is another frame
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void The_next_frame_is_planned_a_little_before_the_panel_reads_again_and_follows_the_last_tab_asked()
    {
        var cache = new FrameCache();
        cache.Plan("monitor-1", 0, false, T0, Every, T0.AddHours(1));
        cache.Plan("monitor-1", 2, false, T0.AddSeconds(5), Every, T0.AddHours(1));
        Assert.Empty(cache.Due(T0.AddSeconds(100)));
        var due = Assert.Single(cache.Due(T0.AddSeconds(5) + Every - FrameCache.Lead));
        Assert.Equal(2, due.Watch.Tab);
        cache.Rearm(due.Device, due.Watch);
        Assert.Empty(cache.Due(T0.AddSeconds(5) + Every - FrameCache.Lead));
        Assert.Single(cache.Due(T0.AddSeconds(5) + Every * 2 - FrameCache.Lead));
    }

    [Fact]
    public void A_monitoring_that_is_over_leaves_nothing_behind()
    {
        var cache = new FrameCache();
        cache.Put("monitor-1", 1, false, "a", T0, [1], "\"x\"");
        cache.Plan("monitor-1", 1, false, T0, Every, T0.AddMinutes(30));
        Assert.Empty(cache.Due(T0.AddMinutes(31)));
        Assert.Null(cache.Find("monitor-1", 1, false, "a", T0.AddMinutes(31), TimeSpan.FromHours(1)));
    }

    [Fact]
    public void A_short_pause_is_still_planned_ahead_of_the_read()
    {
        var cache = new FrameCache();
        cache.Plan("monitor-1", 0, false, T0, TimeSpan.FromSeconds(20), T0.AddHours(1));
        Assert.Single(cache.Due(T0.AddSeconds(10)));
    }
}
