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

/// <summary>
/// Keeps one event stream per NVR open (contracts/ipcam.md): motion of a channel becomes a <c>motion</c> event of its
/// device in the ingest pipeline, with photos from the NVR when the guard is on, and the channels go online and
/// offline with the stream. Also runs the <c>snapshot</c> command of these devices.
/// </summary>
public sealed partial class IpCamWatch(
    IServiceScopeFactory scopes,
    IngestQueue ingest,
    IDataProtectionProvider protection,
    IOptions<IpCamOptions> options,
    Snapshots snapshots,
    NvrLogins logins,
    IpCamDiagnostics diagnostics,
    VideoStreams video,
    PtzControl ptz,
    NodeState node,
    TimeProvider time,
    ILogger<IpCamWatch> logger) : BackgroundService, IDirectDriver
{
    public const string ProtectorPurpose = "hive.ipcam";
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(60);

    private readonly Channel<bool> _reload = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastMotion = [];
    private List<NvrRun> _runs = [];

    public string Driver => Drivers.IpCam;

    public bool Accepts(string command) => command == "snapshot";

    /// <summary>The NVRs or their channels changed: streams are reopened with the new settings.</summary>
    public void Reload()
    {
        _reload.Writer.TryWrite(true);
        video.Changed(); // the live streams follow the cameras
        ptz.Forget();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The build runs the app to write the OpenAPI document; it must not need a database.
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider" || !options.Value.Enabled)
            return;
        Reload();
        using var timer = new PeriodicTimer(Tick, time);
        try
        {
            do
            {
                try
                {
                    if (_reload.Reader.TryRead(out _))
                        await ReloadAsync(stoppingToken);
                    CheckStatus();
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "IP camera watch round failed");
                    Reload(); // the database may be back next time
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await StopAllAsync();
        }
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        await StopAllAsync();
        // An observer (the city copy) keeps the settings but never talks to the NVR: the home node does.
        if (node.ReadOnly)
            return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        await scope.ServiceProvider.GetRequiredService<IpCamService>().EnsureDevicesAsync(ct);
        var nvrs = await db.Nvrs.AsNoTracking().Include(n => n.Channels).ToListAsync(ct);
        var protector = protection.CreateProtector(ProtectorPurpose);
        var runs = new List<NvrRun>();
        var now = time.GetUtcNow();
        foreach (var nvr in nvrs.Where(n => n.Channels.Count > 0))
        {
            string password;
            try
            {
                password = protector.Unprotect(nvr.PasswordProtected);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                logger.LogError(ex, "Password of NVR {Nvr} cannot be decrypted (data protection keys lost?); enter it again", nvr.Name);
                continue;
            }
            var endpoint = new NvrEndpoint(nvr.Host, nvr.Port, nvr.Username, password, nvr.RtspPort, nvr.SnapshotUrl, nvr.RtspMain, nvr.RtspSub);
            var run = new NvrRun(nvr.Id, nvr.Name, new DahuaClient(endpoint, logins),
                nvr.Channels.ToDictionary(c => c.Channel, c => new ChannelInfo(c.DeviceId, c.Channel, c.RecordWhenDisarmed, c.SnapshotSource, c.SnapshotVia, c.SnapshotQuality)), now)
            {
                // A camera on its own has no device of the NVR kind; an ONVIF one is watched its own way.
                DeviceId = nvr.Kind == NvrKinds.Camera ? null : IpCamService.NvrDeviceId(nvr.Id),
                Vendor = nvr.Vendor,
                EventsUrl = nvr.EventsUrl,
            };
            run.Task = Task.Run(() => RunStreamAsync(run, run.Stop.Token), CancellationToken.None);
            runs.Add(run);
        }
        lock (_gate)
            _runs = runs;
    }

    private async Task StopAllAsync()
    {
        List<NvrRun> runs;
        lock (_gate)
        {
            runs = _runs;
            _runs = [];
        }
        foreach (var run in runs)
        {
            await run.Stop.CancelAsync();
            try
            {
                await run.Task;
            }
            catch (OperationCanceledException)
            {
            }
            run.Client.Dispose();
            run.Stop.Dispose();
        }
    }

    /// <summary>
    /// Channels online while their NVR's stream is up (refreshed every minute so "last seen" moves), offline once it has
    /// been down for OfflineAfterS. Offline is dated by the last sign of life, like a hornet that went quiet.
    /// </summary>
    private void CheckStatus()
    {
        List<NvrRun> runs;
        lock (_gate)
            runs = _runs;
        var now = time.GetUtcNow();
        foreach (var run in runs)
        {
            var login = logins.State(run.Client.Endpoint).State;
            if (login != run.LoginShown && run.DeviceId is not null)
            {
                // "locked" on the NVR's row in the swarm without opening its page
                run.LoginShown = login;
                ingest.Enqueue(new DeviceStateReceived(run.DeviceId, Ulid.New(now), now, now,
                    JsonSerializer.SerializeToDocument(new { login }), Merge: true));
            }
            if (run.Connected && (run.Online != true || now - run.Refreshed >= Refresh))
            {
                if (run.DeviceId is not null)
                    ingest.Enqueue(new DeviceStatusChanged(run.DeviceId, true, now));
                foreach (var c in run.Channels.Values.Where(c => !run.IsLost(c.Channel)))
                    ingest.Enqueue(new DeviceStatusChanged(c.DeviceId, true, now));
                run.Online = true;
                run.Refreshed = now;
            }
            else if (!run.Connected && run.Online != false && now - run.LastAlive > TimeSpan.FromSeconds(options.Value.OfflineAfterS))
            {
                logger.LogWarning("NVR {Nvr}: no events for {Seconds} s, its cameras are offline", run.Name, options.Value.OfflineAfterS);
                if (run.DeviceId is not null)
                    ingest.Enqueue(new DeviceStatusChanged(run.DeviceId, false, run.LastAlive));
                foreach (var c in run.Channels.Values)
                    ingest.Enqueue(new DeviceStatusChanged(c.DeviceId, false, run.LastAlive));
                run.Online = false;
            }
        }
    }

    /// <summary>The NVR's event stream for the settings page; "off" when the hive does not run one for it.</summary>
    public NvrStreamDto Stream(int nvrId)
    {
        NvrRun? run;
        lock (_gate)
            run = _runs.FirstOrDefault(r => r.Id == nvrId);
        if (run is null)
            return new NvrStreamDto("off", null, null, 0, null);
        return run.Connected
            ? new NvrStreamDto("connected", run.Since, run.LastAlive, run.Reconnects, run.LastError)
            : new NvrStreamDto(run.Reconnects == 0 && run.LastError is null ? "connecting" : "down", run.Since, run.LastAlive, run.Reconnects, run.LastError);
    }
}
