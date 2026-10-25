using Hive.Contracts.Messages;
using Hive.Modules.Devices;

namespace Hive.Tests.Unit;

/// <summary>The hive draws the frames of e-ink monitors: black on white, packed one bit a pixel (contracts/monitor-http.md).</summary>
public class DisplayRenderTests
{
    private static readonly DisplayScreenDto Epaper = new(200, 200, Rendered: true);

    private static DisplayMessage Message(params DisplayItem[] items) => new() { Ts = 0, Items = items };

    [Fact]
    public void A_frame_is_one_bit_a_pixel_with_the_leftmost_pixel_in_the_high_bit()
    {
        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(10, 2, SkiaSharp.SKColorType.Gray8, SkiaSharp.SKAlphaType.Opaque));
        bitmap.Erase(SkiaSharp.SKColors.White);
        bitmap.SetPixel(0, 0, SkiaSharp.SKColors.Black);
        bitmap.SetPixel(9, 1, SkiaSharp.SKColors.Black);
        var bits = DisplayRender.Pack(bitmap);
        Assert.Equal(new byte[] { 0x80, 0x00, 0x00, 0x40 }, bits);  // rows of 10 pixels take 2 bytes each
    }

    [Fact]
    public void A_placed_value_is_drawn_where_it_is_placed_at_its_size()
    {
        using var bitmap = DisplayRender.Render(Message(new DisplayItem { Text = "21.5°", X = 10, Y = 120, Size = 3 }), Epaper);
        var (top, bottom, left) = Ink(bitmap);
        Assert.InRange(top, 118, 124);          // the top of the digits at y
        Assert.InRange(bottom - top, 52, 66);   // size 3 on 200 px: digits about 60 px tall
        Assert.InRange(left, 9, 16);            // starting at x
        Assert.Equal(200 * 200 / 8, DisplayRender.Pack(bitmap).Length);
    }

    [Fact]
    public void A_right_aligned_value_ends_at_its_x()
    {
        using var bitmap = DisplayRender.Render(Message(new DisplayItem { Text = "63%", X = 190, Y = 10, Size = 2, Align = "right" }), Epaper);
        var right = 0;
        for (var y = 0; y < 200; y++)
            for (var x = 0; x < 200; x++)
                if (bitmap.GetPixel(x, y).Red < 128)
                    right = Math.Max(right, x);
        Assert.InRange(right, 182, 191);
    }

    [Fact]
    public void Without_places_the_screen_is_laid_out_and_an_empty_one_says_so()
    {
        using var two = DisplayRender.Render(Message(new DisplayItem { Text = "-3.2°" }, new DisplayItem { Text = "63%" }), Epaper);
        var (top, bottom, _) = Ink(two);
        Assert.True(top < 40 && bottom > 160, $"two large values fill the screen: ink from {top} to {bottom}");
        using var empty = DisplayRender.Render(Message(), Epaper);
        Assert.True(Ink(empty).Bottom > 0, "a hint is drawn");
        Assert.StartsWith("\u0089PNG", System.Text.Encoding.Latin1.GetString(DisplayRender.Png(empty)[..4]));
    }

    private static (int Top, int Bottom, int Left) Ink(SkiaSharp.SKBitmap bitmap)
    {
        int top = int.MaxValue, bottom = 0, left = int.MaxValue;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).Red < 128)
                {
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                    left = Math.Min(left, x);
                }
        return (top, bottom, left);
    }
}
