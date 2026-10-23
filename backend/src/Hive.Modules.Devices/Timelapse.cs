using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hive.Modules.Devices;

/// <summary>PUT /api/timelapse/{deviceId}: a photo every IntervalMin minutes; Light switches the camera's light on for each shot.</summary>
public sealed record SetTimelapseRequest(int IntervalMin, bool Light = false);

/// <summary>
/// A camera's timelapse. Shooting is true while a shot is in progress. LastError is "offline" (the camera was not on line
/// at its time), "camera" (it refused the snapshot, LastErrorDetail says why) or "no_photo" (it said yes, the photo never came).
/// </summary>
public sealed record TimelapseDto(
    string DeviceId, int IntervalMin, bool Light, DateTimeOffset StartedAt, DateTimeOffset NextAt, bool Shooting,
    DateTimeOffset? LastPhotoAt, string? LastPhotoId, DateTimeOffset? LastFailedAt, string? LastError, string? LastErrorDetail, int Failures);

public enum TimelapseStatus { Ok, NotFound, BadRequest }

/// <summary>
/// One shot in progress. LightChannel is the relay the hive switched on for it (null: none, or it was on already and is
/// left alone). The snapshot goes once the light had <see cref="Timelapse.LightWarmup"/> to come up.
/// </summary>
public sealed record TimelapseShot(DateTimeOffset AskedAt, string? LightChannel, DateTimeOffset? SnapshotAt = null, string? SnapshotCid = null);

/// <summary>What device.timelapse holds: the settings, the shot in progress and how the last ones went.</summary>
public sealed record TimelapseState(
    int IntervalMin, bool Light, DateTimeOffset StartedAt, string? StartedBy, DateTimeOffset NextAt,
    TimelapseShot? Shot = null, DateTimeOffset? LastPhotoAt = null, string? LastPhotoId = null,
    DateTimeOffset? LastFailedAt = null, string? LastError = null, string? LastErrorDetail = null, int Failures = 0)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static TimelapseState? Read(Device d) => d.Timelapse?.Deserialize<TimelapseState>(Json);

    public static void Write(Device d, TimelapseState? state) =>
        d.Timelapse = state is null ? null : JsonSerializer.SerializeToDocument(state, Json);

    public TimelapseDto ToDto(string deviceId) => new(deviceId, IntervalMin, Light, StartedAt, NextAt, Shot is not null,
        LastPhotoAt, LastPhotoId, LastFailedAt, LastError, LastErrorDetail, Failures);
}

/// <summary>
/// Timelapse of a camera, driven by the hive: at each slot it switches the camera's light on (when asked, and only when it is
/// off), asks for a snapshot, and switches the light off again as soon as the photo is in, the camera refuses, the photo is
/// late, or the timelapse is changed or stopped. The light goes on with for_s, so the hornet switches it off by itself
/// even when the hive or the network is gone (cmd/relay, RelayTimer). The rules here are pure; <see cref="TimelapseWatch"/>
/// runs them.
/// </summary>
public static class Timelapse
{
    /// <summary>5, 10, 15, 30, 60, 90 minutes and 2, 3, 4, 6, 8, 12, 24 hours.</summary>
    public static readonly int[] Intervals = [5, 10, 15, 30, 60, 90, 120, 180, 240, 360, 480, 720, 1440];

    public const string By = "system:timelapse";

    /// <summary>Time the light has to come up before the photo.</summary>
    public static readonly TimeSpan LightWarmup = TimeSpan.FromSeconds(3);

    /// <summary>How long the photo may take to come after the snapshot command (the hornet tries the upload, then its SD card).</summary>
    public static readonly TimeSpan PhotoTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// for_s of the light: the hornet switches it off by itself after this, whatever happens to the hive. Longer than the
    /// warm-up, one tick and the photo time-out, so the hive normally switches it off first.
    /// </summary>
    public const int LightForS = 120;

    /// <summary>A shot that fails is tried once more after this (then the timelapse keeps its own pace).</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    /// <summary>The first slot after now: StartedAt plus a whole number of intervals, so the pace keeps after misses and restarts.</summary>
    public static DateTimeOffset NextSlot(TimelapseState s, DateTimeOffset now)
    {
        var interval = TimeSpan.FromMinutes(s.IntervalMin);
        if (now < s.StartedAt)
            return s.StartedAt;
        var k = (long)Math.Floor((now - s.StartedAt) / interval) + 1;
        return s.StartedAt + k * interval;
    }

    /// <summary>The photo is in: the shot is over and the next one waits for its slot.</summary>
    public static TimelapseState Done(TimelapseState s, DateTimeOffset now, string photoId) =>
        s with { Shot = null, LastPhotoAt = now, LastPhotoId = photoId, Failures = 0, NextAt = NextSlot(s, now) };

