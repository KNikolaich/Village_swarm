using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests.Integration;

/// <summary>
/// A Dahua NVR on a local port (contracts/ipcam.md): digest auth, the attach event stream with heartbeats, snapshots,
/// and the probe answers. Tests push motion with <see cref="Motion"/> and pull the plug with <see cref="GoDown"/>.
/// </summary>
public sealed class FakeNvr : IAsyncDisposable
{
    public const string User = "admin";
    public const string Password = "nvr-secret-1";
    private const string Realm = "Login to FAKE0123";
    private const string Nonce = "1234567890abcdef";

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<Guid, Channel<string>> _streams = new();
    private readonly byte[] _jpeg;
    private volatile bool _down;

    public int Port { get; private set; }

    public int Snapshots;
    public int StreamsOpened;

    /// <summary>snapshot.cgi takes this long (an NVR fetching a frame from a camera), so overlapping requests can be seen.</summary>
    public volatile int SnapshotDelayMs;

    /// <summary>The most snapshot.cgi requests that were being answered at the same moment.</summary>
    public int MaxConcurrentSnapshots;

    private int _snapshotting;

    /// <summary>Requests that came with a login (each one counts towards a Dahua lockout).</summary>
    public int LoginRequests;

    /// <summary>Set: the account is locked for so many seconds, every request with a login gets Dahua's 403 and its error.</summary>
    public volatile int LockedFor;

    /// <summary>A channel listed here answers so many more snapshots, then none (a busy NVR in the middle of a burst).</summary>
    public ConcurrentDictionary<int, int> SnapshotsLeft { get; } = new();

    /// <summary>Counts down a channel's snapshots left (only for a channel listed); true once they are spent.</summary>
    private bool SpentAll(int channel)
    {
        while (SnapshotsLeft.TryGetValue(channel, out var left))
            if (SnapshotsLeft.TryUpdate(channel, left - 1, left))
                return left <= 0;
        return false;
    }

    /// <summary>Channels whose snapshot.cgi answers like a Dahua NVR with a third-party ONVIF camera: 200, text, no JPEG.</summary>
    public ConcurrentDictionary<int, bool> NoSnapshot { get; } = new();

    private FakeNvr(WebApplication app, byte[] jpeg)
    {
        _app = app;
        _jpeg = jpeg;
    }

