namespace Hive.Modules.IpCams;

public sealed class IpCamOptions
{
    public const string Section = "IpCams";

    public bool Enabled { get; set; } = true;

    /// <summary>Another motion of the same channel within this time is ignored.</summary>
    public int CooldownS { get; set; } = 20;

    public int Photos { get; set; } = 3;
    public int PhotoIntervalMs { get; set; } = 700;

    /// <summary>An NVR without a working event stream this long takes its cameras offline.</summary>
    public int OfflineAfterS { get; set; } = 60;

    /// <summary>A stream silent this long (no heartbeat, no event) is taken for dead and reconnected.</summary>
    public int StaleStreamS { get; set; } = 30;

    /// <summary>After the NVR refused a login, the same password is not tried again for this long (Dahua locks after a few).</summary>
    public int AuthRetryS { get; set; } = 60;
}
