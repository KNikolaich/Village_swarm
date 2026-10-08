using System.Text.RegularExpressions;
using Hive.Contracts;

namespace Hive.Tests.Unit;

public class UlidTests
{
    [Fact]
    public void Matches_contract_pattern_and_encodes_timestamp()
    {
        var time = new DateTimeOffset(2026, 10, 8, 12, 0, 0, 123, TimeSpan.Zero);
        var id = Ulid.New(time);

        Assert.Matches(new Regex("^[0-9A-HJKMNP-TV-Z]{26}$"), id);
        Assert.Equal(time.ToUnixTimeMilliseconds(), Ulid.Timestamp(id));
    }

    [Fact]
    public void Ids_are_unique_and_sort_by_time()
    {
        var earlier = Ulid.New(DateTimeOffset.UnixEpoch.AddDays(1));
        var later = Ulid.New(DateTimeOffset.UnixEpoch.AddDays(2));
        Assert.True(string.CompareOrdinal(earlier, later) < 0);
        Assert.Equal(1000, Enumerable.Range(0, 1000).Select(_ => Ulid.New()).Distinct().Count());
    }
}
