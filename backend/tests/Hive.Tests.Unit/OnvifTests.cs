using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Hive.Modules.IpCams;

namespace Hive.Tests.Unit;

/// <summary>ONVIF for cameras on their own (contracts/ipcam.md): motion notifications, the login digest, stream addresses.</summary>
public sealed class OnvifTests
{
    private const string Pull = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2" xmlns:tt="http://www.onvif.org/ver10/schema">
          <s:Body><tev:PullMessagesResponse xmlns:tev="http://www.onvif.org/ver10/events/wsdl">
            <wsnt:NotificationMessage>
              <wsnt:Topic>tns1:RuleEngine/CellMotionDetector/Motion</wsnt:Topic>
              <wsnt:Message><tt:Message><tt:Source><tt:SimpleItem Name="VideoSourceConfigurationToken" Value="0"/></tt:Source>
                <tt:Data><tt:SimpleItem Name="IsMotion" Value="true"/></tt:Data></tt:Message></wsnt:Message>
            </wsnt:NotificationMessage>
            <wsnt:NotificationMessage>
              <wsnt:Topic>tns1:VideoSource/MotionAlarm</wsnt:Topic>
              <wsnt:Message><tt:Message><tt:Data><tt:SimpleItem Name="State" Value="false"/></tt:Data></tt:Message></wsnt:Message>
            </wsnt:NotificationMessage>
            <wsnt:NotificationMessage>
              <wsnt:Topic>tns1:Device/Trigger/DigitalInput</wsnt:Topic>
              <wsnt:Message><tt:Message><tt:Data><tt:SimpleItem Name="LogicalState" Value="true"/></tt:Data></tt:Message></wsnt:Message>
            </wsnt:NotificationMessage>
          </tev:PullMessagesResponse></s:Body>
        </s:Envelope>
        """;

    [Fact]
    public void Motion_is_read_from_cell_motion_and_motion_alarm_topics_only()
    {
        var events = OnvifClient.ParseEvents(XDocument.Parse(Pull));
        Assert.Equal(3, events.Count);
        Assert.True(events[0].Motion);
        Assert.False(events[1].Motion);
        Assert.Null(events[2].Motion); // a digital input is not motion, whatever its state
    }

    [Fact]
    public void The_password_digest_follows_ws_username_token()
    {
        var nonce = Convert.FromBase64String("LKqI6G/AikKCQrN0zqZFlg==");
        const string created = "2010-09-16T07:50:45Z";
        var expected = Convert.ToBase64String(SHA1.HashData([.. nonce, .. Encoding.UTF8.GetBytes(created), .. Encoding.UTF8.GetBytes("userpassword")]));
        Assert.Equal(expected, OnvifClient.PasswordDigest(nonce, created, "userpassword"));
        Assert.Equal("tuOSpGlFlIXsozq4HFNeeGeFLEI=", OnvifClient.PasswordDigest(nonce, created, "userpassword")); // the ONVIF Core spec's example
    }

    [Fact]
    public void Streams_typed_in_or_found_get_the_login_only_for_ffmpeg()
    {
        var camera = new NvrEndpoint("192.168.1.50", 80, "admin", "p@ss", 554, null, "rtsp://192.168.1.50:554/main", "rtsp://192.168.1.50:554/sub");
        Assert.Equal("rtsp://admin:p%40ss@192.168.1.50:554/main", camera.RtspUrlWithLogin(1, 0));
        Assert.Equal("rtsp://admin:p%40ss@192.168.1.50:554/sub", camera.RtspUrlWithLogin(1, 1));
        Assert.Equal("rtsp://192.168.1.50:554/sub", camera.RtspUrl(1));
        Assert.False(camera.HasSnapshot); // streams only: no snapshot.cgi to ask

        var onlyMain = camera with { RtspSub = null, SnapshotUrl = "http://192.168.1.50/snap.jpg" };
        Assert.Equal("rtsp://admin:p%40ss@192.168.1.50:554/main", onlyMain.RtspUrlWithLogin(1, 1));
        Assert.True(onlyMain.HasSnapshot);
    }
}
