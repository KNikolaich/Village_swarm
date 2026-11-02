using System.Xml.Linq;
using Hive.Modules.IpCams;

namespace Hive.Tests.Unit;

/// <summary>PTZ rules that need no camera: Dahua's direction codes, what a command may ask, the presets cameras answer.</summary>
public sealed class PtzTests
{
    [Theory]
    [InlineData(1, 0, 0, "Right", 0, 8)]
    [InlineData(-0.5, 0, 0, "Left", 0, 4)]
    [InlineData(0, 0.3, 0, "Up", 0, 2)]
    [InlineData(0, -0.01, 0, "Down", 0, 1)]
    [InlineData(-1, 0.5, 0, "LeftUp", 4, 8)]
    [InlineData(0.25, -1, 0, "RightDown", 8, 2)]
    [InlineData(0, 0, 0.5, "ZoomTele", 0, 4)]
    [InlineData(0, 0, -1, "ZoomWide", 0, 8)]
    [InlineData(1, 0, 1, "Right", 0, 8)]
    public void Dahua_moves_become_a_direction_code_and_speeds_1_to_8(double pan, double tilt, double zoom, string code, int arg1, int arg2) =>
        Assert.Equal((code, arg1, arg2), PtzControl.DahuaMove(pan, tilt, zoom));

    [Fact]
    public void A_command_asks_for_speeds_within_minus_one_to_one_and_a_known_op()
    {
        Assert.Null(PtzControl.Invalid(new PtzRequest(PtzOps.Move, Pan: -1, Tilt: 1)));
        Assert.Null(PtzControl.Invalid(new PtzRequest(PtzOps.Stop)));
        Assert.Null(PtzControl.Invalid(new PtzRequest(PtzOps.Preset, Preset: "2")));
        Assert.NotNull(PtzControl.Invalid(new PtzRequest(PtzOps.Move, Pan: 1.5)));
        Assert.NotNull(PtzControl.Invalid(new PtzRequest(PtzOps.Move, Zoom: double.NaN)));
        Assert.NotNull(PtzControl.Invalid(new PtzRequest(PtzOps.Move, Pan: 0.001)));
        Assert.NotNull(PtzControl.Invalid(new PtzRequest(PtzOps.Preset)));
        Assert.NotNull(PtzControl.Invalid(new PtzRequest(PtzOps.Preset, Preset: new string('1', 65))));
        Assert.NotNull(PtzControl.Invalid(new PtzRequest("spin")));
    }

    [Fact]
    public void Onvif_presets_keep_their_token_and_fall_back_to_it_for_a_name()
    {
        var answer = XDocument.Parse(
            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\" " +
            "xmlns:tt=\"http://www.onvif.org/ver10/schema\"><s:Body><tptz:GetPresetsResponse>" +
            "<tptz:Preset token=\"7\"><tt:Name>Двор</tt:Name><tt:PTZPosition/></tptz:Preset><tptz:Preset token=\"8\"><tt:Name> </tt:Name></tptz:Preset>" +
            "</tptz:GetPresetsResponse></s:Body></s:Envelope>");
        Assert.Equal([new PtzPresetDto("7", "Двор"), new PtzPresetDto("8", "8")], OnvifClient.ParsePresets(answer));
    }

    [Fact]
    public void Dahua_presets_are_read_by_index_until_the_list_ends() =>
        Assert.Equal([new PtzPresetDto("1", "Ворота"), new PtzPresetDto("5", "5")],
            DahuaClient.ParsePresets("presets[0].Index=1\r\npresets[0].Name=Ворота\r\npresets[1].Index=5\r\npresets[1].Name=\r\npresets[3].Index=9\r\n"));
}
