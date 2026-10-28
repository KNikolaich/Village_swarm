using System.Text;
using Hive.Contracts.Messages;
using SkiaSharp;

namespace Hive.Modules.Devices;

/// <summary>
/// Draws a screen's frame in the hive (stage 4 of the monitors): an e-ink panel far away only shows the picture, so its
/// fonts, layout and background image are the hive's business. Black text on white, the same rules as the firmware of
/// the small OLED: placed items at x, y with a text height of size/10 of the screen, else an automatic layout.
/// </summary>
public static class DisplayRender
{
    /// <summary>Label height: the smallest text, as on the OLED where labels use the smallest font.</summary>
    private static int LabelHeight(int screenHeight) => DisplayText.PixelHeight(1, screenHeight);

    internal static SKFont MakeFont(FontFace face, float size) => new(face.Typeface, size)
    {
        Edging = SKFontEdging.Alias,
        Subpixel = false,
        Hinting = SKFontHinting.Full,
        Embolden = face.FakeBold,
        SkewX = face.FakeItalic ? -0.25f : 0,
    };

    /// <summary>The font size at which capital letters and digits are <paramref name="capHeight"/> pixels tall.</summary>
    private static float SizeForCap(SKTypeface typeface, float capHeight)
    {
        using var probe = new SKFont(typeface, 100);
        var cap = probe.Metrics.CapHeight;
        return 100 * capHeight / (cap > 0 ? cap : 72);
    }

