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
