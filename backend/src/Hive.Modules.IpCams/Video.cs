using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.IpCams;

public sealed class VideoOptions
{
    public const string Section = "Video";

    /// <summary>go2rtc's HTTP API inside the docker network (http://go2rtc:1984); empty: no live video.</summary>
    public string Go2RtcUrl { get; set; } = "";

    /// <summary>How often go2rtc is checked against the cameras (it forgets its streams when it restarts).</summary>
    public int SyncEveryS { get; set; } = 60;

    /// <summary>
    /// An H.264 copy of each IP camera stream for browsers without H.265 (Chrome on Windows, most phones): "" (none, the
    /// RPi: it cannot afford it), "cpu" (ffmpeg in software) or "vaapi" (the graphics card: the go2rtc -hardware image
    /// with /dev/dri). go2rtc runs it only while someone watches the copy.
    /// </summary>
    public string Transcode { get; set; } = "";

    /// <summary>Live viewers at once (all cameras together); one more is refused. Each takes its own copy of the stream.</summary>
    public int MaxViewers { get; set; } = 5;

    /// <summary>Up to this many viewers «Не выключать» keeps the picture on; above it the 5-minute stop comes back for all.</summary>
    public int KeepOnViewers { get; set; } = 3;
}

/// <summary>
/// A camera that can be watched live: its stream names in go2rtc (sub: light, main: detailed). Kind ipcam (NvrId, Channel
/// for its snapshots), guard-cam (frames only) or car (one MJPEG stream as both, NvrId and Channel 0).
/// </summary>
public sealed record VideoCameraDto(
    string DeviceId, string Name, int? ZoneId, string? Zone, int NvrId, int Channel, string Sub, string Main, string Status, string Kind = Cameras.IpCam,
    int Rotate = 0, string? SubH264 = null, string? MainH264 = null);

/// <summary>Live viewers now (all cameras), the most at once, and up to how many «Не выключать» holds.</summary>
public sealed record VideoViewersDto(int Viewers, int Max, int KeepOnUpTo);

/// <summary>Whether live video works on this hive (go2rtc configured), and the cameras.</summary>
public sealed record VideoStreamsDto(bool Enabled, IReadOnlyList<VideoCameraDto> Cameras);

/// <summary>go2rtc's HTTP API: the streams it knows (name → source), add one, remove one.</summary>
public sealed class Go2RtcClient(HttpClient http)
{
    public async Task<Dictionary<string, string?>> ListAsync(CancellationToken ct)
    {
        var json = await http.GetFromJsonAsync<JsonElement>("api/streams", ct);
        var streams = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (json.ValueKind != JsonValueKind.Object)
            return streams;
        foreach (var s in json.EnumerateObject())
        {
            string? url = null;
            if (s.Value.ValueKind == JsonValueKind.Object && s.Value.TryGetProperty("producers", out var producers)
                && producers.ValueKind == JsonValueKind.Array && producers.GetArrayLength() > 0
                && producers[0].TryGetProperty("url", out var u))
                url = u.GetString();
            streams[s.Name] = url;
        }
        return streams;
    }

    /// <summary>
    /// Browsers watching now: consumers of the MSE player (format mse/…) or of a car's MJPEG, on every stream. go2rtc's own
    /// ffmpeg reading a camera for its H.264 copy (rtsp) and single frames (jpeg) are not viewers.
    /// </summary>
    public async Task<int> ViewersAsync(CancellationToken ct)
    {
        var json = await http.GetFromJsonAsync<JsonElement>("api/streams", ct);
        return CountViewers(json);
    }

    public static int CountViewers(JsonElement streams)
    {
        if (streams.ValueKind != JsonValueKind.Object)
            return 0;
        var viewers = 0;
        foreach (var s in streams.EnumerateObject())
            if (s.Value.ValueKind == JsonValueKind.Object && s.Value.TryGetProperty("consumers", out var consumers) && consumers.ValueKind == JsonValueKind.Array)
                foreach (var c in consumers.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("format_name", out var f) && f.GetString() is { } format
                        && (format.StartsWith("mse", StringComparison.Ordinal) || format.StartsWith("mjpeg", StringComparison.Ordinal)))
                        viewers++;
        return viewers;
    }

