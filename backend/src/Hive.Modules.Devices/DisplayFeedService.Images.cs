using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hive.Contracts.Messages;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Telemetry;
using Hive.Modules.Weather;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Hive.Modules.Devices;

/// <summary>Pictures kept for a screen: state images, background and the sleep screen.</summary>
public sealed partial class DisplayFeedService
{
    public const int MaxImageBytes = 256 * 1024;
    public const int MaxImageSide = 100;

    /// <summary>
    /// A picture for a state item: PNG, JPEG, BMP or GIF, at most 100x100 pixels (any proportions), kept as PNG. Its id is what the
    /// item's config holds.
    /// </summary>
    public async Task<(ConfigUpdateStatus Status, string? Error, DisplayImageDto? Image)> AddImageAsync(string deviceId, byte[] image, string by, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId && (d.Driver == Drivers.Hornet || d.Driver == Drivers.Http), ct);
        if (device is null)
            return (ConfigUpdateStatus.NotFound, null, null);
        if (DisplayScreens.For(device.Hw, RotationOf(device)) is not { Rendered: true })
            return (ConfigUpdateStatus.BadRequest, "картинки бывают только у экранов, которые рисует Улей", null);
        using var codec = SKCodec.Create(new SKMemoryStream(image));
        using var bitmap = codec is null ? null : SKBitmap.Decode(codec);
        if (bitmap is null)
            return (ConfigUpdateStatus.BadRequest, "это не картинка: нужен PNG (лучше всего), JPEG, BMP или GIF", null);
        if (bitmap.Width > MaxImageSide || bitmap.Height > MaxImageSide)
            return (ConfigUpdateStatus.BadRequest, $"размер картинки {bitmap.Width}×{bitmap.Height}, а можно не больше {MaxImageSide}×{MaxImageSide}", null);
        // a transparent corner is white on the screen
        using var flat = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(flat))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(bitmap, 0, 0);
        }
        using var picture = SKImage.FromBitmap(flat);
        using var png = picture.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = png.ToArray();
        var id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
        if (!await db.DisplayImages.AnyAsync(i => i.DeviceId == deviceId && i.Id == id, ct))
        {
            db.DisplayImages.Add(new DisplayImage { DeviceId = deviceId, Id = id, Png = bytes, Width = flat.Width, Height = flat.Height, CreatedAt = time.GetUtcNow() });
            Audit.Record(db, time.GetUtcNow(), by, "device_display_image", deviceId);
            await db.SaveChangesAsync(ct);
        }
        return (ConfigUpdateStatus.Updated, null, new DisplayImageDto(id, flat.Width, flat.Height));
    }

    public async Task<byte[]?> ImageAsync(string deviceId, string imageId, CancellationToken ct) =>
        await db.DisplayImages.AsNoTracking().Where(i => i.DeviceId == deviceId && i.Id == imageId).Select(i => i.Png).FirstOrDefaultAsync(ct);

    public const int MaxBackgroundBytes = 1024 * 1024;

    /// <summary>
    /// The picture behind the items of a screen the hive draws. It must be a PNG, JPEG, BMP or GIF of exactly the size of the screen as
    /// it is turned (the page says which); it is kept as PNG. An e-ink panel shows it in black and white: a pixel darker than half is black.
    /// </summary>
    public async Task<(ConfigUpdateStatus Status, string? Error)> SetBackgroundAsync(string deviceId, byte[] image, string by, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId && (d.Driver == Drivers.Hornet || d.Driver == Drivers.Http), ct);
        if (device is null)
            return (ConfigUpdateStatus.NotFound, null);
        if (DisplayScreens.For(device.Hw, RotationOf(device)) is not { Rendered: true } screen)
            return (ConfigUpdateStatus.BadRequest, "фон бывает только у экранов, которые рисует Улей");
        // SKBitmap.Decode throws on bytes no codec recognizes, so open the codec first.
        using var codec = SKCodec.Create(new SKMemoryStream(image));
        using var bitmap = codec is null ? null : SKBitmap.Decode(codec);
        if (bitmap is null)
            return (ConfigUpdateStatus.BadRequest, "это не картинка: нужен PNG (лучше всего), JPEG, BMP или GIF");
        if (bitmap.Width != screen.Width || bitmap.Height != screen.Height)
            return (ConfigUpdateStatus.BadRequest, $"размер картинки {bitmap.Width}×{bitmap.Height}, а экрану нужно ровно {screen.Width}×{screen.Height}");
        using var picture = SKImage.FromBitmap(bitmap);
        using var png = picture.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = png.ToArray();
        device.ScreenBackground = bytes;
        device.ScreenBackgroundSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
        Audit.Record(db, time.GetUtcNow(), by, "device_display_background", deviceId);
        await db.SaveChangesAsync(ct);
        return (ConfigUpdateStatus.Updated, null);
    }

    public async Task<byte[]?> BackgroundAsync(string deviceId, CancellationToken ct) =>
        await db.Devices.AsNoTracking().Where(d => d.DeviceId == deviceId).Select(d => d.ScreenBackground).FirstOrDefaultAsync(ct);

    /// <summary>
    /// The picture a panel shows while it sleeps (a long press of EXIT). A PNG, JPEG, BMP or GIF of any size up to the screen as
    /// it is turned; the panel shows it in the middle on white. Kept as PNG.
    /// </summary>
    public async Task<(ConfigUpdateStatus Status, string? Error)> SetSleepAsync(string deviceId, byte[] image, string by, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId && (d.Driver == Drivers.Hornet || d.Driver == Drivers.Http), ct);
        if (device is null)
            return (ConfigUpdateStatus.NotFound, null);
        if (DisplayScreens.For(device.Hw, RotationOf(device)) is not { Rendered: true } screen)
            return (ConfigUpdateStatus.BadRequest, "картинка сна бывает только у экранов, которые рисует Улей");
        using var codec = SKCodec.Create(new SKMemoryStream(image));
        using var bitmap = codec is null ? null : SKBitmap.Decode(codec);
        if (bitmap is null)
            return (ConfigUpdateStatus.BadRequest, "это не картинка: нужен PNG (лучше всего), JPEG, BMP или GIF");
        if (bitmap.Width > screen.Width || bitmap.Height > screen.Height)
            return (ConfigUpdateStatus.BadRequest, $"размер картинки {bitmap.Width}×{bitmap.Height}, а экран {screen.Width}×{screen.Height}: она не должна быть больше");
        using var picture = SKImage.FromBitmap(bitmap);
        using var png = picture.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = png.ToArray();
        device.ScreenSleep = bytes;
        device.ScreenSleepSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
        Audit.Record(db, time.GetUtcNow(), by, "device_display_sleep", deviceId);
        await db.SaveChangesAsync(ct);
        return (ConfigUpdateStatus.Updated, null);
    }

    public async Task<byte[]?> SleepAsync(string deviceId, CancellationToken ct) =>
        await db.Devices.AsNoTracking().Where(d => d.DeviceId == deviceId).Select(d => d.ScreenSleep).FirstOrDefaultAsync(ct);

    public async Task<bool> RemoveSleepAsync(string deviceId, string by, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
            return false;
        device.ScreenSleep = null;
        device.ScreenSleepSha = null;
        Audit.Record(db, time.GetUtcNow(), by, "device_display_sleep_removed", deviceId);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The sleep picture in the middle of a white screen of the size the person sees it (not yet turned), or null without one.</summary>
    public SKBitmap? DrawSleep(Device device)
    {
        if (device.ScreenSleep is not { } png || DisplayScreens.For(device.Hw, RotationOf(device)) is not { } screen)
            return null;
        using var picture = SKBitmap.Decode(png);
        if (picture is null)
            return null;
        var bitmap = new SKBitmap(new SKImageInfo(screen.Width, screen.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(picture, (screen.Width - picture.Width) / 2, (screen.Height - picture.Height) / 2);
        }
        return InvertOf(device) ? DisplayRender.Invert(bitmap) : bitmap;
    }

    public async Task<bool> RemoveBackgroundAsync(string deviceId, string by, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
            return false;
        device.ScreenBackground = null;
        device.ScreenBackgroundSha = null;
        Audit.Record(db, time.GetUtcNow(), by, "device_display_background_removed", deviceId);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
