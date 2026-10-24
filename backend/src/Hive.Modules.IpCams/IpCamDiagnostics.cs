using System.Collections.Concurrent;

namespace Hive.Modules.IpCams;

/// <summary>One try for a frame: when, why (alarm or look), how (cgi or rtsp), how long, what came of it.</summary>
public sealed record SnapshotAttemptDto(DateTimeOffset At, string Purpose, string? Via, bool Ok, int Ms, string? Error, string? Size = null);

/// <summary>
/// The NVR's event stream as the hive sees it: connected (since when, last line), connecting, down (last error), or off
/// (no NVR run: no channels, an observer node, or the module switched off).
/// </summary>
public sealed record NvrStreamDto(string State, DateTimeOffset? Since, DateTimeOffset? LastLine, int Reconnects, string? LastError);

/// <summary>
/// What the settings page shows of IP cameras beyond their settings, kept in memory: the last motion the NVR reported and the
/// last snapshot try per channel, and which way frames come now. Lost on restart, which is fine for diagnostics.
/// </summary>
public sealed class IpCamDiagnostics
{
    private readonly ConcurrentDictionary<string, SnapshotAttemptDto> _snapshots = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _motion = new();
    private readonly ConcurrentDictionary<string, string> _via = new();
    private readonly ConcurrentDictionary<string, WayChooser> _choosers = new();

    /// <summary>Which way a channel's frames are tried first (see <see cref="WayChooser"/>), starting from the way stored with it.</summary>
    public WayChooser Chooser(string key, string? stored) => _choosers.GetOrAdd(key, _ => new WayChooser(stored));

    private static string Key(SnapshotTarget t) => t.DeviceId ?? $"nvr{t.NvrId}/ch{t.Channel}";

    public void Snapshot(SnapshotTarget target, SnapshotAttemptDto attempt) => _snapshots[Key(target)] = attempt;

    public SnapshotAttemptDto? LastSnapshot(string deviceId) => _snapshots.GetValueOrDefault(deviceId);

    public void Motion(string deviceId, DateTimeOffset at) => _motion[deviceId] = at;

    public DateTimeOffset? LastMotion(string deviceId) => _motion.TryGetValue(deviceId, out var at) ? at : null;

    public void Remember(string deviceId, string via) => _via[deviceId] = via;

    /// <summary>The way learned since the settings were read; null: nothing new.</summary>
    public string? Via(string deviceId) => _via.GetValueOrDefault(deviceId);

    /// <summary>Settings saved: what was learned under the old ones no longer holds.</summary>
    public void Forget(string deviceId)
    {
        _via.TryRemove(deviceId, out _);
        _choosers.TryRemove(deviceId, out _);
    }
}
