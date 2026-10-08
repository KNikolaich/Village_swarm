using Hive.Domain;
using Hive.Modules.Events;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Hive.Api.Live;

public sealed record LiveTelemetry(string DeviceId, string Metric, DateTimeOffset Ts, double Value);

public sealed record LiveDeviceStatus(string DeviceId, string Status, DateTimeOffset At);

public sealed record LiveDeviceHealth(string DeviceId, DateTimeOffset Ts, int Rssi, long UptimeS, long HeapFree, double? Vbat);

/// <summary>Messages the server pushes on /hubs/live (spec 6.5). Method names are the camelCase member names.</summary>
public interface ILiveClient
{
    Task Event(EventDto e);
    Task Telemetry(LiveTelemetry t);
    Task DeviceStatus(LiveDeviceStatus s);
    Task DeviceHealth(LiveDeviceHealth h);
}

/// <summary>Server-to-client only for now; commands go through REST.</summary>
[Authorize]
public sealed class LiveHub : Hub<ILiveClient>
{
    public const string Path = "/hubs/live";
}

/// <summary>Pushes freshly ingested data to the UI after each committed batch.</summary>
public sealed class LiveNotifier(IHubContext<LiveHub, ILiveClient> hub, IServiceScopeFactory scopes) : IIngestListener
{
    public async Task OnIngestedAsync(IReadOnlyList<IngestItem> items, CancellationToken ct)
    {
        var clients = hub.Clients.All;
        var events = items.OfType<DeviceEventReceived>().ToList();
        if (events.Count > 0)
        {
            // Full DTO with device name, zone and photo links, the same shape as GET /api/events.
            using var scope = scopes.CreateScope();
            var queries = scope.ServiceProvider.GetRequiredService<EventQueries>();
            foreach (var e in events)
                if (await queries.GetAsync(e.MessageId!, ct) is { } dto)
                    await clients.Event(dto);
        }

        foreach (var item in items)
        {
            switch (item)
            {
                case TelemetryReceived t:
                    foreach (var (metric, value) in t.Values)
                        await clients.Telemetry(new LiveTelemetry(t.DeviceId, metric, t.Ts, value));
                    break;
                case DeviceStatusChanged s:
                    await clients.DeviceStatus(new LiveDeviceStatus(s.DeviceId, s.Online ? "online" : "offline", s.ReceivedAt));
                    break;
                case DeviceHealthReceived h:
                    await clients.DeviceHealth(new LiveDeviceHealth(h.DeviceId, h.Ts, h.Rssi, h.UptimeS, h.HeapFree, h.Vbat));
                    break;
            }
        }
    }
}
