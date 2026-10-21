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

        // a person's own settings stay open on an observer: the password (a wrong current one is answered as a validation error, not "403"),
        // and linking a Telegram chat to the city's bot
        using var ownPassword = await admin.PostAsJsonAsync("/api/me/password", new { currentPassword = "not-my-password", newPassword = "Another-pass-12345" });
        Assert.NotEqual(HttpStatusCode.Forbidden, ownPassword.StatusCode);
        using var linkCode = await admin.PostAsync("/api/me/telegram/code", null);
        Assert.NotEqual(HttpStatusCode.Forbidden, linkCode.StatusCode);

        // so do the node's own weather and language settings (never copied from home), while the hornets' Wi-Fi is home's business
        using var weather = await admin.PutAsJsonAsync("/api/settings/weather", new { });
        Assert.NotEqual(HttpStatusCode.Forbidden, weather.StatusCode);
        using var language = await admin.PutAsJsonAsync("/api/settings/language", new { language = "ru" });
        Assert.NotEqual(HttpStatusCode.Forbidden, language.StatusCode);
        using var wifi = await admin.PutAsJsonAsync("/api/settings/wifi", new { });
        Assert.Equal(HttpStatusCode.Forbidden, wifi.StatusCode);

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
        var stoppedAt = DateTimeOffset.UtcNow;   // earlier tests of this class may have left events of their own
        var warn = await city.Telegram.WaitForAsync(s => s.Text?.Contains("Связь с домом потеряна", StringComparison.Ordinal) == true, "the warning about home", 30);
        Assert.Equal(777001, warn.ChatId);
        await city.Telegram.WaitForAsync(s => s.Text?.Contains("Дом не на связи", StringComparison.Ordinal) == true, "the alarm about home", 30);
        var events = await city.QueryAsync(db => db.Events.AsNoTracking().Where(e => e.DeviceId == "hive-home" && e.Ts >= stoppedAt).OrderBy(e => e.Ts).ToListAsync());
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

    private async Task PublishCatalogAsync(CatalogSnapshot snapshot)
    {
        using var bridge = await ConnectAsync($"home-bridge-{Guid.NewGuid():N}");
        await bridge.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(CatalogCodec.Topic).WithPayload(CatalogCodec.Pack(snapshot)).WithRetainFlag(true).Build());
    }

    private static CatalogRule NotifyRule(string name, bool enabled = true) => new(name, enabled, JsonSerializer.SerializeToElement(
        new Hive.Modules.Rules.RuleDefinition(new Hive.Modules.Rules.RuleTrigger("telemetry", Device: "meteo-sync1", Metric: "temperature"), [],
            [new Hive.Modules.Rules.RuleAction("notify", Level: "info", Text: "тест")]), new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    [Fact]
    public async Task The_catalog_of_home_is_applied_here_users_sign_in_with_the_same_password_and_nothing_of_the_cities_own_is_lost()
    {
        var hash = new Microsoft.AspNetCore.Identity.PasswordHasher<Hive.Infrastructure.Identity.HiveUser>().HashPassword(null!, "sync-pass-12345");
        var admin = await city.LoginAsync();

        // a rule that exists only here (made by hand) must survive every snapshot
        await city.QueryAsync(async db =>
        {
            db.Rules.Add(new Rule { Name = "Только в городе", Enabled = true, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "user:admin", Definition = JsonSerializer.SerializeToDocument(new { }) });
            await db.SaveChangesAsync();
            return 0;
        });

        var first = new CatalogSnapshot(1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "home",
            [new CatalogZone("Гостиная-синк", "House", 5)],
            [new CatalogDevice("meteo-sync1", "meteo", "Термометр в гостиной", "Гостиная-синк", "hornet", 3, false, null),
             new CatalogDevice("heat-sync-gone", "heat", "Старый обогрев", null, "hornet", 0, true, null)],
            [NotifyRule("Синк-правило"), NotifyRule("Синк-правило-2")],
            [new CatalogUser("synced-user", "Мама", hash, ["viewer"])]);
        await PublishCatalogAsync(first);

        await city.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == "meteo-sync1"), "device from the catalog");
        var device = await city.QueryAsync(db => db.Devices.AsNoTracking().Include(d => d.Zone).SingleAsync(d => d.DeviceId == "meteo-sync1"));
        Assert.Equal(("Термометр в гостиной", "Гостиная-синк", 3), (device.Name, device.Zone?.Name, device.Sort));
        Assert.Equal(DeviceStatus.Disabled, (await city.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == "heat-sync-gone"))).Status);
        Assert.Equal(2, await city.QueryAsync(db => db.Rules.CountAsync(r => r.UpdatedBy == "sync:home")));

        // the user home knows signs in here with the same password, with the role home gave
        var client = city.App.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { login = "synced-user", password = "sync-pass-12345" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Contains("viewer", me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        using var wrong = await city.App.CreateClient().PostAsJsonAsync("/api/auth/login", new { login = "synced-user", password = "not-the-password" });
        Assert.NotEqual(HttpStatusCode.OK, wrong.StatusCode);

        // the cluster state says when the catalog came and how big it was
        var state = await StateAsync(admin);
        Assert.NotNull(state.CatalogAt);
        Assert.Equal(new CatalogCounts(1, 2, 2, 1), state.Catalog);

        // a later snapshot: renamed device, one rule gone, the password changed and the role promoted
        var hash2 = new Microsoft.AspNetCore.Identity.PasswordHasher<Hive.Infrastructure.Identity.HiveUser>().HashPassword(null!, "second-pass-67890");
        var second = first with
        {
            Ts = first.Ts + 1,
            Devices = [first.Devices[0] with { Name = "Термометр (переименован)" }, first.Devices[1] with { Disabled = false }],
            Rules = [NotifyRule("Синк-правило", enabled: false)],
            Users = [new CatalogUser("synced-user", "Мама", hash2, ["member"])],
        };
        await PublishCatalogAsync(second);
        await city.WaitForAsync(async db => (await db.Devices.SingleAsync(d => d.DeviceId == "meteo-sync1")).Name == "Термометр (переименован)", "renamed device");
        Assert.NotEqual(DeviceStatus.Disabled, (await city.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == "heat-sync-gone"))).Status);
        var rules = await city.QueryAsync(db => db.Rules.AsNoTracking().OrderBy(r => r.Name).ToListAsync());
        Assert.Equal(["Синк-правило", "Только в городе"], rules.Select(r => r.Name).ToArray());   // the one home dropped is gone, the city's own stays
        Assert.False(rules.Single(r => r.Name == "Синк-правило").Enabled);
        using var oldPassword = await city.App.CreateClient().PostAsJsonAsync("/api/auth/login", new { login = "synced-user", password = "sync-pass-12345" });
        Assert.NotEqual(HttpStatusCode.OK, oldPassword.StatusCode);
        using var newPassword = await city.App.CreateClient().PostAsJsonAsync("/api/auth/login", new { login = "synced-user", password = "second-pass-67890" });
        Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);

        // a snapshot that is older than the one applied is ignored
        await PublishCatalogAsync(first with { Ts = first.Ts - 5, Devices = [first.Devices[0] with { Name = "Старое имя" }] });
        await Task.Delay(1500);
        Assert.Equal("Термометр (переименован)", (await city.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == "meteo-sync1"))).Name);
    }

    [Fact]
    public async Task What_the_catalog_builder_reads_is_what_the_applier_needs()
    {
        await city.QueryAsync(async db =>
        {
            db.Zones.Add(new Zone { Name = "Зона-строителя", Kind = ZoneKind.Garden, Sort = 2 });
            await db.SaveChangesAsync();
            return 0;
        });
        using var scope = city.App.Services.CreateScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<CatalogBuilder>().BuildAsync(CancellationToken.None);
        Assert.Contains(snapshot.Zones, z => z is { Name: "Зона-строителя", Kind: "Garden", Sort: 2 });
        Assert.Contains(snapshot.Users, u => u.UserName == "admin" && u.Roles.Contains("admin") && !string.IsNullOrEmpty(u.PasswordHash));

        // packed and unpacked it is the same, and the fingerprint ignores the time
        var again = CatalogCodec.Unpack(CatalogCodec.Pack(snapshot))!;
        Assert.Equal(snapshot.Zones.Count, again.Zones.Count);
        Assert.Equal(CatalogCodec.Fingerprint(snapshot), CatalogCodec.Fingerprint(snapshot with { Ts = snapshot.Ts + 1000 }));
        Assert.NotEqual(CatalogCodec.Fingerprint(snapshot), CatalogCodec.Fingerprint(snapshot with { Zones = [] }));
    }

    [Fact]
    public async Task Home_publishes_the_catalog_when_it_changes_and_not_otherwise()
    {
        var published = new List<CatalogSnapshot>();
        using var listener = await ConnectAsync($"catalog-listener-{Guid.NewGuid():N}");
        listener.ApplicationMessageReceivedAsync += m =>
        {
            if (m.ApplicationMessage.Topic == CatalogCodec.Topic && CatalogCodec.Unpack(System.Buffers.BuffersExtensions.ToArray(m.ApplicationMessage.Payload)) is { } snapshot)
                lock (published) published.Add(snapshot);
            return Task.CompletedTask;
        };
        await listener.SubscribeAsync(CatalogCodec.Topic);
        int Count() { lock (published) return published.Count; }
        var before = Count();

        var publisher = new CatalogPublisher(
            Microsoft.Extensions.Options.Options.Create(new ClusterOptions { Enabled = true, CatalogEveryS = 3600 }),
            Microsoft.Extensions.Options.Options.Create(new Hive.Infrastructure.HiveOptions { Node = "home" }),
            city.App.Services.GetRequiredService<Hive.Infrastructure.Mqtt.MqttGateway>(),
            city.App.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CatalogPublisher>.Instance);

        await publisher.PublishIfNeededAsync(CancellationToken.None);
        for (var i = 0; i < 50 && Count() == before; i++)
            await Task.Delay(100);
        Assert.Equal(before + 1, Count());
        Assert.Equal(1, published[^1].V);   // the snapshot says which node built it; this test app is the city, so it is not checked

        await publisher.PublishIfNeededAsync(CancellationToken.None);   // nothing changed: nothing sent
        await Task.Delay(800);
        Assert.Equal(before + 1, Count());

        await city.QueryAsync(async db =>
        {
            db.Zones.Add(new Zone { Name = $"Новая-зона-{Guid.NewGuid():N}", Kind = ZoneKind.Garden, Sort = 9 });
            await db.SaveChangesAsync();
            return 0;
        });
        await publisher.PublishIfNeededAsync(CancellationToken.None);   // a zone was added: sent again
        for (var i = 0; i < 50 && Count() == before + 1; i++)
            await Task.Delay(100);
        Assert.Equal(before + 2, Count());
    }
}
