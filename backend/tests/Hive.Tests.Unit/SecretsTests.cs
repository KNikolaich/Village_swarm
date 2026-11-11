using Hive.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hive.Tests.Unit;

public class SecretsTests
{
    // The database is not touched by Protect/TryUnprotect.
    private static SecretStore Store(IDataProtectionProvider provider) =>
        new(null!, provider, new ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<SecretStore>.Instance);

    [Fact]
    public void Secret_is_encrypted_and_decrypts_back()
    {
        var store = Store(new EphemeralDataProtectionProvider());
        var cipher = store.Protect("123456:bot-token");
        Assert.DoesNotContain("bot-token", cipher);
        Assert.Equal("123456:bot-token", store.TryUnprotect(cipher));
    }

    [Fact]
    public void Lost_keys_give_unreadable_not_an_exception()
    {
        var cipher = Store(new EphemeralDataProtectionProvider()).Protect("secret");
        Assert.Null(Store(new EphemeralDataProtectionProvider()).TryUnprotect(cipher));
    }

    [Fact]
    public void Every_secret_has_a_unique_key_and_config_path()
    {
        Assert.Equal(Secrets.All.Count, Secrets.All.Select(d => d.Key).Distinct().Count());
        Assert.Equal(Secrets.All.Count, Secrets.All.Select(d => d.ConfigPath).Distinct().Count());
        Assert.NotNull(Secrets.Find("telegram.token"));
        Assert.Null(Secrets.Find("pg.password"));
    }
}
