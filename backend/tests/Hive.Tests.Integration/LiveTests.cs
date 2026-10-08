using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Hive.Api.Live;
using Hive.Modules.Events;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace Hive.Tests.Integration;

/// <summary>Live updates over SignalR /hubs/live (build step 6 acceptance: "live events arrive in a test client").</summary>
[Collection(HiveCollection.Name)]
public sealed class LiveTests(HiveFixture hive)
{
    private async Task<string> SessionCookieAsync()
    {
        var client = hive.App.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { login = "admin", password = HiveFixture.AdminPassword });
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("hive_session=", StringComparison.Ordinal)).Split(';')[0];
    }

    private HubConnection Connect(string? cookie) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(hive.App.Server.BaseAddress, LiveHub.Path), o =>
            {
                o.Transports = HttpTransportType.LongPolling; // TestServer has no WebSockets
                o.HttpMessageHandlerFactory = _ => new CookieHandler(hive.App.Server.CreateHandler(), cookie);
            })
            .Build();

    [Fact]
    public async Task Motion_event_and_telemetry_arrive_live()
    {
        var events = Channel.CreateUnbounded<EventDto>();
        var telemetry = Channel.CreateUnbounded<LiveTelemetry>();
        await using var connection = Connect(await SessionCookieAsync());
        connection.On<EventDto>("Event", e => events.Writer.TryWrite(e));
        connection.On<LiveTelemetry>("Telemetry", t => telemetry.Writer.TryWrite(t));
        await connection.StartAsync();

        var spec = new HornetSpec { Id = "guard-live1", Type = "guard-cam", Motion = new MotionSpec { EverySeconds = 0 } };
        var role = (GuardCamRole)HornetRole.Create(spec, "no-photos");
        var hornet = new VirtualHornet(spec, role);
        hornet.Start(DateTimeOffset.UtcNow.AddMinutes(-10));
        await using var link = new MqttHornetLink(new BrokerOptions { Host = hive.MqttHost, Port = hive.MqttPort });
        await link.ConnectAsync(spec.Id, CancellationToken.None);
        foreach (var m in hornet.OnConnected(DateTimeOffset.UtcNow))
            await link.PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);

        role.TriggerMotion(DateTimeOffset.UtcNow);
        var motion = hornet.TakePending(connected: true).Single(m => m.Topic.EndsWith("/event/motion", StringComparison.Ordinal));
        await link.PublishAsync(motion.Topic, motion.Payload, false, true, CancellationToken.None);
        await link.PublishAsync("vs/v1/dev/guard-live1/tele/temperature",
            $$"""{"v":1,"id":"{{Hive.Contracts.Ulid.New()}}","ts":0,"seq":99,"boot":1,"value":21.5}""", false, false, CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        EventDto live;
        do
            live = await events.Reader.ReadAsync(timeout.Token);
        while (live.DeviceId != "guard-live1");
        Assert.Equal(JsonDocument.Parse(motion.Payload).RootElement.GetProperty("id").GetString(), live.Id);
        Assert.Equal(("motion", "alarm"), (live.Type, live.Severity));

        LiveTelemetry t;
        do
            t = await telemetry.Reader.ReadAsync(timeout.Token);
        while (t.DeviceId != "guard-live1");
        Assert.Equal(("temperature", 21.5), (t.Metric, t.Value));
    }

    [Fact]
    public async Task Hub_rejects_anonymous_clients()
    {
        await using var connection = Connect(cookie: null);
        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    private sealed class CookieHandler(HttpMessageHandler inner, string? cookie) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (cookie is not null)
                request.Headers.Add("Cookie", cookie);
            return base.SendAsync(request, ct);
        }
    }
}
