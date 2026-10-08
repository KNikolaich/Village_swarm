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
    public string? MqttUser { get; set; }

    /// <summary>SHA-256 (hex) of the bearer token the hornet uses for photo uploads; issued at provisioning.</summary>
    public string? UploadTokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public string? Notes { get; set; }
}
