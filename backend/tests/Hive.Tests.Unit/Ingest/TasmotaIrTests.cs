using System.Text;
using System.Text.Json.Nodes;
using Hive.Domain;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Telemetry.Ingest;

namespace Hive.Tests.Unit.Ingest;

/// <summary>Infrared on a Tasmota device (contracts/tasmota.md): send a code, learn one from the remote.</summary>
public class TasmotaIrTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly TasmotaPending _pending = new(new ManualTime(Now));

    private IReadOnlyList<IngestItem> Parse(string topic, string payload) =>
        TasmotaAdapter.Parse(topic, Encoding.UTF8.GetBytes(payload), Now, _pending);

    private static TasmotaPlan? Plan(string name, string? json = null) =>
        TasmotaCommands.Plan(name, json is null ? null : JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void An_infrared_code_is_sent_as_irsend()
    {
        var plan = Plan("ir", """{"protocol":"NEC","bits":32,"data":"0x02FD807F"}""")!;
        Assert.Equal("IRSend", plan.Calls[0].Command);
        Assert.Equal("""{"Protocol":"NEC","Bits":32,"Data":"0x02FD807F","Repeat":0}""", plan.Calls[0].Payload);
        Assert.Equal("ir", plan.Confirms);
        Assert.Contains("\"Repeat\":3", Plan("ir", """{"protocol":"NEC","bits":32,"data":"0x1","repeat":3}""")!.Calls[0].Payload);
    }

    [Theory]
    [InlineData("""{"protocol":"UNKNOWN","bits":32,"data":"0x1"}""")]
    [InlineData("""{"protocol":"NEC","bits":0,"data":"0x1"}""")]
    [InlineData("""{"protocol":"NEC","bits":65,"data":"0x1"}""")]
    [InlineData("""{"protocol":"NEC","bits":32,"data":"12345"}""")]
    [InlineData("""{"protocol":"NE C;x","bits":32,"data":"0x1"}""")]
    [InlineData("""{"bits":32,"data":"0x1"}""")]
    public void Codes_that_cannot_be_sent_are_refused(string payload)
    {
        Assert.Null(Plan("ir", payload));
    }

    [Fact]
    public void An_ir_command_without_a_payload_is_refused()
    {
        Assert.Null(Plan("ir"));
    }

    [Fact]
    public void A_received_code_is_kept_for_learning()
    {
        var learned = Parse("tele/ir-hall1/RESULT", """{"IrReceived":{"Protocol":"NEC","Bits":32,"Data":"0x02FD807F","DataLSB":"0x40BF01FE","Repeat":0}}""");
        var state = Assert.IsType<DeviceStateReceived>(Assert.Single(learned));
        Assert.True(state.Merge);
        Assert.Equal("NEC 32 0x02FD807F", state.State.RootElement.GetProperty("ir_last").GetString());
        Assert.Empty(Parse("tele/ir-hall1/RESULT", """{"POWER":"ON"}"""));
    }

    [Fact]
    public void The_send_answer_closes_the_command()
    {
        _pending.Expect("ir-hall1", "ir", "01IR");
        var ack = Assert.IsType<CommandAckReceived>(Assert.Single(Parse("stat/ir-hall1/RESULT", """{"IRSend":"Done"}""")));
        Assert.Equal("01IR", ack.Cid);
    }
}
