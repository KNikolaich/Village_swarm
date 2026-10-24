using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Hive.Modules.IpCams;

/// <summary>What "Проверить" found on an ONVIF camera: who made it, where its snapshots and streams are, whether it tells motion.</summary>
public sealed record OnvifCamera(string? Manufacturer, string? Model, string? SnapshotUrl, string? RtspMain, string? RtspSub, string? EventsUrl, bool Events);

/// <summary>One notification of a PullPoint: its topic and whether it says motion is on (null: not about motion).</summary>
public sealed record OnvifEvent(string Topic, bool? Motion);

/// <summary>
/// ONVIF over SOAP for a camera that is not a Dahua (contracts/ipcam.md): device information, media URIs, and motion through
/// a PullPoint subscription. Each request carries a WS-UsernameToken with a password digest; the HTTP layer answers a digest
/// challenge too, as some cameras ask for both. Requests go through <see cref="NvrLogins"/> like the Dahua ones.
/// </summary>
public sealed class OnvifClient : IDisposable
{
    private const string DevicePath = "/onvif/device_service";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient _http;
    private readonly NvrLogins? _logins;
    private readonly TimeProvider _time;

    public OnvifClient(NvrEndpoint endpoint, NvrLogins? logins = null, TimeProvider? time = null)
    {
        Endpoint = endpoint;
        _logins = logins;
        _time = time ?? TimeProvider.System;
        var handler = new SocketsHttpHandler
        {
            Credentials = new NetworkCredential(endpoint.Username, endpoint.Password),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            UseProxy = false,
        };
        _http = new HttpClient(handler) { BaseAddress = endpoint.BaseUri, Timeout = Timeout.InfiniteTimeSpan };
    }

    public NvrEndpoint Endpoint { get; }

    /// <summary>Who it is, its first two profiles' stream and snapshot URIs, and whether a PullPoint can be made (then dropped).</summary>
    public async Task<OnvifCamera> ProbeAsync(CancellationToken ct)
    {
        var info = await CallAsync(DevicePath, "<tds:GetDeviceInformation/>", ct);
        var manufacturer = Text(info, "Manufacturer");
        var model = Text(info, "Model");

        var caps = await CallAsync(DevicePath, "<tds:GetCapabilities><tds:Category>All</tds:Category></tds:GetCapabilities>", ct);
        var media = Path(XAddr(caps, "Media")) ?? "/onvif/media";
        var events = WithHost(XAddr(caps, "Events"));

        string? snapshot = null, main = null, sub = null;
        var profiles = (await CallAsync(media, "<trt:GetProfiles/>", ct)).Descendants().Where(e => e.Name.LocalName == "Profiles")
            .Select(p => (string?)p.Attribute("token")).OfType<string>().Take(2).ToList();
        if (profiles.Count > 0)
        {
            snapshot = WithHost(await OptionalAsync(() => UriOf(media, $"<trt:GetSnapshotUri><trt:ProfileToken>{Esc(profiles[0])}</trt:ProfileToken></trt:GetSnapshotUri>", ct)));
            main = WithHost(await OptionalAsync(() => UriOf(media, StreamUri(profiles[0]), ct)));
            if (profiles.Count > 1)
                sub = WithHost(await OptionalAsync(() => UriOf(media, StreamUri(profiles[1]), ct)));
        }

        var canTell = false;
        if (events is not null)
        {
            try
            {
                var subscription = await SubscribeAsync(events, ct);
                canTell = true;
                await OptionalAsync(async () =>
                {
                    await UnsubscribeAsync(subscription, ct);
                    return "";
                });
            }
            catch (Exception ex) when (ex is OnvifFaultException or HttpRequestException && !ct.IsCancellationRequested)
            {
                // No PullPoint: the camera works for snapshots only.
            }
        }
        return new OnvifCamera(manufacturer, model, snapshot, main, sub, events, canTell);
    }

