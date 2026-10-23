using System.ComponentModel;
using System.Diagnostics;
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
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-rtsp_transport", "tcp", "-i", rtspUrl, "-frames:v", "1", "-q:v", "3", "-f", "image2", "-" })
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

/// <summary>What the hive knows of a channel when it needs a frame.</summary>
/// <summary>DeviceId is null for a channel not taken into the swarm (a look before picking it).</summary>
public sealed record SnapshotTarget(int NvrId, string? DeviceId, int Channel, string Source, string? Via);

/// <summary>A frame and the way it came (cgi or rtsp).</summary>
public sealed record SnapshotTaken(byte[] Jpeg, string Via);

/// <summary>
/// Takes a frame of an NVR channel (contracts/ipcam.md): the NVR's snapshot.cgi, and when that gives no JPEG (third-party
/// ONVIF cameras behind a Dahua NVR often get none) a frame of the RTSP stream, main stream first. In auto the way that
/// worked last time goes first, and it is remembered. Failures go to the log and to the camera's journal, without passwords.
/// </summary>
public sealed class Snapshots(IFrameGrabber grabber, NvrLogins logins, IServiceScopeFactory scopes, IngestQueue ingest, TimeProvider time, ILogger<Snapshots> logger)
{
    public static readonly TimeSpan RtspTimeout = TimeSpan.FromSeconds(8);

    public async Task<SnapshotTaken> TakeAsync(DahuaClient client, SnapshotTarget target, CancellationToken ct)
    {
        var ways = target.Source switch
        {
            SnapshotSources.Cgi => [SnapshotSources.Cgi],
            SnapshotSources.Rtsp => [SnapshotSources.Rtsp],
            _ when target.Via == SnapshotSources.Rtsp => new[] { SnapshotSources.Rtsp, SnapshotSources.Cgi },
            _ => [SnapshotSources.Cgi, SnapshotSources.Rtsp],
        };
        var reasons = new List<string>();
        foreach (var way in ways)
        {
            try
            {
                var jpeg = way == SnapshotSources.Cgi ? await client.SnapshotAsync(target.Channel, ct) : await RtspAsync(client.Endpoint, target.Channel, ct);
                if (way != target.Via)
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
        }

        var why = string.Join("; ", reasons);
        logger.LogWarning("NVR {Nvr} channel {Channel} ({Device}): no snapshot: {Reason}", target.NvrId, target.Channel, target.DeviceId, why);
        if (target.DeviceId is not null)
        {
            var now = time.GetUtcNow();
            ingest.Enqueue(new DeviceLogReceived(target.DeviceId, Hive.Contracts.Ulid.New(now), now, now, "warn", $"snapshot failed: {why}", null));
        }
        throw new SnapshotFailedException(why);
    }

    /// <summary>Main stream (subtype 0), then the sub-stream: some cameras give the NVR only one of them.</summary>
    private async Task<byte[]> RtspAsync(NvrEndpoint nvr, int channel, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var subtype in new[] { 0, 1 })
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
                // ffmpeg's 401 is a wrong login like any other: counted, and the sub-stream is not tried with it.
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
