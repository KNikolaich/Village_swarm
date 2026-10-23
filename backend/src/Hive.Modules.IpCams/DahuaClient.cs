using System.Globalization;
using System.Net;

namespace Hive.Modules.IpCams;

/// <summary>Where an NVR is and how to log in (the password already decrypted).</summary>
public sealed record NvrEndpoint(string Host, int Port, string Username, string Password, int RtspPort = 554)
{
    public Uri BaseUri => new UriBuilder(Uri.UriSchemeHttp, Host, Port).Uri;

    private string RtspHost => RtspPort == 554 ? Host : $"{Host}:{RtspPort.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The sub-stream of a channel, kept for the video stage (contracts/ipcam.md). No login in it: safe to show.</summary>
    public string RtspUrl(int channel) => $"rtsp://{RtspHost}/cam/realmonitor?channel={channel}&subtype=1";

    /// <summary>The stream with the login in it, only for ffmpeg: never logged or sent to the browser.</summary>
    public string RtspUrlWithLogin(int channel, int subtype) =>
        $"rtsp://{Uri.EscapeDataString(Username)}:{Uri.EscapeDataString(Password)}@{Host}:{RtspPort.ToString(CultureInfo.InvariantCulture)}" +
        $"/cam/realmonitor?channel={channel.ToString(CultureInfo.InvariantCulture)}&subtype={subtype.ToString(CultureInfo.InvariantCulture)}";
}

public sealed record ProbeChannelDto(int Channel, string? Title, bool? Connected, string RtspUrl);

/// <summary>
/// "Проверить": Ok, or Error = auth (wrong login), locked (the NVR locked the login after wrong ones), unreachable (no answer on
/// host:port) or not_dahua (an answer, but not the Dahua API). Detail is the technical reason for the curious.
/// </summary>
public sealed record ProbeResultDto(bool Ok, string? Error, string? Detail, string? DeviceType, IReadOnlyList<ProbeChannelDto> Channels);

/// <summary>The NVR refused the login (<see cref="Error"/>), or the hive held the request back (<see cref="Block"/>).</summary>
public sealed class NvrAuthException(string message, NvrAuthError? error = null, NvrBlock? block = null) : Exception(message)
{
    public NvrAuthError? Error { get; } = error;
    public NvrBlock? Block { get; } = block;

    public bool Locked => Error?.Locked == true || Block?.Kind == NvrLogins.Locked;
}

/// <summary>
/// The Dahua HTTP API of one NVR (contracts/ipcam.md), digest auth through the handler's credentials. One per NVR:
/// it keeps a connection for snapshots next to the long-lived event stream.
/// </summary>
public sealed class DahuaClient : IDisposable
{
    public const string AttachPath = "/cgi-bin/eventManager.cgi?action=attach&codes=[VideoMotion,VideoLoss]&heartbeat=5";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Short: on a third-party camera the RTSP frame is still to be tried after it.</summary>
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(6);
    private readonly HttpClient _http;

    private readonly NvrLogins? _logins;

    /// <summary>With <paramref name="logins"/>, every request first asks whether the NVR may be asked (see <see cref="NvrLogins"/>).</summary>
    public DahuaClient(NvrEndpoint endpoint, NvrLogins? logins = null)
    {
        Endpoint = endpoint;
        _logins = logins;
        var handler = new SocketsHttpHandler
        {
            // Digest (what Dahua asks for) or basic, whichever the NVR's challenge names.
            Credentials = new NetworkCredential(endpoint.Username, endpoint.Password),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            UseProxy = false,
        };
        _http = new HttpClient(handler) { BaseAddress = endpoint.BaseUri, Timeout = Timeout.InfiniteTimeSpan };
    }

    public NvrEndpoint Endpoint { get; }

    /// <summary>
    /// A JPEG of the channel (1-based) right now, or <see cref="SnapshotFailedException"/> saying why not: the HTTP status,
    /// the content type and the start of whatever the NVR sent instead.
    /// </summary>
    public async Task<byte[]> SnapshotAsync(int channel, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SnapshotTimeout);
        Guard();
        try
        {
            using var response = await _http.GetAsync($"/cgi-bin/snapshot.cgi?channel={channel.ToString(CultureInfo.InvariantCulture)}", timeout.Token);
            await RefusedAsync(response, timeout.Token);
            var body = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            var type = response.Content.Headers.ContentType?.MediaType ?? "no content type";
            if (!response.IsSuccessStatusCode || !Hive.Modules.Media.PhotoIngestService.IsJpeg(body))
                throw new SnapshotFailedException(
                    $"cgi: HTTP {(int)response.StatusCode}, {type}, {body.Length} bytes{(body.Length > 0 ? $": {Snapshots.Peek(body)}" : "")}");
            return body;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SnapshotFailedException($"cgi: no answer in {SnapshotTimeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
            throw new SnapshotFailedException($"cgi: {ex.Message}");
        }
    }

