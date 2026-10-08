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
