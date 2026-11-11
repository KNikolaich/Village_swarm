using Hive.Modules.Devices;

namespace Hive.Tests.Unit.Cluster;

/// <summary>The fingerprint typed in Settings: accepted in the usual spellings, refused when it is not a SHA-256.</summary>
public class FailoverPinSettingTests
{
    private const string Hex = "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899";

    [Fact]
    public void The_fingerprint_is_stored_as_plain_lower_case_hex()
    {
        Assert.Equal(Hex, FailoverService.Fingerprint(string.Join(":", Enumerable.Range(0, 32).Select(i => Hex.Substring(i * 2, 2).ToUpperInvariant()))));
        Assert.Null(FailoverService.Fingerprint("abc"));
        Assert.Null(FailoverService.Fingerprint(null));
    }

    [Fact]
    public void A_bad_fingerprint_is_refused_and_an_empty_one_is_fine()
    {
        Assert.Null(FailoverService.Validate(new FailoverDto("192.168.1.3", 1883, null, Hex)));
        Assert.Null(FailoverService.Validate(new FailoverDto("192.168.1.3", 1883, null, "")));
        Assert.Contains("certSha256", FailoverService.Validate(new FailoverDto("192.168.1.3", 1883, null, "12345")));
    }
}
