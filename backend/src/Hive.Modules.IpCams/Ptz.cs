using System.Collections.Concurrent;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hive.Modules.IpCams;

/// <summary>Available: the camera can turn; Presets: the positions stored on it. Error: why the check failed (null when it did not).</summary>
public sealed record PtzStateDto(bool Available, IReadOnlyList<PtzPresetDto> Presets, string? Error);

/// <summary>
/// Op move (Pan, Tilt, Zoom: speeds -1..1, at least one not 0; the move lasts <see cref="PtzControl.MoveLimit"/> unless
/// repeated), stop, or preset (Preset: a token of <see cref="PtzStateDto.Presets"/>).
/// </summary>
public sealed record PtzRequest(string Op, double? Pan = null, double? Tilt = null, double? Zoom = null, string? Preset = null);

public enum PtzResult
{
    Ok,
    NotFound,
    NoPtz,
    BadRequest,
    Failed,
}

/// <summary>
/// Pan, tilt and zoom of a camera on its own (contracts/ipcam.md "PTZ"): ONVIF ContinuousMove/Stop/GotoPreset, or Dahua's
/// ptz.cgi. A move always ends: the hive sends Stop when no new move comes within <see cref="MoveLimit"/> (an ONVIF camera
/// also gets it as the move's timeout), so a closed tab or a lost connection never leaves the camera spinning. While the camera
/// turns and a little after, its own motion detection sees the picture move: <see cref="Quiet"/> tells the watch to ignore it.
/// </summary>
public sealed class PtzControl(IServiceScopeFactory scopes, IDataProtectionProvider protection, NvrLogins logins, TimeProvider time, ILogger<PtzControl> logger)
{
    public static readonly TimeSpan MoveLimit = TimeSpan.FromSeconds(1);

    /// <summary>Motion is ignored this long after the camera stopped turning (the picture and the detector settle).</summary>
    public static readonly TimeSpan QuietAfterMove = TimeSpan.FromSeconds(5);

    /// <summary>A preset is one command, the travel takes a while: motion is ignored this long after it.</summary>
    public static readonly TimeSpan QuietAfterPreset = TimeSpan.FromSeconds(15);

    /// <summary>Whether a camera can turn is asked again after this long (or at once when the cameras change).</summary>
    private static readonly TimeSpan CheckAgain = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Camera> _cameras = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _quiet = new();

    /// <summary>The camera turned recently: its motion is the picture moving, not someone walking by.</summary>
    public bool Quiet(string deviceId, DateTimeOffset now) => _quiet.TryGetValue(deviceId, out var until) && now < until;

    /// <summary>The cameras changed: logins and capabilities are read afresh. A move in progress is stopped first.</summary>
    public void Forget()
    {
        foreach (var id in _cameras.Keys.ToList())
            if (_cameras.TryRemove(id, out var camera))
                _ = Task.Run(async () =>
                {
                    await StopQuietlyAsync(camera);
                    camera.Dispose();
                });
    }

    public async Task<(PtzResult Result, PtzStateDto? State)> StateAsync(string deviceId, CancellationToken ct)
    {
        if (await CameraAsync(deviceId, ct) is not { } camera)
            return (PtzResult.NotFound, null);
        return (PtzResult.Ok, await CheckAsync(camera, ct));
    }

