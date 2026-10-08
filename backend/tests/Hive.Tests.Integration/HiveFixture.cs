using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Hive.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Hive.Tests.Integration;

/// <summary>PostgreSQL 17 and Mosquitto 2 in containers plus the api host wired to them. Shared by a test collection.</summary>
public sealed class HiveFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("hive")
        .WithUsername("hive")
        .WithPassword("hive")
        .Build();

    private readonly IContainer _mosquitto = new ContainerBuilder("eclipse-mosquitto:2")
        .WithCommand("mosquitto", "-c", "/mosquitto-no-auth.conf")
        .WithPortBinding(1883, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("running"))
        .Build();

    public WebApplicationFactory<Program> App { get; private set; } = null!;

    public string MediaRoot { get; } = Path.Combine(Path.GetTempPath(), $"hive-media-{Guid.NewGuid():N}");

    public string MqttHost => _mosquitto.Hostname;
    public int MqttPort => _mosquitto.GetMappedPublicPort(1883);

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _mosquitto.StartAsync());

        App = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Hive", _postgres.GetConnectionString());
            b.UseSetting("Mqtt:Host", MqttHost);
            b.UseSetting("Mqtt:Port", MqttPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Mqtt:ClientId", $"hive-api-test-{Guid.NewGuid():N}");
            b.UseSetting("Database:MigrateOnStartup", "true");
            b.UseSetting("Media:Root", MediaRoot);
            b.UseSetting("Media:AllowUploadsWithoutToken", "true");
            b.UseSetting("Hive:TimeZone", "Europe/Moscow");
        });
        _ = App.Server; // start the host: migrations, MQTT gateway, ingest pipeline
    }

    public async Task DisposeAsync()
    {
        await App.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _mosquitto.DisposeAsync().AsTask());
        if (Directory.Exists(MediaRoot))
            Directory.Delete(MediaRoot, recursive: true);
    }

    public async Task<T> QueryAsync<T>(Func<HiveDbContext, Task<T>> query)
    {
        using var scope = App.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<HiveDbContext>());
    }

    /// <summary>Polls until <paramref name="condition"/> holds; ingest is asynchronous (batches of up to 1 s).</summary>
    public async Task WaitForAsync(Func<HiveDbContext, Task<bool>> condition, string what, int timeoutS = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutS);
        while (DateTime.UtcNow < deadline)
        {
            if (await QueryAsync(condition))
                return;
            await Task.Delay(250);
        }
        Assert.Fail($"Timed out waiting for: {what}");
    }
}

[CollectionDefinition(Name)]
public sealed class HiveCollection : ICollectionFixture<HiveFixture>
{
    public const string Name = "hive";
}
