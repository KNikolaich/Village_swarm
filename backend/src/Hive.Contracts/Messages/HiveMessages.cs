using System.Text.Json.Serialization;

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

/// <summary>vs/v1/dev/{id}/cmd/ir (role irtx): one infrared code, as Tasmota's IRSend takes it.</summary>
public sealed record IrCommand : Command
{
    public required string Protocol { get; init; }
    public required int Bits { get; init; }
    public required string Data { get; init; }
    public int? Repeat { get; init; }
}

/// <summary>vs/v1/dev/{id}/cmd/arm</summary>
public sealed record ArmCommand : Command
{
    public required bool Armed { get; init; }
}

/// <summary>vs/v1/dev/{id}/cmd/ota: download <see cref="Url"/>, check <see cref="Md5"/> and install.</summary>
public sealed record OtaCommand : Command
{
    public required string Url { get; init; }
    public required string Md5 { get; init; }
    public required long Size { get; init; }
    public required string Version { get; init; }

    /// <summary>Ed25519 signature (hex) of the SHA-512 of the image; hornets that carry the public key require it.</summary>
    public string? Sig { get; init; }
}

/// <summary>vs/v1/dev/{id}/cmd/wifi: try <see cref="Ssid"/>; if the hornet does not reach the hive over it, the old network comes back.</summary>
public sealed record WifiCommand : Command
{
    /// <summary>Required except for <see cref="WifiMode.Forget"/>, which has neither.</summary>
    public string? Ssid { get; init; }
    public string? Pass { get; init; }

    /// <summary>stage: keep as a spare network; switch (the default): restart onto it now, with a way back; forget: drop the spare.</summary>
    public WifiMode? Mode { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<WifiMode>))]
public enum WifiMode
{
    Stage,
    Switch,
    Forget,
}

/// <summary>vs/v1/dev/{id}/cmd/snapshot</summary>
public sealed record SnapshotCommand : Command
{
    public int? Count { get; init; }
}

public enum RelaySet { On, Off }

public enum SleepMode { Deep, Light }

/// <summary>vs/v1/dev/{id}/config (retained). Sections a role does not use stay null.</summary>
public sealed record DeviceConfig
{
    public int V { get; init; } = 1;
    public required int Rev { get; init; }
    public required string Name { get; init; }
    public required int HealthIntervalS { get; init; }
    public int? TeleIntervalS { get; init; }

    /// <summary>Battery hornets sleep this long between readings; null or 0: always awake.</summary>
    public int? SleepS { get; init; }

    /// <summary>deep (default) or light: see contracts/schemas/config.schema.json sleep_mode.</summary>
    public SleepMode? SleepMode { get; init; }

    /// <summary>What the hornet is used for: thermometer, bme280, relay, heater. Null: the role's default.</summary>
    public IReadOnlyList<string>? Features { get; init; }

    public FailsafeConfig? Failsafe { get; init; }
    public GuardConfig? Guard { get; init; }
    public CameraConfig? Camera { get; init; }
    public UploadConfig? Upload { get; init; }
    public HwConfig? Hw { get; init; }
    public RelayConfig? Relay { get; init; }
    public DisplayConfig? Display { get; init; }
    public BleConfig? Ble { get; init; }
    public IrConfig? Ir { get; init; }
}

/// <summary>Roles irrx and irtx: the buttons of a remote, kept by the hive; the hornet does not read them.</summary>
public sealed record IrConfig
{
    public IReadOnlyDictionary<string, IrButton>? Buttons { get; init; }
}

public sealed record IrButton
{
    public required string Protocol { get; init; }
    public required int Bits { get; init; }
    public required string Data { get; init; }
}

/// <summary>Board wiring; omitted pins keep the firmware defaults for the board.</summary>
public sealed record HwConfig
{
    public int? OnewirePin { get; init; }

    /// <summary>DHT11/DHT22 data pin, used when Thermometer is dht11 or dht22.</summary>
    public int? DhtPin { get; init; }

    public int? SdaPin { get; init; }
    public int? SclPin { get; init; }
    public int? RelayPin { get; init; }

    /// <summary>Roles irrx and irtx: the pin of the infrared receiver or emitter.</summary>
    public int? IrPin { get; init; }

    public bool? RelayActiveLow { get; init; }
    public int? ButtonPin { get; init; }
    public int? LedPin { get; init; }

    /// <summary>ADC pin with a voltage divider from the battery.</summary>
    public int? VbatPin { get; init; }

    /// <summary>Divider ratio (R1+R2)/R2: battery V = pin V x ratio.</summary>
    public double? VbatRatio { get; init; }

    /// <summary>The sensor the feature "thermometer" reads: ds18b20, dht11 or dht22; null: the board's.</summary>
    public string? Thermometer { get; init; }
}

