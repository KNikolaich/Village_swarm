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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hive.Modules.Devices;

/// <summary>One line of a hornet's screen: a reading of this hornet (<see cref="Device"/> absent) or of another device.</summary>
public sealed record DisplayItemDto(string? Device, string Metric, string? Label);

public sealed record DisplayItemsDto(IReadOnlyList<DisplayItemDto> Items);

/// <summary>How a reading reads on a tiny screen, the same way the firmware writes its own (meteo role).</summary>
public static class DisplayText
{
    public const int MaxItems = 4;
    public const int MaxLabel = 16;

    /// <summary>A reading older than this is shown as "--": better no number than a wrong one.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(30);

    private static readonly Regex Segment = new("^[a-z0-9_]+$");
    private static readonly Regex DeviceId = new("^[a-z][a-z0-9]*(-[a-z0-9]+)*$");

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

    public static string? Validate(IReadOnlyList<DisplayItemDto> items)
    {
        if (items.Count > MaxItems)
            return $"не больше {MaxItems} строк";
        foreach (var item in items)
        {
            if (!Segment.IsMatch(item.Metric ?? ""))
                return "metric: латиница, цифры и «_»";
            if (item.Device is { Length: > 0 } d && (d.Length > 32 || !DeviceId.IsMatch(d)))
                return "device: неверный id устройства";
            if (item.Label is { Length: > MaxLabel })
                return $"label: не длиннее {MaxLabel} знаков";
        }
        return null;
    }
}

/// <summary>
/// A hornet with a screen may show readings of other devices (config display.items). The hive keeps the choice in the hornet's
/// config and sends the ready texts to vs/v1/hive/display/{id}: the hornet only draws them. Without items it shows its own readings.
/// </summary>
public sealed class DisplayFeedService(HiveDbContext db, TelemetryQueries telemetry, MqttGateway mqtt, TimeProvider time)
{
    public async Task<IReadOnlyList<DisplayItemDto>?> GetAsync(string deviceId, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.Driver == Drivers.Hornet, ct);
        return device is null ? null : ItemsOf(device);
    }

    public static IReadOnlyList<DisplayItemDto> ItemsOf(Device device)
    {
        if (device.Config is null || !device.Config.RootElement.TryGetProperty("display", out var display)
            || !display.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];
        return items.EnumerateArray()
            .Select(i => new DisplayItemDto(
                i.TryGetProperty("device", out var d) ? d.GetString() : null,
                i.TryGetProperty("metric", out var m) ? m.GetString() ?? "" : "",
                i.TryGetProperty("label", out var l) ? l.GetString() : null))
            .ToList();
    }

    /// <summary>Stores the choice in the config (bumping its revision), sends the config, then the screen text.</summary>
    public async Task<ConfigUpdateStatus> SaveAsync(string deviceId, IReadOnlyList<DisplayItemDto> items, string by, CancellationToken ct)
    {
        if (DisplayText.Validate(items) is not null)
            return ConfigUpdateStatus.BadRequest;
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.Driver == Drivers.Hornet, ct);
        if (device is null)
            return ConfigUpdateStatus.NotFound;
        var config = device.Config is { } current
            ? JsonNode.Parse(current.RootElement.GetRawText())!.AsObject()
            : new JsonObject { ["v"] = 1, ["name"] = device.Name, ["health_interval_s"] = 60 };
        var display = config["display"] as JsonObject ?? new JsonObject();
        if (items.Count == 0)
            display.Remove("items");
        else
            display["items"] = new JsonArray(items.Select(i =>
            {
                var o = new JsonObject { ["metric"] = i.Metric };
                if (!string.IsNullOrEmpty(i.Device) && i.Device != deviceId)
                    o["device"] = i.Device;
                if (!string.IsNullOrWhiteSpace(i.Label))
                    o["label"] = i.Label.Trim();
                return (JsonNode)o;
            }).ToArray());
        if (display.Count == 0)
            config.Remove("display");
        else
            config["display"] = display;
        device.ConfigRev++;
        config["rev"] = device.ConfigRev;
        device.Config = JsonDocument.Parse(config.ToJsonString());
        Audit.Record(db, time.GetUtcNow(), by, "device_display", deviceId, new { items });
        await db.SaveChangesAsync(ct);
        if (mqtt.IsConnected)
        {
            await mqtt.PublishAsync(Topics.Config(deviceId), config.ToJsonString(), retain: true, ct);
            await PublishAsync(device, ct);
        }
        return ConfigUpdateStatus.Updated;
    }

    /// <summary>The message the screen of <paramref name="device"/> should get now.</summary>
    public async Task<DisplayMessage> BuildAsync(Device device, CancellationToken ct)
    {
        var items = ItemsOf(device);
        var now = time.GetUtcNow();
        var values = items.Count == 0 ? [] : await telemetry.LatestAsync(null, ct);
        var names = await db.Devices.AsNoTracking().Select(d => new { d.DeviceId, d.Name }).ToDictionaryAsync(d => d.DeviceId, d => d.Name, ct);
        var lines = new List<DisplayItem>();
        foreach (var item in items)
        {
            var source = string.IsNullOrEmpty(item.Device) ? device.DeviceId : item.Device;
            var value = values.FirstOrDefault(v => v.DeviceId == source && v.Metric == item.Metric);
            var text = value is null || now - value.Ts > DisplayText.Stale ? "--" : DisplayText.Format(item.Metric, value.Value);
            // Another device is named by its own name unless the person chose a label; the hornet's own readings need none.
            var label = !string.IsNullOrWhiteSpace(item.Label) ? item.Label.Trim()
                : source != device.DeviceId && names.TryGetValue(source, out var name) ? name : null;
            lines.Add(new DisplayItem { Label = label is { Length: > DisplayText.MaxLabel } l ? l[..DisplayText.MaxLabel] : label, Text = text });
        }
        return new DisplayMessage { Ts = now.ToUnixTimeMilliseconds(), Items = lines };
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
