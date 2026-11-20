using System.Text.Json;
using Hive.Modules.IpCams;

namespace Hive.Tests.Unit;

/// <summary>Live video on a strong hive: who counts as a viewer, which requests open one, and the H.264 copy's source.</summary>
public sealed class VideoViewersTests
{
    [Fact]
    public void Viewers_are_the_browsers_players_not_go2rtcs_own_ffmpeg_or_single_frames()
    {
        // As go2rtc 1.9.4 lists them: a browser on the H.265 stream, one on the H.264 copy whose ffmpeg reads the camera
        // stream back over RTSP, a car's MJPEG, a single frame, and a stream nobody watches.
        var streams = JsonDocument.Parse("""
            {
              "ipcam1-ch1-sub": {"producers": [{"url": "rtsp://cam"}], "consumers": [
                {"format_name": "mse/fmp4", "protocol": "ws"},
                {"format_name": "rtsp", "protocol": "rtsp+tcp"}]},
              "ipcam1-ch1-sub-h264": {"producers": [{"url": "ffmpeg:ipcam1-ch1-sub#video=h264"}], "consumers": [
                {"format_name": "mse/fmp4", "protocol": "ws"}]},
              "car-cam2-sub": {"producers": [{"url": "http://car"}], "consumers": [
                {"format_name": "mjpeg", "protocol": "http"},
                {"format_name": "jpeg", "protocol": "http"}]},
              "ipcam1-ch2-main": {"producers": [{"url": "rtsp://cam"}], "consumers": null}
            }
            """).RootElement;
        Assert.Equal(3, Go2RtcClient.CountViewers(streams));
        Assert.Equal(0, Go2RtcClient.CountViewers(JsonDocument.Parse("{}").RootElement));
        Assert.Equal(0, Go2RtcClient.CountViewers(JsonDocument.Parse("null").RootElement));
    }

    [Fact]
    public void Only_the_player_socket_and_mjpeg_open_a_viewer()
    {
        Assert.True(VideoStreams.IsLive("/api/ws?src=ipcam1-ch1-sub"));
        Assert.True(VideoStreams.IsLive("/api/stream.mjpeg?src=car-cam2-sub"));
        Assert.False(VideoStreams.IsLive("/api/frame.jpeg?src=ipcam1-ch1-sub"));
        Assert.False(VideoStreams.IsLive(null));
    }

    [Fact]
    public void The_h264_copy_is_made_by_the_cpu_or_the_graphics_card_or_not_at_all()
    {
        Assert.Equal("#video=h264", VideoStreams.TranscodeSuffix("cpu"));
        Assert.Equal("#video=h264#hardware=vaapi", VideoStreams.TranscodeSuffix(" VAAPI "));
        Assert.Null(VideoStreams.TranscodeSuffix(""));
        Assert.Null(VideoStreams.TranscodeSuffix(null));
        Assert.Null(VideoStreams.TranscodeSuffix("nvenc"));
    }
}
