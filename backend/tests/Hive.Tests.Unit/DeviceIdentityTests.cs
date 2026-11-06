using Hive.Domain;
using Hive.Modules.Devices;
using Hive.Modules.Telemetry.Ingest;

namespace Hive.Tests.Unit;

/// <summary>The MAC and build a panel tells with its reads (X-Device-Mac, X-Fw-Version): kept when well formed, never replaced by nothing.</summary>
public class DeviceIdentityTests
{
    [Theory]
    [InlineData("a0:b7:65:12:34:56", "A0:B7:65:12:34:56")]
    [InlineData("A0-B7-65-12-34-56", "A0:B7:65:12:34:56")]
    [InlineData(" A0:B7:65:12:34:56 ", "A0:B7:65:12:34:56")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("A0:B7:65:12:34", null)]
    [InlineData("zz:b7:65:12:34:56", null)]
    [InlineData("A0:B7:65:12:34:56:78", null)]
    public void A_mac_is_normalized_or_refused(string? raw, string? expected) => Assert.Equal(expected, ScreenAccessService.NormalizeMac(raw));

    [Theory]
    [InlineData("0.2.0+a1b2c3d", "0.2.0+a1b2c3d")]
    [InlineData("0.2.0-test", "0.2.0-test")]
    [InlineData("", null)]
    [InlineData("has space", null)]
    [InlineData("<script>", null)]
    [InlineData("1234567890123456789012345678901234567890x", null)]
    public void A_build_is_kept_or_refused(string raw, string? expected) => Assert.Equal(expected, ScreenAccessService.NormalizeFw(raw));

    [Fact]
    public void Known_values_are_never_overwritten_with_nothing_or_junk()
    {
        var device = new Device { DeviceId = "monitor-x", TypeCode = "monitor", Name = "x", Mac = "A0:B7:65:12:34:56", FwVersion = "0.2.0+aaa" };
        Assert.False(ScreenAccessService.NoteIdentity(device, "", ""));
        Assert.False(ScreenAccessService.NoteIdentity(device, null, null));
        Assert.False(ScreenAccessService.NoteIdentity(device, "garbage", "bad value"));
        Assert.Equal(("A0:B7:65:12:34:56", "0.2.0+aaa"), (device.Mac, device.FwVersion));
        Assert.True(ScreenAccessService.NoteIdentity(device, "a0:b7:65:12:34:99", "0.3.0+bbb"));
        Assert.Equal(("A0:B7:65:12:34:99", "0.3.0+bbb"), (device.Mac, device.FwVersion));
        Assert.False(ScreenAccessService.NoteIdentity(device, "A0:B7:65:12:34:99", "0.3.0+bbb"));  // nothing changed: no write
    }

    [Fact]
    public void A_new_device_learns_both()
    {
        var device = new Device { DeviceId = "monitor-y", TypeCode = "monitor", Name = "y" };
        Assert.True(ScreenAccessService.NoteIdentity(device, "a0:b7:65:12:34:56", null));
        Assert.Equal(("A0:B7:65:12:34:56", null), (device.Mac, device.FwVersion));
    }

    [Theory]
    [InlineData("A0:B7:65:12:34:56", "OLD", "A0:B7:65:12:34:56")]
    [InlineData("", "A0:B7:65:12:34:56", "A0:B7:65:12:34:56")]
    [InlineData("  ", "A0:B7:65:12:34:56", "A0:B7:65:12:34:56")]
    [InlineData(null, "0.2.0+a", "0.2.0+a")]
    [InlineData("", null, null)]
    public void An_info_with_nothing_in_it_keeps_the_last_known_value(string? fresh, string? known, string? expected) =>
        Assert.Equal(expected, IngestWriter.Keep(fresh, known));
}