    /// <summary>A PullPoint for motion; the address to pull from (absolute, as the camera gave it).</summary>
    public async Task<string> SubscribeAsync(string eventsUrl, CancellationToken ct)
    {
        var answer = await CallAsync(eventsUrl,
            "<tev:CreatePullPointSubscription><tev:InitialTerminationTime>PT120S</tev:InitialTerminationTime></tev:CreatePullPointSubscription>", ct,
            action: "http://www.onvif.org/ver10/events/wsdl/EventPortType/CreatePullPointSubscriptionRequest");
        return WithHost(answer.Descendants().FirstOrDefault(e => e.Name.LocalName == "Address")?.Value.Trim())
               ?? throw new OnvifFaultException("CreatePullPointSubscription: no subscription address");
    }

    /// <summary>Waits up to 10 s on the camera for notifications; an empty list is a quiet camera (and a sign of life).</summary>
    public async Task<IReadOnlyList<OnvifEvent>> PullAsync(string subscription, CancellationToken ct)
    {
        var answer = await CallAsync(subscription,
            "<tev:PullMessages><tev:Timeout>PT10S</tev:Timeout><tev:MessageLimit>20</tev:MessageLimit></tev:PullMessages>", ct,
            action: "http://www.onvif.org/ver10/events/wsdl/PullPointSubscription/PullMessagesRequest", timeout: TimeSpan.FromSeconds(25));
        return ParseEvents(answer);
    }

    public Task RenewAsync(string subscription, CancellationToken ct) =>
        CallAsync(subscription, "<wsnt:Renew><wsnt:TerminationTime>PT120S</wsnt:TerminationTime></wsnt:Renew>", ct,
            action: "http://docs.oasis-open.org/wsn/bw-2/SubscriptionManager/RenewRequest");

    public Task UnsubscribeAsync(string subscription, CancellationToken ct) =>
        CallAsync(subscription, "<wsnt:Unsubscribe/>", ct, action: "http://docs.oasis-open.org/wsn/bw-2/SubscriptionManager/UnsubscribeRequest");

    /// <summary>A sign of life without a login (GetSystemDateAndTime needs none), so it never counts towards a lockout.</summary>
    public async Task AliveAsync(CancellationToken ct) => await CallAsync(DevicePath, "<tds:GetSystemDateAndTime/>", ct, secure: false);

    /// <summary>Motion notifications: a topic about motion (CellMotionDetector/Motion, VideoSource/MotionAlarm…) and its state item.</summary>
    public static IReadOnlyList<OnvifEvent> ParseEvents(XDocument answer) =>
        answer.Descendants().Where(e => e.Name.LocalName == "NotificationMessage").Select(m =>
        {
            var topic = m.Descendants().FirstOrDefault(e => e.Name.LocalName == "Topic")?.Value.Trim() ?? "";
            var data = m.Descendants().FirstOrDefault(e => e.Name.LocalName == "Data");
            var item = data?.Descendants().FirstOrDefault(e => e.Name.LocalName == "SimpleItem"
                && (string?)e.Attribute("Name") is "IsMotion" or "State" or "Motion" or "LogicalState");
            bool? motion = topic.Contains("Motion", StringComparison.OrdinalIgnoreCase) && item is not null
                ? ((string?)item.Attribute("Value"))?.Trim().ToLowerInvariant() is "true" or "1"
                : null;
            return new OnvifEvent(topic, motion);
        }).ToList();

    /// <summary>WS-UsernameToken digest: Base64(SHA1(nonce + created + password)), as ONVIF Core asks.</summary>
    public static string PasswordDigest(byte[] nonce, string created, string password) =>
        Convert.ToBase64String(SHA1.HashData([.. nonce, .. Encoding.UTF8.GetBytes(created), .. Encoding.UTF8.GetBytes(password)]));

    private static string StreamUri(string profile) =>
        "<trt:GetStreamUri><trt:StreamSetup><tt:Stream>RTP-Unicast</tt:Stream><tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport></trt:StreamSetup>" +
        $"<trt:ProfileToken>{Esc(profile)}</trt:ProfileToken></trt:GetStreamUri>";

    private async Task<string?> UriOf(string path, string body, CancellationToken ct) =>
        (await CallAsync(path, body, ct)).Descendants().FirstOrDefault(e => e.Name.LocalName == "Uri")?.Value.Trim();

