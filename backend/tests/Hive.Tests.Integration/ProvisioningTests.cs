using System.Net;
using System.Net.Http.Json;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Hive.Domain;
using Hive.Infrastructure.Data;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using Testcontainers.PostgreSql;

namespace Hive.Tests.Integration;

/// <summary>A hive whose broker runs Mosquitto dynamic security, like production (spec 4.1).</summary>
public sealed class DynSecFixture : IAsyncLifetime
{
    public const string AdminPassword = "dynsec-admin-password";
    private const string BrokerConfig = """
        listener 1883
        allow_anonymous false
        plugin /usr/lib/mosquitto_dynamic_security.so
        plugin_opt_config_file /mosquitto/data/dynsec.json
        """;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    private readonly IContainer _mosquitto = new ContainerBuilder("eclipse-mosquitto:2")
        .WithResourceMapping(Encoding.UTF8.GetBytes(BrokerConfig), "/mosquitto/config/dynsec.conf")
        .WithEntrypoint("sh", "-c",
            $"mosquitto_ctrl dynsec init /mosquitto/data/dynsec.json admin {AdminPassword} >/dev/null " +
            "&& chown mosquitto:mosquitto /mosquitto/data/dynsec.json && exec mosquitto -c /mosquitto/config/dynsec.conf")
        .WithPortBinding(1883, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("running"))
        .Build();

    public WebApplicationFactory<Program> App { get; private set; } = null!;
    public BrokerOptions Broker => new() { Host = _mosquitto.Hostname, Port = _mosquitto.GetMappedPublicPort(1883) };

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _mosquitto.StartAsync());
        App = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Hive", _postgres.GetConnectionString());
            b.UseSetting("Mqtt:Host", Broker.Host);
            b.UseSetting("Mqtt:Port", Broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Mqtt:Username", "admin");
            b.UseSetting("Mqtt:Password", AdminPassword);
            b.UseSetting("Mqtt:ClientId", "hive-api-dynsec");
            b.UseSetting("Mqtt:DynamicSecurity", "true");
            b.UseSetting("Telegram:Token", ""); // never the developer's real bot from deploy/.env
            b.UseSetting("Provisioning:MqttHost", Broker.Host);
            b.UseSetting("Provisioning:MqttPort", Broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Media:Root", Path.Combine(Path.GetTempPath(), $"hive-media-{Guid.NewGuid():N}"));
            b.UseSetting("Media:AllowUploadsWithoutToken", "false");
            b.UseSetting("Auth:BootstrapAdminPassword", HiveFixture.AdminPassword);
            b.UseSetting("Auth:AuthRequestsPerMinute", "10000");
        });
        _ = App.Server;
    }

    public async Task DisposeAsync()
    {
        await App.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _mosquitto.DisposeAsync().AsTask());
    }

    public async Task<SimProvisioner> AdminAsync()
    {
        var provisioner = new SimProvisioner(App.CreateClient());
        await provisioner.LoginAsync("admin", HiveFixture.AdminPassword, CancellationToken.None);
        return provisioner;
    }

    public async Task WaitForAsync(Func<HiveDbContext, Task<bool>> condition, string what, int timeoutS = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutS);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = App.Services.CreateScope();
            if (await condition(scope.ServiceProvider.GetRequiredService<HiveDbContext>()))
                return;
            await Task.Delay(250);
        }
        Assert.Fail($"Timed out waiting for: {what}");
    }

    public async Task<T> QueryAsync<T>(Func<HiveDbContext, Task<T>> query)
    {
        using var scope = App.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<HiveDbContext>());
    }
}

/// <summary>Build step 10: enrollment codes, per-hornet broker logins with ACLs, revocation.</summary>
public sealed class ProvisioningTests(DynSecFixture hive) : IClassFixture<DynSecFixture>
{
    private static HornetSpec Spec(string id) =>
        new() { Id = id, Type = "guard-cam", Motion = new MotionSpec { EverySeconds = 0 } };

    private async Task<(VirtualHornet Hornet, MqttHornetLink Link)> ConnectAsync(HornetSpec spec, Enrollment e)
    {
        var hornet = new VirtualHornet(spec, HornetRole.Create(spec, "no-photos"));
        hornet.Start(DateTimeOffset.UtcNow.AddMinutes(-10));
        var link = new MqttHornetLink(hive.Broker, e.MqttUser, e.MqttPass);
        await link.ConnectAsync(spec.Id, CancellationToken.None);
        foreach (var m in hornet.OnConnected(DateTimeOffset.UtcNow))
            await link.PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
        return (hornet, link);
    }

