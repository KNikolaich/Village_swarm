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
/// One item of a screen: a reading of this hornet (<see cref="Device"/> absent) or of another device, a caption the person
/// typed (<see cref="Text"/>, then <see cref="Metric"/> is empty), or the hive's clock (<see cref="Clock"/> time or date, written
/// as <see cref="Format"/> says, then <see cref="Metric"/> is empty too). With <see cref="Size"/> the item is placed: <see cref="X"/>,
/// <see cref="Y"/> in pixels, <see cref="Align"/> left (absent), center or right. On screens the hive draws, <see cref="Font"/>
/// (a family id), <see cref="Px"/> (6-120), <see cref="Bold"/> and <see cref="Italic"/> say how it is written.
/// </summary>
public sealed record DisplayItemDto(string? Device, string Metric, string? Label, int? X = null, int? Y = null, int? Size = null, string? Align = null,
    string? Text = null, string? Font = null, int? Px = null, bool? Bold = null, bool? Italic = null, string? Clock = null, string? Format = null);

/// <summary>
/// Size of a screen in pixels as the person sees it (turned by the rotation): where items can be placed and what size a background
/// image must be. <see cref="Rendered"/>: the hive draws the whole frame (e-ink panels), so a preview from the hive is exactly
/// what the screen shows. <see cref="Background"/>: an image lies behind the items. <see cref="Tabs"/>: screens the panel flips
/// through with its buttons (contracts/monitor-http.md, tabs); the items are the last one, above the tab bar.
/// </summary>
public sealed record DisplayScreenDto(int Width, int Height, bool Rendered = false, bool Background = false, int Tabs = 0);

/// <summary>
/// What the screen shows; <see cref="Screen"/> is filled by the hive for a board it knows (ignored when saving).
/// <see cref="Rotation"/> turns the picture of a screen the hive draws (0, 90, 180, 270); <see cref="Invert"/> swaps its black and
/// white (a dark screen); either absent when saving: unchanged.
/// </summary>
public sealed record DisplayItemsDto(
    IReadOnlyList<DisplayItemDto> Items, DisplayScreenDto? Screen = null, int? Rotation = null, DisplayMonitorDto? Monitor = null, bool? Invert = null);

/// <summary>
/// Monitoring of a panel far away: until <see cref="Until"/> it reads its screen every <see cref="EveryMin"/> minutes
/// (X-Frame-Refresh, contracts/monitor-http.md), then goes back to its own interval by itself.
/// </summary>
public sealed record DisplayMonitorDto(DateTimeOffset Until, int EveryMin);

/// <summary>How long and how often a panel is monitored (hours 0.5-12, every 1-10 minutes).</summary>
public sealed record DisplayMonitorRequest(double Hours, int EveryMin);

/// <summary>Screens of the boards the hive knows, by board (info hw).</summary>
public static class DisplayScreens
{
    private static readonly Dictionary<string, DisplayScreenDto> Boards = new()
    {
        ["esp32c3-oled-042"] = new(72, 40),
        // Waveshare ESP32-S3-ePaper-1.54 (V2): 200x200 black and white, firmware monitor-epd154
        ["waveshare-s3-epaper-154"] = new(200, 200, Rendered: true),
        // Elecrow CrowPanel ESP32 3.7" (UC8253, 240x416, rocker with a press): three tabs, firmware monitor-crowpanel37
        ["crowpanel-epd37"] = new(240, 416, Rendered: true, Tabs: 3),
    };

    /// <summary>How a board stands when the person did not choose: the CrowPanel lies landscape.</summary>
    private static readonly Dictionary<string, int> DefaultRotations = new() { ["crowpanel-epd37"] = 270 };

    public static int DefaultRotation(string? board) => board is not null && DefaultRotations.TryGetValue(board, out var r) ? r : 0;

    /// <summary>The panel's own screen.</summary>
    public static DisplayScreenDto? For(string? board) => board is not null && Boards.TryGetValue(board, out var s) ? s : null;

