using System.Text.RegularExpressions;

namespace Hive.Contracts.Mqtt;

/// <summary>
/// MQTT topic hierarchy, contract v1. Source of truth: contracts/mqtt-topics.md.
/// Must stay in sync with firmware/lib/hornet-core/src/Topics.h.
/// </summary>
public static partial class Topics
{
    public const string Prefix = "vs/v1";
    public const int MaxDeviceIdLength = 32;

    /// <summary>Retained topic telling hornets which node is active (spec 11.3).</summary>
    public const string HiveActive = Prefix + "/hive/active";

    /// <summary>Everything any hornet publishes; the backend subscribes to this.</summary>
    public const string AllDevices = Prefix + "/dev/+/#";

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$")]
    private static partial Regex DeviceIdRegex();

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex SegmentRegex();

    public static bool IsValidDeviceId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= MaxDeviceIdLength && DeviceIdRegex().IsMatch(id);

    public static bool IsValidSegment(string? segment) =>
        !string.IsNullOrEmpty(segment) && SegmentRegex().IsMatch(segment);

    public static string Device(string id) => $"{Prefix}/dev/{CheckId(id)}";

    public static string Status(string id) => $"{Device(id)}/status";
    public static string Info(string id) => $"{Device(id)}/info";
    public static string Health(string id) => $"{Device(id)}/health";
    public static string State(string id) => $"{Device(id)}/state";
    public static string Log(string id) => $"{Device(id)}/log";
    public static string Config(string id) => $"{Device(id)}/config";
    public static string Tele(string id, string metric) => $"{Device(id)}/tele/{CheckSegment(metric)}";
    public static string Event(string id, string type) => $"{Device(id)}/event/{CheckSegment(type)}";
    public static string Cmd(string id, string name) => $"{Device(id)}/cmd/{CheckSegment(name)}";
    public static string CmdAck(string id, string name) => $"{Cmd(id, name)}/ack";

    public static string HiveHeartbeat(string node) => $"{Prefix}/hive/{CheckSegment(node)}/heartbeat";
    public static string Broadcast(string name) => $"{Prefix}/hive/broadcast/cmd/{CheckSegment(name)}";

    /// <summary>Extracts the device_id from a <c>vs/v1/dev/{id}/...</c> topic, or null if it is not a device topic.</summary>
    public static string? TryGetDeviceId(string topic)
    {
        var parts = topic.Split('/');
        if (parts.Length < 5 || parts[0] != "vs" || parts[1] != "v1" || parts[2] != "dev")
            return null;
        return IsValidDeviceId(parts[3]) ? parts[3] : null;
    }

    private static string CheckId(string id) =>
        IsValidDeviceId(id) ? id : throw new ArgumentException($"Invalid device_id '{id}'", nameof(id));

    private static string CheckSegment(string s) =>
        IsValidSegment(s) ? s : throw new ArgumentException($"Invalid topic segment '{s}'", nameof(s));
}