    public async Task<(PtzResult Result, string? Error)> CommandAsync(string deviceId, PtzRequest r, string by, CancellationToken ct)
    {
        if (Invalid(r) is { } invalid)
            return (PtzResult.BadRequest, invalid);
        if (await CameraAsync(deviceId, ct) is not { } camera)
            return (PtzResult.NotFound, null);
        var state = await CheckAsync(camera, ct);
        if (!state.Available)
            return (PtzResult.NoPtz, state.Error ?? "the camera has no PTZ");

        await camera.Gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            switch (r.Op)
            {
                case PtzOps.Move:
                    var started = !camera.Moving;
                    await MoveAsync(camera, Round(r.Pan), Round(r.Tilt), Round(r.Zoom), ct);
                    camera.Moving = true;
                    Hush(deviceId, now + MoveLimit + QuietAfterMove);
                    camera.ArmStop(time, MoveLimit, () => _ = Task.Run(() => TimedStopAsync(camera)));
                    if (started)
                        await AuditAsync(by, "ipcam.ptz.move", deviceId, new { pan = r.Pan, tilt = r.Tilt, zoom = r.Zoom });
                    break;
                case PtzOps.Stop:
                    camera.DisarmStop();
                    await StopAsync(camera, ct);
                    Hush(deviceId, now + QuietAfterMove);
                    break;
                case PtzOps.Preset:
                    camera.DisarmStop();
                    if (camera.Moving)
                        await StopAsync(camera, ct);
                    await GotoAsync(camera, r.Preset!, ct);
                    Hush(deviceId, now + QuietAfterPreset);
                    await AuditAsync(by, "ipcam.ptz.preset", deviceId, new { preset = r.Preset });
                    break;
            }
            return (PtzResult.Ok, null);
        }
        catch (Exception ex) when (Answerable(ex, ct))
        {
            logger.LogWarning("PTZ {Op} of {Device} failed: {Error}", r.Op, deviceId, ex.Message);
            return (PtzResult.Failed, ex.Message);
        }
        finally
        {
            camera.Gate.Release();
        }
    }

    internal static string? Invalid(PtzRequest r)
    {
        switch (r.Op)
        {
            case PtzOps.Move:
                double?[] speeds = [r.Pan, r.Tilt, r.Zoom];
                if (speeds.Any(v => v is { } x && (!double.IsFinite(x) || x < -1 || x > 1)))
                    return "pan, tilt and zoom are speeds from -1 to 1";
                return speeds.All(v => Round(v) == 0) ? "a move needs pan, tilt or zoom" : null;
            case PtzOps.Stop:
                return null;
            case PtzOps.Preset:
                return string.IsNullOrWhiteSpace(r.Preset) || r.Preset.Length > 64 ? "preset is required" : null;
            default:
                return "op must be move, stop or preset";
        }
    }

    private static double Round(double? v) => Math.Round(v ?? 0, 2);

    /// <summary>
    /// Dahua's direction code for a move and its arguments: arg1 the vertical speed of a diagonal, arg2 the speed (1..8). A move
    /// that pans or tilts ignores the zoom (the controls never ask for both).
    /// </summary>
    internal static (string Code, int Arg1, int Arg2) DahuaMove(double pan, double tilt, double zoom)
    {
        static int Speed(double v) => Math.Clamp((int)Math.Round(Math.Abs(v) * 8), 1, 8);
        if (pan == 0 && tilt == 0)
            return (zoom > 0 ? "ZoomTele" : "ZoomWide", 0, Speed(zoom));
        var vertical = tilt > 0 ? "Up" : tilt < 0 ? "Down" : "";
        var horizontal = pan > 0 ? "Right" : pan < 0 ? "Left" : "";
        if (vertical.Length > 0 && horizontal.Length > 0)
            return (horizontal + vertical, Speed(tilt), Speed(pan));
        return (horizontal + vertical, 0, Speed(horizontal.Length > 0 ? pan : tilt));
    }

    private async Task MoveAsync(Camera camera, double pan, double tilt, double zoom, CancellationToken ct)
    {
        if (camera.Onvif is { } onvif)
        {
            await camera.OnvifClient!.MoveAsync(onvif, pan, tilt, zoom, MoveLimit, ct);
            return;
        }
        var (code, arg1, arg2) = DahuaMove(pan, tilt, zoom);
        // A new direction: the old one is stopped first, Dahua stops only the code it is told.
        if (camera.DahuaCode is { } previous && previous != code)
            await camera.DahuaClient!.PtzStopAsync(camera.Channel, previous, ct);
        await camera.DahuaClient!.PtzStartAsync(camera.Channel, code, arg1, arg2, ct);
        camera.DahuaCode = code;
    }

    private async Task StopAsync(Camera camera, CancellationToken ct)
    {
        camera.Moving = false;
        if (camera.Onvif is { } onvif)
            await camera.OnvifClient!.StopAsync(onvif, ct);
        else if (camera.DahuaCode is { } code)
        {
            await camera.DahuaClient!.PtzStopAsync(camera.Channel, code, ct);
            camera.DahuaCode = null;
        }
    }

    private static Task GotoAsync(Camera camera, string preset, CancellationToken ct)
    {
        if (camera.Onvif is { } onvif)
            return camera.OnvifClient!.GotoPresetAsync(onvif, preset, ct);
        if (!int.TryParse(preset, out var index))
            throw new HttpRequestException($"unknown preset {preset}");
        return camera.DahuaClient!.PtzStartAsync(camera.Channel, "GotoPreset", 0, index, ct);
    }

    /// <summary>No new move came in time (released, tab closed, connection lost): the camera is stopped.</summary>
    private async Task TimedStopAsync(Camera camera)
    {
        await camera.Gate.WaitAsync();
        try
        {
            if (!camera.Moving || camera.StopDue > time.GetUtcNow())
                return; // stopped already, or a newer move came meanwhile
            logger.LogInformation("PTZ of {Device}: no command for {Limit} s, stopped", camera.DeviceId, MoveLimit.TotalSeconds);
            await StopAsync(camera, CancellationToken.None);
            Hush(camera.DeviceId, time.GetUtcNow() + QuietAfterMove);
        }
        catch (Exception ex) when (Answerable(ex, CancellationToken.None))
        {
            logger.LogWarning("PTZ stop of {Device} failed: {Error}", camera.DeviceId, ex.Message);
        }
        finally
        {
            camera.Gate.Release();
        }
    }

    private async Task StopQuietlyAsync(Camera camera)
    {
        camera.DisarmStop();
        if (!camera.Moving)
            return;
        try
        {
            await StopAsync(camera, CancellationToken.None);
        }
        catch (Exception ex) when (Answerable(ex, CancellationToken.None))
        {
            logger.LogWarning("PTZ stop of {Device} failed: {Error}", camera.DeviceId, ex.Message);
        }
    }

    private void Hush(string deviceId, DateTimeOffset until) =>
        _quiet.AddOrUpdate(deviceId, until, (_, old) => old > until ? old : until);

    /// <summary>Whether the camera can turn, asked once and kept for a while; a failed check is asked again next time.</summary>
    private async Task<PtzStateDto> CheckAsync(Camera camera, CancellationToken ct)
    {
        if (camera.State is { } known && time.GetUtcNow() - camera.CheckedAt < CheckAgain)
            return known;
        await camera.Gate.WaitAsync(ct);
        try
        {
            if (camera.State is { } again && time.GetUtcNow() - camera.CheckedAt < CheckAgain)
                return again;
            PtzStateDto state;
            try
            {
                if (camera.OnvifClient is { } onvif)
                {
                    camera.Onvif = await onvif.PtzAsync(ct);
                    state = camera.Onvif is { } ptz
                        ? new PtzStateDto(true, await PresetsOrNoneAsync(() => onvif.PresetsAsync(ptz, ct)), null)
                        : new PtzStateDto(false, [], null);
                }
                else
                    state = new PtzStateDto(true, await camera.DahuaClient!.PtzPresetsAsync(camera.Channel, ct), null);
                camera.State = state;
                camera.CheckedAt = time.GetUtcNow();
            }
            catch (HttpRequestException ex) when (camera.DahuaClient is not null && ex.StatusCode is null or System.Net.HttpStatusCode.BadRequest)
            {
                // ptz.cgi answers an error to a camera without PTZ.
                state = camera.State = new PtzStateDto(false, [], null);
                camera.CheckedAt = time.GetUtcNow();
            }
            catch (Exception ex) when (Answerable(ex, ct))
            {
                state = new PtzStateDto(false, [], ex.Message); // not kept: the camera may answer next time
            }
            return state;
        }
        finally
        {
            camera.Gate.Release();
        }
    }

    private static async Task<IReadOnlyList<PtzPresetDto>> PresetsOrNoneAsync(Func<Task<IReadOnlyList<PtzPresetDto>>> presets)
    {
        try
        {
            return await presets();
        }
        catch (Exception ex) when (ex is OnvifFaultException or HttpRequestException)
        {
            return []; // turning works without presets
        }
    }

    /// <summary>A camera on its own with its login, kept until the cameras change; null for any other device.</summary>
    private async Task<Camera?> CameraAsync(string deviceId, CancellationToken ct)
    {
        if (_cameras.TryGetValue(deviceId, out var known))
            return known;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var found = await (from c in db.NvrChannels.AsNoTracking()
                           join n in db.Nvrs.AsNoTracking() on c.NvrId equals n.Id
                           where c.DeviceId == deviceId && n.Kind == NvrKinds.Camera
                           select new { c.Channel, Nvr = n }).FirstOrDefaultAsync(ct);
        if (found is null)
            return null;
        string password;
        try
        {
            password = protection.CreateProtector(IpCamWatch.ProtectorPurpose).Unprotect(found.Nvr.PasswordProtected);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null; // the keys are gone: the camera has to be saved again with its password
        }
        var nvr = found.Nvr;
        var endpoint = new NvrEndpoint(nvr.Host, nvr.Port, nvr.Username, password, nvr.RtspPort, nvr.SnapshotUrl, nvr.RtspMain, nvr.RtspSub);
        var camera = nvr.Vendor == CameraVendors.Onvif
            ? new Camera(deviceId, found.Channel) { OnvifClient = new OnvifClient(endpoint, logins, time) }
            : new Camera(deviceId, found.Channel) { DahuaClient = new DahuaClient(endpoint, logins) };
        if (_cameras.TryAdd(deviceId, camera))
            return camera;
        camera.Dispose();
        return _cameras[deviceId];
    }

    private async Task AuditAsync(string by, string action, string deviceId, object details)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
            Audit.Record(db, time.GetUtcNow(), by, action, deviceId, details);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PTZ audit of {Device} not saved", deviceId);
        }
    }

    private static bool Answerable(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is HttpRequestException or OnvifFaultException or NvrAuthException or OperationCanceledException or IOException;

    private sealed class Camera(string deviceId, int channel) : IDisposable
    {
        private ITimer? _stop;

        public string DeviceId { get; } = deviceId;
        public int Channel { get; } = channel;
        public OnvifClient? OnvifClient { get; init; }
        public DahuaClient? DahuaClient { get; init; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public OnvifPtz? Onvif { get; set; }
        public PtzStateDto? State { get; set; }
        public DateTimeOffset CheckedAt { get; set; }
        public bool Moving { get; set; }

        /// <summary>The direction code a Dahua camera is moving with (its stop needs the same code).</summary>
        public string? DahuaCode { get; set; }

        /// <summary>When the running move ends unless another comes.</summary>
        public DateTimeOffset StopDue { get; private set; }

        public void ArmStop(TimeProvider time, TimeSpan after, Action stop)
        {
            StopDue = time.GetUtcNow() + after;
            _stop?.Dispose();
            _stop = time.CreateTimer(_ => stop(), null, after, Timeout.InfiniteTimeSpan);
        }

        public void DisarmStop()
        {
            _stop?.Dispose();
            _stop = null;
        }

        public void Dispose()
        {
            DisarmStop();
            OnvifClient?.Dispose();
            DahuaClient?.Dispose();
        }
    }
}

public static class PtzOps
{
    public const string Move = "move";
    public const string Stop = "stop";
    public const string Preset = "preset";
}
