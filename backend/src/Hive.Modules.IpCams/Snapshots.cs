using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
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

/// <summary>A frame could not be taken; the message says why and never holds a password.</summary>
public sealed class SnapshotFailedException(string message) : Exception(message);

/// <summary>One frame of an RTSP stream as JPEG (ffmpeg on the hive; a fake in tests).</summary>
public interface IFrameGrabber
{
    Task<byte[]> GrabAsync(string rtspUrl, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// <c>ffmpeg -rtsp_transport tcp -i URL -frames:v 1 -q:v 3 -f image2 -</c>: the first frame of the stream on stdout. The URL
/// carries the NVR login, so it is passed as one argument (no shell) and cut out of anything ffmpeg says.
/// </summary>
public sealed class FfmpegFrameGrabber : IFrameGrabber
{
    public async Task<byte[]> GrabAsync(string rtspUrl, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[]
                 {
                     // Without these ffmpeg studies the stream for up to 5 s before the first frame.
                     "-hide_banner", "-loglevel", "error", "-fflags", "nobuffer", "-analyzeduration", "1000000", "-probesize", "1000000",
                     "-rtsp_transport", "tcp", "-i", rtspUrl, "-an", "-frames:v", "1", "-q:v", "3", "-f", "image2", "-",
                 })
            start.ArgumentList.Add(a);

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            throw new SnapshotFailedException("rtsp: ffmpeg is not installed on the hive");
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        using var frame = new MemoryStream();
        var stderr = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(frame, limit.Token);
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            throw new SnapshotFailedException($"rtsp: no frame in {timeout.TotalSeconds:0} s");
        }
        finally
        {
            if (ct.IsCancellationRequested)
                Kill(process);
        }

        var said = "";
        try
        {
            said = await stderr;
        }
        catch (OperationCanceledException)
        {
        }
        if (process.ExitCode != 0 || frame.Length == 0)
            throw new SnapshotFailedException($"rtsp: ffmpeg exit {process.ExitCode}: {Snapshots.Redact(said, rtspUrl).Trim()}");
        return frame.ToArray();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}

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
/// Takes a frame of an NVR channel (contracts/ipcam.md): the NVR's snapshot.cgi, and when that gives no JPEG (third-party
/// ONVIF cameras behind a Dahua NVR often get none) a frame of the RTSP stream. In auto the way that worked last time goes
/// first, and it is remembered. One NVR is asked one frame at a time (it, and the Pi decoding 8 MP, cope badly with several
/// at once); a second request for the same channel while one is under way joins it instead of starting another.
/// Every attempt is noted for the settings page; failures go to the log and the camera's journal, without passwords.
/// </summary>
public sealed class Snapshots(
    IFrameGrabber grabber, NvrLogins logins, IpCamDiagnostics diagnostics, IServiceScopeFactory scopes, IngestQueue ingest,
    TimeProvider time, ILogger<Snapshots> logger)
{
    public static readonly TimeSpan RtspTimeout = TimeSpan.FromSeconds(12);

    /// <summary>A request gives up after this, waiting in the queue included.</summary>
    public static readonly TimeSpan MaxTime = TimeSpan.FromSeconds(50);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _queues = new();
    private readonly Dictionary<string, Task<SnapshotTaken>> _inFlight = [];

    public async Task<SnapshotTaken> TakeAsync(NvrEndpoint nvr, SnapshotTarget target, SnapshotPurpose purpose, CancellationToken ct)
    {
        var key = $"{nvr.Host}:{nvr.Port.ToString(CultureInfo.InvariantCulture)}/{target.Channel.ToString(CultureInfo.InvariantCulture)}/{purpose}";
        Task<SnapshotTaken> task;
        bool joined;
        lock (_inFlight)
        {
            joined = _inFlight.TryGetValue(key, out var running);
            task = running ?? QueuedAsync(nvr, target, purpose);
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

    private async Task<SnapshotTaken> QueuedAsync(NvrEndpoint nvr, SnapshotTarget target, SnapshotPurpose purpose)
    {
        await Task.Yield();
        using var limit = new CancellationTokenSource(MaxTime, time);
        var queue = _queues.GetOrAdd($"{nvr.Host}:{nvr.Port.ToString(CultureInfo.InvariantCulture)}", _ => new SemaphoreSlim(1, 1));
        try
        {
            await queue.WaitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            throw Fail(target, purpose, null, 0, $"waited {MaxTime.TotalSeconds:0} s for the NVR's other snapshots");
        }
        try
        {
            return await TakeNowAsync(nvr, target, purpose, limit.Token);
        }
        finally
        {
            queue.Release();
        }
    }

    private async Task<SnapshotTaken> TakeNowAsync(NvrEndpoint nvr, SnapshotTarget target, SnapshotPurpose purpose, CancellationToken ct)
    {
        // The way that worked last time, as known now: another request may have learned it while this one queued.
        var via = target.DeviceId is null ? target.Via : diagnostics.Via(target.DeviceId) ?? target.Via;
        var ways = target.Source switch
        {
            SnapshotSources.Cgi => [SnapshotSources.Cgi],
            SnapshotSources.Rtsp => [SnapshotSources.Rtsp],
            _ when via == SnapshotSources.Rtsp => new[] { SnapshotSources.Rtsp, SnapshotSources.Cgi },
            _ => [SnapshotSources.Cgi, SnapshotSources.Rtsp],
        };
        var started = time.GetTimestamp();
        var reasons = new List<string>();
        using var client = new DahuaClient(nvr, logins);
        foreach (var way in ways)
        {
            try
            {
                var jpeg = way == SnapshotSources.Cgi ? await client.SnapshotAsync(target.Channel, ct) : await RtspAsync(nvr, target.Channel, target.Quality, ct);
                var ms = (int)time.GetElapsedTime(started).TotalMilliseconds;
                var size = ImageTools.Identify(jpeg) is { } wh ? $"{wh.Width}x{wh.Height}" : "?";
                logger.LogInformation("NVR {Nvr} channel {Channel}: snapshot {Size} via {Via} in {Ms} ms ({Purpose}, {Quality})",
                    target.NvrId, target.Channel, size, way, ms, purpose, target.Quality);
                diagnostics.Snapshot(target, new SnapshotAttemptDto(time.GetUtcNow(), purpose.ToString().ToLowerInvariant(), way, true, ms,
                    reasons.Count > 0 ? string.Join("; ", reasons) : null, size));
                if (way != via)
                    await RememberAsync(target, way, ct);
                return new SnapshotTaken(jpeg, way);
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
                break;
            }
        }
        throw Fail(target, purpose, null, (int)time.GetElapsedTime(started).TotalMilliseconds, string.Join("; ", reasons));
    }

    private SnapshotFailedException Fail(SnapshotTarget target, SnapshotPurpose purpose, string? via, int ms, string why)
    {
        logger.LogWarning("NVR {Nvr} channel {Channel} ({Device}): no snapshot after {Ms} ms ({Purpose}): {Reason}",
            target.NvrId, target.Channel, target.DeviceId, ms, purpose, why);
        diagnostics.Snapshot(target, new SnapshotAttemptDto(time.GetUtcNow(), purpose.ToString().ToLowerInvariant(), via, false, ms, why));
        if (target.DeviceId is not null)
        {
            var now = time.GetUtcNow();
            ingest.Enqueue(new DeviceLogReceived(target.DeviceId, Hive.Contracts.Ulid.New(now), now, now, "warn", $"snapshot failed: {why}", null));
        }
        return new SnapshotFailedException(why);
    }

    /// <summary>
    /// Both streams, the channel's quality saying which first: fast the light sub-stream (an 8 MP main stream is slow to give
    /// its first frame on the Pi), detailed the main stream. The other one follows when the first gives nothing, so a frame comes
    /// whenever either can give one.
    /// </summary>
    private async Task<byte[]> RtspAsync(NvrEndpoint nvr, int channel, string quality, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var subtype in quality == SnapshotQualities.Detailed ? new[] { 0, 1 } : [1, 0])
        {
            if (logins.Blocked(nvr) is { } block)
                throw new NvrAuthException(logins.Explain(block), block: block);
            try
            {
                var jpeg = await grabber.GrabAsync(nvr.RtspUrlWithLogin(channel, subtype), RtspTimeout, ct);
                if (!PhotoIngestService.IsJpeg(jpeg))
                    throw new SnapshotFailedException("rtsp: ffmpeg gave no JPEG");
                return jpeg;
            }
            catch (SnapshotFailedException ex)
            {
                errors.Add(ex.Message.Replace("rtsp:", subtype == 0 ? "rtsp main:" : "rtsp sub:", StringComparison.Ordinal));
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
