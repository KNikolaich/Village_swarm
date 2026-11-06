using System.Security.Cryptography;
using System.Text;

namespace Hive.Infrastructure.Mqtt;

/// <summary>The secret home and the city share (<c>Cluster__BridgePassword</c>, CITY_MQTT_PASSWORD in the .env of both).</summary>
public sealed class CityBrokerOptions
{
    public string? BridgePassword { get; set; }
}

/// <summary>
/// The login a hornet has on the CITY broker (spec 4.5, 11.3). Nothing is sent between the two nodes for it: the password is derived
/// from the shared secret and the hornet's id, so home can tell the hornet (in its config) and the city can create the login.
/// </summary>
public static class CityBrokerLogin
{
    public static string Password(string secret, string deviceId)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"hornet-city:{deviceId}"));
        return Convert.ToBase64String(mac).Replace('+', '-').Replace('/', '_').TrimEnd('=')[..32];
    }
}