    /// <summary>The shot failed: tried again in a minute the first time, then at the next slot.</summary>
    public static TimelapseState Failed(TimelapseState s, DateTimeOffset now, string error, string? detail = null)
    {
        var failures = s.Failures + 1;
        var next = NextSlot(s, now);
        if (failures == 1 && now + RetryAfter < next)
            next = now + RetryAfter;
        return s with { Shot = null, LastFailedAt = now, LastError = error, LastErrorDetail = detail, Failures = failures, NextAt = next };
    }

    /// <summary>The relay channel that lights the camera: relay.channel of its config, else the one its state reports, else "relay".</summary>
    public static string LightChannel(Device d)
    {
        if (d.Config?.RootElement is { ValueKind: JsonValueKind.Object } config && config.TryGetProperty("relay", out var relay)
            && relay.ValueKind == JsonValueKind.Object && relay.TryGetProperty("channel", out var ch) && ch.GetString() is { Length: > 0 } name)
            return name;
        if (d.State?.RootElement is { ValueKind: JsonValueKind.Object } state)
            foreach (var p in state.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is "on" or "off")
                    return p.Name;
        return "relay";
    }

    public static bool IsOn(Device d, string channel) =>
        d.State?.RootElement is { ValueKind: JsonValueKind.Object } state && state.TryGetProperty(channel, out var v)
        && v.ValueKind == JsonValueKind.String && v.GetString() == "on";

    public static JsonObject LightOn(string channel) => new() { ["channel"] = channel, ["set"] = "on", ["for_s"] = LightForS };

    public static JsonObject LightOff(string channel) => new() { ["channel"] = channel, ["set"] = "off" };

    public static JsonObject Snapshot() => new() { ["count"] = 1 };
}

public sealed class TimelapseService(HiveDbContext db, CommandService commands, TimeProvider time)
{
    public static string? Validate(SetTimelapseRequest r) =>
        Timelapse.Intervals.Contains(r.IntervalMin) ? null : "intervalMin: " + string.Join(", ", Timelapse.Intervals);

    /// <summary>Why this device has no timelapse, or null when it can.</summary>
    public static string? Unsupported(Device d) =>
        d.Driver != Drivers.Hornet || d.TypeCode != "guard-cam" ? "Таймлапс снимают только камеры"
        : d.Status == DeviceStatus.Disabled ? "Шершень удалён"
        : null;

    public async Task<IReadOnlyList<TimelapseDto>> ListAsync(CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().Where(d => d.Timelapse != null).OrderBy(d => d.DeviceId).ToListAsync(ct);
        return devices.Select(d => TimelapseState.Read(d)?.ToDto(d.DeviceId)).OfType<TimelapseDto>().ToList();
    }

    /// <summary>Starts a timelapse, or changes the one running: a shot in progress is dropped (its light switched off), the first new one goes now.</summary>
    public async Task<(TimelapseStatus Status, string? Error, TimelapseDto? Timelapse)> SetAsync(
        string deviceId, SetTimelapseRequest r, string by, CancellationToken ct)
    {
        if (Validate(r) is { } invalid)
            return (TimelapseStatus.BadRequest, invalid, null);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
            return (TimelapseStatus.NotFound, null, null);
        if (Unsupported(device) is { } why)
            return (TimelapseStatus.BadRequest, why, null);

        var now = time.GetUtcNow();
        var running = TimelapseState.Read(device);
        var state = new TimelapseState(r.IntervalMin, r.Light, now, by, now,
            LastPhotoAt: running?.LastPhotoAt, LastPhotoId: running?.LastPhotoId);
        TimelapseState.Write(device, state);
        Audit.Record(db, now, by, running is null ? "timelapse_start" : "timelapse_change", deviceId, new { r.IntervalMin, r.Light });
        await db.SaveChangesAsync(ct);
        await LightOffAsync(deviceId, running?.Shot, ct);
        return (TimelapseStatus.Ok, null, state.ToDto(deviceId));
    }

    /// <summary>Stops the timelapse; a light it switched on for a shot goes off now. False when none was running.</summary>
    public async Task<bool> StopAsync(string deviceId, string by, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null || TimelapseState.Read(device) is not { } state)
            return false;
        TimelapseState.Write(device, null);
        Audit.Record(db, time.GetUtcNow(), by, "timelapse_stop", deviceId);
        await db.SaveChangesAsync(ct);
        await LightOffAsync(deviceId, state.Shot, ct);
        return true;
    }

    private async Task LightOffAsync(string deviceId, TimelapseShot? shot, CancellationToken ct)
    {
        if (shot?.LightChannel is { } channel)
            await commands.SendAsync(deviceId, "relay", Timelapse.LightOff(channel), Timelapse.By, ttlS: null, ct);
    }
}

