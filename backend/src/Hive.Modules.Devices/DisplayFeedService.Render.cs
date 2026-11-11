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

/// <summary>Drawing and publishing the screen: what the hornet or the panel shows.</summary>
public sealed partial class DisplayFeedService
{
    /// <summary>The message the screen of <paramref name="device"/> should get now.</summary>
    public async Task<DisplayMessage> BuildAsync(Device device, CancellationToken ct)
    {
        var items = ItemsOf(device);
        var now = time.GetUtcNow();
        var values = items.Count == 0 || items.All(i => i.State == true) ? [] : await telemetry.LatestAsync(null, ct);
        var names = await db.Devices.AsNoTracking().Select(d => new { d.DeviceId, d.Name }).ToDictionaryAsync(d => d.DeviceId, d => d.Name, ct);
        var placed = DisplayText.Placed(items);
        var states = await StatesAsync(device, items, ct);
        var ranges = items.Any(i => !string.IsNullOrEmpty(i.Metric))
            ? (await new ValueRangeStore(db, time).ListAsync(ct)).ToDictionary(r => (r.DeviceId, r.Metric))
            : [];
        var lines = new List<DisplayItem>();
        foreach (var item in items)
        {
            var source = string.IsNullOrEmpty(item.Device) ? device.DeviceId : item.Device;
            var clock = item.Clock is { } kind && DisplayText.ClockFormats.TryGetValue(kind, out var formats) ? item.Format ?? formats[0] : null;
            var caption = item.Text is { Length: > 0 } || clock is not null;
            var isState = item.State == true && !caption;
            var value = caption || isState ? null : values.FirstOrDefault(v => v.DeviceId == source && v.Metric == item.Metric);
            var text = clock is not null ? DisplayText.ClockText(clock, hive.Value.ToLocal(now))
                : caption ? item.Text!.Trim()
                : isState ? StateText(item, states.GetValueOrDefault(item))
                : value is null || now - value.Ts > DisplayText.Stale ? "--" : DisplayText.Format(item.Metric, value.Value);
            var fresh = value is not null && now - value.Ts <= DisplayText.Stale;
            var range = fresh && item.Metric is { } metric && ranges.TryGetValue((source, metric), out var r) ? DisplayText.RangeOf(value!.Value, r.Min, r.Max) : null;
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
                Range = range,
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
        var corner = await CornerBatteryAsync(device, screen, ct);
        var set = await fonts.LoadAsync(message.Items.Select(i => i.Font).Concat(corner is null ? [] : ScreenTabs.Families), ct);
        using var background = device.ScreenBackground is { } png ? SKBitmap.Decode(png) : null;
        var images = await ImagesAsync(device, ct);
        try
        {
            var bitmap = DisplayRender.Render(message, screen, set, background, images);
            ScreenTabs.DrawBatteryCorner(bitmap, set, corner?.Percent, corner?.Charging ?? false, BatteryOf(device));
            return InvertOf(device) ? DisplayRender.Invert(bitmap) : bitmap;
        }
        finally
        {
            foreach (var picture in images.Values)
                picture.Dispose();
        }
    }

    /// <summary>What besides the items makes the picture what it is (background, turn, fonts): a panel redraws when it changes.</summary>
    public async Task<string> PictureTagAsync(Device device, CancellationToken ct) =>
        $"{device.ScreenBackgroundSha}/{RotationOf(device)}{(InvertOf(device) ? "/dark" : "")}/{await fonts.VersionAsync(ct)}/{ImageIds(device)}"
        + (await CornerBatteryAsync(device, DisplayScreens.For(device.Hw), ct) is { } c ? $"/bat{c.Percent}{(c.Charging ? "c" : "")}{BatteryTag(device)}" : "");

    /// <summary>The icon's place and size in the picture's tag: moving it redraws the picture.</summary>
    private static string BatteryTag(Device device) => BatteryOf(device) is { } b ? $"@{b.X},{b.Y},{b.Scale}" : "";

    /// <summary>
    /// The battery for the corner of a picture the hive draws for a screen without tabs (the small panel), in steps of five; null when
    /// the panel has not told it lately or the screen is not one the hive draws. Tabs draw their own battery.
    /// </summary>
    private async Task<(int Percent, bool Charging)?> CornerBatteryAsync(Device device, DisplayScreenDto? screen, CancellationToken ct)
    {
        if (screen is not { Rendered: true, Tabs: 0 } || BatteryOf(device).Enabled == false)
            return null;
        return await BatteryAsync(device.DeviceId, ct) is { } pct ? (ScreenTabs.CornerPercent(pct), await ChargingAsync(device.DeviceId, ct)) : null;
    }

    /// <summary>
    /// Whether the panel told it is on external power (X-Charging: 1) with its latest battery reading, which is under two hours old.
    /// A "charging" told earlier than the latest battery reading does not count: the panel stopped saying it (unplugged, or an old firmware).
    /// </summary>
    private async Task<bool> ChargingAsync(string deviceId, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddHours(-2);
        var latest = await db.Telemetry.AsNoTracking()
            .Where(t => t.DeviceId == deviceId && (t.Metric == "charging" || t.Metric == "battery_voltage") && t.Ts >= since)
            .OrderByDescending(t => t.Ts).Take(4).Select(t => new { t.Metric, t.Ts, t.Value }).ToListAsync(ct);
        var battery = latest.FirstOrDefault(t => t.Metric == "battery_voltage");
        var charging = latest.FirstOrDefault(t => t.Metric == "charging");
        return battery is not null && charging is not null && charging.Value >= 0.5 && Math.Abs((charging.Ts - battery.Ts).TotalSeconds) < 5;
    }

    /// <summary>The pictures the state items of the screen are drawn with: another choice is another picture.</summary>
    private static string ImageIds(Device device) =>
        string.Join(",", ItemsOf(device).Where(i => i.OnImage is not null).Select(i => $"{i.OnImage}{i.OffImage}"));

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
    public async Task<SKBitmap?> DrawTabAsync(Device device, DisplayMessage message, int tab, CancellationToken ct, bool asleep = false)
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
        var context = new TabContext(now, options.ToLocal, await BatteryAsync(device.DeviceId, ct), weather, events, asleep, await ChargingAsync(device.DeviceId, ct));
        var images = tab == ScreenTabs.Measure ? await ImagesAsync(device, ct) : new Dictionary<int, SKBitmap>();
        try
        {
            var bitmap = ScreenTabs.Draw(tab, screen, context, set, message, background, images);
            return InvertOf(device) ? DisplayRender.Invert(bitmap) : bitmap;
        }
        finally
        {
            foreach (var picture in images.Values)
                picture.Dispose();
        }
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

    /// <summary>"вкл" and "выкл" unless the person wrote their own; "--" while the device has not said.</summary>
    public static string StateText(DisplayItemDto item, bool? on) =>
        on is null ? "--" : on.Value ? item.OnText is { Length: > 0 } onText ? onText : "вкл" : item.OffText is { Length: > 0 } offText ? offText : "выкл";

    /// <summary>
    /// A state as on or off: "on", "true", "1", a true or a non-zero number are on; "off", "false", "0", a false or zero are off.
    /// Null for what is not said or not understood.
    /// </summary>
    public static bool? IsOn(JsonDocument? state, string channel)
    {
        if (state is null || state.RootElement.ValueKind != JsonValueKind.Object || !state.RootElement.TryGetProperty(channel, out var v))
            return null;
        switch (v.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number: return v.TryGetDouble(out var n) ? n != 0 : null;
            case JsonValueKind.String:
                return v.GetString()?.Trim().ToLowerInvariant() switch
                {
                    "on" or "true" or "1" or "open" => true,
                    "off" or "false" or "0" or "closed" => false,
                    _ => null,
                };
            default: return null;
        }
    }

    /// <summary>The state of every state item of the screen, by item: null while its device has not said.</summary>
    private async Task<Dictionary<DisplayItemDto, bool?>> StatesAsync(Device device, IReadOnlyList<DisplayItemDto> items, CancellationToken ct)
    {
        var wanted = items.Where(i => i.State == true).ToList();
        var result = new Dictionary<DisplayItemDto, bool?>();
        if (wanted.Count == 0)
            return result;
        var ids = wanted.Select(i => string.IsNullOrEmpty(i.Device) ? device.DeviceId : i.Device).Distinct().ToList();
        var docs = await db.Devices.AsNoTracking().Where(d => ids.Contains(d.DeviceId)).Select(d => new { d.DeviceId, d.State }).ToDictionaryAsync(d => d.DeviceId, d => d.State, ct);
        foreach (var item in wanted)
        {
            var source = string.IsNullOrEmpty(item.Device) ? device.DeviceId : item.Device;
            result[item] = IsOn(docs.GetValueOrDefault(source), item.Metric);
        }
        return result;
    }

    /// <summary>
    /// The pictures of the state items that have them, by the item's place in the screen: the one for its state now. A state not
    /// said yet has none (the text "--" is drawn). The caller disposes them.
    /// </summary>
    private async Task<Dictionary<int, SKBitmap>> ImagesAsync(Device device, CancellationToken ct)
    {
        var items = ItemsOf(device);
        var images = new Dictionary<int, SKBitmap>();
        if (!DisplayText.Placed(items) || items.All(i => i.State != true || i.OnImage is null))
            return images;
        var states = await StatesAsync(device, items, ct);
        var ranges = items.Any(i => !string.IsNullOrEmpty(i.Metric))
            ? (await new ValueRangeStore(db, time).ListAsync(ct)).ToDictionary(r => (r.DeviceId, r.Metric))
            : [];
        var ids = items.Where(i => i.OnImage is not null).SelectMany(i => new[] { i.OnImage!, i.OffImage! }).Distinct().ToList();
        var pngs = await db.DisplayImages.AsNoTracking().Where(i => i.DeviceId == device.DeviceId && ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Png, ct);
        for (var n = 0; n < items.Count; n++)
        {
            var item = items[n];
            if (item.State != true || item.OnImage is null || states.GetValueOrDefault(item) is not { } on)
                continue;
            if (pngs.TryGetValue(on ? item.OnImage : item.OffImage!, out var png) && SKBitmap.Decode(png) is { } bitmap)
                images[n] = bitmap;
        }
        return images;
    }

    public async Task<string> PublishAsync(Device device, CancellationToken ct)
    {
        var message = await BuildAsync(device, ct);
        var json = JsonSerializer.Serialize(message, ContractJson.Options);
        await mqtt.PublishAsync(Topics.Display(device.DeviceId), json, retain: true, ct);
        return json;
    }
}
