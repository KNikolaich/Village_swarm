using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
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
}

/// <summary>A camera that can be watched live: its stream names in go2rtc (sub: light, main: detailed).</summary>
public sealed record VideoCameraDto(
    string DeviceId, string Name, int? ZoneId, string? Zone, int NvrId, int Channel, string Sub, string Main, string Status);

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

    private readonly Channel<bool> _changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, string> _pushed = new(StringComparer.Ordinal);
    private volatile IReadOnlySet<string> _names = new HashSet<string>();

    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.Go2RtcUrl);

    /// <summary>The cameras changed: go2rtc is brought in line now rather than at the next round.</summary>
    public void Changed() => _changed.Writer.TryWrite(true);

    /// <summary>Whether a request for this stream may go to go2rtc: a name of the list, nothing else (no addresses).</summary>
    public bool Allows(string? src) => src is not null && _names.Contains(src);

    public static string Sub(string deviceId) => deviceId + SubSuffix;
    public static string Main(string deviceId) => deviceId + MainSuffix;

    /// <summary>A stream the hive manages: one of an IP camera's two.</summary>
    public static bool Ours(string name) => name.StartsWith("ipcam", StringComparison.Ordinal) && (name.EndsWith(SubSuffix, StringComparison.Ordinal) || name.EndsWith(MainSuffix, StringComparison.Ordinal));

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
            foreach (var c in nvr.Channels.Where(c => live.Contains(c.DeviceId)))
            {
                wanted[Sub(c.DeviceId)] = endpoint.RtspUrlWithLogin(c.Channel, 1);
                wanted[Main(c.DeviceId)] = endpoint.RtspUrlWithLogin(c.Channel, 0);
            }
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
        var cameras = devices.Join(channels, d => d.DeviceId, c => c.DeviceId, (d, c) => new VideoCameraDto(
                d.DeviceId, d.Name, d.ZoneId, d.Zone?.Name, c.NvrId, c.Channel, Sub(d.DeviceId), Main(d.DeviceId), d.Status.ToString().ToLowerInvariant()))
            .OrderBy(c => c.Zone ?? "￿").ThenBy(c => c.Name).ToList();
        return new VideoStreamsDto(Enabled, cameras);
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
