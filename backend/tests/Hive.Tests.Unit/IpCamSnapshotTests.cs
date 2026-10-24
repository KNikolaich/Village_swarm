using System.Text;
using Hive.Modules.IpCams;
using Hive.Modules.Media;
using SkiaSharp;

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

    private static byte[] Jpeg(int width, int height, Action<SKBitmap> paint)
    {
        using var bitmap = new SKBitmap(width, height);
        paint(bitmap);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    [Fact]
    public void Grey_frames_of_a_broken_decode_are_told_from_real_pictures_dark_ones_included()
    {
        var grey = Jpeg(320, 240, b => b.Erase(new SKColor(128, 128, 128)));
        var greyWithFragments = Jpeg(320, 240, b =>
        {
            b.Erase(new SKColor(127, 128, 129));
            for (var x = 30; x < 38; x++)
                for (var y = 10; y < 18; y++)
                    b.SetPixel(x, y, new SKColor(190, 40, 200)); // a bit of someone walking by
        });
        var random = new Random(7);
        var scene = Jpeg(320, 240, b =>
        {
            for (var x = 0; x < b.Width; x++)
                for (var y = 0; y < b.Height; y++)
                    b.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        });
        var night = Jpeg(320, 240, b => b.Erase(new SKColor(18, 20, 18)));

        Assert.True(ImageTools.MeasureFlatness(grey)!.Grey);
        Assert.True(ImageTools.MeasureFlatness(greyWithFragments)!.Grey);
        Assert.False(ImageTools.MeasureFlatness(scene)!.Grey);
        Assert.False(ImageTools.MeasureFlatness(night)!.Grey); // flat, but a dark yard at night is a real picture
        Assert.Null(ImageTools.MeasureFlatness([1, 2, 3]));
    }

    [Fact]
    public void Jpegs_are_cut_out_of_ffmpegs_pipe_whatever_the_chunks()
    {
        var a = Jpeg(64, 48, b => b.Erase(SKColors.Red));
        var c = Jpeg(80, 60, b => b.Erase(SKColors.Blue));
        var pipe = a.Concat(c).ToArray();

        var buffer = new MemoryStream();
        var frames = new List<byte[]>();
        foreach (var piece in pipe.Chunk(37))
        {
            buffer.Write(piece);
            frames.AddRange(JpegStream.TakeFrames(buffer));
        }
        Assert.Equal(2, frames.Count);
        Assert.Equal(a, frames[0]);
        Assert.Equal(c, frames[1]);
        Assert.Equal(0, buffer.Length);

        buffer.Write(a.AsSpan(0, a.Length - 1)); // the end has not come yet
        Assert.Empty(JpegStream.TakeFrames(buffer));
        Assert.Equal(a.Length - 1, buffer.Length);
    }

    [Fact]
    public void A_single_snapshot_decodes_key_frames_only()
    {
        var args = FfmpegFrameGrabber.Arguments("rtsp://u:p@h:554/x", 1, TimeSpan.FromMilliseconds(700)).ToList();
        var input = args.IndexOf("-i");
        Assert.True(args.IndexOf("nokey") is var k && k > 0 && k < input, "-skip_frame nokey goes on the input");
        Assert.Equal("-skip_frame", args[args.IndexOf("nokey") - 1]);
        Assert.DoesNotContain("nobuffer", args);
        Assert.Equal("passthrough", args[args.IndexOf("-fps_mode") + 1]);
        Assert.Equal("1", args[args.IndexOf("-frames:v") + 1]);
    }

    [Fact]
    public void An_alarm_burst_waits_for_one_key_frame_then_takes_frames_apart()
    {
        var args = FfmpegFrameGrabber.Arguments("rtsp://u:p@h:554/x", 3, TimeSpan.FromMilliseconds(700)).ToList();
        Assert.DoesNotContain("-skip_frame", args); // the frames after the key frame are wanted, decoded against it
        var filter = args[args.IndexOf("-vf") + 1];
        Assert.Equal("select='eq(pict_type\\,I)+not(isnan(prev_selected_n))',fps=1000/700,showinfo", filter);
        Assert.Equal("info", args[args.IndexOf("-loglevel") + 1]); // showinfo speaks at info level
        Assert.True(args.IndexOf("-vf") > args.IndexOf("-i"), "the filter is an output option");
        Assert.Equal("3", args[args.IndexOf("-frames:v") + 1]);
    }

    [Fact]
    public void Showinfo_lines_give_the_frames_time_in_the_stream()
    {
        Assert.Equal(0.7, FfmpegFrameGrabber.ShowInfoTime("[Parsed_showinfo_2 @ 0x5583] n:   1 pts:  63000 pts_time:0.7     duration:  6300 fmt:yuvj420p"));
        Assert.Equal(12.5005, FfmpegFrameGrabber.ShowInfoTime("[Parsed_showinfo_2 @ 0x5583] n:   0 pts:1125045 pts_time:12.5005 duration:3600"));
        Assert.Null(FfmpegFrameGrabber.ShowInfoTime("Input #0, rtsp, from 'rtsp://***':"));
        Assert.Null(FfmpegFrameGrabber.ShowInfoTime("[Parsed_showinfo_2 @ 0x5583] config in time_base: 1/90000, frame_rate: 25/1"));
    }

    [Fact]
    public void One_failure_does_not_move_the_preferred_way_three_do_and_a_faster_one_comes_back()
    {
        var t0 = DateTimeOffset.Parse("2026-10-24T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        string[] both = ["cgi", "rtsp"];
        var chooser = new WayChooser("cgi");
        Assert.Null(chooser.Record("cgi", true, 750, t0));
        Assert.Equal(both, chooser.Order(both, t0));

        // A busy NVR misses one snapshot: the stream gives the frame this time, cgi stays first.
        Assert.Null(chooser.Record("cgi", false, 6000, t0.AddMinutes(1)));
        Assert.Null(chooser.Record("rtsp", true, 3500, t0.AddMinutes(1)));
        Assert.Equal("cgi", chooser.Preferred);
        Assert.Null(chooser.Record("cgi", true, 760, t0.AddMinutes(2)));

        // Three misses in a row: the stream goes first.
        chooser.Record("cgi", false, 6000, t0.AddMinutes(3));
        chooser.Record("rtsp", true, 3400, t0.AddMinutes(3));
        chooser.Record("cgi", false, 6000, t0.AddMinutes(4));
        chooser.Record("rtsp", true, 3300, t0.AddMinutes(4));
        Assert.Equal("cgi", chooser.Preferred);
        var change = chooser.Record("cgi", false, 6000, t0.AddMinutes(5));
        Assert.Equal(new WaySwitch("cgi", "rtsp", "cgi failed 3 times in a row"), change);
        Assert.Equal(["rtsp", "cgi"], chooser.Order(both, t0.AddMinutes(6)));

        // Ten minutes on, cgi is tried first again; it works and is faster: back to cgi.
        Assert.Equal(both, chooser.Order(both, t0.AddMinutes(16)));
        var back = chooser.Record("cgi", true, 700, t0.AddMinutes(16));
        Assert.Equal("cgi", back!.To);
        Assert.Contains("first frame", back.Reason);
    }

    [Fact]
    public void A_channel_stored_as_rtsp_tries_cgi_once_again()
    {
        var t0 = DateTimeOffset.Parse("2026-10-24T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        string[] both = ["cgi", "rtsp"];
        var chooser = new WayChooser("rtsp");
        Assert.Equal(both, chooser.Order(both, t0)); // cgi never tried in this run: it goes first once
        chooser.Record("cgi", true, 750, t0);
        chooser.Record("rtsp", true, 3600, t0.AddMinutes(1));
        Assert.Equal(["rtsp", "cgi"], chooser.Order(both, t0.AddMinutes(2)));
        Assert.Equal(both, chooser.Order(both, t0.AddMinutes(12))); // cgi was faster: retried after ten minutes
        Assert.Equal("cgi", chooser.Record("cgi", true, 740, t0.AddMinutes(12))!.To);
    }

    [Fact]
    public void A_slower_way_that_works_is_not_retried_for_nothing()
    {
        var t0 = DateTimeOffset.Parse("2026-10-24T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        string[] both = ["cgi", "rtsp"];
        var chooser = new WayChooser("cgi");
        chooser.Record("cgi", true, 700, t0);
        chooser.Record("cgi", false, 6000, t0.AddMinutes(1));
        chooser.Record("rtsp", true, 3500, t0.AddMinutes(1));
        chooser.Record("cgi", true, 700, t0.AddMinutes(2));
        Assert.Equal(both, chooser.Order(both, t0.AddHours(1))); // rtsp works but is slower: cgi stays first, no retry of rtsp
    }
}