    public async Task PutAsync(string name, string source, CancellationToken ct)
    {
        using var response = await http.PutAsync($"api/streams?name={Uri.EscapeDataString(name)}&src={Uri.EscapeDataString(source)}", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        using var response = await http.DeleteAsync($"api/streams?src={Uri.EscapeDataString(name)}", ct);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// Live video through go2rtc (contracts/ipcam.md "Live video"): the hive owns go2rtc's list of streams, two per camera
/// ({device}-sub, {device}-main) with the RTSP addresses and their logins, which stay between the hive and go2rtc. go2rtc
/// opens a camera only while someone watches. The browser reaches go2rtc through Caddy, which asks <see cref="Allows"/>
/// first: only a signed-in user, only a stream of the list.
/// </summary>
public sealed class VideoStreams(
    IServiceScopeFactory scopes,
    IDataProtectionProvider protection,
    IHttpClientFactory clients,
    IOptions<VideoOptions> options,
    NodeState node,
    ILogger<VideoStreams> logger) : BackgroundService
{
    public const string SubSuffix = "-sub";
    public const string MainSuffix = "-main";
    public const string H264Suffix = "-h264";

    /// <summary>
    /// go2rtc asks an RTSP camera for its ONVIF audio back channel by default; some cameras (a direct one on stream0/stream1)
    /// then send no video at all ("i/o timeout"). The hive never talks to a camera, so it is never asked for.
    /// </summary>
    public const string NoBackchannel = "#backchannel=0";

    private readonly Channel<bool> _changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, string> _pushed = new(StringComparer.Ordinal);
    private volatile IReadOnlySet<string> _names = new HashSet<string>();
    private (DateTime At, int Count) _viewers = (DateTime.MinValue, 0);
    private readonly SemaphoreSlim _viewersGate = new(1, 1);

    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.Go2RtcUrl);

    /// <summary>The go2rtc suffix of the H.264 copy, or null when the hive makes none (Video:Transcode).</summary>
    public static string? TranscodeSuffix(string? transcode) => transcode?.Trim().ToLowerInvariant() switch
    {
        "cpu" => "#video=h264",
        "vaapi" => "#video=h264#hardware=vaapi",
        _ => null,
    };

    private bool Transcodes => TranscodeSuffix(options.Value.Transcode) is not null;

    public int MaxViewers => Math.Max(1, options.Value.MaxViewers);

    /// <summary>Browsers watching now (go2rtc's consumers), asked at most once a second; 0 when go2rtc does not answer.</summary>
    public async Task<int> ViewersAsync(CancellationToken ct)
    {
        if (!Enabled)
            return 0;
        await _viewersGate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _viewers.At < TimeSpan.FromSeconds(1))
                return _viewers.Count;
            int count;
            try
            {
                count = await new Go2RtcClient(clients.CreateClient(nameof(VideoStreams))).ViewersAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                logger.LogDebug("go2rtc viewers not known: {Error}", ex.Message);
                count = 0;
            }
            _viewers = (DateTime.UtcNow, count);
            return count;
        }
        finally
        {
            _viewersGate.Release();
        }
    }

    public async Task<VideoViewersDto> ViewersDtoAsync(CancellationToken ct) =>
        new(await ViewersAsync(ct), MaxViewers, Math.Clamp(options.Value.KeepOnViewers, 0, MaxViewers));

    /// <summary>
    /// What Caddy asks before a browser reaches go2rtc: 401/403 as <see cref="Allows"/> says, 429 for one live viewer too
    /// many (the player socket or a car's MJPEG; single frames are always let through), 204 otherwise.
    /// </summary>
    public async Task<int> AuthorizeAsync(string? forwardedUri, CancellationToken ct)
    {
        if (!Allows(SourceOf(forwardedUri)))
            return StatusCodes.Status403Forbidden;
        if (IsLive(forwardedUri) && await ViewersAsync(ct) >= MaxViewers)
            return StatusCodes.Status429TooManyRequests;
        return StatusCodes.Status204NoContent;
    }

    /// <summary>A request that opens a live picture (and so a viewer): the MSE socket or the MJPEG stream.</summary>
    public static bool IsLive(string? forwardedUri)
    {
        var path = forwardedUri?.Split('?', 2)[0];
        return path is "/api/ws" or "/api/stream.mjpeg";
    }

