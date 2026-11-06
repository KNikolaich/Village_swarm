using Hive.Domain;
using Hive.Modules.Devices;
using static Hive.Modules.Devices.OtaRolloutRules;

namespace Hive.Tests.Unit;

/// <summary>"Update all outdated": which build, in what order, and when a hornet counts as done or failed.</summary>
public class OtaRolloutRulesTests
{
    private sealed record B(string Role, string Board, string? Version, DateTime Built);

    private static readonly DateTimeOffset Sent = new(2026, 10, 20, 18, 0, 0, TimeSpan.Zero);

    private static B? Newer(IEnumerable<B> builds, string type, string? hw, string? fw) =>
        NewerBuild(builds, b => (b.Role, b.Board, b.Version, b.Built), type, hw, fw);

    [Fact]
    public void Newer_build_matches_role_and_board_and_takes_the_newest()
    {
        var old = new B("meteo", "esp32c3-supermini", "0.2.0", new DateTime(2026, 10, 1));
        var fresh = new B("meteo", "esp32c3-supermini", "0.3.0", new DateTime(2026, 10, 19));
        var otherBoard = new B("meteo", "esp-01", "0.4.0", new DateTime(2026, 10, 20));
        var otherRole = new B("relay", "esp32c3-supermini", "0.4.0", new DateTime(2026, 10, 20));
        Assert.Same(fresh, Newer([old, fresh, otherBoard, otherRole], "meteo", "esp32c3-supermini", "0.1.0"));
    }

    [Fact]
    public void Up_to_date_or_unversioned_builds_offer_nothing()
    {
        var current = new B("meteo", "esp32c3-supermini", "0.3.0", new DateTime(2026, 10, 19));
        var unversioned = new B("meteo", "esp32c3-supermini", null, new DateTime(2026, 10, 20));
        Assert.Null(Newer([current, unversioned], "meteo", "esp32c3-supermini", "0.3.0"));
    }

    [Fact]
    public void Boards_without_rollback_go_last()
    {
        var items = new[] { ("Баня", true), ("Ворота", false), ("Аккумулятор", true), ("Гараж", false) };
        var order = Order(items, i => i.Item2, i => i.Item1).Select(i => i.Item1).ToList();
        Assert.Equal(["Ворота", "Гараж", "Аккумулятор", "Баня"], order);
    }

    [Theory]
    [InlineData("ESP8266", true)]
    [InlineData("ESP32-C3", false)]
    [InlineData("ESP32", false)]
    public void Only_esp8266_has_no_rollback(string chip, bool expected) => Assert.Equal(expected, NoRollback(chip));

    [Fact]
    public void Done_when_the_hornet_is_back_online_with_the_new_version()
    {
        var (verdict, _) = Judge("0.3.0", "0.3.0", DeviceStatus.Online, Sent, Sent.AddSeconds(40), CommandStatus.Ok, null, null, null);
        Assert.Equal(Verdict.Done, verdict);
    }

    [Fact]
    public void Waits_while_the_hornet_downloads_or_restarts()
    {
        Assert.Equal(Verdict.Waiting, Judge("0.3.0", "0.2.0", DeviceStatus.Online, Sent, Sent.AddMinutes(2), CommandStatus.Ok, null, null, null).Verdict);
        Assert.Equal(Verdict.Waiting, Judge("0.3.0", "0.2.0", DeviceStatus.Offline, Sent, Sent.AddMinutes(3), CommandStatus.Ok, null, null, null).Verdict);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("rolled_back")]
    public void The_hornets_own_failure_stops_it(string phase)
    {
        var (verdict, error) = Judge("0.3.0", "0.2.0", DeviceStatus.Online, Sent, Sent.AddSeconds(30), CommandStatus.Ok, null, phase, "md5 mismatch");
        Assert.Equal(Verdict.Failed, verdict);
        Assert.Equal($"{phase}: md5 mismatch", error);
    }

    [Fact]
    public void A_refused_command_fails_with_the_hornets_reason()
    {
        var (verdict, error) = Judge("0.3.0", "0.2.0", DeviceStatus.Online, Sent, Sent.AddSeconds(5), CommandStatus.Rejected, "bad signature", null, null);
        Assert.Equal(Verdict.Failed, verdict);
        Assert.Equal("bad signature", error);
    }

    [Fact]
    public void Not_back_with_the_new_version_in_time_is_a_failure()
    {
        var (verdict, error) = Judge("0.3.0", "0.2.0", DeviceStatus.Offline, Sent, Sent + ItemTimeout + TimeSpan.FromSeconds(1), CommandStatus.Ok, null, null, null);
        Assert.Equal(Verdict.Failed, verdict);
        Assert.Equal("timeout", error);
    }
}
