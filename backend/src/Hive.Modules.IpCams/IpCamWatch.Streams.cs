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

/// <summary>Event streams of the NVRs: Dahua stream, ONVIF, motion handling.</summary>
public sealed partial class IpCamWatch
{
    /// <summary>One NVR: open the stream, read it until it breaks, wait (2, 4 … 60 s), again.</summary>
    private async Task RunStreamAsync(NvrRun run, CancellationToken ct)
    {
        if (run.Vendor == CameraVendors.Onvif)
        {
            await RunOnvifAsync(run, ct);
            return;
        }
        var backoff = TimeSpan.FromSeconds(2);
        var stale = TimeSpan.FromSeconds(Math.Max(5, options.Value.StaleStreamS));
        while (!ct.IsCancellationRequested)
        {
            // A locked or refused login: nothing goes to the NVR (the stream's reconnects would keep it locked) until it may.
            if (logins.Blocked(run.Client.Endpoint) is { } block)
            {
                run.Connected = false;
                var wait = block.Until is { } until ? until - time.GetUtcNow() : TimeSpan.FromSeconds(30);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 1, 30)), time, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }
            try
            {
                using var response = await run.Client.AttachAsync(ct);
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                lock (run.Lost)
                    run.Lost.Clear(); // the NVR repeats the losses that still last
                run.Alive(time.GetUtcNow(), connected: true);
                run.Opened(time.GetUtcNow());
                logger.LogInformation("NVR {Nvr}: event stream open", run.Name);
                backoff = TimeSpan.FromSeconds(2);
                while (true)
                {
                    using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    silence.CancelAfter(stale);
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(silence.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning("NVR {Nvr}: nothing for {Seconds} s, reconnecting", run.Name, stale.TotalSeconds);
                        break;
                    }
                    if (line is null)
                    {
                        logger.LogWarning("NVR {Nvr}: event stream closed", run.Name);
                        break;
                    }
                    run.Alive(time.GetUtcNow(), connected: true);
                    if (DahuaEventParser.ParseLine(line) is { IsHeartbeat: false } e)
                        Handle(run, e, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (NvrAuthException ex) when (ex.Block is not null || ex.Error is { Locked: true } or { Status: 401 })
            {
                run.LastError = ex.Message; // NvrLogins has logged it once; the loop waits above
            }
            catch (Exception ex)
            {
                run.LastError = ex.Message;
                logger.LogWarning("NVR {Nvr}: event stream failed: {Error}", run.Name, ex.Message);
            }
            run.Connected = false;
            try
            {
                await Task.Delay(backoff, time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
        }
    }

    /// <summary>
    /// An ONVIF camera: a PullPoint subscription pulled every 10 s (each answer is a sign of life), renewed every minute; motion
    /// is handled as a Dahua VideoMotion of channel 1. Without an event service the camera is only checked for life (snapshots
    /// work, motion does not: the settings page says so).
    /// </summary>
    private async Task RunOnvifAsync(NvrRun run, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        var firstMotion = new DahuaEvent(DahuaEvent.VideoMotion, "Start", 0);
        while (!ct.IsCancellationRequested)
        {
            if (logins.Blocked(run.Client.Endpoint) is { } block)
            {
                run.Connected = false;
                var wait = block.Until is { } until ? until - time.GetUtcNow() : TimeSpan.FromSeconds(30);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 1, 30)), time, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }
            using var onvif = new OnvifClient(run.Client.Endpoint, logins, time);
            string? subscription = null;
            try
            {
                if (run.EventsUrl is null)
                {
                    await onvif.AliveAsync(ct);
                    if (!run.Connected)
                        run.Opened(time.GetUtcNow());
                    run.Alive(time.GetUtcNow(), connected: true);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, options.Value.StaleStreamS)), time, ct);
                    continue;
                }
                subscription = await onvif.SubscribeAsync(run.EventsUrl, ct);
                run.Alive(time.GetUtcNow(), connected: true);
                run.Opened(time.GetUtcNow());
                logger.LogInformation("Camera {Nvr}: ONVIF events subscribed", run.Name);
                backoff = TimeSpan.FromSeconds(2);
                var renewAt = time.GetUtcNow().AddSeconds(60);
                while (!ct.IsCancellationRequested)
                {
                    var events = await onvif.PullAsync(subscription, ct);
                    run.Alive(time.GetUtcNow(), connected: true);
                    if (events.Any(e => e.Motion == true))
                        Handle(run, firstMotion, ct);
                    if (time.GetUtcNow() >= renewAt)
                    {
                        await onvif.RenewAsync(subscription, ct);
                        renewAt = time.GetUtcNow().AddSeconds(60);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (subscription is not null)
                {
                    try
                    {
                        using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await onvif.UnsubscribeAsync(subscription, bye.Token);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or OnvifFaultException or NvrAuthException)
                    {
                    }
                }
                return;
            }
            catch (NvrAuthException ex) when (ex.Block is not null || ex.Error is { Locked: true } or { Status: 401 })
            {
                run.LastError = ex.Message; // NvrLogins has logged it once; the loop waits above
            }
            catch (Exception ex)
            {
                run.LastError = ex.Message;
                logger.LogWarning("Camera {Nvr}: ONVIF {What} failed: {Error}", run.Name, run.EventsUrl is null ? "check" : "events", ex.Message);
            }
            run.Connected = false;
            try
            {
                await Task.Delay(backoff, time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
        }
    }

    private void Handle(NvrRun run, DahuaEvent e, CancellationToken ct)
    {
        if (e.Channel is not { } channel || !run.Channels.TryGetValue(channel, out var info))
            return; // a channel nobody took into the swarm
        var now = time.GetUtcNow();
        switch (e.Code)
        {
            case DahuaEvent.VideoMotion when e.IsStart:
                lock (_lastMotion)
                {
                    diagnostics.Motion(info.DeviceId, now);
                    if (ptz.Quiet(info.DeviceId, now))
                    {
                        // The camera is turning (or just stopped): its picture moves, nobody walked by.
                        logger.LogInformation("NVR {Nvr} channel {Channel} ({Device}): motion while the camera turns, ignored", run.Id, channel, info.DeviceId);
                        return;
                    }
                    var cooling = _lastMotion.TryGetValue(info.DeviceId, out var last) && now - last < TimeSpan.FromSeconds(options.Value.CooldownS);
                    // Said every time, so the log shows whether the NVR reports motion of a camera at all.
                    logger.LogInformation("NVR {Nvr} channel {Channel} ({Device}): motion{Cooldown}", run.Id, channel, info.DeviceId, cooling ? ", within the cooldown" : "");
                    if (cooling)
                        return;
                    _lastMotion[info.DeviceId] = now;
                }
                _ = Task.Run(() => MotionAsync(run, info, ct), CancellationToken.None);
                break;
            case DahuaEvent.VideoLoss when e.IsStart:
                lock (run.Lost)
                    if (!run.Lost.Add(channel))
                        return;
                ingest.Enqueue(new DeviceStatusChanged(info.DeviceId, false, now));
                break;
            case DahuaEvent.VideoLoss when e.IsStop:
                lock (run.Lost)
                    if (!run.Lost.Remove(channel))
                        return;
                ingest.Enqueue(new DeviceStatusChanged(info.DeviceId, true, now));
                break;
        }
    }

    /// <summary>
    /// Motion of one channel: the event at once, then the photos as the NVR gives them. Severity follows the guard as for a
    /// guard-cam (spec 6.7): armed → alarm (Telegram with photos), arriving → warn, disarmed → info (feed only).
    /// </summary>
    internal async Task MotionAsync(NvrRun run, ChannelInfo info, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
            if (!await db.Devices.AnyAsync(d => d.DeviceId == info.DeviceId && d.Status != DeviceStatus.Disabled, ct))
                return;
            var modes = await db.Modes.AsNoTracking().Where(m => m.Key == Mode.Armed || m.Key == Mode.Presence)
                .ToDictionaryAsync(m => m.Key, m => m.Value, ct);
            var armed = modes.GetValueOrDefault(Mode.Armed) == "true";
            var arriving = armed && modes.GetValueOrDefault(Mode.Presence) == Mode.PresenceArriving;
            var severity = !armed ? EventSeverity.Info : arriving ? EventSeverity.Warn : EventSeverity.Alarm;
            var withPhotos = armed || info.RecordWhenDisarmed;

            var now = time.GetUtcNow();
            var eventId = Ulid.New(now);
            var photos = withPhotos ? Enumerable.Range(1, Math.Max(1, options.Value.Photos)).Select(i => $"{eventId}-{i}").ToList() : [];
            var payload = JsonSerializer.SerializeToDocument(new { source = "camera", channel = info.Channel, photos, via = "nvr" });
            if (arriving)
                payload = ArrivingMode.Mark(payload);
            ingest.Enqueue(new DeviceEventReceived(info.DeviceId, eventId, now, now, "motion", severity, armed, payload));

            var media = scope.ServiceProvider.GetRequiredService<PhotoIngestService>();
            string? failed = null;
            var delivered = 0;
            if (photos.Count > 0)
            {
                try
                {
                    // One ffmpeg run gives the key frames one after another; each photo is stored as it comes, so Telegram
                    // can send the first without waiting for the rest.
                    delivered = await snapshots.BurstAsync(run.Client.Endpoint,
                        new SnapshotTarget(run.Id, info.DeviceId, info.Channel, info.Source, info.Via, info.Quality), photos.Count,
                        TimeSpan.FromMilliseconds(options.Value.PhotoIntervalMs), async (taken, i) =>
                        {
                            info.Via = taken.Via;
                            var stored = await media.StoreFetchedAsync(info.DeviceId, photos[i], eventId, "motion", taken.Jpeg, "nvr", ct);
                            if (stored.Status is not (PhotoIngestStatus.Created or PhotoIngestStatus.Duplicate))
                                logger.LogWarning("NVR {Nvr} channel {Channel}: snapshot not stored: {Error}", run.Id, info.Channel, stored.Error);
                        }, ct);
                    if (delivered < photos.Count)
                        failed = $"{delivered} of {photos.Count} frames";
                }
                catch (SnapshotFailedException ex)
                {
                    failed = ex.Message; // the details are in the log and the camera's journal already
                }
            }
            var missing = photos.Count - delivered;
            if (failed is not null)
                PhotoFailed(info.DeviceId, failed, eventId, missing);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Motion of {Device} could not be handled", info.DeviceId);
        }
    }
}
