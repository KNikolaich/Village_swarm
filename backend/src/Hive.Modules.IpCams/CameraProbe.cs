using Hive.Domain;

namespace Hive.Modules.IpCams;

/// <summary>
/// "Проверить" for a camera on its own (contracts/ipcam.md): a Dahua camera speaks the NVR's HTTP API (channel 1); any other is
/// asked over ONVIF for its snapshot and stream addresses and whether it can be subscribed to for motion.
/// </summary>
public static class CameraProbe
{
    public static async Task<ProbeResultDto> ProbeAsync(NvrEndpoint endpoint, NvrLogins logins, CancellationToken ct)
    {
        using (var dahua = new DahuaClient(endpoint, logins))
        {
            var answer = await dahua.ProbeAsync(ct);
            if (answer.Ok)
                return answer with
                {
                    Vendor = CameraVendors.Dahua,
                    Model = answer.DeviceType,
                    Motion = CameraVendors.Dahua,
                    Channels = [new ProbeChannelDto(1, answer.Channels.FirstOrDefault()?.Title, true, endpoint.RtspUrl(1))],
                };
            // A refused login is final: trying ONVIF with the same one would only count another wrong attempt.
            if (answer.Error is "auth" or "locked")
                return answer;
        }

        using var onvif = new OnvifClient(endpoint, logins);
        try
        {
            var camera = await onvif.ProbeAsync(ct);
            var model = string.Join(' ', new[] { camera.Manufacturer, camera.Model }.OfType<string>());
            var shown = endpoint with { SnapshotUrl = camera.SnapshotUrl, RtspMain = camera.RtspMain, RtspSub = camera.RtspSub };
            return new ProbeResultDto(true, null, null, model, [new ProbeChannelDto(1, null, true, shown.RtspUrl(1))],
                CameraVendors.Onvif, model, camera.SnapshotUrl, camera.RtspMain, camera.RtspSub, camera.Events ? camera.EventsUrl : null,
                camera.Events ? CameraVendors.Onvif : "none");
        }
        catch (NvrAuthException ex)
        {
            return new ProbeResultDto(false, ex.Locked ? "locked" : "auth", ex.Message, null, []);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OnvifFaultException or OperationCanceledException or IOException)
        {
            var reached = ex is HttpRequestException { StatusCode: not null } or OnvifFaultException;
            return new ProbeResultDto(false, reached ? "not_camera" : "unreachable", ex.Message, null, []);
        }
    }
}