/// <summary>Small local screen: up to two metrics shown large.</summary>
public sealed record DisplayConfig
{
    public IReadOnlyList<string>? Lines { get; init; }

    /// <summary>What to show, kept by the hive; it feeds the screen through vs/v1/hive/display/{id}.</summary>
    public IReadOnlyList<DisplayConfigItem>? Items { get; init; }

    /// <summary>Screens the hive draws: how the picture is turned clockwise, 0/90/180/270. Absent: 0.</summary>
    public int? Rotation { get; init; }

    /// <summary>Monitoring of a monitor far away: it reads its screen every <see cref="DisplayMonitorConfig.EveryMin"/> minutes until the time is up.</summary>
    public DisplayMonitorConfig? Monitor { get; init; }
}

public sealed record DisplayMonitorConfig
{
    /// <summary>Unix ms when the monitoring ends.</summary>
    public required long Until { get; init; }

    /// <summary>1, 2, 5 or 10.</summary>
    public required int EveryMin { get; init; }
}

public sealed record DisplayConfigItem
{
    /// <summary>The device whose reading is shown; absent: the hornet itself.</summary>
    public string? Device { get; init; }

    /// <summary>The reading shown; absent when the item is a caption (<see cref="Text"/>).</summary>
    public string? Metric { get; init; }

    /// <summary>A caption the person typed instead of a reading, shown as it is.</summary>
    public string? Text { get; init; }

    public string? Label { get; init; }
    /// <summary>Placed layout: pixels from the left edge to the anchor of the text (<see cref="Align"/> says which edge).</summary>
    public int? X { get; init; }

    /// <summary>Placed layout: pixels from the top edge to the top of the text (of its label when there is one).</summary>
    public int? Y { get; init; }

    /// <summary>Relative text height 1-10 (size/10 of the screen height). Set on any item: the whole screen is placed.</summary>
    public int? Size { get; init; }

    /// <summary>left (absent), center or right.</summary>
    public string? Align { get; init; }

    /// <summary>Screens the hive draws: id of the font family (GET /api/display/fonts). Absent: the standard font.</summary>
    public string? Font { get; init; }

    /// <summary>Screens the hive draws: the size of the font in pixels, 6-120. Absent: derived from <see cref="Size"/>.</summary>
    public int? Px { get; init; }

    public bool? Bold { get; init; }
    public bool? Italic { get; init; }

    /// <summary>The hive's clock instead of a reading: time or date.</summary>
    public string? Clock { get; init; }

    /// <summary>How the clock is written (contracts: config display.items[].format); absent: the first format of its kind.</summary>
    public string? Format { get; init; }
}

/// <summary>vs/v1/hive/display/{id} (retained): ready texts for the screen, in the automatic layout or placed (x, y, size). No items: nothing chosen.</summary>
public sealed record DisplayMessage
{
    public int V { get; init; } = 1;
    public required long Ts { get; init; }
    public required IReadOnlyList<DisplayItem> Items { get; init; }
}

public sealed record DisplayItem
{
    public string? Label { get; init; }
    public required string Text { get; init; }
    /// <summary>Placed layout: pixels from the left edge to the anchor of the text (<see cref="Align"/> says which edge).</summary>
    public int? X { get; init; }

    /// <summary>Placed layout: pixels from the top edge to the top of the text (of its label when there is one).</summary>
    public int? Y { get; init; }

    /// <summary>Relative text height 1-10 (size/10 of the screen height). Set on any item: the whole screen is placed.</summary>
    public int? Size { get; init; }

    /// <summary>left (absent), center or right.</summary>
    public string? Align { get; init; }

    /// <summary>Screens the hive draws: id of the font family (GET /api/display/fonts). Absent: the standard font.</summary>
    public string? Font { get; init; }

    /// <summary>Screens the hive draws: the size of the font in pixels, 6-120. Absent: derived from <see cref="Size"/>.</summary>
    public int? Px { get; init; }

    public bool? Bold { get; init; }
    public bool? Italic { get; init; }
}

/// <summary>Role ble: the Bluetooth LE thermometer the hornet is a bridge for.</summary>
public sealed record BleConfig
{
    /// <summary>Bluetooth address, AA:BB:CC:DD:EE:FF. Absent: the hornet scans and lists what it hears in its log.</summary>
    public string? Mac { get; init; }

    /// <summary>Raw notification frames go to the hornet's log.</summary>
    public bool? Dump { get; init; }
}

public sealed record RelayConfig
{
    public string? Channel { get; init; }

    /// <summary>0 = no limit; the relay switches off by itself after this.</summary>
    public int? MaxOnS { get; init; }
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
