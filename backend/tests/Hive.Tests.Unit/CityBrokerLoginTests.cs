using Hive.Infrastructure.Mqtt;

namespace Hive.Tests.Unit;

public class CityBrokerLoginTests
{
    [Fact]
    public void The_same_secret_and_id_give_the_same_password_on_both_nodes_and_every_hornet_has_its_own()
    {
        var a = CityBrokerLogin.Password("secret", "meteo-out1");
        Assert.Equal(a, CityBrokerLogin.Password("secret", "meteo-out1"));
        Assert.NotEqual(a, CityBrokerLogin.Password("secret", "meteo-out2"));
        Assert.NotEqual(a, CityBrokerLogin.Password("other-secret", "meteo-out1"));
        Assert.Equal(32, a.Length);
        Assert.Matches("^[A-Za-z0-9_-]{32}$", a);   // fits the config and an MQTT password without quoting
    }
}