    /// <summary>The cameras changed: go2rtc is brought in line now rather than at the next round.</summary>
    public void Changed() => _changed.Writer.TryWrite(true);

    /// <summary>Whether a request for this stream may go to go2rtc: a name of the list, nothing else (no addresses).</summary>
    public bool Allows(string? src) => src is not null && _names.Contains(src);

    public static string Sub(string deviceId) => deviceId + SubSuffix;
    public static string Main(string deviceId) => deviceId + MainSuffix;

    /// <summary>A stream the hive manages: an IP camera's (with its H.264 copies), or one it pushed (a car's).</summary>
    private bool Ours(string name) =>
        (name.StartsWith("ipcam", StringComparison.Ordinal) && (name.EndsWith(SubSuffix, StringComparison.Ordinal) || name.EndsWith(MainSuffix, StringComparison.Ordinal)
            || name.EndsWith(SubSuffix + H264Suffix, StringComparison.Ordinal) || name.EndsWith(MainSuffix + H264Suffix, StringComparison.Ordinal)))
        || _pushed.ContainsKey(name);

    /// <summary>
    /// A car's camera (contracts/mqtt-topics.md "Car"): its MJPEG stream with the key the hive gave it, or null without an
    /// address or a key yet.
    /// </summary>
    public static string? CarSource(string? ip, System.Text.Json.JsonDocument? config)
    {
        if (string.IsNullOrEmpty(ip) || config is null
            || !config.RootElement.TryGetProperty("car", out var car) || car.ValueKind != System.Text.Json.JsonValueKind.Object
            || !car.TryGetProperty("stream_key", out var key) || key.GetString() is not { Length: > 0 } k)
            return null;
        return $"http://hive:{Uri.EscapeDataString(k)}@{ip}:81/stream";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider" || !Enabled)
            return;
        Changed();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("go2rtc not in line with the cameras: {Error}", ex.Message);
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.SyncEveryS)));
            try
            {
                await _changed.Reader.ReadAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>The streams the cameras call for (name → RTSP address with its login), from the settings.</summary>
    private async Task<Dictionary<string, string>> WantedAsync(CancellationToken ct)
    {
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node.ReadOnly)
            return wanted; // the city copy cannot reach the cameras
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var protector = protection.CreateProtector(IpCamWatch.ProtectorPurpose);
        var live = await db.Devices.AsNoTracking().Where(d => d.Driver == Drivers.IpCam && d.Status != DeviceStatus.Disabled)
            .Select(d => d.DeviceId).ToListAsync(ct);
        foreach (var nvr in await db.Nvrs.AsNoTracking().Include(n => n.Channels).ToListAsync(ct))
        {
            string password;
            try
            {
                password = protector.Unprotect(nvr.PasswordProtected);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                continue; // IpCamWatch says so in the log
            }
            var endpoint = new NvrEndpoint(nvr.Host, nvr.Port, nvr.Username, password, nvr.RtspPort, nvr.SnapshotUrl, nvr.RtspMain, nvr.RtspSub);
            var transcode = TranscodeSuffix(options.Value.Transcode);
            foreach (var c in nvr.Channels.Where(c => live.Contains(c.DeviceId)))
            {
                wanted[Sub(c.DeviceId)] = endpoint.RtspUrlWithLogin(c.Channel, 1) + NoBackchannel;
                wanted[Main(c.DeviceId)] = endpoint.RtspUrlWithLogin(c.Channel, 0) + NoBackchannel;
                if (transcode is null)
                    continue;
                // ffmpeg reads the camera's stream back from go2rtc by name, so the camera still has one connection per stream.
                wanted[Sub(c.DeviceId) + H264Suffix] = $"ffmpeg:{Sub(c.DeviceId)}{transcode}";
                wanted[Main(c.DeviceId) + H264Suffix] = $"ffmpeg:{Main(c.DeviceId)}{transcode}";
            }
        }
        var cars = await db.Devices.AsNoTracking().Where(d => d.TypeCode == Cars.Type && d.Status != DeviceStatus.Disabled)
            .Select(d => new { d.DeviceId, d.Ip, d.Config }).ToListAsync(ct);
        foreach (var car in cars)
            if (CarSource(car.Ip, car.Config) is { } source)
            {
                // One stream: the light and the detailed name lead to it alike.
                wanted[Sub(car.DeviceId)] = source;
                wanted[Main(car.DeviceId)] = source;
            }
        return wanted;
    }

    internal async Task SyncAsync(CancellationToken ct)
    {
        var wanted = await WantedAsync(ct);
        _names = wanted.Keys.ToHashSet(StringComparer.Ordinal);
        var go2rtc = new Go2RtcClient(clients.CreateClient(nameof(VideoStreams)));
        var known = await go2rtc.ListAsync(ct);
        foreach (var (name, source) in wanted)
        {
            var there = known.ContainsKey(name);
            var same = _pushed.TryGetValue(name, out var pushed) && pushed == source;
            if (there && same)
                continue;
            if (there)
                await go2rtc.DeleteAsync(name, ct); // a new address or login
            await go2rtc.PutAsync(name, source, ct);
            _pushed[name] = source;
            logger.LogInformation("go2rtc: stream {Stream} {What}", name, there ? "updated" : "added");
        }
        foreach (var name in known.Keys.Where(n => Ours(n) && !wanted.ContainsKey(n)))
        {
            await go2rtc.DeleteAsync(name, ct);
            _pushed.Remove(name);
            logger.LogInformation("go2rtc: stream {Stream} removed", name);
        }
    }

    /// <summary>The cameras for the video widget, with their streams; no addresses, no logins.</summary>
    public async Task<VideoStreamsDto> ListAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var channels = await db.NvrChannels.AsNoTracking().ToListAsync(ct);
        var ids = channels.Select(c => c.DeviceId).ToList();
        var devices = await db.Devices.AsNoTracking().Include(d => d.Zone)
            .Where(d => ids.Contains(d.DeviceId) && d.Status != DeviceStatus.Disabled).ToListAsync(ct);
        var h264 = Transcodes && !node.ReadOnly;
        var cameras = devices.Join(channels, d => d.DeviceId, c => c.DeviceId, (d, c) => new VideoCameraDto(
                d.DeviceId, d.Name, d.ZoneId, d.Zone?.Name, c.NvrId, c.Channel, Sub(d.DeviceId), Main(d.DeviceId), d.Status.ToString().ToLowerInvariant(),
                Rotate: VideoRotation.Of(d.Config),
                SubH264: h264 ? Sub(d.DeviceId) + H264Suffix : null, MainH264: h264 ? Main(d.DeviceId) + H264Suffix : null))
            .ToList();
        var cars = await db.Devices.AsNoTracking().Include(d => d.Zone)
            .Where(d => d.TypeCode == Cars.Type && d.Status != DeviceStatus.Disabled).ToListAsync(ct);
        cameras.AddRange(cars.Where(d => CarSource(d.Ip, d.Config) is not null).Select(d => new VideoCameraDto(
            d.DeviceId, d.Name, d.ZoneId, d.Zone?.Name, 0, 0, Sub(d.DeviceId), Main(d.DeviceId), d.Status.ToString().ToLowerInvariant(), Cars.Type, VideoRotation.Of(d.Config))));
        // A guard-cam (ESP32-CAM) has no stream: the widget offers it as frames only, taken by its snapshot command.
        var guards = await db.Devices.AsNoTracking().Include(d => d.Zone)
            .Where(d => d.TypeCode == Cameras.GuardCam && d.Status != DeviceStatus.Disabled).ToListAsync(ct);
        cameras.AddRange(guards.Where(d => cameras.All(c => c.DeviceId != d.DeviceId)).Select(d => new VideoCameraDto(
            d.DeviceId, d.Name, d.ZoneId, d.Zone?.Name, 0, 0, Sub(d.DeviceId), Main(d.DeviceId), d.Status.ToString().ToLowerInvariant(), Cameras.GuardCam, VideoRotation.Of(d.Config))));
        return new VideoStreamsDto(Enabled, cameras.OrderBy(c => c.Zone ?? "￿").ThenBy(c => c.Name).ToList());
    }

    /// <summary>The src of a request as Caddy forwards it (X-Forwarded-Uri), only when there is exactly one.</summary>
    public static string? SourceOf(string? forwardedUri)
    {
        if (string.IsNullOrEmpty(forwardedUri))
            return null;
        var q = forwardedUri.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
            return null;
        var values = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(forwardedUri[q..]);
        return values.TryGetValue("src", out var src) && src.Count == 1 ? src[0] : null;
    }
}
