using System.Text;
using System.Text.Json.Nodes;
using Hive.Domain;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Telemetry.Ingest;

namespace Hive.Tests.Unit.Ingest;

/// <summary>The Tasmota console (device page, administrators): any command to the device, its answers in the device log.</summary>
public class TasmotaConsoleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Now);
    private readonly TasmotaPending _pending;

    public TasmotaConsoleTests() => _pending = new TasmotaPending(_time);

    private IReadOnlyList<IngestItem> Parse(string topic, string payload, DateTimeOffset? at = null) =>
        TasmotaAdapter.Parse(topic, Encoding.UTF8.GetBytes(payload), at ?? Now, _pending);

    private static TasmotaPlan? Plan(string json) => TasmotaCommands.Plan("tasmota", JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void A_console_command_goes_to_the_device_as_typed()
    {
        var plan = Plan("""{"command":"RfSend","value":"{\"Data\":\"A1B2C3\"}"}""")!;
        Assert.Equal(new TasmotaCall("RfSend", """{"Data":"A1B2C3"}"""), plan.Calls[0]);
        Assert.Equal("console", plan.Confirms);
        Assert.Equal(new TasmotaCall("Status", ""), Plan("""{"command":"Status"}""")!.Calls[0]);
    }

    [Theory]
    [InlineData("""{"command":"Power1 ON"}""")]
    [InlineData("""{"command":"../x","value":"1"}""")]
    [InlineData("""{"command":"","value":"1"}""")]
    [InlineData("""{"value":"1"}""")]
    [InlineData("""{"command":"Backlog","value":"a\nb"}""")]
    public void Commands_that_could_escape_the_topic_or_the_line_are_refused(string payload)
    {
        Assert.Null(Plan(payload));
    }

    [Fact]
    public void A_long_value_is_refused()
    {
        Assert.Null(Plan($$"""{"command":"Backlog","value":"{{new string('a', 301)}}"}"""));
    }

    [Theory]
    [InlineData("MqttPassword", true)]
    [InlineData("WebPassword", true)]
    [InlineData("Password1", true)]
    [InlineData("RfSend", false)]
    [InlineData("PulseTime1", false)]
    public void Secrets_are_not_kept_in_the_journal(string command, bool secret)
    {
        Assert.Equal(secret, TasmotaCommands.IsSecretCommand(command));
    }

    [Fact]
    public void What_a_device_answers_goes_to_its_log_and_closes_the_console_command()
    {
        _pending.Expect("rf-bridge1", "console", "01CON");
        var items = Parse("stat/rf-bridge1/RESULT", """{"RfSend":"Done"}""");

        var log = Assert.IsType<DeviceLogReceived>(items[0]);
        Assert.Equal(("info", """{"RfSend":"Done"}"""), (log.Level, log.Msg));
        Assert.Equal("01CON", Assert.IsType<CommandAckReceived>(items[1]).Cid);
    }

    [Fact]
    public void A_received_rf_code_is_in_the_log_and_a_chatty_device_is_limited()
    {
        var first = Parse("tele/rf-bridge1/RESULT", """{"RfReceived":{"Sync":12030,"Low":370,"High":1170,"Data":"A1B2C3","RfKey":"None"}}""");
        Assert.Contains("RfReceived", Assert.IsType<DeviceLogReceived>(Assert.Single(first)).Msg);

        // two more within half a second: dropped; a quarter of a second later still dropped, a second later kept
        Assert.Empty(Parse("tele/rf-bridge1/RESULT", """{"RfReceived":{"Data":"D4E5F6"}}""", Now.AddMilliseconds(100)));
        Assert.Empty(Parse("tele/rf-bridge1/RESULT", """{"RfReceived":{"Data":"D4E5F6"}}""", Now.AddMilliseconds(450)));
        Assert.Single(Parse("tele/rf-bridge1/RESULT", """{"RfReceived":{"Data":"D4E5F6"}}""", Now.AddSeconds(1)));
    }

    [Fact]
    public void Plain_relay_answers_and_oversized_messages_stay_out_of_the_log()
    {
        Assert.Empty(Parse("stat/heat-bed1/RESULT", """{"POWER":"ON"}"""));
        Assert.Empty(Parse("stat/heat-bed2/RESULT", "{\"Status\":\"" + new string('x', 500) + "\"}"));
    }
}
