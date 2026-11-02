using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests.Integration;

/// <summary>
/// An ONVIF camera that is not a Dahua (contracts/ipcam.md): device information, capabilities, media profiles with snapshot and
/// stream URIs, and a PullPoint for motion, over SOAP with WS-UsernameToken digest checked for real. Its addresses name another
/// host (10.0.0.99), as a camera behind NAT does, so the hive must use the one it reached it by.
/// </summary>
public sealed class FakeOnvifCamera : IAsyncDisposable
{
    public const string User = "admin";
    public const string Password = "onvif-secret-1";
    private const string ItsOwnHost = "10.0.0.99";

    private readonly WebApplication _app;
    private readonly Channel<bool> _motion = Channel.CreateUnbounded<bool>();
    private readonly byte[] _jpeg;

    public int Port { get; private set; }

    /// <summary>False: no event service (no PullPoint), the camera gives snapshots only.</summary>
    public bool Events { get; init; } = true;

    /// <summary>True: a PTZ service (moves, stops, two presets); its calls are kept in <see cref="PtzCalls"/>.</summary>
    public bool Ptz { get; init; }

    /// <summary>"move {x} {y} {zoom} {timeout}", "stop", "goto {token}", in the order they came.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> PtzCalls { get; } = new();

    public int Snapshots;
    public int Pulls;
    public int Subscriptions;

    private FakeOnvifCamera(WebApplication app, byte[] jpeg)
    {
        _app = app;
        _jpeg = jpeg;
    }

