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
}