    /// <summary>The screen as the person sees it: a panel turned a quarter is as wide as it is tall and as tall as it is wide.</summary>
    public static DisplayScreenDto? For(string? board, int rotation) =>
        For(board) is { } s && rotation is 90 or 270 ? s with { Width = s.Height, Height = s.Width } : For(board);

    public static readonly int[] Rotations = [0, 90, 180, 270];

    /// <summary>Boards whose frame the hive draws: what a monitor far away can be.</summary>
    public static IReadOnlyList<string> RenderedBoards => Boards.Where(b => b.Value.Rendered).Select(b => b.Key).ToList();
}

/// <summary>How a reading reads on a tiny screen, the same way the firmware writes its own (meteo role).</summary>
public static class DisplayText
{
    /// <summary>Items the screen lays out itself; placed items (with a size) may be more.</summary>
    public const int MaxItems = 4;
    public const int MaxPlaced = 8;
    public const int MaxLabel = 16;
    public const int MaxText = 24;
    public const int MinPx = 6;
    public const int MaxPx = 120;
    public const int MaxCoordinate = 1023;
    public static readonly string[] Aligns = ["left", "center", "right"];

    /// <summary>A reading older than this is shown as "--": better no number than a wrong one.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(30);

    private static readonly Regex Segment = new("^[a-z0-9_]+$");
    private static readonly Regex DeviceId = new("^[a-z][a-z0-9]*(-[a-z0-9]+)*$");
    private static readonly Regex FontId = new("^[a-z0-9]+(-[a-z0-9]+)*$");

    /// <summary>How the clock items may be written (config display.items[].format), the first one when absent.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ClockFormats = new Dictionary<string, string[]>
    {
        ["time"] = ["HH:mm", "H:mm"],
        ["date"] = ["d MMMM", "d MMM", "dd.MM", "dd.MM.yyyy", "dd/MM", "dddd", "ddd, d MMM", "d MMMM yyyy", "MMMM"],
    };