    /// <summary>
    /// The frame as a greyscale bitmap the size of the screen (as it is turned: <see cref="Rotate"/> makes it the panel's):
    /// the background image when there is one and it is the screen's size, then the items.
    /// </summary>
    public static SKBitmap Render(DisplayMessage message, DisplayScreenDto screen, FontSet? fonts = null, SKBitmap? background = null,
        IReadOnlyDictionary<int, SKBitmap>? images = null)
    {
        fonts ??= FontSet.Empty;
        var bitmap = new SKBitmap(new SKImageInfo(screen.Width, screen.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        if (background is not null && background.Width == screen.Width && background.Height == screen.Height)
            canvas.DrawBitmap(background, 0, 0);
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        var items = message.Items;
        if (items.Any(i => i.Size is not null))
            DrawPlaced(canvas, ink, items, screen, fonts, images);
        else
            DrawAuto(canvas, ink, items, screen, fonts);
        return bitmap;
    }

    /// <summary>Black for white and white for black, in place (a gray picture, as the hive draws them): the dark look of a screen.</summary>
    public static SKBitmap Invert(SKBitmap bitmap)
    {
        var pixels = bitmap.GetPixelSpan();
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)(255 - pixels[i]);
        return bitmap;
    }

    /// <summary>The picture turned clockwise by 0, 90, 180 or 270 degrees: from the screen as the person sees it to the panel's own.</summary>
    public static SKBitmap Rotate(SKBitmap source, int degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        if (degrees == 0)
            return source.Copy();
        var sideways = degrees is 90 or 270;
        var target = new SKBitmap(new SKImageInfo(sideways ? source.Height : source.Width, sideways ? source.Width : source.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.White);
        switch (degrees)
        {
            case 90: canvas.Translate(target.Width, 0); break;
            case 180: canvas.Translate(target.Width, target.Height); break;
            default: canvas.Translate(0, target.Height); break;
        }
        canvas.RotateDegrees(degrees);
        canvas.DrawBitmap(source, 0, 0);
        return target;
    }

    /// <summary>
    /// Draws text whose top (capital letters) is at <paramref name="top"/>. <paramref name="size"/> is the font size in pixels,
    /// or with <paramref name="capSized"/> the height of capital letters. A letter the font lacks (a degree sign in a segment
    /// display, Russian letters in a Gothic one) is drawn with the standard font instead.
    /// </summary>
    internal static float Text(SKCanvas canvas, SKPaint ink, string text, float x, float top, FontFace face, FontFace fallback, float size, bool capSized, string? align)
    {
        SKFont Make(FontFace f) => MakeFont(f, capSized ? SizeForCap(f.Typeface, size) : size);
        using var main = Make(face);
        using var alt = Make(fallback);
        var cap = main.Metrics.CapHeight;
        if (cap <= 0)
            cap = main.Size * 0.7f;
        var runs = new List<(string Text, SKFont Font, float Width)>();
        var current = new StringBuilder();
        var currentMain = true;
        void Flush()
        {
            if (current.Length == 0)
                return;
            var font = currentMain ? main : alt;
            runs.Add((current.ToString(), font, font.MeasureText(current.ToString())));
            current.Clear();
        }
        foreach (var rune in text.EnumerateRunes())
        {
            var inMain = Rune.IsWhiteSpace(rune) || face.Typeface.ContainsGlyph(rune.Value);
            if (inMain != currentMain)
                Flush();
            currentMain = inMain;
            current.Append(rune.ToString());
        }
        Flush();
        var width = runs.Sum(r => r.Width);
        // whole pixels: a pixel font at its own size lands on the screen's grid, not smeared across two pixels
        var left = MathF.Round(align switch { "center" => x - width / 2, "right" => x - width, _ => x });
        var baseline = MathF.Round(top + cap);
        foreach (var (run, font, runWidth) in runs)
        {
            canvas.DrawText(run, left, baseline, SKTextAlign.Left, font, ink);
            left += runWidth;
        }
        return cap;
    }

    private static void DrawPlaced(SKCanvas canvas, SKPaint ink, IReadOnlyList<DisplayItem> items, DisplayScreenDto screen, FontSet fonts,
        IReadOnlyDictionary<int, SKBitmap>? images)
    {
        var label = LabelHeight(screen.Height);
        var fallback = fonts.Fallback;
        for (var n = 0; n < items.Count; n++)
        {
            var item = items[n];
            var face = fonts.Resolve(item.Font, item.Bold == true, item.Italic == true);
            float y = item.Y ?? 0;
            if (images is not null && images.TryGetValue(n, out var picture))
            {
                // a picture instead of the text: from x, y as the text would stand (its x is the middle or the right edge when aligned so)
                if (!string.IsNullOrEmpty(item.Label))
                {
                    var cap = Text(canvas, ink, item.Label, item.X ?? 0, y, face, fallback, label, true, item.Align);
                    y += cap + Math.Max(2, label / 3);
                }
                var left = item.Align switch { "center" => (item.X ?? 0) - picture.Width / 2f, "right" => (item.X ?? 0) - picture.Width, _ => item.X ?? 0 };
                canvas.DrawBitmap(picture, MathF.Round(left), MathF.Round(y));
                continue;
            }
            if (item.Px is { } px)
            {
                // font size in pixels: the label in half of it (at least 6)
                var labelPx = Math.Max(6, (float)Math.Round(px / 2.0));
                if (!string.IsNullOrEmpty(item.Label))
                {
                    var cap = Text(canvas, ink, item.Label, item.X ?? 0, y, face, fallback, labelPx, false, item.Align);
                    y += cap + Math.Max(2, labelPx / 3);
                }
                Text(canvas, ink, item.Text, item.X ?? 0, y, face, fallback, px, false, item.Align);
                continue;
            }
            if (!string.IsNullOrEmpty(item.Label))
            {
                Text(canvas, ink, item.Label, item.X ?? 0, y, face, fallback, label, true, item.Align);
                y += label + Math.Max(2, label / 3);
            }
            Text(canvas, ink, item.Text, item.X ?? 0, y, face, fallback, DisplayText.PixelHeight(item.Size ?? 3, screen.Height), true, item.Align);
        }
    }

    /// <summary>
    /// As the OLED does it, scaled: one item is a label above a large value, two are two large values, more are rows with
    /// the label on the left and the value on the right. Nothing chosen: a short hint.
    /// </summary>
    private static void DrawAuto(SKCanvas canvas, SKPaint ink, IReadOnlyList<DisplayItem> items, DisplayScreenDto screen, FontSet fonts)
    {
        float w = screen.Width, h = screen.Height;
        var label = LabelHeight(screen.Height);
        var pad = Math.Max(2, w / 36);
        var face = fonts.Resolve(null, true, false);
        var fallback = fonts.Fallback;
        void Put(string text, float x, float top, float cap, string? align) => Text(canvas, ink, text, x, top, face, fallback, cap, true, align);
        switch (items.Count)
        {
            case 0:
                Put("Нет строк:", pad, h * 0.3f, label, null);
                Put("настройте в Улье", pad, h * 0.3f + label * 2, label, null);
                break;
            case 1:
                if (!string.IsNullOrEmpty(items[0].Label))
                    Put(items[0].Label!, pad, pad, label, null);
                Put(items[0].Text, w / 2, h * 0.35f, h * 0.4f, "center");
                break;
            case 2:
                Put(items[0].Text, pad, h * 0.08f, h * 0.36f, null);
                Put(items[1].Text, pad, h * 0.56f, h * 0.36f, null);
                break;
            default:
                var row = h / items.Count;
                var text = Math.Min(row * 0.6f, h / 6);
                for (var i = 0; i < items.Count; i++)
                {
                    var top = i * row + (row - text) / 2;
                    Put(items[i].Label ?? "", pad, top, text, null);
                    Put(items[i].Text, w - pad, top, text, "right");
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
