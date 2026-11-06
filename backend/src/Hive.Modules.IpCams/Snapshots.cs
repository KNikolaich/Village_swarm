using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Hive.Domain;
using Hive.Infrastructure.Data;
using Hive.Modules.Media;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hive.Modules.IpCams;

/// <summary>What the hive knows of a channel when it needs a frame. DeviceId is null for a channel not taken into the swarm.</summary>
public sealed record SnapshotTarget(int NvrId, string? DeviceId, int Channel, string Source, string? Via, string Quality = SnapshotQualities.Fast);

/// <summary>Why a frame is wanted: for the photos of an alarm, or to look (a button, /photo, the settings preview). Logged and shown.</summary>
public enum SnapshotPurpose
{
    Alarm,
    Look,
}

/// <summary>A frame and the way it came (cgi or rtsp). Shared: another request for the same frame was already running and this one joined it.</summary>
public sealed record SnapshotTaken(byte[] Jpeg, string Via, bool Shared = false);

/// <summary>
/// Takes frames of an NVR channel (contracts/ipcam.md): the NVR's snapshot.cgi, and when that gives no JPEG (third-party
/// ONVIF cameras behind a Dahua NVR often get none) key frames of the RTSP stream. In auto the way that worked last time
/// goes first, and it is remembered. Grey pictures (a broken decode) are refused and the next way is tried. One NVR is asked
/// for one thing at a time (it, and the Pi decoding 8 MP, cope badly with several at once); a second request for the same
/// channel while one is under way joins it instead of starting another. Every attempt is noted for the settings page;
/// failures go to the log and the camera's journal, without passwords.
/// </summary>
public sealed class Snapshots(
    IFrameGrabber grabber, NvrLogins logins, IpCamDiagnostics diagnostics, IServiceScopeFactory scopes, IngestQueue ingest,
    TimeProvider time, ILogger<Snapshots> logger)
{
    public static readonly TimeSpan RtspTimeout = TimeSpan.FromSeconds(12);

    /// <summary>A request gives up after this, waiting in the queue included.</summary>
    public static readonly TimeSpan MaxTime = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _queues = new();
    private readonly Dictionary<string, Task<SnapshotTaken>> _inFlight = [];

    /// <summary>One frame to look at; joins a request for the same channel already under way.</summary>
    public async Task<SnapshotTaken> TakeAsync(NvrEndpoint nvr, SnapshotTarget target, SnapshotPurpose purpose, CancellationToken ct)
    {
        var key = $"{Nvr(nvr)}/{target.Channel.ToString(CultureInfo.InvariantCulture)}/{purpose}";
        Task<SnapshotTaken> task;
        bool joined;
        lock (_inFlight)
        {
            joined = _inFlight.TryGetValue(key, out var running);
            task = running ?? OneAsync(nvr, target, purpose);
            if (!joined)
            {
                _inFlight[key] = task;
                _ = task.ContinueWith(_ =>
                {
                    lock (_inFlight)
                        _inFlight.Remove(key);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        // The work is not the caller's: a closed browser tab does not cancel a frame someone else is waiting for.
        var taken = await task.WaitAsync(ct);
        return joined ? taken with { Shared = true } : taken;
    }

    /// <summary>
    /// The photos of an alarm: up to <paramref name="count"/> frames, each handed to <paramref name="onFrame"/> as soon as it is
    /// there (the first one need not wait for the others). From the stream they are successive key frames of one ffmpeg run;
    /// from snapshot.cgi, frames <paramref name="interval"/> apart. Returns how many came; none is a <see cref="SnapshotFailedException"/>.
    /// </summary>
    public async Task<int> BurstAsync(NvrEndpoint nvr, SnapshotTarget target, int count, TimeSpan interval,
        Func<SnapshotTaken, int, Task> onFrame, CancellationToken ct)
    {
        await Task.Yield();
        return await QueuedAsync(nvr, target, SnapshotPurpose.Alarm, limit => CollectAsync(nvr, target, SnapshotPurpose.Alarm, count, interval, onFrame, limit), ct);
    }

    private async Task<SnapshotTaken> OneAsync(NvrEndpoint nvr, SnapshotTarget target, SnapshotPurpose purpose)
    {
        await Task.Yield();
        SnapshotTaken? first = null;
        await QueuedAsync(nvr, target, purpose, limit => CollectAsync(nvr, target, purpose, 1, TimeSpan.Zero, (taken, _) =>
        {
            first = taken;
            return Task.CompletedTask;
        }, limit), CancellationToken.None);
        return first!;
    }

    private async Task<int> QueuedAsync(NvrEndpoint nvr, SnapshotTarget target, SnapshotPurpose purpose, Func<CancellationToken, Task<int>> work, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(MaxTime);
        var queue = _queues.GetOrAdd(Nvr(nvr), _ => new SemaphoreSlim(1, 1));
        try
        {
            await queue.WaitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Fail(target, purpose, 0, $"waited {MaxTime.TotalSeconds:0} s for the NVR's other snapshots");
        }
        try
        {
            return await work(limit.Token);
        }
        finally
        {
            queue.Release();
        }
    }

    private async Task<int> CollectAsync(NvrEndpoint nvr, SnapshotTarget target, SnapshotPurpose purpose, int count, TimeSpan interval,
        Func<SnapshotTaken, int, Task> onFrame, CancellationToken ct)
    {
        var key = target.DeviceId ?? $"nvr{target.NvrId}/ch{target.Channel}";
        var auto = nvr.HasSnapshot && target.Source is not (SnapshotSources.Cgi or SnapshotSources.Rtsp);
        // Nothing learned yet: snapshot.cgi first, so one early miss does not make the slower stream stick.
        var chooser = diagnostics.Chooser(key, target.Via ?? (auto ? SnapshotSources.Cgi : null));
        var ways = !nvr.HasSnapshot ? [SnapshotSources.Rtsp]
            : target.Source == SnapshotSources.Cgi ? [SnapshotSources.Cgi]
            : target.Source == SnapshotSources.Rtsp ? [SnapshotSources.Rtsp]
            : chooser.Order([SnapshotSources.Cgi, SnapshotSources.Rtsp], time.GetUtcNow());
        var started = time.GetTimestamp();
        var reasons = new List<string>();
        var delivered = 0;
        string? size = null, firstWay = null;
        long wayStarted = 0;
        int? firstMs = null;
        double? firstPts = null;
        async Task Deliver(GrabbedFrame frame, string way)
        {
            size ??= ImageTools.Identify(frame.Jpeg) is { } wh ? $"{wh.Width}x{wh.Height}" : "?";
            firstWay ??= way;
            firstMs ??= (int)time.GetElapsedTime(wayStarted).TotalMilliseconds;
            if (count > 1)
            {
                // The time in the stream says how far apart the photos really are (an NVR may hand over buffered frames at once).
                firstPts ??= frame.PtsS;
                var inStream = frame.PtsS is { } pts && firstPts is { } p0 ? $", +{pts - p0:0.00} s in the stream" : "";
                logger.LogInformation("NVR {Nvr} channel {Channel}: frame {Frame} of {Count} via {Via} at {Ms} ms{InStream}",
                    target.NvrId, target.Channel, delivered + 1, count, way, (int)time.GetElapsedTime(started).TotalMilliseconds, inStream);
            }
            await onFrame(new SnapshotTaken(frame.Jpeg, way), delivered++);
        }

        using var client = new DahuaClient(nvr, logins);
        foreach (var way in ways)
        {
            var before = delivered;
            wayStarted = time.GetTimestamp();
            firstMs = null;
            string? cut = null;
            try
            {
                cut = way == SnapshotSources.Cgi
                    ? await CgiAsync(client, target, count - delivered, interval, Deliver, ct)
                    : await RtspAsync(nvr, target, count - delivered, interval, Deliver, reasons, ct);
            }
            catch (SnapshotFailedException ex)
            {
                reasons.Add(ex.Message);
            }
            catch (NvrAuthException ex)
            {
                // RTSP logs in with the same account: trying it would only count another wrong login.
                reasons.Add($"{way}: {ex.Message}");
                break;
            }
            catch (OperationCanceledException)
            {
                reasons.Add($"{way}: out of time ({MaxTime.TotalSeconds:0} s for the whole snapshot)");
                break; // frames that came before the time ran out still count
            }
            if (auto)
                await ChooseAsync(chooser, target, way, delivered > before, firstMs ?? (int)time.GetElapsedTime(wayStarted).TotalMilliseconds, ct);
            if (delivered >= count)
                break;
            if (cut is not null)
            {
                // A burst cut short (a snapshot that did not come in time) is finished by the next way, not given up.
                reasons.Add($"{way}: {cut} after {delivered - before} frame(s)");
                logger.LogWarning("NVR {Nvr} channel {Channel}: burst cut after {Got} of {Count} frame(s) via {Via}: {Reason}",
                    target.NvrId, target.Channel, delivered, count, way, cut);
            }
        }
        if (delivered == 0)
            throw Fail(target, purpose, (int)time.GetElapsedTime(started).TotalMilliseconds, string.Join("; ", reasons));

        var ms = (int)time.GetElapsedTime(started).TotalMilliseconds;
        logger.LogInformation("NVR {Nvr} channel {Channel}: {Frames} snapshot(s) {Size} via {Via} in {Ms} ms ({Purpose}, {Quality})",
            target.NvrId, target.Channel, delivered, size, firstWay, ms, purpose, target.Quality);
        diagnostics.Snapshot(target, new SnapshotAttemptDto(time.GetUtcNow(), purpose.ToString().ToLowerInvariant(), firstWay, true, ms,
            reasons.Count > 0 ? string.Join("; ", reasons) : null, size));
        return delivered;
    }

    /// <summary>The outcome of a way told to the chooser; a change of the preferred way is logged with its reason and stored.</summary>
    private async Task ChooseAsync(WayChooser chooser, SnapshotTarget target, string way, bool ok, int ms, CancellationToken ct)
    {
        var was = chooser.Preferred;
        var change = chooser.Record(way, ok, ms, time.GetUtcNow());
        if (change is not null)
            logger.LogInformation("NVR {Nvr} channel {Channel}: snapshots now come via {To} first (was {From}): {Reason}",
                target.NvrId, target.Channel, change.To, change.From, change.Reason);
        if (chooser.Preferred is { } now && now != was)
            await RememberAsync(target, now, ct);
    }

    /// <summary>
    /// snapshot.cgi, <paramref name="interval"/> apart. A miss before the first frame fails the way; a later one cuts the burst
    /// (its reason returned, the rest left to the next way).
    /// </summary>
    private async Task<string?> CgiAsync(DahuaClient client, SnapshotTarget target, int count, TimeSpan interval, Func<GrabbedFrame, string, Task> deliver, CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                await Task.Delay(interval, time, ct);
            try
            {
                var jpeg = await client.SnapshotAsync(target.Channel, ct);
                Refuse(target, "cgi", jpeg);
                await deliver(new GrabbedFrame(jpeg, null), SnapshotSources.Cgi);
            }
            catch (SnapshotFailedException ex) when (i > 0)
            {
                return ex.Message;
            }
        }
        return null;
    }

    /// <summary>
    /// Frames of the streams, the channel's quality saying which first: fast the light sub-stream (an 8 MP main stream is slow on
    /// the Pi), detailed the main stream. The other one follows when the first gives nothing, so a frame comes whenever either can
    /// give one. A grey frame is not passed on; one that comes after good frames cuts the burst (its reason returned).
    /// </summary>
    private async Task<string?> RtspAsync(NvrEndpoint nvr, SnapshotTarget target, int count, TimeSpan spacing, Func<GrabbedFrame, string, Task> deliver,
        List<string> notes, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var subtype in target.Quality == SnapshotQualities.Detailed ? new[] { 0, 1 } : [1, 0])
        {
            var stream = subtype == 0 ? "rtsp main" : "rtsp sub";
            if (logins.Blocked(nvr) is { } block)
                throw new NvrAuthException(logins.Explain(block), block: block);
            var got = 0;
            try
            {
                await foreach (var frame in grabber.GrabAsync(nvr.RtspUrlWithLogin(target.Channel, subtype), count, spacing, RtspTimeout, ct))
                {
                    if (!PhotoIngestService.IsJpeg(frame.Jpeg))
                        throw new SnapshotFailedException("rtsp: ffmpeg gave no JPEG");
                    Refuse(target, stream, frame.Jpeg);
                    await deliver(frame, SnapshotSources.Rtsp);
                    got++;
                }
                if (got > 0)
                {
                    notes.AddRange(errors); // what the other stream did wrong stays visible
                    return got < count ? $"{stream}: the stream ended" : null;
                }
                errors.Add($"{stream}: no frame");
            }
            catch (SnapshotFailedException ex) when (got > 0)
            {
                notes.AddRange(errors);
                return ex.Message;
            }
            catch (SnapshotFailedException ex)
            {
                errors.Add(ex.Message.Replace("rtsp:", $"{stream}:", StringComparison.Ordinal));
                // ffmpeg's 401 is a wrong login like any other: counted, and the other stream is not tried with it.
                if (ex.Message.Contains("401", StringComparison.Ordinal))
                {
                    logins.Failed(nvr, new NvrAuthError(401, null, null, null));
                    break;
                }
            }
        }
        throw new SnapshotFailedException(string.Join("; ", errors));
    }

    /// <summary>A grey picture (a decoder that started without its reference frame) is refused, with its numbers in the log.</summary>
    private void Refuse(SnapshotTarget target, string way, byte[] jpeg)
    {
        if (ImageTools.MeasureFlatness(jpeg) is not { Grey: true } flat)
            return;
        logger.LogWarning("NVR {Nvr} channel {Channel}: snapshot rejected: grey frame via {Way} ({Stats})", target.NvrId, target.Channel, way, flat);
        throw new SnapshotFailedException($"{way}: grey frame ({flat})");
    }

    private SnapshotFailedException Fail(SnapshotTarget target, SnapshotPurpose purpose, int ms, string why)
    {
        logger.LogWarning("NVR {Nvr} channel {Channel} ({Device}): no snapshot after {Ms} ms ({Purpose}): {Reason}",
            target.NvrId, target.Channel, target.DeviceId, ms, purpose, why);
        diagnostics.Snapshot(target, new SnapshotAttemptDto(time.GetUtcNow(), purpose.ToString().ToLowerInvariant(), null, false, ms, why));
        if (target.DeviceId is not null)
        {
            var now = time.GetUtcNow();
            ingest.Enqueue(new DeviceLogReceived(target.DeviceId, Hive.Contracts.Ulid.New(now), now, now, "warn", $"snapshot failed: {why}", null));
        }
        return new SnapshotFailedException(why);
    }

    private static string Nvr(NvrEndpoint nvr) => $"{nvr.Host}:{nvr.Port.ToString(CultureInfo.InvariantCulture)}";

    private async Task RememberAsync(SnapshotTarget target, string way, CancellationToken ct)
    {
        if (target.DeviceId is not null)
            diagnostics.Remember(target.DeviceId, way);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
            await db.NvrChannels.Where(c => c.NvrId == target.NvrId && c.Channel == target.Channel)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.SnapshotVia, way), ct);
            logger.LogInformation("NVR {Nvr} channel {Channel}: snapshots now come via {Via}", target.NvrId, target.Channel, way);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "NVR {Nvr} channel {Channel}: could not remember the snapshot way", target.NvrId, target.Channel);
        }
    }

    /// <summary>Cuts the URL with the login, and the password alone, out of a message.</summary>
    public static string Redact(string text, string rtspUrl)
    {
        var redacted = text.Replace(rtspUrl, "rtsp://***", StringComparison.Ordinal);
        if (Uri.TryCreate(rtspUrl, UriKind.Absolute, out var uri) && uri.UserInfo.Split(':', 2) is [_, var pass] && pass.Length > 0)
        {
            redacted = redacted.Replace(pass, "***", StringComparison.Ordinal);
            redacted = redacted.Replace(Uri.UnescapeDataString(pass), "***", StringComparison.Ordinal);
        }
        return redacted.Length > 300 ? redacted[..300] : redacted;
    }

    /// <summary>The start of a body that is not a JPEG, as printable text: the NVR's error usually says what is wrong.</summary>
    public static string Peek(byte[] body, int max = 200)
    {
        var text = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, max));
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(char.IsControl(c) ? ' ' : c);
        return sb.ToString().Trim();
    }
}