    // Written out: the hive's image may run without culture data.
    private static readonly string[] Weekdays = ["понедельник", "вторник", "среда", "четверг", "пятница", "суббота", "воскресенье"];
    private static readonly string[] WeekdaysShort = ["пн", "вт", "ср", "чт", "пт", "сб", "вс"];
    private static readonly string[] Months = ["январь", "февраль", "март", "апрель", "май", "июнь", "июль", "август", "сентябрь", "октябрь", "ноябрь", "декабрь"];
    private static readonly string[] MonthsOf = ["января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];
    private static readonly string[] MonthsShort = ["янв", "фев", "мар", "апр", "мая", "июн", "июл", "авг", "сен", "окт", "ноя", "дек"];
    private static readonly Regex ClockToken = new("dddd|ddd|dd|d|MMMM|MMM|MM|yyyy|HH|H|mm");

    /// <summary>
    /// The hive's local time <paramref name="local"/> written as <paramref name="format"/> (one of <see cref="ClockFormats"/>):
    /// a month after a day is "of" it (28 октября), alone it is itself (октябрь).
    /// </summary>
    public static string ClockText(string format, DateTimeOffset local)
    {
        var withDay = format.Contains('d');
        var weekday = ((int)local.DayOfWeek + 6) % 7;
        return ClockToken.Replace(format, m => m.Value switch
        {
            "dddd" => Weekdays[weekday],
            "ddd" => WeekdaysShort[weekday],
            "dd" => local.Day.ToString("00", CultureInfo.InvariantCulture),
            "d" => local.Day.ToString(CultureInfo.InvariantCulture),
            "MMMM" => withDay ? MonthsOf[local.Month - 1] : Months[local.Month - 1],
            "MMM" => MonthsShort[local.Month - 1],
            "MM" => local.Month.ToString("00", CultureInfo.InvariantCulture),
            "yyyy" => local.Year.ToString(CultureInfo.InvariantCulture),
            "HH" => local.Hour.ToString("00", CultureInfo.InvariantCulture),
            "H" => local.Hour.ToString(CultureInfo.InvariantCulture),
            _ => local.Minute.ToString("00", CultureInfo.InvariantCulture),
        });
    }

    public static string Format(string metric, double v)
    {
        var inv = CultureInfo.InvariantCulture;
        if (metric.StartsWith("temperature", StringComparison.Ordinal))
            return v.ToString("0.0", inv) + "°";
        return metric switch
        {
            "humidity" => v.ToString("0", inv) + "%",
            "battery_voltage" => v.ToString("0.0", inv) + "V",
            "pressure" => v.ToString("0", inv),
            _ => v.ToString("0.0", inv),
        };
    }

    /// <summary>
    /// Text height in pixels for a relative size 1-10: size/10 of the screen height, at least 6 px (the smallest font).
    /// The firmware takes its largest font not taller than this (contracts/mqtt-topics.md).
    /// </summary>
    public static int PixelHeight(int size, int screenHeight) =>
        Math.Max(6, (int)Math.Round(screenHeight * Math.Clamp(size, 1, 10) / 10.0, MidpointRounding.AwayFromZero));

    /// <summary>Whether the screen is placed by x, y, size: any item has a size.</summary>
    public static bool Placed(IReadOnlyList<DisplayItemDto> items) => items.Any(i => i.Size is not null);

    public static string? Validate(IReadOnlyList<DisplayItemDto> items, DisplayScreenDto? screen = null)
    {
        var placed = Placed(items);
        if (!placed && items.Count > MaxItems)
            return $"не больше {MaxItems} строк (или расставьте их вручную: тогда до {MaxPlaced})";
        if (items.Count > MaxPlaced)
            return $"не больше {MaxPlaced} строк";
        foreach (var item in items)
        {
            if (item.Size is { } size && size is < 1 or > 10)
                return "size: от 1 до 10";
            if (item.X is < 0 or > MaxCoordinate || item.Y is < 0 or > MaxCoordinate)
                return $"x, y: от 0 до {MaxCoordinate}";
            if (screen is not null && (item.X >= screen.Width || item.Y >= screen.Height))
                return $"x, y: экран {screen.Width}×{screen.Height}, x до {screen.Width - 1}, y до {screen.Height - 1}";
            if (item.Align is { } align && !Aligns.Contains(align))
                return "align: left, center или right";
            if (item.Clock is { } clock)
            {
                // the hive's clock: nothing to read from a device
                if (!ClockFormats.TryGetValue(clock, out var formats))
                    return "clock: time или date";
                if (item.Format is { } format && !formats.Contains(format))
                    return $"format: {string.Join(", ", formats)}";
                if (!string.IsNullOrEmpty(item.Metric) || !string.IsNullOrEmpty(item.Device) || !string.IsNullOrEmpty(item.Text))
                    return "часы: без датчика и без текста";
            }
            else if (item.Format is not null)
                return "format: только у часов (clock)";
            else if (item.Text is { Length: > 0 } text)
            {
                // a caption typed by the person: nothing to read from anywhere
                if (text.Length > MaxText)
                    return $"текст: не длиннее {MaxText} знаков";
                if (!string.IsNullOrEmpty(item.Metric) || !string.IsNullOrEmpty(item.Device))
                    return "текст: подпись без источника данных";
            }
            else
            {
                if (!Segment.IsMatch(item.Metric ?? ""))
                    return "metric: латиница, цифры и «_»";
                if (item.Device is { Length: > 0 } d && (d.Length > 32 || !DeviceId.IsMatch(d)))
                    return "device: неверный id устройства";
            }
            if (item.Label is { Length: > MaxLabel })
                return $"label: не длиннее {MaxLabel} знаков";
            if (item.Px is < MinPx or > MaxPx)
                return $"px: от {MinPx} до {MaxPx}";
            if (item.Font is { Length: > 0 } font && (font.Length > 40 || !FontId.IsMatch(font)))
                return "font: неверный id шрифта";
            if ((item.Font is not null || item.Px is not null || item.Bold is not null || item.Italic is not null)
                && (screen is not { Rendered: true } || item.Size is null))
                return "шрифт и размер в пикселях: только у экранов, которые рисует Улей, и при ручной расстановке";
        }
        return null;
    }
}

/// <summary>
/// A hornet with a screen may show readings of other devices (config display.items). The hive keeps the choice in the hornet's
/// config and sends the ready texts to vs/v1/hive/display/{id}: the hornet only draws them. Without items it shows its own readings.
/// </summary>
public sealed class DisplayFeedService(
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
        return new DisplayItemsDto(ItemsOf(device), DisplayScreens.For(device.Hw, rotation) is { } screen ? screen with { Background = device.ScreenBackground is not null } : null, rotation,
            MonitorOf(device, time.GetUtcNow()), InvertOf(device));
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
                i.TryGetProperty("format", out var format) ? format.GetString() : null))
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
        string deviceId, IReadOnlyList<DisplayItemDto> items, int? rotation, string by, CancellationToken ct, bool? invert = null)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId && (d.Driver == Drivers.Hornet || d.Driver == Drivers.Http), ct);
        if (device is null)
            return (ConfigUpdateStatus.NotFound, null);
        if (rotation is { } turn && (!DisplayScreens.Rotations.Contains(turn) || (turn != 0 && DisplayScreens.For(device.Hw) is not { Rendered: true })))
            return (ConfigUpdateStatus.BadRequest, "поворот: 0, 90, 180 или 270, и только у экранов, которые рисует Улей");
        if (invert == true && DisplayScreens.For(device.Hw) is not { Rendered: true })
            return (ConfigUpdateStatus.BadRequest, "инверсия бывает только у экранов, которые рисует Улей");
        var newRotation = rotation ?? RotationOf(device);
        if (DisplayText.Validate(items, DisplayScreens.For(device.Hw, newRotation)) is { } error)
            return (ConfigUpdateStatus.BadRequest, error);
        // A placed screen keeps every item placed; the automatic one drops the placement fields.
        var placed = DisplayText.Placed(items);
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
        // A turn that changes the screen's shape (a panel on its side) makes the background image the wrong size: it goes.
        if (device.ScreenBackground is not null && DisplayScreens.For(device.Hw, newRotation) is { } shape && DisplayScreens.For(device.Hw, RotationOf(device)) is { } before
            && (shape.Width != before.Width || shape.Height != before.Height))
        {
            device.ScreenBackground = null;
            device.ScreenBackgroundSha = null;
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

    /// <summary>The message the screen of <paramref name="device"/> should get now.</summary>
    public async Task<DisplayMessage> BuildAsync(Device device, CancellationToken ct)
    {
        var items = ItemsOf(device);
        var now = time.GetUtcNow();
        var values = items.Count == 0 ? [] : await telemetry.LatestAsync(null, ct);
        var names = await db.Devices.AsNoTracking().Select(d => new { d.DeviceId, d.Name }).ToDictionaryAsync(d => d.DeviceId, d => d.Name, ct);
        var placed = DisplayText.Placed(items);
        var lines = new List<DisplayItem>();
        foreach (var item in items)
        {
            var source = string.IsNullOrEmpty(item.Device) ? device.DeviceId : item.Device;
            var clock = item.Clock is { } kind && DisplayText.ClockFormats.TryGetValue(kind, out var formats) ? item.Format ?? formats[0] : null;
            var caption = item.Text is { Length: > 0 } || clock is not null;
            var value = caption ? null : values.FirstOrDefault(v => v.DeviceId == source && v.Metric == item.Metric);
            var text = clock is not null ? DisplayText.ClockText(clock, hive.Value.ToLocal(now))
                : caption ? item.Text!.Trim() : value is null || now - value.Ts > DisplayText.Stale ? "--" : DisplayText.Format(item.Metric, value.Value);
            // Another device is named by its own name unless the person chose a label; the hornet's own readings need none.
            // A placed screen has its own place for names (a background, say): only a typed label goes there.
            var label = caption ? null : !string.IsNullOrWhiteSpace(item.Label) ? item.Label.Trim()
                : !placed && source != device.DeviceId && names.TryGetValue(source, out var name) ? name : null;
            lines.Add(new DisplayItem
            {
                Label = label is { Length: > DisplayText.MaxLabel } l ? l[..DisplayText.MaxLabel] : label,
                Text = text,
                X = placed ? item.X ?? 0 : null,
                Y = placed ? item.Y ?? 0 : null,
                Size = placed ? item.Size ?? 3 : null,
                Align = placed && item.Align is { } align && align != "left" ? align : null,
                Font = placed && !string.IsNullOrEmpty(item.Font) && item.Font != FontSet.Standard ? item.Font : null,
                Px = placed ? item.Px : null,
                Bold = placed && item.Bold == true ? true : null,
                Italic = placed && item.Italic == true ? true : null,
            });
        }
        return new DisplayMessage { Ts = now.ToUnixTimeMilliseconds(), Items = lines };
    }

    /// <summary>
    /// The frame the hive draws for this device's screen as the person sees it (not yet turned for the panel): the background
    /// image, then the items in their fonts. Null without a screen the hive knows.
    /// </summary>
    public async Task<SKBitmap?> DrawAsync(Device device, DisplayMessage message, CancellationToken ct)
    {
        if (DisplayScreens.For(device.Hw, RotationOf(device)) is not { } screen)
            return null;
        var set = await fonts.LoadAsync(message.Items.Select(i => i.Font), ct);
        using var background = device.ScreenBackground is { } png ? SKBitmap.Decode(png) : null;
        var bitmap = DisplayRender.Render(message, screen, set, background);
        return InvertOf(device) ? DisplayRender.Invert(bitmap) : bitmap;
    }

    /// <summary>What besides the items makes the picture what it is (background, turn, fonts): a panel redraws when it changes.</summary>
    public async Task<string> PictureTagAsync(Device device, CancellationToken ct) =>
        $"{device.ScreenBackgroundSha}/{RotationOf(device)}{(InvertOf(device) ? "/dark" : "")}/{await fonts.VersionAsync(ct)}";

    /// <summary>The frame as PNG (the preview in the UI), one tab of a panel with tabs; null without a screen the hive draws.</summary>
    public async Task<byte[]?> PreviewAsync(string deviceId, int? tab, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null || !HasScreen(device))
            return null;
        var message = await BuildAsync(device, ct);
        var tabs = DisplayScreens.For(device.Hw)?.Tabs ?? 0;
        using var bitmap = tabs > 0
            ? await DrawTabAsync(device, message, Math.Clamp(tab ?? ScreenTabs.Measure, 0, tabs - 1), ct)
            : await DrawAsync(device, message, ct);
        return bitmap is null ? null : DisplayRender.Png(bitmap);
    }

    /// <summary>Recent events worth a line on a calendar: not the chatter (configs applied, snapshots, timelapse frames).</summary>
    private static readonly string[] Chatter = ["config_applied", "snapshot", "photo_failed", "device_online"];

    /// <summary>Short names of what happened, for a line on a small screen.</summary>
    public static string EventName(string type) => type switch
    {
        "motion" => "Движение",
        "door_open" => "Дверь",
        "leak" => "Протечка",
        "failsafe" => "Защита обогрева",
        "tamper" => "Камера закрыта",
        "rule" => "Правило",
        "ota" => "Прошивка",
        "ota_rollout" => "Прошивки",
        "backup" => "Бэкап",
        "backup_cleared" => "Бэкап в порядке",
        "disk_cleanup" => "Диск очищен",
        "device_offline" => "Не на связи",
        _ => type,
    };

    /// <summary>One tab of a panel with tabs, as the person sees it (not yet turned for the panel).</summary>
    public async Task<SKBitmap?> DrawTabAsync(Device device, DisplayMessage message, int tab, CancellationToken ct)
    {
        if (DisplayScreens.For(device.Hw, RotationOf(device)) is not { } screen)
            return null;
        var now = time.GetUtcNow();
        var options = hive.Value;
        WeatherForecast? weather = null;
        if (tab == ScreenTabs.Forecast && await db.WeatherSnapshots.AsNoTracking().FirstOrDefaultAsync(ct) is { } snapshot)
            weather = snapshot.Payload.Deserialize<WeatherForecast>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        IReadOnlyList<TabEvent> events = [];
        if (tab == ScreenTabs.Calendar)
        {
            var since = now.AddDays(-30);
            var rows = await db.Events.AsNoTracking()
                .Where(e => e.Ts >= since && !Chatter.Contains(e.Type) && !db.Media.Any(m => m.EventId == e.Id && m.Kind == MediaKind.Timelapse))
                .OrderByDescending(e => e.Ts).Take(60)
                .Select(e => new { e.Ts, e.Type, Name = db.Devices.Where(d => d.DeviceId == e.DeviceId).Select(d => d.Name).FirstOrDefault() })
                .ToListAsync(ct);
            events = ScreenTabs.Distinct(rows.Select(e => new TabEvent(e.Ts, EventName(e.Type), e.Name)), options.ToLocal);
        }
        var set = await fonts.LoadAsync(message.Items.Select(i => i.Font).Concat(ScreenTabs.Families), ct);
        using var background = tab == ScreenTabs.Measure && device.ScreenBackground is { } png ? SKBitmap.Decode(png) : null;
        var context = new TabContext(now, options.ToLocal, await BatteryAsync(device.DeviceId, ct), weather, events);
        var bitmap = ScreenTabs.Draw(tab, screen, context, set, message, background);
        return InvertOf(device) ? DisplayRender.Invert(bitmap) : bitmap;
    }

    /// <summary>The panel's battery in percent from its last word on it (X-Battery-mV), when that is under two hours old.</summary>
    private async Task<int?> BatteryAsync(string deviceId, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddHours(-2);
        var volts = await db.Telemetry.AsNoTracking()
            .Where(t => t.DeviceId == deviceId && t.Metric == "battery_voltage" && t.Ts >= since)
            .OrderByDescending(t => t.Ts).Select(t => (double?)t.Value).FirstOrDefaultAsync(ct);
        return volts is { } v ? ScreenTabs.BatteryPercent(v) : null;
    }

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

    public async Task<string> PublishAsync(Device device, CancellationToken ct)
    {
        var message = await BuildAsync(device, ct);
        var json = JsonSerializer.Serialize(message, ContractJson.Options);
        await mqtt.PublishAsync(Topics.Display(device.DeviceId), json, retain: true, ct);
        return json;
    }
}

