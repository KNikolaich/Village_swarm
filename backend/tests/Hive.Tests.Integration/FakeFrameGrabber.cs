using System.Collections.Concurrent;
using Hive.Modules.IpCams;

namespace Hive.Tests.Integration;

/// <summary>ffmpeg for the tests: a JPEG for every RTSP URL asked for, or a failure for the sub-streams listed in <see cref="FailSubtypes"/>.</summary>
public sealed class FakeFrameGrabber : IFrameGrabber
{
    private readonly Lazy<byte[]> _jpeg = new(() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "scenarios", "photos", "frame1.jpg")));

    public ConcurrentQueue<string> Urls { get; } = new();

    /// <summary>"subtype=0" here makes the main stream fail like a camera that gives the NVR only its sub-stream.</summary>
    public ConcurrentDictionary<string, bool> FailSubtypes { get; } = new();

    public void Reset()
    {
        Urls.Clear();
        FailSubtypes.Clear();
    }

    public Task<byte[]> GrabAsync(string rtspUrl, TimeSpan timeout, CancellationToken ct)
    {
        Urls.Enqueue(rtspUrl);
        if (FailSubtypes.Keys.Any(rtspUrl.EndsWith))
            throw new SnapshotFailedException("rtsp: ffmpeg exit 1: method DESCRIBE failed: 404 Stream Not Found");
        return Task.FromResult(_jpeg.Value);
    }
}
