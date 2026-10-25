using Hive.Contracts.Messages;
using SkiaSharp;

namespace Hive.Modules.Devices;

/// <summary>
/// Draws a screen's frame in the hive (stage 4 of the monitors): an e-ink panel far away only shows the picture, so its
/// fonts, layout and (later) background are the hive's business. Black text on white, the same rules as the firmware of
/// the small OLED: placed items at x, y with a text height of size/10 of the screen, else an automatic layout.
/// </summary>
public static class DisplayRender
{
    private static readonly Lazy<SKTypeface> Font = new(() =>
    {
        using var stream = typeof(DisplayRender).Assembly.GetManifestResourceStream("Hive.Modules.Devices.Fonts.DejaVuSansCondensed-Bold.ttf")
            ?? throw new InvalidOperationException("the screen font is not embedded");
        return SKTypeface.FromStream(stream) ?? throw new InvalidOperationException("the screen font cannot be read");
    });

    /// <summary>A font whose capital letters and digits are <paramref name="capHeight"/> pixels tall.</summary>
    private static SKFont FontFor(float capHeight)
    {
        var font = new SKFont(Font.Value, 100) { Edging = SKFontEdging.Alias, Subpixel = false, Hinting = SKFontHinting.Full };
        var cap = font.Metrics.CapHeight;
        font.Size = 100 * capHeight / (cap > 0 ? cap : 72);
        return font;
    }

    /// <summary>Label height: the smallest text, as on the OLED where labels use the smallest font.</summary>
    private static int LabelHeight(int screenHeight) => DisplayText.PixelHeight(1, screenHeight);

    /// <summary>The frame as a greyscale bitmap the size of the screen.</summary>
    public static SKBitmap Render(DisplayMessage message, DisplayScreenDto screen)
    {
        var bitmap = new SKBitmap(new SKImageInfo(screen.Width, screen.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        var items = message.Items;
        if (items.Any(i => i.Size is not null))
            DrawPlaced(canvas, ink, items, screen);
        else
            DrawAuto(canvas, ink, items, screen);
        return bitmap;
    }

    private static void Text(SKCanvas canvas, SKPaint ink, string text, float x, float top, float capHeight, string? align)
    {
        using var font = FontFor(capHeight);
        var width = font.MeasureText(text);
        var left = align switch { "center" => x - width / 2, "right" => x - width, _ => x };
        canvas.DrawText(text, left, top + capHeight, SKTextAlign.Left, font, ink);
    }

    private static void DrawPlaced(SKCanvas canvas, SKPaint ink, IReadOnlyList<DisplayItem> items, DisplayScreenDto screen)
    {
        var label = LabelHeight(screen.Height);
        foreach (var item in items)
        {
            float y = item.Y ?? 0;
            if (!string.IsNullOrEmpty(item.Label))
            {
                Text(canvas, ink, item.Label, item.X ?? 0, y, label, item.Align);
                y += label + Math.Max(2, label / 3);
            }
            Text(canvas, ink, item.Text, item.X ?? 0, y, DisplayText.PixelHeight(item.Size ?? 3, screen.Height), item.Align);
        }
    }

    /// <summary>
    /// As the OLED does it, scaled: one item is a label above a large value, two are two large values, more are rows with
    /// the label on the left and the value on the right. Nothing chosen: a short hint.
    /// </summary>
    private static void DrawAuto(SKCanvas canvas, SKPaint ink, IReadOnlyList<DisplayItem> items, DisplayScreenDto screen)
    {
        float w = screen.Width, h = screen.Height;
        var label = LabelHeight(screen.Height);
        var pad = Math.Max(2, w / 36);
        switch (items.Count)
        {
            case 0:
                Text(canvas, ink, "Нет строк:", pad, h * 0.3f, label, null);
                Text(canvas, ink, "настройте в Улье", pad, h * 0.3f + label * 2, label, null);
                break;
            case 1:
                if (!string.IsNullOrEmpty(items[0].Label))
                    Text(canvas, ink, items[0].Label!, pad, pad, label, null);
                Text(canvas, ink, items[0].Text, w / 2, h * 0.35f, h * 0.4f, "center");
                break;
            case 2:
                Text(canvas, ink, items[0].Text, pad, h * 0.08f, h * 0.36f, null);
                Text(canvas, ink, items[1].Text, pad, h * 0.56f, h * 0.36f, null);
                break;
            default:
                var row = h / items.Count;
                var text = Math.Min(row * 0.6f, h / 6);
                for (var i = 0; i < items.Count; i++)
                {
                    var top = i * row + (row - text) / 2;
                    Text(canvas, ink, items[i].Label ?? "", pad, top, text, null);
                    Text(canvas, ink, items[i].Text, w - pad, top, text, "right");
                }
                break;
        }
    }

    /// <summary>
    /// One bit a pixel, rows top to bottom, 8 pixels a byte with the leftmost in the high bit, a row padded to whole bytes;
    /// a set bit is black (contracts/monitor-http.md, frame.bin).
    /// </summary>
    public static byte[] Pack(SKBitmap bitmap)
    {
        var stride = (bitmap.Width + 7) / 8;
        var bits = new byte[stride * bitmap.Height];
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (pixels[y * bitmap.RowBytes + x] < 128)
                    bits[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
        return bits;
    }

    public static byte[] Png(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