/// <summary>Keeps the screens of hornets that show other devices' readings up to date: looks every 15 s, sends what changed (and everything every 5 minutes).</summary>
public sealed class DisplayFeed(IServiceScopeFactory scopes, NodeState node, MqttGateway mqtt, TimeProvider time, ILogger<DisplayFeed> logger) : BackgroundService
{
    private readonly Dictionary<string, (string Content, DateTimeOffset At)> _sent = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The build runs the app to write the OpenAPI document; it must not need a database.
        if (Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider")
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Screen feed round failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        // The city only watches: the screens are fed by the node that commands.
        if (!mqtt.IsConnected || node.ReadOnly)
            return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var feed = scope.ServiceProvider.GetRequiredService<DisplayFeedService>();
        var now = time.GetUtcNow();
        var screens = (await db.Devices.AsNoTracking().Where(d => d.Driver == Drivers.Hornet && d.Config != null).ToListAsync(ct))
            .Where(d => DisplayFeedService.ItemsOf(d).Count > 0).ToList();
        foreach (var device in screens)
        {
            var message = await feed.BuildAsync(device, ct);
            // The time is not part of the comparison: only the texts are.
            var content = JsonSerializer.Serialize(message.Items, ContractJson.Options);
            if (_sent.TryGetValue(device.DeviceId, out var last) && last.Content == content && now - last.At < TimeSpan.FromMinutes(5))
                continue;
            await mqtt.PublishAsync(Topics.Display(device.DeviceId), JsonSerializer.Serialize(message, ContractJson.Options), retain: true, ct);
            _sent[device.DeviceId] = (content, now);
        }
    }
}
