using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Hive.Modules.IpCams;

/// <summary>A frame could not be taken; the message says why and never holds a password.</summary>
public sealed class SnapshotFailedException(string message) : Exception(message);

/// <summary>Key frames of an RTSP stream as JPEGs (ffmpeg on the hive; a fake in tests).</summary>
public interface IFrameGrabber
{
    /// <summary>
    /// Up to <paramref name="count"/> frames, each as soon as it is decoded: a key frame first (never a grey half-decoded picture),
    /// then frames <paramref name="spacing"/> apart. The first must come within <paramref name="firstTimeout"/> (else
    /// <see cref="SnapshotFailedException"/>); later ones end the sequence when they are late.
    /// </summary>
    IAsyncEnumerable<byte[]> GrabAsync(string rtspUrl, int count, TimeSpan spacing, TimeSpan firstTimeout, CancellationToken ct);
}

/// <summary>
/// <c>ffmpeg -skip_frame nokey … -i URL -frames:v N -f image2pipe -c:v mjpeg -</c>: key frames only, so every picture is
/// whole. Decoding from the first frame that comes (mid-GOP) gave grey pictures with a few fragments of whatever moved: a
/// P-frame drawn over a reference the decoder never saw. The URL carries the NVR login, so it is passed as one argument
/// (no shell) and cut out of anything ffmpeg says.
/// </summary>
public sealed class FfmpegFrameGrabber : IFrameGrabber
{
    /// <summary>Key frames come once per GOP: up to a few seconds apart on a camera at night.</summary>
    public static readonly TimeSpan NextFrameTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// One frame: key frames only (-skip_frame nokey). Several (an alarm's photos): everything is decoded but nothing is kept
    /// before the first key frame, so the frames after it, decoded against it, are whole; then one every
    /// <paramref name="spacing"/>. A burst so takes the first key frame plus the spacing, not one key frame per photo.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string rtspUrl, int count, TimeSpan spacing)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (count == 1)
            args.AddRange(["-skip_frame", "nokey"]);
        // Do not study the stream for seconds before the first frame.
        args.AddRange(["-analyzeduration", "1000000", "-probesize", "1000000", "-rtsp_transport", "tcp", "-i", rtspUrl, "-an"]);
        if (count > 1)
        {
            // Selected: the first I-frame, and every frame once one has been selected (prev_selected_n is NAN until then).
            var ms = Math.Max(100, (int)spacing.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            args.AddRange(["-vf", $"select='eq(pict_type\\,I)+not(isnan(prev_selected_n))',fps=1000/{ms}"]);
        }
        else
            args.AddRange(["-fps_mode", "passthrough"]); // each key frame once, as it comes (no frames repeated to fill a rate)
        args.AddRange(["-frames:v", count.ToString(CultureInfo.InvariantCulture), "-q:v", "3", "-f", "image2pipe", "-c:v", "mjpeg", "-"]);
        return args;
    }

    public async IAsyncEnumerable<byte[]> GrabAsync(string rtspUrl, int count, TimeSpan spacing, TimeSpan firstTimeout, [EnumeratorCancellation] CancellationToken ct)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in Arguments(rtspUrl, count, spacing))
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

        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var output = process.StandardOutput.BaseStream;
        var pending = new MemoryStream();
        var chunk = new byte[64 * 1024];
        var got = 0;
        try
        {
            while (got < count)
            {
                var read = await ReadAsync(output, chunk, got == 0 ? firstTimeout : NextFrameTimeout, ct);
                if (read == Timeout)
                {
                    if (got == 0)
                        throw new SnapshotFailedException($"rtsp: no key frame in {firstTimeout.TotalSeconds:0} s");
                    yield break;
                }
                if (read == 0)
                    break; // ffmpeg is done (or gave up: said below)
                pending.Write(chunk, 0, read);
                foreach (var frame in JpegStream.TakeFrames(pending))
                {
                    got++;
                    yield return frame;
                    if (got >= count)
                        break;
                }
            }
            if (got == 0)
            {
                var said = await stderr.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : "", TaskScheduler.Default);
                var exit = process.HasExited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "?";
                throw new SnapshotFailedException($"rtsp: ffmpeg exit {exit}: {Snapshots.Redact(said, rtspUrl).Trim()}");
            }
        }
        finally
        {
            Kill(process);
        }
    }

    private const int Timeout = -1;

    /// <summary>One read from ffmpeg's output, or <see cref="Timeout"/> when nothing came in time.</summary>
    private static async Task<int> ReadAsync(Stream output, byte[] chunk, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            return await output.ReadAsync(chunk, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Timeout;
        }
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

/// <summary>Cuts JPEGs out of a byte stream that carries them one after another (ffmpeg's image2pipe).</summary>
public static class JpegStream
{
    /// <summary>
    /// The whole JPEGs at the start of <paramref name="buffer"/>; what follows them (a JPEG still arriving) stays in it.
    /// Segments are walked by their lengths and the scan data by its markers, so bytes inside a table never pass for an end.
    /// </summary>
    public static IReadOnlyList<byte[]> TakeFrames(MemoryStream buffer)
    {
        var data = buffer.GetBuffer();
        var length = (int)buffer.Length;
        var frames = new List<byte[]>();
        var pos = 0;
        while (true)
        {
            var soi = Find(data, pos, length);
            if (soi < 0)
            {
                // No start yet: keep a lone trailing 0xFF, it may be half of one.
                pos = length > 0 && data[length - 1] == 0xFF ? length - 1 : length;
                break;
            }
            var end = End(data, soi, length);
            if (end < 0)
            {
                pos = soi;
                break;
            }
            frames.Add(data[soi..end]);
            pos = end;
        }
        var rest = data[pos..length];
        buffer.SetLength(0);
        buffer.Write(rest);
        return frames;
    }

    private static int Find(byte[] data, int from, int length)
    {
        for (var i = from; i + 1 < length; i++)
            if (data[i] == 0xFF && data[i + 1] == 0xD8)
                return i;
        return -1;
    }

    /// <summary>The index just past the EOI of the JPEG starting at <paramref name="soi"/>, or -1 when it is not all here yet.</summary>
    private static int End(byte[] data, int soi, int length)
    {
        var i = soi + 2;
        while (i + 1 < length)
        {
            if (data[i] != 0xFF)
            {
                i++; // not expected outside scan data; skip
                continue;
            }
            var marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++; // fill byte
                continue;
            }
            if (marker == 0xD9)
                return i + 2;
            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                i += 2;
                continue;
            }
            if (i + 3 >= length)
                return -1;
            var segment = (data[i + 2] << 8) | data[i + 3];
            i += 2 + segment;
            if (marker != 0xDA)
                continue;
            // Scan data: runs until a marker that is not a stuffed 0xFF00 or a restart marker.
            while (i + 1 < length && !(data[i] == 0xFF && data[i + 1] != 0x00 && data[i + 1] is not (>= 0xD0 and <= 0xD7)))
                i++;
        }
        return -1;
    }
}
