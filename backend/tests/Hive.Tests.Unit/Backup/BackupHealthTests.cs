using Hive.Modules.Backup;

namespace Hive.Tests.Unit.Backup;

public sealed class BackupHealthTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00+03:00");
    private static readonly TimeSpan Limit = TimeSpan.FromHours(36);

    private static BackupRunDto Run(string kind, bool ok, double hoursAgo = 1) =>
        new(1, kind, Now.AddHours(-hoursAgo), Now.AddHours(-hoursAgo), ok, 1000, ok ? null : "boom");

    private static IReadOnlyList<string> Judge(IReadOnlyList<BackupRunDto> latest, DateTimeOffset? lastGood, double uptimeHours = 1000) =>
        BackupHealth.Evaluate(latest, lastGood, Now, TimeSpan.FromHours(uptimeHours), Limit);

    [Fact]
    public void A_fresh_dump_and_a_good_verification_is_all_well()
    {
        Assert.Empty(Judge([Run("dump", true, 5), Run("verify", true, 50)], Now.AddHours(-5)));
    }

    [Fact]
    public void A_dump_older_than_the_limit_is_stale()
    {
        Assert.Equal(["dump_stale"], Judge([Run("dump", true, 40)], Now.AddHours(-40)));
        Assert.Empty(Judge([Run("dump", true, 35)], Now.AddHours(-35)));
    }

    [Fact]
    public void A_failed_last_dump_is_a_problem_even_when_an_older_one_is_fresh()
    {
        Assert.Equal(["dump_failed"], Judge([Run("dump", false, 1)], Now.AddHours(-20)));
    }

    [Fact]
    public void No_runs_at_all_is_a_problem_only_once_the_hive_has_been_up_long_enough()
    {
        Assert.Empty(Judge([], null, uptimeHours: 2));
        Assert.Equal(["dump_stale"], Judge([], null, uptimeHours: 40));
    }

    [Fact]
    public void Off_site_and_verification_failures_are_reported_on_their_own()
    {
        var problems = Judge([Run("dump", true, 2), Run("offsite", false, 2), Run("verify", false, 30)], Now.AddHours(-2));
        Assert.Equal(["offsite_failed", "verify_failed"], problems);
    }
}
