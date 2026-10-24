using SkiaSharp;

namespace Hive.Modules.Media;

/// <summary>Image work via SkiaSharp (MIT; native libs for linux-arm64 included).</summary>
public static class ImageTools
{
    /// <summary>Reads JPEG dimensions from the header without decoding pixels; null if it is not a readable image.</summary>
    public static (int Width, int Height)? Identify(byte[] data)
    {
        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream);
        return codec is null ? null : (codec.Info.Width, codec.Info.Height);
    }

    /// <summary>How flat an image is: mean and spread of the brightness (0..255), and the share of pixels within ±4 of the mean.</summary>
    public sealed record Flatness(double Mean, double StdDev, double NearMean)
    {
        /// <summary>
        /// A grey picture: what a video decoder gives when it starts on a frame that needs one it never saw (mid-GOP), grey with
        /// at most a few fragments of whatever moved. A dark night picture is flat too but far from grey, and stays.
        /// </summary>
        public bool Grey => Mean is >= 100 and <= 156 && (StdDev < 6 || NearMean >= 0.95);

        public override string ToString() =>
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"mean {Mean:0}, spread {StdDev:0.0}, {NearMean:P0} near the mean");
    }

    /// <summary>Brightness statistics of a JPEG, decoded at about 64 px wide (cheap on the Pi); null when it cannot be decoded.</summary>
    public static Flatness? MeasureFlatness(byte[] data)
    {
        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
            return null;
        var size = codec.GetScaledDimensions(Math.Min(1f, 64f / codec.Info.Width));
        using var bitmap = SKBitmap.Decode(codec, new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (bitmap is null)
            return null;
        var luma = new double[bitmap.Width * bitmap.Height];
        var i = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                luma[i++] = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
            }
        var mean = luma.Average();
        var std = Math.Sqrt(luma.Average(v => (v - mean) * (v - mean)));
        var near = luma.Count(v => Math.Abs(v - mean) <= 4) / (double)luma.Length;
        return new Flatness(mean, std, near);
    }

    /// <summary>Downscales to <paramref name="width"/> px wide (never upscales) and encodes WebP.</summary>
    public static byte[] Thumbnail(string sourcePath, int width, int quality = 75)
    {
        using var codec = SKCodec.Create(sourcePath) ?? throw new InvalidDataException($"Not an image: {sourcePath}");
        // Let the JPEG decoder skip detail we would throw away anyway: much less memory on the RPi.
        var scale = Math.Min(1f, (float)width / codec.Info.Width);
        var decodeSize = codec.GetScaledDimensions(scale);
        using var decoded = SKBitmap.Decode(codec, codec.Info.WithSize(decodeSize.Width, decodeSize.Height))
            ?? SKBitmap.Decode(sourcePath)
            ?? throw new InvalidDataException($"Cannot decode {sourcePath}");

        var target = Math.Min(width, decoded.Width);
        var height = Math.Max(1, (int)Math.Round(decoded.Height * (double)target / decoded.Width));
        using var resized = target == decoded.Width && height == decoded.Height
            ? decoded.Copy()
            : decoded.Resize(new SKImageInfo(target, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var image = SKImage.FromBitmap(resized);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, quality);
        return encoded.ToArray();
    }
}
