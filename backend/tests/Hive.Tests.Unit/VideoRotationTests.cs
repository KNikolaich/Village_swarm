using System.Text.Json;
using Hive.Domain;
using Hive.Modules.Media;
using SkiaSharp;

namespace Hive.Tests.Unit;

/// <summary>A camera mounted turned: where its rotation is read from, and a photo turned once when stored.</summary>
public sealed class VideoRotationTests
{
    [Fact]
    public void The_rotation_is_camera_rotate_of_the_config_and_anything_else_is_0()
    {
        Assert.Equal(90, VideoRotation.Of(JsonDocument.Parse("""{"camera":{"rotate":90}}""")));
        Assert.Equal(270, VideoRotation.Of(JsonDocument.Parse("""{"camera":{"frame":"VGA","rotate":270}}""")));
        Assert.Equal(0, VideoRotation.Of(JsonDocument.Parse("""{"camera":{"rotate":45}}""")));
        Assert.Equal(0, VideoRotation.Of(JsonDocument.Parse("""{"camera":"x"}""")));
        Assert.Equal(0, VideoRotation.Of(null));
    }

    [Fact]
    public void A_photo_turned_by_90_swaps_its_sides_and_by_0_stays_the_same_bytes()
    {
        using var bitmap = new SKBitmap(64, 32);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.Orange);
        using var image = SKImage.FromBitmap(bitmap);
        var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();

        Assert.Equal((32, 64), ImageTools.Identify(ImageTools.Rotate(jpeg, 90)));
        Assert.Equal((32, 64), ImageTools.Identify(ImageTools.Rotate(jpeg, 270)));
        Assert.Equal((64, 32), ImageTools.Identify(ImageTools.Rotate(jpeg, 180)));
        Assert.Same(jpeg, ImageTools.Rotate(jpeg, 0));
    }
}
