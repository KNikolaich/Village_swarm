using System.Text.Json;

namespace Hive.Domain;

public enum DeviceStatus
{
    Pending,
    Online,
    Offline,
    Disabled,
}

public enum ZoneKind
{
    Perimeter,
    House,
    Garden,
    Tech,
}

/// <summary>Area of the property, e.g. "Ворота", "Гостиная" (spec 6.4).</summary>
public class Zone
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public ZoneKind Kind { get; set; }

    /// <summary>Place in lists (swarm groups, climate cards): lower first, equal ones by id.</summary>
    public int Sort { get; set; }
}

/// <summary>
/// How the overview is laid out on one kind of screen ("desktop" or "phone"): the widgets in order, each with its
/// width in twelfths of a row and whether it is hidden. One for the whole family, set by an administrator.
/// </summary>
public class DashboardLayout
{
    public required string Screen { get; set; }
    public required JsonDocument Items { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Kind of hornet: guard-cam, meteo, heat... Seeded; provisioning picks one (spec 6.4).</summary>
public class DeviceType
{
    public required string Code { get; set; }
    public required string Title { get; set; }
    public JsonDocument? Caps { get; set; }
    public JsonDocument? DefaultConfig { get; set; }

    /// <summary>No health for this long marks the device offline.</summary>
    public int OfflineAfterS { get; set; } = 180;
}

/// <summary>How a device talks MQTT. Hornets use the vs/v1 contract; Tasmota and OpenBeken use cmnd/stat/tele (contracts/tasmota.md).</summary>
public static class Drivers
{
    public const string Hornet = "hornet";
    public const string Tasmota = "tasmota";
    public const string Zigbee = "zigbee";

    /// <summary>A channel of a Dahua NVR: the hive itself reads its events and snapshots over HTTP (contracts/ipcam.md).</summary>
    public const string IpCam = "ipcam";
}

/// <summary>Device types that take photos: our guard-cams and IP cameras behind an NVR.</summary>
public static class Cameras
{
    public const string GuardCam = "guard-cam";
    public const string IpCam = "ipcam";

    /// <summary>For queries: <c>Cameras.Types.Contains(d.TypeCode)</c> translates to SQL.</summary>
    public static readonly string[] Types = [GuardCam, IpCam];
}

/// <summary>A hornet. <see cref="DeviceId"/> is the MQTT id (guard-gate1), <see cref="Id"/> the surrogate key.</summary>
public class Device
{
    public int Id { get; set; }
    public required string DeviceId { get; set; }
    public required string TypeCode { get; set; }
    public required string Name { get; set; }
    public int? ZoneId { get; set; }
    public Zone? Zone { get; set; }
    public string? Mac { get; set; }
    public string? Hw { get; set; }
    public string? FwVersion { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.Pending;
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>When the family was told this hornet is gone (null: not reported, or back again).</summary>
    public DateTimeOffset? OfflineReportedAt { get; set; }
    public string? Ip { get; set; }
    public int? Rssi { get; set; }
    public int? Boot { get; set; }

    /// <summary>Capabilities, metrics and commands from the last info message.</summary>
    public JsonDocument? Info { get; set; }

    /// <summary>Last retained state (actuators), e.g. {"heater":"on"}.</summary>
    public JsonDocument? State { get; set; }

    public JsonDocument? Config { get; set; }
    public int ConfigRev { get; set; }
    public int? ConfigAppliedRev { get; set; }

    /// <summary>A monitoring session in progress (Hive.Modules.Telemetry.Monitoring): frequent readings for a while; null when off.</summary>
    public JsonDocument? Monitor { get; set; }

    /// <summary>A camera's timelapse (Hive.Modules.Devices.Timelapse): a photo every so often, with its light if asked; null when off.</summary>
    public JsonDocument? Timelapse { get; set; }

    public string? MqttUser { get; set; }

    /// <summary>Who speaks for the device on the broker: our own firmware or a Tasmota/OpenBeken one (see <see cref="Drivers"/>).</summary>
    public string Driver { get; set; } = Drivers.Hornet;

    /// <summary>SHA-256 (hex) of the bearer token the hornet uses for photo uploads; issued at provisioning.</summary>
    public string? UploadTokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public string? Notes { get; set; }

    /// <summary>Place in the swarm list inside its group: lower first, equal ones in the usual order (trouble first, then id).</summary>
    public int Sort { get; set; }

    /// <summary>
    /// What the zone cards and the overview show of this hornet: its readings by metric (temperature_2...) and its
    /// buttons (snapshot, light, relay). Null: the defaults of its kind, chosen by the UI.
    /// </summary>
    public List<string>? Dashboard { get; set; }

    /// <summary>
    /// "Cleared" on the hornet's page: its command history and its journal show only what came after. The rows stay
    /// until the retention cleanup takes them (logs after a month, commands with the rest of the housekeeping).
    /// </summary>
    public DateTimeOffset? CommandsClearedAt { get; set; }

    public DateTimeOffset? LogsClearedAt { get; set; }
}
