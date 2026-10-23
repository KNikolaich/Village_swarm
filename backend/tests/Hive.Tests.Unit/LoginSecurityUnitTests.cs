using Hive.Infrastructure.Identity;
using Hive.Modules.Users;

namespace Hive.Tests.Unit;

public sealed class LoginSecurityUnitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fifth_failure_pauses_for_an_hour_and_tenth_for_a_day()
    {
        var t = new LoginThrottle { Key = "ip:1.2.3.4" };
        for (var i = 1; i <= 4; i++)
            Assert.Null(LoginThrottlePolicy.RegisterFailure(t, T0.AddMinutes(i)));
        Assert.Equal(T0.AddMinutes(5).AddHours(1), LoginThrottlePolicy.RegisterFailure(t, T0.AddMinutes(5)));
        Assert.True(LoginThrottlePolicy.IsLocked(t, T0.AddMinutes(30)));

        var later = T0.AddHours(2);
        Assert.False(LoginThrottlePolicy.IsLocked(t, later));
        for (var i = 6; i <= 9; i++)
            Assert.Null(LoginThrottlePolicy.RegisterFailure(t, later.AddMinutes(i)));
        Assert.Equal(later.AddMinutes(10).AddDays(1), LoginThrottlePolicy.RegisterFailure(t, later.AddMinutes(10)));
        Assert.True(LoginThrottlePolicy.IsLocked(t, later.AddHours(23)));
    }

    [Fact]
    public void A_quiet_day_forgets_old_failures()
    {
        var t = new LoginThrottle { Key = "device:x" };
        for (var i = 0; i < 4; i++)
            LoginThrottlePolicy.RegisterFailure(t, T0);
        Assert.Null(LoginThrottlePolicy.RegisterFailure(t, T0.AddDays(1).AddMinutes(1)));
        Assert.Equal(1, t.Failures);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Mobile Safari/537.36", "Chrome · Android")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "Safari · iPhone")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36 Edg/141.0", "Edge · Windows")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 YaBrowser/25.8 Safari/537.36", "Яндекс Браузер · Windows")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:143.0) Gecko/20100101 Firefox/143.0", "Firefox · Linux")]
    [InlineData(null, "Браузер")]
    public void User_agent_reads_as_browser_and_system(string? userAgent, string expected) =>
        Assert.Equal(expected, UserAgents.Describe(userAgent));
}
