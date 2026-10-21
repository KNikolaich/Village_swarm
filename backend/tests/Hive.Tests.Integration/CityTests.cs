using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Hive.Contracts;
using Hive.Contracts.Messages;
using Hive.Domain;
using Hive.Infrastructure.Data;
using Hive.Modules.Cluster;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using Testcontainers.PostgreSql;

namespace Hive.Tests.Integration;

/// <summary>The city node: its own database and broker, its own Telegram bot, told short silence times so the watch can be seen working.</summary>
public sealed class CityFixture : IAsyncLifetime
{
    public const string AdminPassword = "city-admin-password";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").WithDatabase("hive").WithUsername("hive").WithPassword("hive").Build();

    private readonly IContainer _mosquitto = new ContainerBuilder("eclipse-mosquitto:2")
        .WithCommand("mosquitto", "-c", "/mosquitto-no-auth.conf")
        .WithPortBinding(1883, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("running"))
        .Build();

    public WebApplicationFactory<Program> App { get; private set; } = null!;
    public FakeTelegram Telegram { get; private set; } = null!;
    public string MqttHost => _mosquitto.Hostname;
    public int MqttPort => _mosquitto.GetMappedPublicPort(1883);

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _mosquitto.StartAsync());
        Telegram = await FakeTelegram.StartAsync();
        App = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Hive", _postgres.GetConnectionString());
            b.UseSetting("Mqtt:Host", MqttHost);
            b.UseSetting("Mqtt:Port", MqttPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Mqtt:ClientId", $"hive-city-test-{Guid.NewGuid():N}");
            b.UseSetting("Database:MigrateOnStartup", "true");
            b.UseSetting("Media:Root", Path.Combine(Path.GetTempPath(), $"hive-media-{Guid.NewGuid():N}"));
            b.UseSetting("Hive:Node", "city");
            b.UseSetting("Hive:Role", "observer");
            b.UseSetting("Cluster:Enabled", "true");
            b.UseSetting("Cluster:HeartbeatSeconds", "1");
            b.UseSetting("Cluster:HomeSilenceS", "3");
            b.UseSetting("Cluster:HomeAlarmS", "6");
            b.UseSetting("Cluster:TickS", "1");
            b.UseSetting("Auth:BootstrapAdminPassword", AdminPassword);
            b.UseSetting("Auth:AuthRequestsPerMinute", "10000");
            b.UseSetting("Telegram:Token", FakeTelegram.Token);
            b.UseSetting("Telegram:Endpoints:0:BaseUrl", Telegram.BaseUrl);
            b.UseSetting("Telegram:PollTimeoutS", "1");
        });
        _ = App.Server;
    }

    public async Task DisposeAsync()
    {
        await App.DisposeAsync();
        await Telegram.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _mosquitto.DisposeAsync().AsTask());
    }

    public async Task<HttpClient> LoginAsync()
    {
        var client = App.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { login = "admin", password = AdminPassword });
        response.EnsureSuccessStatusCode();
        return client;
    }

    public async Task<T> QueryAsync<T>(Func<HiveDbContext, Task<T>> query)
    {
        using var scope = App.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<HiveDbContext>());
    }

    public async Task WaitForAsync(Func<HiveDbContext, Task<bool>> condition, string what, int timeoutS = 30)
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