    public static async Task<FakeNvr> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeNvr(app, await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "scenarios", "photos", "frame0.jpg")));
        app.Map("/cgi-bin/{script}", fake.HandleAsync);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        fake.Port = new Uri(address).Port;
        return fake;
    }

    /// <summary>VideoMotion Start of a channel, as the NVR counts them (from 0).</summary>
    public void Motion(int index) => Send($"Code=VideoMotion;action=Start;index={index};data={{\n   \"SmartMotionEnable\" : false\n}}");

    public int OpenStreams => _streams.Count;

    /// <summary>The NVR stops answering: streams are cut, new requests fail.</summary>
    public void GoDown()
    {
        _down = true;
        foreach (var s in _streams.Values)
            s.Writer.TryComplete();
    }

    public void ComeBack() => _down = false;

    private void Send(string body)
    {
        foreach (var s in _streams.Values)
            s.Writer.TryWrite(body);
    }

    private async Task HandleAsync(HttpContext ctx, string script)
    {
        if (_down)
        {
            ctx.Abort();
            return;
        }
        var withLogin = ctx.Request.Headers.Authorization.ToString().StartsWith("Digest ", StringComparison.Ordinal);
        if (withLogin)
            Interlocked.Increment(ref LoginRequests);
        if (withLogin && LockedFor > 0)
        {
            // What the real NVR at the dacha answered once the hive had tried a stale password too often.
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Response.ContentType = "text/plain;charset=utf-8";
            await ctx.Response.WriteAsync($"Error {{\"ErrorCode\":268632081,\"Result\":false,\"RmLock\":{LockedFor},\"RmLogin\":0}}");
            return;
        }
        if (!Authorized(ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = $"Digest realm=\"{Realm}\", qop=\"auth\", nonce=\"{Nonce}\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"";
            if (withLogin)
                await ctx.Response.WriteAsync("Error {\"ErrorCode\":268632085,\"Result\":false,\"RmLock\":0,\"RmLogin\":4}");
            return;
        }

        var action = ctx.Request.Query["action"].ToString();
        switch (script)
        {
            case "magicBox.cgi" when action == "getDeviceType":
                await ctx.Response.WriteAsync("type=DHI-NVR4104HS-P-4KS2\r\n");
                break;
            case "configManager.cgi" when ctx.Request.Query["name"] == "ChannelTitle":
                await ctx.Response.WriteAsync("table.ChannelTitle[0].Name=Ворота\r\ntable.ChannelTitle[1].Name=Двор\r\ntable.ChannelTitle[2].Name=Сад\r\n");
                break;
            case "LogicDeviceManager.cgi":
                await ctx.Response.WriteAsync("states[0].channel=0\r\nstates[0].connectionState=Connected\r\nstates[1].channel=1\r\nstates[1].connectionState=Connected\r\nstates[2].channel=2\r\nstates[2].connectionState=Disconnected\r\n");
                break;
            case "snapshot.cgi" when int.TryParse(ctx.Request.Query["channel"], out var ch)
                                     && (NoSnapshot.ContainsKey(ch) || SpentAll(ch)):
                Interlocked.Increment(ref Snapshots);
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync("Error\r\nInternal Error\r\n");
                break;
            case "snapshot.cgi":
                Interlocked.Increment(ref Snapshots);
                var now = Interlocked.Increment(ref _snapshotting);
                int seen;
                while ((seen = MaxConcurrentSnapshots) < now && Interlocked.CompareExchange(ref MaxConcurrentSnapshots, now, seen) != seen)
                {
                }
                try
                {
                    if (SnapshotDelayMs > 0)
                        await Task.Delay(SnapshotDelayMs);
                    ctx.Response.ContentType = "image/jpeg";
                    await ctx.Response.Body.WriteAsync(_jpeg);
                }
                finally
                {
                    Interlocked.Decrement(ref _snapshotting);
                }
                break;
            case "eventManager.cgi" when action == "attach":
                await StreamAsync(ctx);
                break;
            default:
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("Error\r\nBad Request!\r\n");
                break;
        }
    }

    private async Task StreamAsync(HttpContext ctx)
    {
        var id = Guid.NewGuid();
        var events = Channel.CreateUnbounded<string>();
        _streams[id] = events;
        Interlocked.Increment(ref StreamsOpened);
        try
        {
            ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=myboundary";
            await ctx.Response.StartAsync();
            await PartAsync(ctx, "Heartbeat");
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                using var beat = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                beat.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    if (!await events.Reader.WaitToReadAsync(beat.Token))
                        break; // GoDown
                    while (events.Reader.TryRead(out var body))
                        await PartAsync(ctx, body);
                }
                catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
                {
                    await PartAsync(ctx, "Heartbeat");
                }
            }
            if (_down)
                ctx.Abort();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _streams.TryRemove(id, out _);
        }
    }

    private static async Task PartAsync(HttpContext ctx, string body)
    {
        var bytes = Encoding.UTF8.GetByteCount(body);
        await ctx.Response.WriteAsync($"--myboundary\r\nContent-Type: text/plain\r\nContent-Length: {bytes}\r\n\r\n{body}\r\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }

    /// <summary>RFC 2617 digest with qop=auth, checked for real: a wrong password is refused like the NVR does.</summary>
    private static bool Authorized(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Digest ", StringComparison.Ordinal))
            return false;
        // The uri holds commas of its own ("codes=[VideoMotion,VideoLoss]"): split on the quoting, not on every comma.
        var p = System.Text.RegularExpressions.Regex.Matches(header[7..], "(\\w+)=(?:\"([^\"]*)\"|([^,\\s]*))")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value, StringComparer.OrdinalIgnoreCase);
        if (p.GetValueOrDefault("username") != User || p.GetValueOrDefault("realm") != Realm || p.GetValueOrDefault("nonce") != Nonce)
            return false;
        var ha1 = Md5($"{User}:{Realm}:{Password}");
        var ha2 = Md5($"{request.Method}:{p.GetValueOrDefault("uri")}");
        var expected = Md5($"{ha1}:{Nonce}:{p.GetValueOrDefault("nc")}:{p.GetValueOrDefault("cnonce")}:{p.GetValueOrDefault("qop")}:{ha2}");
        return expected == p.GetValueOrDefault("response");
    }

    private static string Md5(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    public async ValueTask DisposeAsync()
    {
        GoDown();
        await _app.DisposeAsync();
    }
}