    /// <summary>Opens the event stream; the caller reads it until it ends and disposes the response.</summary>
    public async Task<HttpResponseMessage> AttachAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        Guard();
        var response = await _http.GetAsync(AttachPath, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        try
        {
            await RefusedAsync(response, timeout.Token);
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task<ProbeResultDto> ProbeAsync(CancellationToken ct)
    {
        string type;
        try
        {
            var answer = await GetTextAsync("/cgi-bin/magicBox.cgi?action=getDeviceType", ct);
            type = Values(answer).GetValueOrDefault("type") ?? "";
            if (type.Length == 0)
                return Failed("not_dahua", "getDeviceType: no type= in the answer");
        }
        catch (NvrAuthException ex)
        {
            return Failed(ex.Locked ? "locked" : "auth", ex.Message);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            return Failed("not_dahua", ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return Failed("unreachable", ex.Message);
        }

        var titles = new SortedDictionary<int, string?>();
        try
        {
            // table.ChannelTitle[0].Name=Ворота
            foreach (var (key, value) in Values(await GetTextAsync("/cgi-bin/configManager.cgi?action=getConfig&name=ChannelTitle", ct)))
                if (Indexed(key, "table.ChannelTitle[", "].Name") is { } i)
                    titles[i + 1] = value;
        }
        catch (Exception ex) when (Optional(ex, ct))
        {
            // Some firmware hides the titles from a login without rights to the config: channels can still be typed in.
        }

        var connected = new Dictionary<int, bool>();
        try
        {
            // states[0].channel=0, states[0].connectionState=Connected
            var values = Values(await GetTextAsync("/cgi-bin/LogicDeviceManager.cgi?action=getCameraState&uniqueChannels[0]=-1", ct));
            foreach (var (key, value) in values)
                if (Indexed(key, "states[", "].channel") is { } i
                    && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ch)
                    && values.TryGetValue($"states[{i.ToString(CultureInfo.InvariantCulture)}].connectionState", out var state))
                    connected[ch + 1] = state.Equals("Connected", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (Optional(ex, ct))
        {
            // Older firmware does not know getCameraState.
        }

        var channels = titles.Keys.Union(connected.Keys).Order()
            .Select(c => new ProbeChannelDto(c, titles.GetValueOrDefault(c), connected.TryGetValue(c, out var on) ? on : null, Endpoint.RtspUrl(c)))
            .ToList();
        return new ProbeResultDto(true, null, null, type, channels);
    }

    private static bool Optional(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is HttpRequestException or NvrAuthException or OperationCanceledException or IOException;

    private static ProbeResultDto Failed(string error, string detail) => new(false, error, detail, null, []);

    private async Task<string> GetTextAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        Guard();
        using var response = await _http.GetAsync(path, timeout.Token);
        await RefusedAsync(response, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeout.Token);
    }

    /// <summary>Nothing goes to an NVR that locked the login, or to one that just refused this password (see <see cref="NvrLogins"/>).</summary>
    private void Guard()
    {
        if (_logins?.Blocked(Endpoint) is { } block)
            throw new NvrAuthException(_logins.Explain(block), block: block);
    }

    /// <summary>
    /// A refusal is read for what the NVR says (locked, attempts left) and reported, so the next requests wait; any other answer
    /// means the login works. A 403 without a lock may be a missing right of the account and does not count as a wrong login.
    /// </summary>
    private async Task RefusedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            _logins?.Succeeded(Endpoint);
            return;
        }
        var error = NvrAuthError.Parse((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        if (error.Locked || response.StatusCode == HttpStatusCode.Unauthorized)
            _logins?.Failed(Endpoint, error);
        throw new NvrAuthException($"login refused: {error}", error);
    }

    /// <summary>The key=value lines Dahua answers with.</summary>
    internal static Dictionary<string, string> Values(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
                values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return values;
    }

    private static int? Indexed(string key, string prefix, string suffix) =>
        key.StartsWith(prefix, StringComparison.Ordinal) && key.EndsWith(suffix, StringComparison.Ordinal)
        && int.TryParse(key.AsSpan(prefix.Length, key.Length - prefix.Length - suffix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : null;

    public void Dispose() => _http.Dispose();
}