/// <summary>Every few seconds: starts the shots that are due and finishes the ones in progress (see <see cref="Timelapse"/>).</summary>
public sealed class TimelapseWatch(IServiceScopeFactory scopes, TimeProvider time, ILogger<TimelapseWatch> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    // One check at a time: a shot must never be started twice (the timer and a test, or a slow check and the next tick).
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider")
            return;
        using var timer = new PeriodicTimer(Tick, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await CheckAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Timelapse check failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await CheckAllAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CheckAllAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var commands = scope.ServiceProvider.GetRequiredService<CommandService>();
        var ids = await db.Devices.Where(d => d.Timelapse != null).Select(d => d.DeviceId).ToListAsync(ct);
        foreach (var id in ids)
        {
            try
            {
                await StepAsync(db, commands, id, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Timelapse of {Device} failed a step", id);
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task StepAsync(HiveDbContext db, CommandService commands, string deviceId, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null || TimelapseState.Read(device) is not { } state)
            return;
        var now = time.GetUtcNow();
        var next = state;
        string? lightOff = null;

        if (state.Shot is { } shot)
        {
            if (shot.SnapshotAt is null)
            {
                if (now - shot.AskedAt < Timelapse.LightWarmup)
                    return;
                var (status, command) = await commands.SendAsync(deviceId, "snapshot", Timelapse.Snapshot(), Timelapse.By, ttlS: null, ct);
                next = status == SendStatus.Sent
                    ? state with { Shot = shot with { SnapshotAt = now, SnapshotCid = command!.Cid } }
                    : Timelapse.Failed(state, now, "offline");
                if (next.Shot is null)
                    lightOff = shot.LightChannel;
            }
            else
            {
                var since = shot.SnapshotAt.Value;
                var photo = await db.Media.Where(m => m.DeviceId == deviceId && m.ReceivedAt >= since && m.Kind == MediaKind.Snapshot)
                    .OrderBy(m => m.ReceivedAt).FirstOrDefaultAsync(ct);
                var ack = shot.SnapshotCid is { } cid ? await db.Commands.AsNoTracking().FirstOrDefaultAsync(c => c.Cid == cid, ct) : null;
                if (photo is not null)
                {
                    photo.Kind = MediaKind.Timelapse;
                    next = Timelapse.Done(state, now, photo.Id);
                }
                else if (ack is { Status: CommandStatus.Error or CommandStatus.Rejected or CommandStatus.Unsupported or CommandStatus.Expired })
                    next = Timelapse.Failed(state, now, "camera", AckMessage(ack));
                else if (now - since >= Timelapse.PhotoTimeout)
                    next = Timelapse.Failed(state, now, "no_photo");
                if (next.Shot is null)
                    lightOff = shot.LightChannel;
            }
        }
        else if (now >= state.NextAt)
        {
            if (device.Status != DeviceStatus.Online)
                next = Timelapse.Failed(state, now, "offline");
            else
            {
                // A light that is on already (by hand, a rule) is left as it is, before and after the shot.
                var channel = state.Light ? Timelapse.LightChannel(device) : null;
                if (channel is not null && Timelapse.IsOn(device, channel))
                    channel = null;
                if (channel is not null)
                {
                    var (status, _) = await commands.SendAsync(deviceId, "relay", Timelapse.LightOn(channel), Timelapse.By, ttlS: null, ct);
                    next = status == SendStatus.Sent ? state with { Shot = new TimelapseShot(now, channel) } : Timelapse.Failed(state, now, "offline");
                }
                else
                {
                    var (status, command) = await commands.SendAsync(deviceId, "snapshot", Timelapse.Snapshot(), Timelapse.By, ttlS: null, ct);
                    next = status == SendStatus.Sent
                        ? state with { Shot = new TimelapseShot(now, null, now, command!.Cid) }
                        : Timelapse.Failed(state, now, "offline");
                }
            }
        }

        if (next == state)
            return;
        // Read again: the user may have stopped or changed the timelapse while the commands went out.
        await db.Entry(device).ReloadAsync(ct);
        if (TimelapseState.Read(device) is { } current && current.StartedAt == state.StartedAt)
            TimelapseState.Write(device, next);
        else
        {
            // Stopped or changed meanwhile: a light this step switched on has no shot to wait for any more.
            lightOff ??= next.Shot?.LightChannel;
            next = state;
        }
        await db.SaveChangesAsync(ct);
        if (lightOff is not null)
            await commands.SendAsync(deviceId, "relay", Timelapse.LightOff(lightOff), Timelapse.By, ttlS: null, ct);
        if (next.LastError is { } error && next.LastFailedAt == now)
            logger.LogInformation("Timelapse shot of {Device} failed: {Error} {Detail}", deviceId, error, next.LastErrorDetail);
    }

    private static string? AckMessage(DeviceCommand c) =>
        c.AckPayload?.RootElement is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : c.Status.ToString().ToLowerInvariant();
}
