using Hive.Modules.IpCams;

namespace Hive.Tests.Unit;

/// <summary>The Dahua event stream (contracts/ipcam.md): what the NVR at the dacha sends on eventManager.cgi?action=attach.</summary>
public sealed class DahuaEventParserTests
{
    // Boundaries, part headers and blank lines around the bodies, CRLF as the NVR writes them; the newer firmware's data= JSON
    // spans several lines and holds ';' and '=' of its own.
    private const string Stream =
        "--myboundary\r\n" +
        "Content-Type: text/plain\r\n" +
        "Content-Length: 9\r\n" +
        "\r\n" +
        "Heartbeat\r\n" +
        "--myboundary\r\n" +
        "Content-Type: text/plain\r\n" +
        "Content-Length: 37\r\n" +
        "\r\n" +
        "Code=VideoMotion;action=Start;index=0\r\n" +
        "--myboundary\r\n" +
        "Content-Type: text/plain\r\n" +
        "Content-Length: 120\r\n" +
        "\r\n" +
        "Code=VideoMotion;action=Stop;index=1;data={\r\n" +
        "   \"SmartMotionEnable\" : false,\r\n" +
        "   \"Code\" : \"x;index=7\"\r\n" +
        "}\r\n" +
        "--myboundary\r\n" +
        "Content-Type: text/plain\r\n" +
        "\r\n" +
        "Code=VideoLoss;action=Start;index=3\r\n";

    [Fact]
    public void Reads_events_and_heartbeats_in_order_and_skips_the_rest()
    {
        var events = DahuaEventParser.ParseAll(Stream);

        Assert.Equal(4, events.Count);
        Assert.True(events[0].IsHeartbeat);
        Assert.Equal(new DahuaEvent("VideoMotion", "Start", 0), events[1]);
        Assert.True(events[1].IsStart);
        Assert.Equal(1, events[1].Channel); // index 0 is channel 1 of snapshot.cgi
        Assert.Equal(new DahuaEvent("VideoMotion", "Stop", 1), events[2]); // data= JSON ignored, its own index= too
        Assert.True(events[2].IsStop);
        Assert.Equal(new DahuaEvent("VideoLoss", "Start", 3), events[3]);
        Assert.Equal(4, events[3].Channel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--myboundary")]
    [InlineData("Content-Type: text/plain")]
    [InlineData("Content-Length: 37")]
    [InlineData("   \"SmartMotionEnable\" : false,")]
    [InlineData("Code=;action=Start;index=0")]
    public void Lines_that_are_not_events_give_nothing(string? line) => Assert.Null(DahuaEventParser.ParseLine(line));

    [Fact]
    public void A_bad_or_missing_index_leaves_the_channel_unknown()
    {
        Assert.Null(DahuaEventParser.ParseLine("Code=VideoMotion;action=Start;index=x")!.Channel);
        Assert.Null(DahuaEventParser.ParseLine("Code=VideoMotion;action=Start;index=-1")!.Channel);
        Assert.Null(DahuaEventParser.ParseLine("Code=AlarmLocal;action=Pulse")!.Index);
    }

    [Fact]
    public void Key_value_answers_of_the_nvr_are_read()
    {
        var values = DahuaClient.Values("table.ChannelTitle[0].Name=Ворота\r\ntable.ChannelTitle[1].Name=Двор = сад\r\n");
        Assert.Equal("Ворота", values["table.ChannelTitle[0].Name"]);
        Assert.Equal("Двор = сад", values["table.ChannelTitle[1].Name"]);
    }
}