/// <summary>Build step 16: the city watches home with heartbeats, alerts through its own bot, reads only, and takes over when hornets reach it directly.</summary>
public sealed class CityTests(CityFixture city) : IClassFixture<CityFixture>
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ContractJson.Options;

    /// <summary>What home's api does: a heartbeat on its topic. Stopped by cancelling.</summary>
    private async Task BeatAsync(IMqttClient home, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var beat = new HeartbeatMessage { Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Node = BrokerNode.Home, Role = HiveRole.Active, Version = "test" };
            await home.PublishStringAsync("vs/v1/hive/home/heartbeat", JsonSerializer.Serialize(beat, Json));
            try
            {
                await Task.Delay(500, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<IMqttClient> ConnectAsync(string clientId)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(city.MqttHost, city.MqttPort).WithClientId(clientId).Build());
        return client;
    }

    private async Task LinkChatAsync(long chatId) => await city.QueryAsync(async db =>
    {
        var userId = await db.Users.Select(u => u.Id).FirstAsync();
        db.TgLinks.Add(new TgLink { UserId = userId, ChatId = chatId, Username = "kirill", LinkedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return 0;
    });

    private static async Task<ClusterDto> StateAsync(HttpClient admin) => (await admin.GetFromJsonAsync<ClusterDto>("/api/cluster"))!;

    private async Task WaitForRoleAsync(HttpClient admin, string role, int timeoutS = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutS);
        while (DateTime.UtcNow < deadline)
        {
            if ((await StateAsync(admin)).Role == role)
                return;
            await Task.Delay(250);
        }
        Assert.Fail($"The node never became {role}: {JsonSerializer.Serialize(await StateAsync(admin))}");
    }

    [Fact]
    public async Task The_city_watches_home_alerts_through_its_own_bot_reads_only_and_takes_over_when_hornets_reach_it()
    {
        var admin = await city.LoginAsync();
        await LinkChatAsync(777001);
        using var homeClient = await ConnectAsync("home-heartbeat");
        using var beating = new CancellationTokenSource();
        var beats = Task.Run(() => BeatAsync(homeClient, beating.Token));

        // home is alive: the city is an observer and says so
        await city.WaitForAsync(async _ => (await StateAsync(admin)).Home is { Phase: "Up", LastHeartbeatAt: not null }, "home heartbeats seen");
        var state = await StateAsync(admin);
        Assert.Equal(("city", "Observer"), (state.Node, state.Role));

        // an observer only reads
        using var refused = await admin.PostAsJsonAsync("/api/zones", new { name = "Не должна появиться", kind = "house" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/zones")).StatusCode);

        // and it stays silent about what home already tells people: an alarm from a hornet reaches the city bridge as a copy
        var spec = new HornetSpec { Id = "guard-city1", Type = "guard-cam", Motion = new MotionSpec { EverySeconds = 0 } };
        var hornet = new VirtualHornet(spec, HornetRole.Create(spec, "no-photos"));
        hornet.Start(DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var link = new MqttHornetLink(new BrokerOptions { Host = city.MqttHost, Port = city.MqttPort });
        await link.ConnectAsync(spec.Id, CancellationToken.None);
        foreach (var m in hornet.OnConnected(DateTimeOffset.UtcNow))
            await link.PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
        await city.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == "guard-city1"), "copy of the hornet in the city");
        var eventId = Ulid.New();
        await link.PublishAsync("vs/v1/dev/guard-city1/event/motion",
            $$"""{"v":1,"id":"{{eventId}}","ts":{{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}},"seq":1,"boot":1,"severity":"alarm","armed":true}""", false, true, CancellationToken.None);
        await city.WaitForAsync(db => db.Events.AnyAsync(e => e.Id == eventId), "copied alarm stored in the city");
        await Task.Delay(2500);
        Assert.DoesNotContain(city.Telegram.Outbox, s => s.Text?.Contains("guard-city1", StringComparison.Ordinal) == true);

        // home goes quiet: a warning, then an alarm, from the city's bot
        await beating.CancelAsync();
        await beats;
        var warn = await city.Telegram.WaitForAsync(s => s.Text?.Contains("Связь с домом потеряна", StringComparison.Ordinal) == true, "the warning about home", 30);
        Assert.Equal(777001, warn.ChatId);
        await city.Telegram.WaitForAsync(s => s.Text?.Contains("Дом не на связи", StringComparison.Ordinal) == true, "the alarm about home", 30);
        var events = await city.QueryAsync(db => db.Events.AsNoTracking().Where(e => e.DeviceId == "hive-home").OrderBy(e => e.Ts).ToListAsync());
        Assert.Equal(["home_offline", "home_offline_long"], events.Select(e => e.Type).ToArray());
        Assert.Equal([EventSeverity.Warn, EventSeverity.Alarm], events.Select(e => e.Severity).ToArray());
        Assert.Equal("Observer", (await StateAsync(admin)).Role);   // nobody speaks to the city directly yet

        // a hornet talks to the city broker directly: the city takes over the alarms and may be written to
        await link.PublishAsync("vs/v1/dev/guard-city1/status", "online", true, true, CancellationToken.None);
        await WaitForRoleAsync(admin, "Standby");
        string? activeNode = null;
        using var watcher = await ConnectAsync("active-watch");
        watcher.ApplicationMessageReceivedAsync += m => { activeNode = m.ApplicationMessage.ConvertPayloadToString(); return Task.CompletedTask; };
        await watcher.SubscribeAsync("vs/v1/hive/active");   // retained: the city said it is in charge
        for (var i = 0; i < 50 && activeNode is null; i++)
            await Task.Delay(100);
        Assert.Contains("\"city\"", activeNode ?? "");
        using var allowed = await admin.PostAsJsonAsync("/api/zones", new { name = "Дача-запасная", kind = "house" });
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);

        // home is back: the city goes back to watching, and the feed says so (info: not worth a message)
        using var resumed = new CancellationTokenSource();
        var again = Task.Run(() => BeatAsync(homeClient, resumed.Token));
        await WaitForRoleAsync(admin, "Observer");
        await city.WaitForAsync(db => db.Events.AnyAsync(e => e.DeviceId == "hive-home" && e.Type == "home_online"), "home back event");
        await resumed.CancelAsync();
        await again;
    }
}
