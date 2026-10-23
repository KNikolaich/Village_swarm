using System.Text;
using Hive.Modules.IpCams;

namespace Hive.Tests.Unit;

/// <summary>RTSP frames for IP cameras (contracts/ipcam.md): the login goes to ffmpeg and nowhere else.</summary>
public sealed class IpCamSnapshotTests
{
    private static readonly NvrEndpoint Nvr = new("192.168.10.245", 80, "admin", "p@ss:w/rd", 554);

    [Fact]
    public void Rtsp_url_carries_the_escaped_login_only_where_ffmpeg_needs_it()
    {
        Assert.Equal("rtsp://admin:p%40ss%3Aw%2Frd@192.168.10.245:554/cam/realmonitor?channel=2&subtype=0", Nvr.RtspUrlWithLogin(2, 0));
        Assert.Equal("rtsp://192.168.10.245/cam/realmonitor?channel=2&subtype=1", Nvr.RtspUrl(2));
        Assert.Equal("rtsp://192.168.10.245:8554/cam/realmonitor?channel=1&subtype=1", (Nvr with { RtspPort = 8554 }).RtspUrl(1));
    }

    [Fact]
    public void What_ffmpeg_says_is_cleaned_of_the_url_and_the_password()
    {
        var url = Nvr.RtspUrlWithLogin(2, 0);
        var said = $"[rtsp @ 0x55] method DESCRIBE failed: 401 Unauthorized\n{url}: Server returned 401 Unauthorized (p@ss:w/rd)";
        var clean = Snapshots.Redact(said, url);
        Assert.DoesNotContain("p@ss", clean);
        Assert.DoesNotContain("p%40ss", clean);
        Assert.Contains("rtsp://***", clean);
        Assert.Contains("401 Unauthorized", clean);
        Assert.True(Snapshots.Redact(new string('x', 1000), url).Length <= 300);
    }

    [Fact]
    public void A_body_that_is_not_a_jpeg_is_shown_as_short_printable_text()
    {
        Assert.Equal("Error  Internal Error", Snapshots.Peek(Encoding.UTF8.GetBytes("Error\r\nInternal Error\r\n")));
        Assert.Equal(200, Snapshots.Peek(Encoding.UTF8.GetBytes(new string('a', 500))).Length);
    }

    [Fact]
    public void A_locked_account_is_read_from_the_nvrs_error()
    {
        var locked = NvrAuthError.Parse(403, "Error {\"ErrorCode\":268632081,\"Result\":false,\"RmLock\":1320,\"RmLogin\":0}");
        Assert.True(locked.Locked);
        Assert.Equal(1320, locked.RmLock);
        Assert.Equal(NvrAuthError.LockedCode, locked.Code);
        Assert.Contains("locked for 1320 s", locked.ToString());

        var wrong = NvrAuthError.Parse(401, "Error {\"ErrorCode\":268632085,\"Result\":false,\"RmLock\":0,\"RmLogin\":4}");
        Assert.False(wrong.Locked);
        Assert.Equal(4, wrong.RmLogin);

        Assert.False(NvrAuthError.Parse(401, "").Locked);
        Assert.False(NvrAuthError.Parse(403, "Error\r\nNo Authority!\r\n").Locked);
    }
}
