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

/// <summary>
/// A hornet with a screen may show readings of other devices (config display.items). The hive keeps the choice in the hornet's
/// config and sends the ready texts to vs/v1/hive/display/{id}: the hornet only draws them. Without items it shows its own readings.
/// </summary>
public sealed partial class DisplayFeedService(
    HiveDbContext db, TelemetryQueries telemetry, MqttGateway mqtt, TimeProvider time, DisplayFontCatalog fonts, IOptions<HiveOptions> hive)
{
    /// <summary>Devices with a screen the hive feeds: hornets (over MQTT) and monitors far away (over HTTP).</summary>
    private static bool HasScreen(Device d) => d.Driver == Drivers.Hornet || d.Driver == Drivers.Http;

    public async Task<DisplayItemsDto?> GetAsync(string deviceId, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId && (d.Driver == Drivers.Hornet || d.Driver == Drivers.Http), ct);
        if (device is null)
            return null;
        var rotation = RotationOf(device);
        return new DisplayItemsDto(ItemsOf(device), DisplayScreens.For(device.Hw, rotation) is { } screen ? screen with { Background = device.ScreenBackground is not null, Sleep = device.ScreenSleep is not null } : null, rotation,
            MonitorOf(device, time.GetUtcNow()), InvertOf(device), BatteryOf(device));
    }

    /// <summary>What a panel may be monitored for: as long and as often as a person can choose (the same hours as a thermometer).</summary>
    public static readonly double[] MonitorHours = [0.5, 1, 2, 3, 4, 6, 8, 12];
    public static readonly int[] MonitorEveryMin = [1, 2, 5, 10];

    /// <summary>The monitoring of a panel (config display.monitor) while it lasts; null when off or over.</summary>
    public static DisplayMonitorDto? MonitorOf(Device device, DateTimeOffset now)
    {
        if (device.Config is null || !device.Config.RootElement.TryGetProperty("display", out var display)
            || !display.TryGetProperty("monitor", out var monitor) || !monitor.TryGetProperty("until", out var u) || !u.TryGetInt64(out var untilMs)
            || !monitor.TryGetProperty("every_min", out var e) || !e.TryGetInt32(out var every))
            return null;
        var until = DateTimeOffset.FromUnixTimeMilliseconds(untilMs);
        return until > now ? new DisplayMonitorDto(until, every) : null;
    }

    /// <summary>
    /// Starts (or changes) the monitoring of a panel far away: it reads its screen more often until the time is up. Null hours
    /// stops it. Only monitors that read over HTTP: a hornet's screen is fed when it changes anyway.
    /// </summary>
    public async Task<(ConfigUpdateStatus Status, string? Error)> SetMonitorAsync(string deviceId, DisplayMonitorRequest? request, string by, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.Driver == Drivers.Http, ct);
        if (device is null)
            return (ConfigUpdateStatus.NotFound, null);
        if (request is not null && (!MonitorHours.Contains(request.Hours) || !MonitorEveryMin.Contains(request.EveryMin)))
            return (ConfigUpdateStatus.BadRequest, $"часы: {string.Join(", ", MonitorHours)}; минуты: {string.Join(", ", MonitorEveryMin)}");
        var config = device.Config is { } current
            ? JsonNode.Parse(current.RootElement.GetRawText())!.AsObject()
            : new JsonObject { ["v"] = 1, ["name"] = device.Name, ["health_interval_s"] = 60 };
        var display = config["display"] as JsonObject ?? new JsonObject();
        if (request is null)
            display.Remove("monitor");
        else
            display["monitor"] = new JsonObject
            {
                ["until"] = time.GetUtcNow().AddHours(request.Hours).ToUnixTimeMilliseconds(),
                ["every_min"] = request.EveryMin,
            };
        if (display.Count == 0)
            config.Remove("display");
        else
            config["display"] = display;
        device.ConfigRev++;
        config["rev"] = device.ConfigRev;
        device.Config = JsonDocument.Parse(config.ToJsonString());
        Audit.Record(db, time.GetUtcNow(), by, request is null ? "display_monitor_stopped" : "display_monitor_started", deviceId, request);
        await db.SaveChangesAsync(ct);
        return (ConfigUpdateStatus.Updated, null);
    }

    /// <summary>How the picture of a screen the hive draws is turned (config display.rotation): 0, 90, 180 or 270; absent: the board's way.</summary>
    public static int RotationOf(Device device) =>
        device.Config is not null && device.Config.RootElement.TryGetProperty("display", out var display)
            && display.TryGetProperty("rotation", out var r) && r.TryGetInt32(out var degrees) && DisplayScreens.Rotations.Contains(degrees)
            ? degrees : DisplayScreens.DefaultRotation(device.Hw);

    /// <summary>Where the battery icon stands (config display.battery); the defaults (top right corner, base size, shown) when absent.</summary>
    public static BatteryLayout BatteryOf(Device device)
    {
        if (device.Config is null || !device.Config.RootElement.TryGetProperty("display", out var display)
            || !display.TryGetProperty("battery", out var b) || b.ValueKind != JsonValueKind.Object)
            return new BatteryLayout(true);
        return new BatteryLayout(!(b.TryGetProperty("enabled", out var on) && on.ValueKind == JsonValueKind.False), Int(b, "x"), Int(b, "y"), Int(b, "scale"));
    }

    /// <summary>Whether the picture of a screen the hive draws is dark: white on black (config display.invert).</summary>
    public static bool InvertOf(Device device) =>
        device.Config is not null && device.Config.RootElement.TryGetProperty("display", out var display)
            && display.TryGetProperty("invert", out var i) && i.ValueKind == JsonValueKind.True;

    public static IReadOnlyList<DisplayItemDto> ItemsOf(Device device)
    {
        if (device.Config is null || !device.Config.RootElement.TryGetProperty("display", out var display)
            || !display.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];
        return items.EnumerateArray()
            .Select(i => new DisplayItemDto(
                i.TryGetProperty("device", out var d) ? d.GetString() : null,
                i.TryGetProperty("metric", out var m) ? m.GetString() ?? "" : "",
                i.TryGetProperty("label", out var l) ? l.GetString() : null,
                Int(i, "x"), Int(i, "y"), Int(i, "size"),
                i.TryGetProperty("align", out var a) ? a.GetString() : null,
                i.TryGetProperty("text", out var text) ? text.GetString() : null,
                i.TryGetProperty("font", out var font) ? font.GetString() : null,
                Int(i, "px"), Bool(i, "bold"), Bool(i, "italic"),
                i.TryGetProperty("clock", out var clock) ? clock.GetString() : null,
                i.TryGetProperty("format", out var format) ? format.GetString() : null,
                Bool(i, "state"),
                i.TryGetProperty("on_text", out var onText) ? onText.GetString() : null,
                i.TryGetProperty("off_text", out var offText) ? offText.GetString() : null,
                i.TryGetProperty("on_image", out var onImage) ? onImage.GetString() : null,
                i.TryGetProperty("off_image", out var offImage) ? offImage.GetString() : null))
            .ToList();
    }

    private static int? Int(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;

    private static bool? Bool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    /// <summary>
    /// Stores the choice in the config (bumping its revision), sends the config, then the screen text. A refusal comes
    /// with its reason (checked against the screen of the board too).
    /// </summary>
    public async Task<(ConfigUpdateStatus Status, string? Error)> SaveAsync(
        string deviceId, IReadOnlyList<DisplayItemDto> items, int? rotation, string by, CancellationToken ct, bool? invert = null, BatteryLayout? battery = null)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId && (d.Driver == Drivers.Hornet || d.Driver == Drivers.Http), ct);
        if (device is null)
            return (ConfigUpdateStatus.NotFound, null);
        if (rotation is { } turn && (!DisplayScreens.Rotations.Contains(turn) || (turn != 0 && DisplayScreens.For(device.Hw) is not { Rendered: true })))
            return (ConfigUpdateStatus.BadRequest, "поворот: 0, 90, 180 или 270, и только у экранов, которые рисует Улей");
        if (invert == true && DisplayScreens.For(device.Hw) is not { Rendered: true })
            return (ConfigUpdateStatus.BadRequest, "инверсия бывает только у экранов, которые рисует Улей");
        if (battery is not null && DisplayScreens.For(device.Hw) is not { Rendered: true, Tabs: 0 })
            return (ConfigUpdateStatus.BadRequest, "значок батареи рисует Улей только на экране без вкладок (Waveshare 1,54\")");
        if (battery is { Scale: { } scale } && scale is < 1 or > BatteryLayout.MaxScale)
            return (ConfigUpdateStatus.BadRequest, $"значок батареи: масштаб от 1 до {BatteryLayout.MaxScale}");
        if (battery is { X: { } bx } && bx is < -100 or > 400 || battery is { Y: { } by0 } && by0 is < -100 or > 400)
            return (ConfigUpdateStatus.BadRequest, "значок батареи: X и Y от -100 до 400");
        var newRotation = rotation ?? RotationOf(device);
        if (DisplayText.Validate(items, DisplayScreens.For(device.Hw, newRotation)) is { } error)
            return (ConfigUpdateStatus.BadRequest, error);
        // A placed screen keeps every item placed; the automatic one drops the placement fields.
        var placed = DisplayText.Placed(items);
        var wanted = items.Where(i => i.State == true && placed).SelectMany(i => new[] { i.OnImage, i.OffImage }).OfType<string>().Distinct().ToList();
        if (wanted.Count > 0 && await db.DisplayImages.CountAsync(i => i.DeviceId == deviceId && wanted.Contains(i.Id), ct) != wanted.Count)
            return (ConfigUpdateStatus.BadRequest, "картинки: одной из них нет в Улье, загрузите её заново");
        // what the screen no longer uses goes, except what was uploaded a moment ago and is about to be chosen
        var stale = time.GetUtcNow().AddHours(-1);
        db.DisplayImages.RemoveRange(db.DisplayImages.Where(i => i.DeviceId == deviceId && i.CreatedAt < stale && !wanted.Contains(i.Id)));
        var config = device.Config is { } current
            ? JsonNode.Parse(current.RootElement.GetRawText())!.AsObject()
            : new JsonObject { ["v"] = 1, ["name"] = device.Name, ["health_interval_s"] = 60 };
        var display = config["display"] as JsonObject ?? new JsonObject();
        if (items.Count == 0)
            display.Remove("items");
        else
            display["items"] = new JsonArray(items.Select(i =>
            {
                var o = new JsonObject();
                if (i.Clock is { } clock)
                {
                    o["clock"] = clock;
                    if (i.Format is { } format)
                        o["format"] = format;
                }
                else if (i.Text is { Length: > 0 } caption)
                    o["text"] = caption.Trim();
                else
                {
                    o["metric"] = i.Metric;
                    if (!string.IsNullOrEmpty(i.Device) && i.Device != deviceId)
                        o["device"] = i.Device;
                    if (i.State == true)
                    {
                        o["state"] = true;
                        if (!string.IsNullOrWhiteSpace(i.OnText))
                            o["on_text"] = i.OnText.Trim();
                        if (!string.IsNullOrWhiteSpace(i.OffText))
                            o["off_text"] = i.OffText.Trim();
                        if (placed && i.OnImage is not null && i.OffImage is not null)
                        {
                            o["on_image"] = i.OnImage;
                            o["off_image"] = i.OffImage;
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(i.Label))
                    o["label"] = i.Label.Trim();
                if (placed)
                {
                    o["x"] = i.X ?? 0;
                    o["y"] = i.Y ?? 0;
                    o["size"] = i.Size ?? 3;
                    if (i.Align is { } align && align != "left")
                        o["align"] = align;
                    if (!string.IsNullOrEmpty(i.Font) && i.Font != FontSet.Standard)
                        o["font"] = i.Font;
                    if (i.Px is { } px)
                        o["px"] = px;
                    if (i.Bold == true)
                        o["bold"] = true;
                    if (i.Italic == true)
                        o["italic"] = true;
                }
                return (JsonNode)o;
            }).ToArray());
        if (newRotation == DisplayScreens.DefaultRotation(device.Hw))
            display.Remove("rotation");
        else
            display["rotation"] = newRotation;
        if (invert == true)
            display["invert"] = true;
        else if (invert == false)
            display.Remove("invert");
        if (battery is not null)
        {
            // only what differs from the defaults is kept: an old layout (and a reset) has no "battery" at all
            var icon = new JsonObject();
            if (battery.Enabled == false)
                icon["enabled"] = false;
            if (battery.X is { } ix)
                icon["x"] = ix;
            if (battery.Y is { } iy)
                icon["y"] = iy;
            if (battery.Scale is { } isc and not 1)
                icon["scale"] = isc;
            if (icon.Count == 0)
                display.Remove("battery");
            else
                display["battery"] = icon;
        }
        // A turn that changes the screen's shape (a panel on its side) makes the background image the wrong size: it goes.
        if (device.ScreenBackground is not null && DisplayScreens.For(device.Hw, newRotation) is { } shape && DisplayScreens.For(device.Hw, RotationOf(device)) is { } before
            && (shape.Width != before.Width || shape.Height != before.Height))
        {
            device.ScreenBackground = null;
            device.ScreenBackgroundSha = null;
        }
        // The sleep picture may be smaller than the screen but not bigger: a turn can make it too big.
        if (device.ScreenSleep is { } sleep && DisplayScreens.For(device.Hw, newRotation) is { } turned && SKBitmap.DecodeBounds(sleep) is var size
            && (size.Width > turned.Width || size.Height > turned.Height))
        {
            device.ScreenSleep = null;
            device.ScreenSleepSha = null;
        }
        if (display.Count == 0)
            config.Remove("display");
        else
            config["display"] = display;
        device.ConfigRev++;
        config["rev"] = device.ConfigRev;
        device.Config = JsonDocument.Parse(config.ToJsonString());
        Audit.Record(db, time.GetUtcNow(), by, "device_display", deviceId, new { items, rotation = newRotation, invert = invert ?? InvertOf(device) });
        await db.SaveChangesAsync(ct);
        // A monitor far away has no MQTT: it reads the frame over HTTP when it wakes.
        if (mqtt.IsConnected && device.Driver == Drivers.Hornet)
        {
            await mqtt.PublishAsync(Topics.Config(deviceId), config.ToJsonString(), retain: true, ct);
            await PublishAsync(device, ct);
        }
        return (ConfigUpdateStatus.Updated, null);
    }
}
