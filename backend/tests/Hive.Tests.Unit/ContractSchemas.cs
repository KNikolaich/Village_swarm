using System.Text.Json.Nodes;
using Json.Schema;

namespace Hive.Tests.Unit;

/// <summary>Loads contracts/schemas once and validates JSON against them.</summary>
public static class ContractSchemas
{
    public static readonly string ContractsDir = Path.Combine(AppContext.BaseDirectory, "contracts");
    public static readonly string SchemasDir = Path.Combine(ContractsDir, "schemas");
    public static readonly string ExamplesDir = Path.Combine(ContractsDir, "examples");

    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true,
    };

    private static readonly Lazy<Dictionary<string, JsonSchema>> Loaded = new(() =>
    {
        var schemas = new Dictionary<string, JsonSchema>();
        foreach (var file in Directory.GetFiles(SchemasDir, "*.schema.json"))
        {
            var schema = JsonSchema.FromFile(file);
            SchemaRegistry.Global.Register(schema);
            schemas[Path.GetFileName(file)[..^".schema.json".Length]] = schema;
        }
        return schemas;
    });

    public static IReadOnlyDictionary<string, JsonSchema> All => Loaded.Value;

    public static EvaluationResults Evaluate(string schema, JsonNode? instance) => All[schema].Evaluate(instance, Options);

    public static string Describe(EvaluationResults result) =>
        string.Join("; ", result.Details
            .Where(d => d.HasErrors)
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} {e.Key}: {e.Value}")));

    /// <summary>
    /// Schema for a device topic per contracts/mqtt-topics.md, or null for plain-text topics (status).
    /// </summary>
    public static string? ForTopic(string topic)
    {
        var parts = topic.Split('/'); // vs/v1/dev/{id}/{kind}/...
        var kind = parts[4];
        return kind switch
        {
            "status" => null,
            "tele" => parts[5] == "batch" ? "tele-batch" : "tele",
            "event" => All.ContainsKey($"event-{parts[5].Replace('_', '-')}") ? $"event-{parts[5].Replace('_', '-')}" : "event",
            "cmd" when parts.Length == 7 && parts[6] == "ack" => "ack",
            "cmd" => All.ContainsKey($"cmd-{parts[5]}") ? $"cmd-{parts[5]}" : "cmd",
            _ => kind,
        };
    }
}
