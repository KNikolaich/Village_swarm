using System.Text.Json;
using System.Threading.Channels;
using Hive.Contracts;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Modules.Devices;
using Hive.Modules.Media;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.IpCams;

/// <summary>The snapshot command: frames taken from the NVR.</summary>
public sealed partial class IpCamWatch
{
    /// <summary>The snapshot command (Telegram /photo, "📷 Ещё фото", a rule): one frame stored as a snapshot.</summary>
    public void Run(DeviceCommand command) => _ = Task.Run(() => SnapshotCommandAsync(command));

    private async Task SnapshotCommandAsync(DeviceCommand command)
    {
        NvrRun? run;
        ChannelInfo? info = null;
        lock (_gate)
            run = _runs.FirstOrDefault(r => r.Channels.Values.Any(c => c.DeviceId == command.DeviceId));
        if (run is not null)
            info = run.Channels.Values.First(c => c.DeviceId == command.DeviceId);

        var status = CommandStatus.Error;
        object result;
        try
        {
            if (run is null || info is null)
                result = new { error = "the NVR of this camera is not connected" };
            else
            {
                // The snapshot service keeps its own time (the queue for the NVR included), not the command's ttl.
                var taken = await SnapshotAsync(run, info, SnapshotPurpose.Look, CancellationToken.None);
                if (taken.Shared)
                {
                    status = CommandStatus.Ok; // pressed again while a frame was on its way: that frame answers both
                    result = new { shared = true, via = taken.Via };
                }
                else
                {
                    using var scope = scopes.CreateScope();
                    var stored = await scope.ServiceProvider.GetRequiredService<PhotoIngestService>()
                        .StoreFetchedAsync(command.DeviceId, $"{command.Cid}-1", null, "snapshot", taken.Jpeg, "nvr", CancellationToken.None);
                    status = stored.Status is PhotoIngestStatus.Created or PhotoIngestStatus.Duplicate ? CommandStatus.Ok : CommandStatus.Error;
                    result = new { photo = stored.Media?.Id, via = taken.Via, error = stored.Error };
                }
            }
        }
        catch (Exception ex)
        {
            result = new { error = ex.Message };
            PhotoFailed(command.DeviceId, ex.Message, null, 1);
        }

        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
            var row = await db.Commands.FirstOrDefaultAsync(c => c.Cid == command.Cid);
            if (row is not null)
            {
                row.Status = status;
                row.AckAt = time.GetUtcNow();
                row.AckPayload = JsonSerializer.SerializeToDocument(result);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Snapshot command {Cid} result not saved", command.Cid);
        }
    }

    /// <summary>A frame of the channel, cgi or RTSP as its settings say; the way that worked is remembered for next time.</summary>
    private async Task<SnapshotTaken> SnapshotAsync(NvrRun run, ChannelInfo info, SnapshotPurpose purpose, CancellationToken ct)
    {
        var taken = await snapshots.TakeAsync(run.Client.Endpoint,
            new SnapshotTarget(run.Id, info.DeviceId, info.Channel, info.Source, info.Via, info.Quality), purpose, ct);
        info.Via = taken.Via;
        return taken;
    }

    /// <summary>
    /// A missing frame in the event feed (info: no Telegram), so it is seen where people look, not only in the journal.
    /// One per motion or per button press, however many frames were missing.
    /// </summary>
    private void PhotoFailed(string deviceId, string reason, string? eventId, int missing)
    {
        var now = time.GetUtcNow();
        ingest.Enqueue(new DeviceEventReceived(deviceId, Ulid.New(now), now, now, "photo_failed", EventSeverity.Info, null,
            JsonSerializer.SerializeToDocument(new { reason, eventId, missing, via = "nvr" })));
    }
}
