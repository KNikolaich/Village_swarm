using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Hive.Modules.IpCams;
using SkiaSharp;

namespace Hive.Tests.Integration;

/// <summary>
/// ffmpeg for the tests: the asked number of key frames for every RTSP URL, a failure for the streams in <see cref="FailSubtypes"/>,
/// or grey pictures (a decode started mid-GOP) for those in <see cref="GreySubtypes"/>.
/// </summary>
public sealed class FakeFrameGrabber : IFrameGrabber
{
    private readonly Lazy<byte[]> _jpeg = new(() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "scenarios", "photos", "frame1.jpg")));

    public ConcurrentQueue<string> Urls { get; } = new();

    public ConcurrentQueue<TimeSpan> Spacings { get; } = new();

    /// <summary>"subtype=0" here makes the main stream fail like a camera that gives the NVR only its sub-stream.</summary>
    public ConcurrentDictionary<string, bool> FailSubtypes { get; } = new();

    public ConcurrentDictionary<string, bool> GreySubtypes { get; } = new();

    public void Reset()
    {
        Urls.Clear();
        Spacings.Clear();
        FailSubtypes.Clear();
        GreySubtypes.Clear();
    }

    public async IAsyncEnumerable<byte[]> GrabAsync(string rtspUrl, int count, TimeSpan spacing, TimeSpan firstTimeout, [EnumeratorCancellation] CancellationToken ct)
    {
        Urls.Enqueue(rtspUrl);
        Spacings.Enqueue(spacing);
        if (FailSubtypes.Keys.Any(rtspUrl.EndsWith))
            throw new SnapshotFailedException("rtsp: ffmpeg exit 1: method DESCRIBE failed: 404 Stream Not Found");
        var grey = GreySubtypes.Keys.Any(rtspUrl.EndsWith);
        for (var i = 0; i < count; i++)
        {
            await Task.Delay(50, ct);
            yield return grey ? Grey() : _jpeg.Value;
        }
    }

    /// <summary>What the hive got all night from the cameras: flat grey with a few fragments.</summary>
    public static byte[] Grey()
    {
        using var bitmap = new SKBitmap(320, 240);
        bitmap.Erase(new SKColor(128, 128, 128));
        bitmap.SetPixel(40, 20, new SKColor(200, 30, 200));
        bitmap.SetPixel(41, 21, new SKColor(30, 30, 30));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }
}
