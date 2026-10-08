using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Contracts.Messages;
using Json.Schema;

namespace Hive.Tests.Unit;

/// <summary>
/// Validates contracts/examples against contracts/schemas and checks that the C# DTOs round-trip them.
/// Layout: examples/{schema}/{case}.json must be valid, examples/{schema}/invalid/{case}.json must not.
/// </summary>
public class ContractExamplesTests
{
    private static readonly string ContractsDir = Path.Combine(AppContext.BaseDirectory, "contracts");
    private static readonly string SchemasDir = Path.Combine(ContractsDir, "schemas");
    private static readonly string ExamplesDir = Path.Combine(ContractsDir, "examples");

    // Base schemas that only exist to be extended.
    private static readonly HashSet<string> AbstractSchemas = ["common", "cmd"];

    private static readonly Dictionary<string, Type> Dtos = new()
    {
        ["info"] = typeof(InfoMessage),
        ["health"] = typeof(HealthMessage),
        ["tele"] = typeof(TeleMessage),
        ["tele-batch"] = typeof(TeleBatchMessage),
        ["state"] = typeof(StateMessage),
        ["event"] = typeof(EventMessage),
        ["event-motion"] = typeof(MotionEvent),
        ["event-config-applied"] = typeof(ConfigAppliedEvent),
        ["log"] = typeof(LogMessage),
        ["cmd-relay"] = typeof(RelayCommand),
        ["cmd-arm"] = typeof(ArmCommand),
        ["cmd-snapshot"] = typeof(SnapshotCommand),
        ["ack"] = typeof(AckMessage),
        ["config"] = typeof(DeviceConfig),
        ["heartbeat"] = typeof(HeartbeatMessage),
        ["active"] = typeof(ActiveNodeMessage),
    };

    private static readonly Lazy<Dictionary<string, JsonSchema>> Schemas = new(LoadSchemas);

    private static Dictionary<string, JsonSchema> LoadSchemas()
    {
        var schemas = new Dictionary<string, JsonSchema>();
        foreach (var file in Directory.GetFiles(SchemasDir, "*.schema.json"))
        {
            var schema = JsonSchema.FromFile(file);
            SchemaRegistry.Global.Register(schema);
            schemas[Path.GetFileName(file)[..^".schema.json".Length]] = schema;
        }
        return schemas;
    }

    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true,
    };

    public static TheoryData<string, string> ValidExamples() => Examples(invalid: false);

    public static TheoryData<string, string> InvalidExamples() => Examples(invalid: true);

    private static TheoryData<string, string> Examples(bool invalid)
    {
        var data = new TheoryData<string, string>();
        foreach (var dir in Directory.GetDirectories(ExamplesDir))
        {
            var caseDir = invalid ? Path.Combine(dir, "invalid") : dir;
            if (!Directory.Exists(caseDir))
                continue;
            foreach (var file in Directory.GetFiles(caseDir, "*.json"))
                data.Add(Path.GetFileName(dir), Path.GetRelativePath(ExamplesDir, file).Replace('\\', '/'));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ValidExamples))]
    public void Valid_example_matches_schema(string schema, string example)
    {
        var result = Evaluate(schema, ReadExample(example));
        Assert.True(result.IsValid, $"{example}: {Describe(result)}");
    }

    [Theory]
    [MemberData(nameof(InvalidExamples))]
    public void Invalid_example_is_rejected(string schema, string example)
    {
        var result = Evaluate(schema, ReadExample(example));
        Assert.False(result.IsValid, $"{example} should not match {schema}.schema.json");
    }

    [Theory]
    [MemberData(nameof(ValidExamples))]
    public void Dto_round_trip_keeps_example_valid_and_complete(string schema, string example)
    {
        Assert.True(Dtos.TryGetValue(schema, out var type), $"No DTO mapped for schema '{schema}'");

        var original = ReadExample(example)!;
        var dto = JsonSerializer.Deserialize(original.ToJsonString(), type, ContractJson.Options);
        var roundTrip = JsonNode.Parse(JsonSerializer.Serialize(dto, type, ContractJson.Options))!;

        var result = Evaluate(schema, roundTrip);
        Assert.True(result.IsValid, $"{example} after round trip: {Describe(result)}");
        Assert.Equal(NonNullKeys(original), NonNullKeys(roundTrip));
    }

    [Fact]
    public void Every_concrete_schema_has_examples_and_a_dto()
    {
        foreach (var name in Schemas.Value.Keys.Where(n => !AbstractSchemas.Contains(n)))
        {
            Assert.True(Directory.Exists(Path.Combine(ExamplesDir, name)), $"No examples for {name}.schema.json");
            Assert.True(Dtos.ContainsKey(name), $"No DTO for {name}.schema.json");
        }
    }

    [Fact]
    public void Every_example_folder_has_a_schema()
    {
        foreach (var dir in Directory.GetDirectories(ExamplesDir))
            Assert.True(Schemas.Value.ContainsKey(Path.GetFileName(dir)), $"No schema for examples/{Path.GetFileName(dir)}");
    }

    [Fact]
    public void Enums_serialize_as_lowercase_strings()
    {
        var ack = new AckMessage { Id = "01J9Z3K7Q8M4N5P6R7S8T9V1A0", Ts = 1, Seq = 1, Boot = 1, Cid = "01J9Z3K7Q8M4N5P6R7S8T9V170", Status = AckStatus.Unsupported };
        Assert.Contains("\"status\":\"unsupported\"", ContractJson.Serialize(ack));
    }

    [Fact]
    public void Missing_required_field_fails_deserialization() =>
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<TeleMessage>("""{"v":1,"id":"01J9Z3K7Q8M4N5P6R7S8T9V0Z0","ts":0,"seq":1,"boot":1}"""));

    private static EvaluationResults Evaluate(string schema, JsonNode? instance) =>
        Schemas.Value[schema].Evaluate(instance, Options);

    private static JsonNode? ReadExample(string example) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesDir, example)));

    private static string Describe(EvaluationResults result) =>
        string.Join("; ", result.Details
            .Where(d => d.HasErrors)
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} {e.Key}: {e.Value}")));

    private static SortedSet<string> NonNullKeys(JsonNode node, string prefix = "")
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        if (node is not JsonObject obj)
            return keys;
        foreach (var (key, value) in obj)
        {
            if (value is null)
                continue;
            keys.Add(prefix + key);
            keys.UnionWith(NonNullKeys(value, prefix + key + "."));
        }
        return keys;
    }
}
