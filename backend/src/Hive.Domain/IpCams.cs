namespace Hive.Domain;

/// <summary>
/// A Dahua network video recorder the hive reads motion events and snapshots from (contracts/ipcam.md).
/// Its cameras are the devices of its <see cref="Channels"/>; the password is kept encrypted.
/// </summary>
public class Nvr
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Host { get; set; }
    public int Port { get; set; } = 80;

    /// <summary>RTSP port, for frames grabbed from the stream when snapshot.cgi gives none.</summary>
    public int RtspPort { get; set; } = 554;
    public required string Username { get; set; }

    /// <summary>The password, encrypted with ASP.NET data protection; never sent to the browser.</summary>
    public required string PasswordProtected { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public List<NvrChannel> Channels { get; set; } = [];
}

/// <summary>One NVR channel taken into the swarm as the device <see cref="DeviceId"/>.</summary>
public class NvrChannel
{
    public int Id { get; set; }
    public int NvrId { get; set; }

    /// <summary>1-based, as in snapshot.cgi and the NVR's own screen (the event stream counts from 0).</summary>
    public int Channel { get; set; }

    public required string DeviceId { get; set; }

    /// <summary>Photos on motion even when the guard is off (the event itself is always written).</summary>
    public bool RecordWhenDisarmed { get; set; }

    /// <summary>Where frames come from (<see cref="SnapshotSources"/>): auto tries the NVR's snapshot.cgi, then the RTSP stream.</summary>
    public string SnapshotSource { get; set; } = SnapshotSources.Auto;

    /// <summary>The way that gave the last frame (cgi or rtsp): tried first next time in auto.</summary>
    public string? SnapshotVia { get; set; }
}

/// <summary>
/// How a frame of an NVR channel is taken. Third-party ONVIF cameras often get no snapshot from a Dahua NVR, so a frame is
/// grabbed from the RTSP stream instead (contracts/ipcam.md).
/// </summary>
public static class SnapshotSources
{
    public const string Auto = "auto";
    public const string Cgi = "cgi";
    public const string Rtsp = "rtsp";

    public static bool IsKnown(string? s) => s is Auto or Cgi or Rtsp;
}