    [Fact]
    public async Task Enrolled_hornet_connects_with_its_own_login_and_goes_online()
    {
        var admin = await hive.AdminAsync();
        var e = await admin.EnrollAsync(Spec("guard-prov1"), CancellationToken.None);
        Assert.Equal("guard-prov1", e.MqttUser);
        Assert.False(string.IsNullOrEmpty(e.MqttPass));

        var (_, link) = await ConnectAsync(Spec("guard-prov1"), e);
        await using var _ = link;
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == "guard-prov1" && d.Status == DeviceStatus.Online && d.Name == "Симулятор guard-prov1"), "provisioned hornet online");
        var device = await hive.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == "guard-prov1"));
        Assert.NotNull(device.UploadTokenHash);
        Assert.Equal(1, device.ConfigRev);
    }

    [Fact]
    public async Task Code_works_once()
    {
        var client = hive.App.CreateClient();
        await new SimProvisioner(client).LoginAsync("admin", HiveFixture.AdminPassword, CancellationToken.None);
        var code = await (await client.PostAsJsonAsync("/api/provision/codes", new { deviceId = "guard-once1", type = "guard-cam", name = "Once" }))
            .Content.ReadFromJsonAsync<Hive.Modules.Devices.EnrollmentCodeDto>();
        var anonymous = hive.App.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/provision/enroll", new { code = code!.Code, mac = "02:00:00:00:00:01" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostAsJsonAsync("/api/provision/enroll", new { code = code.Code, mac = "02:00:00:00:00:02" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostAsJsonAsync("/api/provision/enroll", new { code = "NOPE2345", mac = "02:00:00:00:00:03" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/provision/codes", new { deviceId = "Bad Id", type = "guard-cam", name = "x" })).StatusCode);
    }

    [Fact]
    public async Task Anonymous_and_wrong_password_are_refused_by_the_broker()
    {
        await using var anonymous = new MqttHornetLink(hive.Broker);
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.ConnectAsync("guard-anon1", CancellationToken.None));
        await using var wrong = new MqttHornetLink(hive.Broker, "guard-prov1", "wrong");
        await Assert.ThrowsAnyAsync<Exception>(() => wrong.ConnectAsync("guard-prov1", CancellationToken.None));
    }

    [Fact]
    public async Task Hornet_cannot_write_another_hornets_topics_or_its_own_config()
    {
        var admin = await hive.AdminAsync();
        var a = await admin.EnrollAsync(Spec("guard-acl-a"), CancellationToken.None);
        await admin.EnrollAsync(Spec("guard-acl-b"), CancellationToken.None);
        var (_, link) = await ConnectAsync(Spec("guard-acl-a"), a);
        await using var _ = link;

        // Watch the broker as the api would, with the admin login.
        var watcher = new MqttClientFactory().CreateMqttClient();
        var seen = new List<string>();
        watcher.ApplicationMessageReceivedAsync += m =>
        {
            // Topic plus payload: the hive's own retained config for A is legitimately there.
            lock (seen) seen.Add($"{m.ApplicationMessage.Topic} {m.ApplicationMessage.ConvertPayloadToString()}");
            return Task.CompletedTask;
        };
        await watcher.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.Broker.Host, hive.Broker.Port)
            .WithCredentials("admin", DynSecFixture.AdminPassword).WithClientId("acl-watcher").Build());
        await watcher.SubscribeAsync("vs/v1/dev/+/#");

        // Spoofing: A pretends to be B, and tries to rewrite its own config and send itself a command.
        await link.PublishAsync("vs/v1/dev/guard-acl-b/event/motion", """{"v":1}""", false, true, CancellationToken.None);
        await link.PublishAsync("vs/v1/dev/guard-acl-a/config", """{"v":1}""", false, true, CancellationToken.None);
        await link.PublishAsync("vs/v1/dev/guard-acl-a/cmd/reboot", """{"v":1}""", false, true, CancellationToken.None);
        await link.PublishAsync("vs/v1/dev/guard-acl-a/log", """{"probe":true}""", false, true, CancellationToken.None);
        const string spoof = """{"v":1}""";

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !Contains(seen, """vs/v1/dev/guard-acl-a/log {"probe":true}"""))
            await Task.Delay(100);
        Assert.True(Contains(seen, """vs/v1/dev/guard-acl-a/log {"probe":true}"""), "own topic is allowed");
        Assert.False(Contains(seen, $"vs/v1/dev/guard-acl-b/event/motion {spoof}"));
        Assert.False(Contains(seen, $"vs/v1/dev/guard-acl-a/config {spoof}"));
        Assert.False(Contains(seen, $"vs/v1/dev/guard-acl-a/cmd/reboot {spoof}"));
        watcher.Dispose();
    }

    [Fact]
    public async Task Removed_hornet_loses_its_login_and_replacement_gets_a_new_one()
    {
        var admin = await hive.AdminAsync();
        var first = await admin.EnrollAsync(Spec("guard-rm1"), CancellationToken.None);
        var second = await admin.EnrollAsync(Spec("guard-rm1"), CancellationToken.None); // "Заменить": same id, new board
        Assert.NotEqual(first.MqttPass, second.MqttPass);
        await using (var old = new MqttHornetLink(hive.Broker, first.MqttUser, first.MqttPass))
            await Assert.ThrowsAnyAsync<Exception>(() => old.ConnectAsync("guard-rm1", CancellationToken.None));

        await admin.RemoveAsync("guard-rm1", CancellationToken.None);
        Assert.Equal(DeviceStatus.Disabled, await hive.QueryAsync(db => db.Devices.Where(d => d.DeviceId == "guard-rm1").Select(d => d.Status).SingleAsync()));
        await using var revoked = new MqttHornetLink(hive.Broker, second.MqttUser, second.MqttPass);
        await Assert.ThrowsAnyAsync<Exception>(() => revoked.ConnectAsync("guard-rm1", CancellationToken.None));
    }

    [Fact]
    public async Task Photo_upload_needs_the_enrolled_token()
    {
        var admin = await hive.AdminAsync();
        var e = await admin.EnrollAsync(Spec("guard-tok2"), CancellationToken.None);
        var jpeg = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "scenarios", "photos", "frame0.jpg"));
        var upload = new PhotoUpload($"{Hive.Contracts.Ulid.New()}-0", Hive.Contracts.Ulid.New(), "motion", 0, jpeg, "http://localhost");
        var http = hive.App.CreateClient();
        Assert.Equal(UploadOutcome.Retry, (await PhotoUploader.UploadAsync(http, "guard-tok2", upload, token: null, CancellationToken.None)).Outcome);
        Assert.Equal(UploadOutcome.Uploaded, (await PhotoUploader.UploadAsync(http, "guard-tok2", upload, e.UploadToken, CancellationToken.None)).Outcome);
    }

    private static bool Contains(List<string> seen, string topic)
    {
        lock (seen)
            return seen.Contains(topic);
    }
}
