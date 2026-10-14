using System.Text.Json.Nodes;

namespace Hive.Modules.Rules;

/// <summary>
/// When a rule is looked at.
/// <list type="bullet">
/// <item><c>telemetry</c>: a new reading of <see cref="Device"/>/<see cref="Metric"/>.</item>
/// <item><c>event</c>: an event of <see cref="EventType"/> (from <see cref="Device"/> when set).</item>
/// <item><c>schedule</c>: every day at <see cref="At"/> ("HH:mm", house time), only on <see cref="Days"/> (1 = Monday) when set.</item>
/// <item><c>sun</c>: at <see cref="Sun"/> (sunrise | sunset) plus <see cref="OffsetMin"/> (negative = before), from the forecast.</item>
/// <item><c>offline</c>: <see cref="Device"/> has been offline for <see cref="OfflineMin"/> minutes (once per outage).</item>
/// </list>
/// </summary>
public sealed record RuleTrigger(
    string Kind, string? Device = null, string? Metric = null, string? EventType = null, string? At = null, int[]? Days = null,
    string? Sun = null, int OffsetMin = 0, int? OfflineMin = null);

/// <summary>
/// Something that must hold for the actions to run; <see cref="Not"/> turns it around.
/// <list type="bullet">
/// <item><c>value</c>: the last reading of <see cref="Device"/>/<see cref="Metric"/> compared with <see cref="Op"/> to
/// <see cref="Value"/>, and every reading of the last <see cref="ForS"/> seconds too. <see cref="Release"/> adds hysteresis:
/// once true, it stays true until the value crosses <see cref="Release"/>. A reading older than <see cref="MaxAgeS"/>
/// (default 900 s) means "no data": the rule does not act and says so.</item>
/// <item><c>mode</c>: hive mode <see cref="Mode"/> (e.g. "armed") equals <see cref="Is"/>.</item>
/// <item><c>time</c>: house time between <see cref="From"/> and <see cref="To"/> ("HH:mm", may pass midnight), on <see cref="Days"/> when set.</item>
/// <item><c>dark</c>: between sunset and sunrise, from the forecast.</item>
/// <item><c>rain_past</c>: at least <see cref="Mm"/> (default 1) mm fell in the last <see cref="Hours"/> (default 24) hours.</item>
/// <item><c>rain_forecast</c>: in the next <see cref="Hours"/> (default 12) hours at least <see cref="Mm"/> (default 1) mm are
/// expected, or some hour has at least <see cref="Probability"/> (default 60) % chance of rain.</item>
/// <item><c>device_state</c>: the retained state <see cref="Channel"/> of <see cref="Device"/> is <see cref="Is"/> (e.g. heater = on).</item>
/// </list>
/// </summary>
public sealed record RuleCondition(
    string Kind, bool Not = false, string? Device = null, string? Metric = null, string? Op = null, double? Value = null,
    double? Release = null, int ForS = 0, int? MaxAgeS = null, string? Mode = null, string? Is = null, string? From = null,
    string? To = null, int[]? Days = null, double? Mm = null, int? Hours = null, int? Probability = null, string? Channel = null);

/// <summary>
/// What a rule does.
/// <list type="bullet">
/// <item><c>command</c>: <see cref="Command"/> with <see cref="Payload"/> to <see cref="Device"/> (e.g. relay {channel, set, for_s}).</item>
/// <item><c>notify</c>: <see cref="Text"/> into the event feed at <see cref="Level"/> (info | warn | alarm | critical, default warn);
/// warn and above also go to Telegram. {value}, {rule} and {device} are filled in.</item>
/// <item><c>set_mode</c>: hive mode <see cref="Mode"/> becomes <see cref="Value"/>.</item>
/// <item><c>snapshot</c>: a photo from camera <see cref="Device"/>.</item>
/// </list>
/// </summary>
public sealed record RuleAction(
    string Kind, string? Device = null, string? Command = null, JsonObject? Payload = null, string? Text = null,
    string? Level = null, string? Mode = null, string? Value = null);

/// <summary>A whole rule; <see cref="CooldownS"/> is the least time between two firings.</summary>
public sealed record RuleDefinition(RuleTrigger Trigger, IReadOnlyList<RuleCondition> Conditions, IReadOnlyList<RuleAction> Actions, int CooldownS = 0);

public sealed record RuleDto(
    int Id, string Name, bool Enabled, RuleDefinition Definition, DateTimeOffset? LastFiredAt, string? LastOutcome, DateTimeOffset UpdatedAt);

public sealed record SaveRuleRequest(string Name, bool Enabled, RuleDefinition Definition);

public sealed record RuleRunDto(long Id, DateTimeOffset Ts, string Outcome, System.Text.Json.JsonElement? Details);

public sealed record DryRunRequest(RuleDefinition Definition, int? Hours = null);

/// <summary>One moment the rule would have acted (or could not, for lack of data) during the dry run.</summary>
public sealed record DryRunHit(DateTimeOffset Ts, string Outcome, double? Value, string? Reason);

/// <summary>
/// The dry run of a rule over past hours. <see cref="Notes"/> says, as codes, what it could not replay exactly:
/// current_state (modes, device states and the forecast are taken as they are now), offline_not_replayed, truncated.
/// </summary>
public sealed record DryRunResult(DateTimeOffset From, DateTimeOffset To, int Checked, IReadOnlyList<DryRunHit> Hits, IReadOnlyList<string> Notes);
