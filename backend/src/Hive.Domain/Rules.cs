using System.Text.Json;

namespace Hive.Domain;

/// <summary>
/// An automation rule (spec 6.6): trigger, conditions and actions as JSON in <see cref="Definition"/> (the Rules module
/// owns its shape). <see cref="State"/> is what the engine remembers between runs, e.g. hysteresis latches.
/// </summary>
public class Rule
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool Enabled { get; set; }
    public required JsonDocument Definition { get; set; }
    public JsonDocument? State { get; set; }
    public DateTimeOffset? LastFiredAt { get; set; }

    /// <summary>fired | no_data | error: the last thing worth showing next to the rule.</summary>
    public string? LastOutcome { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>One entry of a rule's journal: it fired, could not work for lack of data, or failed.</summary>
public class RuleRun
{
    public long Id { get; set; }
    public int RuleId { get; set; }
    public DateTimeOffset Ts { get; set; }
    public required string Outcome { get; set; }
    public JsonDocument? Details { get; set; }
}
