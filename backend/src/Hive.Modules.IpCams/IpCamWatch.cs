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

public sealed class IpCamOptions
{
    public const string Section = "IpCams";

    public bool Enabled { get; set; } = true;

    /// <summary>Another motion of the same channel within this time is ignored.</summary>
    public int CooldownS { get; set; } = 20;

    public int Photos { get; set; } = 3;
    public int PhotoIntervalMs { get; set; } = 700;

    /// <summary>An NVR without a working event stream this long takes its cameras offline.</summary>
    public int OfflineAfterS { get; set; } = 60;

    /// <summary>A stream silent this long (no heartbeat, no event) is taken for dead and reconnected.</summary>
    public int StaleStreamS { get; set; } = 30;

    /// <summary>After the NVR refused a login, the same password is not tried again for this long (Dahua locks after a few).</summary>
    public int AuthRetryS { get; set; } = 60;
}

/// <summary>
/// Keeps one event stream per NVR open (contracts/ipcam.md): motion of a channel becomes a <c>motion</c> event of its
/// device in the ingest pipeline, with photos from the NVR when the guard is on, and the channels go online and
/// offline with the stream. Also runs the <c>snapshot</c> command of these devices.
/// </summary>
public sealed class IpCamWatch(
    IServiceScopeFactory scopes,
    IngestQueue ingest,
    IDataProtectionProvider protection,
    IOptions<IpCamOptions> options,
    Snapshots snapshots,
    NvrLogins logins,
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
    public void Reload() => _reload.Writer.TryWrite(true);

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
            var run = new NvrRun(nvr.Id, nvr.Name, new DahuaClient(new NvrEndpoint(nvr.Host, nvr.Port, nvr.Username, password, nvr.RtspPort), logins),
                nvr.Channels.ToDictionary(c => c.Channel, c => new ChannelInfo(c.DeviceId, c.Channel, c.RecordWhenDisarmed, c.SnapshotSource, c.SnapshotVia)), now);
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

    /// <summary>One NVR: open the stream, read it until it breaks, wait (2, 4 … 60 s), again.</summary>
    private async Task RunStreamAsync(NvrRun run, CancellationToken ct)
    {
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
                // NvrLogins has said it once; the loop waits above.
            }
            catch (Exception ex)
            {
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
            ingest.Enqueue(new DeviceEventReceived(info.DeviceId, eventId, now, now, "motion", severity, armed, payload));

            var media = scope.ServiceProvider.GetRequiredService<PhotoIngestService>();
            for (var i = 0; i < photos.Count; i++)
            {
                if (i > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(options.Value.PhotoIntervalMs), time, ct);
                try
                {
                    var jpeg = await SnapshotAsync(run, info, ct);
                    var stored = await media.StoreFetchedAsync(info.DeviceId, photos[i], eventId, "motion", jpeg, "nvr", ct);
                    if (stored.Status is not (PhotoIngestStatus.Created or PhotoIngestStatus.Duplicate))
                        logger.LogWarning("NVR {Nvr} channel {Channel}: snapshot not stored: {Error}", run.Id, info.Channel, stored.Error);
                }
                catch (SnapshotFailedException)
                {
                    // already in the log and the camera's journal
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Motion of {Device} could not be handled", info.DeviceId);
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
            if (run.Connected && (run.Online != true || now - run.Refreshed >= Refresh))
            {
                foreach (var c in run.Channels.Values.Where(c => !run.IsLost(c.Channel)))
                    ingest.Enqueue(new DeviceStatusChanged(c.DeviceId, true, now));
                run.Online = true;
                run.Refreshed = now;
            }
            else if (!run.Connected && run.Online != false && now - run.LastAlive > TimeSpan.FromSeconds(options.Value.OfflineAfterS))
            {
                logger.LogWarning("NVR {Nvr}: no events for {Seconds} s, its cameras are offline", run.Name, options.Value.OfflineAfterS);
                foreach (var c in run.Channels.Values)
                    ingest.Enqueue(new DeviceStatusChanged(c.DeviceId, false, run.LastAlive));
                run.Online = false;
            }
        }
    }

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
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(command.TtlS, 5, 60)));
                var jpeg = await SnapshotAsync(run, info, timeout.Token);
                using var scope = scopes.CreateScope();
                var stored = await scope.ServiceProvider.GetRequiredService<PhotoIngestService>()
                    .StoreFetchedAsync(command.DeviceId, $"{command.Cid}-1", null, "snapshot", jpeg, "nvr", timeout.Token);
                status = stored.Status is PhotoIngestStatus.Created or PhotoIngestStatus.Duplicate ? CommandStatus.Ok : CommandStatus.Error;
                result = new { photo = stored.Media?.Id, error = stored.Error };
            }
        }
        catch (Exception ex)
        {
            result = new { error = ex.Message };
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
    private async Task<byte[]> SnapshotAsync(NvrRun run, ChannelInfo info, CancellationToken ct)
    {
        var taken = await snapshots.TakeAsync(run.Client, new SnapshotTarget(run.Id, info.DeviceId, info.Channel, info.Source, info.Via), ct);
        info.Via = taken.Via;
        return taken.Jpeg;
    }

    /// <summary>A channel of an NVR as the watch knows it.</summary>
    internal sealed class ChannelInfo(string deviceId, int channel, bool recordWhenDisarmed, string source, string? via)
    {
        public string DeviceId { get; } = deviceId;
        public int Channel { get; } = channel;
        public bool RecordWhenDisarmed { get; } = recordWhenDisarmed;
        public string Source { get; } = source;

        /// <summary>The way the last frame came; set by whichever photo or command got one.</summary>
        public string? Via
        {
            get => Volatile.Read(ref _via);
            set => Volatile.Write(ref _via, value);
        }

        private string? _via = via;
    }

    /// <summary>The stream of one NVR and what is known of its health.</summary>
    internal sealed class NvrRun(int id, string name, DahuaClient client, Dictionary<int, ChannelInfo> channels, DateTimeOffset started)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public DahuaClient Client { get; } = client;
        public Dictionary<int, ChannelInfo> Channels { get; } = channels;
        public CancellationTokenSource Stop { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;

        /// <summary>Channels whose camera the NVR reports lost (VideoLoss): offline whatever the stream does.</summary>
        public HashSet<int> Lost { get; } = [];

        public volatile bool Connected;
        private long _lastAlive = started.UtcTicks;

        /// <summary>The last line from the stream (written by the stream, read by the status check).</summary>
        public DateTimeOffset LastAlive => new(Interlocked.Read(ref _lastAlive), TimeSpan.Zero);

        public bool? Online { get; set; }
        public DateTimeOffset Refreshed { get; set; }

        public void Alive(DateTimeOffset now, bool connected)
        {
            Interlocked.Exchange(ref _lastAlive, now.UtcTicks);
            Connected = connected;
        }

        public bool IsLost(int channel)
        {
            lock (Lost)
                return Lost.Contains(channel);
        }
    }
}
