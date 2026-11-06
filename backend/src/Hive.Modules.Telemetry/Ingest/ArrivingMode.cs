using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Domain;

namespace Hive.Modules.Telemetry.Ingest;

/// <summary>
/// "Едут хозяева" (guard mode arriving): the cameras stay armed, but motion is the family coming, not a burglar. Such an event
/// is a quiet warning that still carries its photos and buttons to disarm or to say "it is not us" (contracts/ipcam.md).
/// Guard-cams do not know the mode (their firmware reports alarm when armed): the hive turns their alarm into this here.
/// </summary>
public static class ArrivingMode
{
    public const string Flag = "arriving";

    /// <summary>Event types a guard turns into an alarm: the ones the arriving mode makes quiet.</summary>
    public static bool Concerns(string type) => type is "motion" or "door_open";

    public static bool Is(JsonDocument payload) =>
        payload.RootElement.ValueKind == JsonValueKind.Object
        && payload.RootElement.TryGetProperty(Flag, out var a) && a.ValueKind == JsonValueKind.True;

    /// <summary>The payload with <c>"arriving": true</c> added.</summary>
    public static JsonDocument Mark(JsonDocument payload)
    {
        var node = JsonNode.Parse(payload.RootElement.GetRawText()) as JsonObject ?? [];
        node[Flag] = true;
        return JsonDocument.Parse(node.ToJsonString());
    }

    /// <summary>An alarm reported by an armed camera while the family is on its way: a warning, marked as arriving.</summary>
    public static DeviceEventReceived Quieten(DeviceEventReceived e) => e with { Severity = EventSeverity.Warn, Payload = Mark(e.Payload) };
}