    public static async Task<FakeOnvifCamera> StartAsync(bool events = true, bool ptz = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeOnvifCamera(app, await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "scenarios", "photos", "frame2.jpg"))) { Events = events, Ptz = ptz };
        app.MapPost("/onvif/{service}", fake.SoapAsync);
        app.MapPost("/onvif/pull/{id}", (HttpContext ctx) => fake.SoapAsync(ctx, "pull"));
        app.MapGet("/snap.jpg", async (HttpContext ctx) =>
        {
            Interlocked.Increment(ref fake.Snapshots);
            ctx.Response.ContentType = "image/jpeg";
            await ctx.Response.Body.WriteAsync(fake._jpeg);
        });
        await app.StartAsync();
        fake.Port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
        return fake;
    }

    /// <summary>Someone walks by: the next PullMessages carries IsMotion=true.</summary>
    public void Motion() => _motion.Writer.TryWrite(true);

    private string Own(string path) => $"http://{ItsOwnHost}:{Port}{path}";

    private async Task SoapAsync(HttpContext ctx, string service)
    {
        var body = XDocument.Parse(await new StreamReader(ctx.Request.Body).ReadToEndAsync());
        var operation = body.Descendants().First(e => e.Name.LocalName == "Body").Elements().First().Name.LocalName;
        if (operation != "GetSystemDateAndTime" && !Authorized(body))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await Reply(ctx, "<s:Fault><s:Code><s:Value>s:Sender</s:Value><s:Subcode><s:Value>ter:NotAuthorized</s:Value></s:Subcode></s:Code>" +
                             "<s:Reason><s:Text xml:lang=\"en\">Sender not Authorized</s:Text></s:Reason></s:Fault>");
            return;
        }
        switch (operation)
        {
            case "GetSystemDateAndTime":
                await Reply(ctx, "<tds:GetSystemDateAndTimeResponse><tds:SystemDateAndTime><tt:DateTimeType>NTP</tt:DateTimeType></tds:SystemDateAndTime></tds:GetSystemDateAndTimeResponse>");
                break;
            case "GetDeviceInformation":
                await Reply(ctx, "<tds:GetDeviceInformationResponse><tds:Manufacturer>Asecam</tds:Manufacturer><tds:Model>8MP-PTZ</tds:Model>" +
                                 "<tds:FirmwareVersion>1.0</tds:FirmwareVersion></tds:GetDeviceInformationResponse>");
                break;
            case "GetCapabilities":
                var events = Events ? $"<tt:Events><tt:XAddr>{Own("/onvif/events")}</tt:XAddr></tt:Events>" : "";
                var ptzService = Ptz ? $"<tt:PTZ><tt:XAddr>{Own("/onvif/ptz")}</tt:XAddr></tt:PTZ>" : "";
                await Reply(ctx, $"<tds:GetCapabilitiesResponse><tds:Capabilities><tt:Media><tt:XAddr>{Own("/onvif/media")}</tt:XAddr></tt:Media>{events}{ptzService}" +
                                 "</tds:Capabilities></tds:GetCapabilitiesResponse>");
                break;
            case "GetProfiles":
                // A PTZ camera says so in its second profile here, so the hive has to look for it rather than take the first.
                var ptzConfiguration = Ptz ? "<tt:PTZConfiguration token=\"ptz0\"/>" : "";
                await Reply(ctx, "<trt:GetProfilesResponse><trt:Profiles token=\"main\" fixed=\"true\"/>" +
                                 $"<trt:Profiles token=\"sub\" fixed=\"true\">{ptzConfiguration}</trt:Profiles></trt:GetProfilesResponse>");
                break;
            case "GetSnapshotUri":
                await Reply(ctx, $"<trt:GetSnapshotUriResponse><trt:MediaUri><tt:Uri>{Own("/snap.jpg")}</tt:Uri></trt:MediaUri></trt:GetSnapshotUriResponse>");
                break;
            case "GetStreamUri":
                var profile = body.Descendants().First(e => e.Name.LocalName == "ProfileToken").Value;
                await Reply(ctx, $"<trt:GetStreamUriResponse><trt:MediaUri><tt:Uri>rtsp://{ItsOwnHost}:554/{profile}</tt:Uri></trt:MediaUri></trt:GetStreamUriResponse>");
                break;
            case "CreatePullPointSubscription" when Events:
                Interlocked.Increment(ref Subscriptions);
                await Reply(ctx, $"<tev:CreatePullPointSubscriptionResponse><tev:SubscriptionReference><wsa:Address>{Own("/onvif/pull/1")}</wsa:Address>" +
                                 "</tev:SubscriptionReference></tev:CreatePullPointSubscriptionResponse>");
                break;
            case "PullMessages":
                Interlocked.Increment(ref Pulls);
                using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                {
                    var moved = false;
                    try
                    {
                        moved = await _motion.Reader.ReadAsync(wait.Token);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    var message = moved
                        ? "<wsnt:NotificationMessage><wsnt:Topic Dialect=\"http://www.onvif.org/ver10/tev/topicExpression/ConcreteSet\">tns1:RuleEngine/CellMotionDetector/Motion</wsnt:Topic>" +
                          "<wsnt:Message><tt:Message UtcTime=\"2026-10-24T09:00:00Z\"><tt:Source><tt:SimpleItem Name=\"VideoSourceConfigurationToken\" Value=\"0\"/></tt:Source>" +
                          "<tt:Data><tt:SimpleItem Name=\"IsMotion\" Value=\"true\"/></tt:Data></tt:Message></wsnt:Message></wsnt:NotificationMessage>"
                        : "";
                    await Reply(ctx, $"<tev:PullMessagesResponse><tev:CurrentTime>2026-10-24T09:00:00Z</tev:CurrentTime>{message}</tev:PullMessagesResponse>");
                }
                break;
            case "Renew":
                await Reply(ctx, "<wsnt:RenewResponse><wsnt:TerminationTime>2026-10-24T09:02:00Z</wsnt:TerminationTime></wsnt:RenewResponse>");
                break;
            case "ContinuousMove" when Ptz && service == "ptz":
                string? Axis(string axis, string attribute) =>
                    (string?)body.Descendants().FirstOrDefault(e => e.Name.LocalName == axis)?.Attribute(attribute);
                var timeout = body.Descendants().FirstOrDefault(e => e.Name.LocalName == "Timeout")?.Value;
                var profileToken = body.Descendants().First(e => e.Name.LocalName == "ProfileToken").Value;
                PtzCalls.Enqueue($"move {Axis("PanTilt", "x") ?? "-"} {Axis("PanTilt", "y") ?? "-"} {Axis("Zoom", "x") ?? "-"} {timeout} {profileToken}");
                await Reply(ctx, "<tptz:ContinuousMoveResponse xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\"/>");
                break;
            case "Stop" when Ptz && service == "ptz":
                PtzCalls.Enqueue("stop");
                await Reply(ctx, "<tptz:StopResponse xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\"/>");
                break;
            case "GetPresets" when Ptz && service == "ptz":
                await Reply(ctx, "<tptz:GetPresetsResponse xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\">" +
                                 "<tptz:Preset token=\"1\"><tt:Name>Ворота</tt:Name></tptz:Preset><tptz:Preset token=\"2\"/></tptz:GetPresetsResponse>");
                break;
            case "GotoPreset" when Ptz && service == "ptz":
                PtzCalls.Enqueue("goto " + body.Descendants().First(e => e.Name.LocalName == "PresetToken").Value);
                await Reply(ctx, "<tptz:GotoPresetResponse xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\"/>");
                break;
            case "Unsubscribe":
                await Reply(ctx, "<wsnt:UnsubscribeResponse/>");
                break;
            default:
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await Reply(ctx, "<s:Fault><s:Code><s:Value>s:Receiver</s:Value><s:Subcode><s:Value>ter:ActionNotSupported</s:Value></s:Subcode></s:Code></s:Fault>");
                break;
        }
    }

    /// <summary>WS-UsernameToken with PasswordDigest = Base64(SHA1(nonce + created + password)).</summary>
    private static bool Authorized(XDocument body)
    {
        string? Find(string name) => body.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        if (Find("Username") != User || Find("Nonce") is not { } nonce || Find("Created") is not { } created || Find("Password") is not { } digest)
            return false;
        var expected = Convert.ToBase64String(SHA1.HashData([.. Convert.FromBase64String(nonce), .. Encoding.UTF8.GetBytes(created), .. Encoding.UTF8.GetBytes(Password)]));
        return expected == digest;
    }

    private static Task Reply(HttpContext ctx, string body)
    {
        ctx.Response.ContentType = "application/soap+xml; charset=utf-8";
        return ctx.Response.WriteAsync(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" " +
            "xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\" xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\" " +
            "xmlns:tev=\"http://www.onvif.org/ver10/events/wsdl\" xmlns:tt=\"http://www.onvif.org/ver10/schema\" " +
            "xmlns:wsnt=\"http://docs.oasis-open.org/wsn/b-2\" xmlns:wsa=\"http://www.w3.org/2005/08/addressing\" " +
            "xmlns:ter=\"http://www.onvif.org/ver10/error\">" +
            $"<s:Body>{body}</s:Body></s:Envelope>");
    }

    public async ValueTask DisposeAsync()
    {
        _motion.Writer.TryComplete();
        await _app.DisposeAsync();
    }
}
