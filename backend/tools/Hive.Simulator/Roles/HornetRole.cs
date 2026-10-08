using System.Text.Json;
using Hive.Contracts.Messages;

namespace Hive.Simulator.Roles;

/// <summary>Device-type behaviour on top of <see cref="VirtualHornet"/> (mirrors src/roles/*.cpp in the firmware).</summary>
public abstract class HornetRole
{
    protected VirtualHornet Hornet { get; private set; } = null!;

    public abstract string Type { get; }
    public abstract string Hw { get; }
    public virtual IReadOnlyList<string> Caps => [];
    public virtual IReadOnlyList<string> Metrics => [];

    /// <summary>Role-specific commands; reboot and config_reload are handled by the hornet.</summary>
    public virtual IReadOnlyList<string> Commands => [];

    internal void Attach(VirtualHornet hornet) => Hornet = hornet;

    public virtual void OnBoot(DateTimeOffset now) { }

    public abstract void Tick(DateTimeOffset now);

    public virtual void ApplyConfig(DeviceConfig config, DateTimeOffset now) { }

    /// <summary>Returns null for commands the role does not know (ack status unsupported).</summary>
    public virtual CommandResult? HandleCommand(string name, JsonElement payload, DateTimeOffset now) => null;

    /// <summary>Photos waiting for HTTP upload (camera roles only).</summary>
    public virtual IReadOnlyList<PhotoUpload> TakeUploads() => [];

    public virtual void ReturnFailedUploads(IEnumerable<PhotoUpload> failed) { }

    public static HornetRole Create(HornetSpec spec, string photosDir) => spec.Type switch
    {
        "guard-cam" => new GuardCamRole(spec.Motion, PhotoLibrary.Load(photosDir)),
        "meteo" => new MeteoRole(spec.Temperature, spec.TeleIntervalS),
        "heat" => new HeatRole(spec.Heater, spec.TeleIntervalS),
        _ => throw new ArgumentException($"Unknown hornet type '{spec.Type}' for {spec.Id}"),
    };
}

/// <summary>A photo to POST to {upload}/api/ingest/photo (spec 5.4).</summary>
public sealed record PhotoUpload(string PhotoId, string EventId, long Ts, byte[] Jpeg, string Url);
