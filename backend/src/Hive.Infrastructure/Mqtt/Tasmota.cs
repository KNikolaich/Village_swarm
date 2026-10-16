using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hive.Infrastructure.Mqtt;

/// <summary>
/// Topics of Tasmota and OpenBeken devices (contracts/tasmota.md). Their MQTT Topic is the device_id, so the standard
/// layout applies: <c>cmnd/{id}/…</c> to the device, <c>stat/{id}/…</c> and <c>tele/{id}/…</c> from it.
/// </summary>
public static class TasmotaTopics
{
    public const string AllTele = "tele/+/#";
    public const string AllStat = "stat/+/#";

    public static string Cmnd(string deviceId, string command) => $"cmnd/{deviceId}/{command}";

    /// <summary>Splits <c>tele/{id}/STATE</c> or <c>stat/{id}/POWER2</c> into the id and the last part; null for anything else.</summary>
    public static (string Area, string DeviceId, string Kind)? TryParse(string topic)
    {
        var parts = topic.Split('/');
        if (parts.Length != 3 || parts[0] is not ("tele" or "stat"))
            return null;
        return Hive.Contracts.Mqtt.Topics.IsValidDeviceId(parts[1]) ? (parts[0], parts[1], parts[2]) : null;
    }
}

/// <summary>One MQTT message to a Tasmota device: the command name (the topic's last part) and its payload.</summary>
public sealed record TasmotaCall(string Command, string Payload);

/// <summary>What to send for one hive command, and which answer from the device confirms it ("power", "restart").</summary>
public sealed record TasmotaPlan(IReadOnlyList<TasmotaCall> Calls, string Confirms);

/// <summary>Turns our commands (contracts/schemas/cmd-relay.schema.json and cmd-reboot) into Tasmota's.</summary>
public static partial class TasmotaCommands
{
    /// <summary>Longest timed switch-on we ask for (Tasmota's PulseTime reaches 18 hours).</summary>
    public const int MaxForSeconds = 64_800;

    [GeneratedRegex(@"^relay(\d+)$")]
    private static partial Regex ChannelRegex();

    /// <summary>Null when the command is not one a Tasmota device can do (or its payload is wrong).</summary>
    public static TasmotaPlan? Plan(string name, JsonObject? payload)
    {
        if (name == "reboot")
            return new TasmotaPlan([new TasmotaCall("Restart", "1")], "restart");
        if (name != "relay")
            return null;

        var set = payload?["set"]?.GetValue<string>();
        var power = Power(payload?["channel"]?.GetValue<string>());
        switch (set)
        {
            case "off":
                return new TasmotaPlan([new TasmotaCall(power, "OFF")], "power");
            case "toggle":
                return new TasmotaPlan([new TasmotaCall(power, "TOGGLE")], "power");
            case "on":
                var forS = payload?["for_s"] is { } f && f.GetValueKind() == System.Text.Json.JsonValueKind.Number ? f.GetValue<int>() : 0;
                if (forS <= 0)
                    return new TasmotaPlan([new TasmotaCall(power, "ON")], "power");
                // PulseTime: 1 to 111 is tenths of a second, above 111 it is seconds plus 100. The relay switches itself
                // off when the time is up, even if the hive is gone: that is the failsafe.
                var seconds = Math.Min(forS, MaxForSeconds);
                var pulse = seconds <= 11 ? seconds * 10 : seconds + 100;
                var index = power.Length > 5 ? power[5..] : "1";
                return new TasmotaPlan([new TasmotaCall("Backlog", $"PulseTime{index} {pulse}; {power} ON")], "power");
            default:
                return null;
        }
    }

    /// <summary>The channel name of a command ("relay2", or anything for a single relay) as Tasmota's POWER command.</summary>
    public static string Power(string? channel) =>
        channel is not null && ChannelRegex().Match(channel) is { Success: true } m ? $"POWER{m.Groups[1].Value}" : "POWER";
}

/// <summary>
/// Commands waiting for a Tasmota device to answer. The device does not know our command ids, so the hive remembers the
/// newest one per device and kind, and the adapter closes it when the matching answer arrives.
/// </summary>
public sealed class TasmotaPending(TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<(string DeviceId, string Kind), (string Cid, DateTimeOffset At)> _waiting = new();

    public void Expect(string deviceId, string kind, string cid) => _waiting[(deviceId, kind)] = (cid, time.GetUtcNow());

    /// <summary>The command this answer belongs to, once; null when nothing (recent) was waiting.</summary>
    public string? Take(string deviceId, string kind)
    {
        if (!_waiting.TryRemove((deviceId, kind), out var entry))
            return null;
        return time.GetUtcNow() - entry.At <= Lifetime ? entry.Cid : null;
    }
}
