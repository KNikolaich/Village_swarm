namespace Hive.Contracts.Messages;

// Messages the hive publishes (hive -> dev). Schemas: contracts/schemas/*.schema.json.

/// <summary>vs/v1/dev/{id}/cmd/{name}. <see cref="Cid"/> is the idempotency key.</summary>
public abstract record Command
{
    public int V { get; init; } = 1;
    public required string Cid { get; init; }
    public required long Ts { get; init; }

    /// <summary>The hornet rejects the command with <see cref="AckStatus.Expired"/> after ts + ttl_s.</summary>
    public required int TtlS { get; init; }

    /// <summary>Issuer: user:x, rule:y, tg:z or system:w.</summary>
    public required string By { get; init; }
}

/// <summary>vs/v1/dev/{id}/cmd/relay</summary>
public sealed record RelayCommand : Command
{
    public required string Channel { get; init; }
    public required RelaySet Set { get; init; }

    /// <summary>Max on-time; the hornet switches off by itself even if the server is gone.</summary>
    public int? ForS { get; init; }
}

/// <summary>vs/v1/dev/{id}/cmd/arm</summary>
public sealed record ArmCommand : Command
{
    public required bool Armed { get; init; }
}

/// <summary>vs/v1/dev/{id}/cmd/snapshot</summary>
public sealed record SnapshotCommand : Command
{
    public int? Count { get; init; }
}

public enum RelaySet { On, Off }

/// <summary>vs/v1/dev/{id}/config (retained). Sections a role does not use stay null.</summary>
public sealed record DeviceConfig
{
    public int V { get; init; } = 1;
    public required int Rev { get; init; }
    public required string Name { get; init; }
    public required int HealthIntervalS { get; init; }
    public int? TeleIntervalS { get; init; }
    public FailsafeConfig? Failsafe { get; init; }
    public GuardConfig? Guard { get; init; }
    public CameraConfig? Camera { get; init; }
    public UploadConfig? Upload { get; init; }
}

public sealed record FailsafeConfig
{
    public HeaterFailsafe? Heater { get; init; }
}

public sealed record HeaterFailsafe
{
    public required double MinC { get; init; }
    public required double MaxC { get; init; }
    public required int MaxOnS { get; init; }
    public string? Sensor { get; init; }
}

public sealed record GuardConfig
{
    public bool? Armed { get; init; }
    public int? CooldownS { get; init; }
    public bool? RecordWhenDisarmed { get; init; }
    public int? CheckPhotoEveryMin { get; init; }
}

public sealed record CameraConfig
{
    public string? Frame { get; init; }
    public int? Quality { get; init; }
    public int? Burst { get; init; }
    public int? BurstIntervalMs { get; init; }
    public IrLedMode? IrLed { get; init; }
}

public sealed record UploadConfig
{
    public required string Primary { get; init; }
    public string? Fallback { get; init; }
}

public enum IrLedMode { Auto, On, Off }

/// <summary>vs/v1/hive/{node}/heartbeat</summary>
public sealed record HeartbeatMessage
{
    public int V { get; init; } = 1;
    public required long Ts { get; init; }
    public required BrokerNode Node { get; init; }
    public required HiveRole Role { get; init; }
    public required string Version { get; init; }
}

/// <summary>vs/v1/hive/active (retained)</summary>
public sealed record ActiveNodeMessage
{
    public int V { get; init; } = 1;
    public required long Ts { get; init; }
    public required BrokerNode Node { get; init; }
}

public enum HiveRole { Active, Standby, Observer }
