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

/// <summary>How a reading reads on a tiny screen, the same way the firmware writes its own (meteo role).</summary>
public static class DisplayText
{
    /// <summary>Items the screen lays out itself; placed items (with a size) may be more.</summary>
    public const int MaxItems = 4;
    public const int MaxPlaced = 8;
    public const int MaxLabel = 16;

    /// <summary>
    /// Where a reading stands against its normal range (ValueRangeStore): "low" below, "high" above, null inside or with no
    /// limit on that side. The same test as the site's rangeState (frontend lib/valueRange.ts).
    /// </summary>
    public static string? RangeOf(double value, double? min, double? max) =>
        min is { } lo && value < lo ? "low" : max is { } hi && value > hi ? "high" : null;
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

    public const int MaxStateText = 12;
    private static readonly Regex ImageId = new("^[0-9a-f]{16}$");

    /// <summary>The state fields of an item: only on a state, texts short, the two pictures together and only where they can be drawn.</summary>
    private static string? StateError(DisplayItemDto item, DisplayScreenDto? screen)
    {
        var extras = item.OnText is not null || item.OffText is not null || item.OnImage is not null || item.OffImage is not null;
        if (item.State != true)
            return extras ? "вкл/выкл и картинки: только у состояния реле или переключателя" : null;
        if (item.Clock is not null || item.Text is { Length: > 0 })
            return "состояние: без часов и без текста";
        if (item.OnText is { Length: > MaxStateText } || item.OffText is { Length: > MaxStateText })
            return $"вкл/выкл: не длиннее {MaxStateText} знаков";
        if ((item.OnImage is null) != (item.OffImage is null))
            return "картинки: нужны обе, для «вкл» и для «выкл»";
        if (item.OnImage is not null)
        {
            if (!ImageId.IsMatch(item.OnImage) || !ImageId.IsMatch(item.OffImage!))
                return "картинки: неверный id";
            if (screen is not { Rendered: true } || item.Size is null)
                return "картинки: только у экранов, которые рисует Улей, и при ручной расстановке";
        }
        return null;
    }

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
            if (StateError(item, screen) is { } stateError)
                return stateError;
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