    private static async Task<string?> OptionalAsync(Func<Task<string?>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is OnvifFaultException or HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<XDocument> CallAsync(string pathOrUrl, string body, CancellationToken ct, string? action = null, bool secure = true, TimeSpan? timeout = null)
    {
        if (secure && _logins?.Blocked(Endpoint) is { } block)
            throw new NvrAuthException(_logins.Explain(block), block: block);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? RequestTimeout);
        var url = Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute) ? absolute : new Uri(Endpoint.BaseUri, pathOrUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(Envelope(body, secure, action, url), Encoding.UTF8, "application/soap+xml"),
        };
        using var response = await _http.SendAsync(request, limit.Token);
        var text = await response.Content.ReadAsStringAsync(limit.Token);
        if (response.StatusCode == HttpStatusCode.Unauthorized || text.Contains("NotAuthorized", StringComparison.Ordinal))
        {
            var error = new NvrAuthError(401, null, null, null);
            if (secure)
                _logins?.Failed(Endpoint, error);
            throw new NvrAuthException($"login refused: {error}", error);
        }
        XDocument xml;
        try
        {
            xml = XDocument.Parse(text);
        }
        catch (System.Xml.XmlException)
        {
            throw new HttpRequestException($"not ONVIF: HTTP {(int)response.StatusCode}, {Snapshots.Peek(Encoding.UTF8.GetBytes(text))}", null, response.StatusCode);
        }
        if (xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault") is { } fault)
            throw new OnvifFaultException($"SOAP fault: {Snapshots.Peek(Encoding.UTF8.GetBytes(fault.Value))}");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
        if (secure)
            _logins?.Succeeded(Endpoint);
        return xml;
    }

    private string Envelope(string body, bool secure, string? action, Uri to)
    {
        var header = new StringBuilder();
        if (secure)
        {
            var nonce = RandomNumberGenerator.GetBytes(16);
            var created = _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            header.Append(
                "<wsse:Security s:mustUnderstand=\"1\" xmlns:wsse=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\" " +
                "xmlns:wsu=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\"><wsse:UsernameToken>" +
                $"<wsse:Username>{Esc(Endpoint.Username)}</wsse:Username>" +
                "<wsse:Password Type=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest\">" +
                $"{PasswordDigest(nonce, created, Endpoint.Password)}</wsse:Password>" +
                "<wsse:Nonce EncodingType=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary\">" +
                $"{Convert.ToBase64String(nonce)}</wsse:Nonce><wsu:Created>{created}</wsu:Created></wsse:UsernameToken></wsse:Security>");
        }
        if (action is not null)
            header.Append($"<wsa:Action>{action}</wsa:Action><wsa:To>{Esc(to.ToString())}</wsa:To>");
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
               "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\" " +
               "xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\" xmlns:tev=\"http://www.onvif.org/ver10/events/wsdl\" " +
               "xmlns:tt=\"http://www.onvif.org/ver10/schema\" xmlns:wsnt=\"http://docs.oasis-open.org/wsn/b-2\" " +
               "xmlns:wsa=\"http://www.w3.org/2005/08/addressing\">" +
               $"<s:Header>{header}</s:Header><s:Body>{body}</s:Body></s:Envelope>";
    }

    private static string? Text(XDocument xml, string name) =>
        xml.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;

    /// <summary>The XAddr of a service in GetCapabilities (Media, Events…).</summary>
    private static string? XAddr(XDocument caps, string service) =>
        caps.Descendants().FirstOrDefault(e => e.Name.LocalName == service)?.Elements().FirstOrDefault(e => e.Name.LocalName == "XAddr")?.Value.Trim();

    /// <summary>The path of a service address: the camera may name a host the hive reaches differently (NAT, another interface).</summary>
    private static string? Path(string? xaddr) =>
        xaddr is not null && Uri.TryCreate(xaddr, UriKind.Absolute, out var u) ? u.PathAndQuery : null;

    /// <summary>
    /// An address the camera gave, with the host the hive reaches it by: a camera behind NAT or with two interfaces names its
    /// own idea of itself. Port, path and query stay.
    /// </summary>
    private string? WithHost(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u))
            return url;
        return new UriBuilder(u) { Host = Endpoint.Host }.Uri.ToString();
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    public void Dispose() => _http.Dispose();
}

/// <summary>The camera answered with a SOAP fault (an operation it does not have, a bad argument).</summary>
public sealed class OnvifFaultException(string message) : Exception(message);
