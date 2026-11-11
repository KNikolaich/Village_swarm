using System.Net;

namespace Hive.Tests.Integration;

/// <summary>Restarting the service is for signed-in admins only.</summary>
[Collection(HiveCollection.Name)]
public sealed class RestartTests(HiveFixture hive)
{
    [Fact]
    public async Task Nobody_can_restart_the_hive_without_signing_in()
    {
        using var response = await hive.App.CreateClient().PostAsync("/api/settings/restart", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
