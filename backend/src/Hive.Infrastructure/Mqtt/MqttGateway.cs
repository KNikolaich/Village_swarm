using System.Buffers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace Hive.Infrastructure.Mqtt;

public sealed class MqttOptions
{
    public const string Section = "Mqtt";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string ClientId { get; set; } = "hive-api";
}

/// <summary>Device adapter (spec 4.6): subscribes to its topics and turns messages into internal ingest items.</summary>
public interface IMqttMessageHandler
{
    IReadOnlyList<string> TopicFilters { get; }

    /// <summary>Called on the MQTT receive path; must be fast and must not throw for bad payloads.</summary>
    ValueTask HandleAsync(string topic, ReadOnlyMemory<byte> payload, bool retained, DateTimeOffset receivedAt);
}

/// <summary>The single broker connection of the api (spec 6.3): subscriptions, publishing, reconnects.</summary>
public sealed class MqttGateway(
    IOptions<MqttOptions> options,
    IEnumerable<IMqttMessageHandler> handlers,
    TimeProvider time,
    ILogger<MqttGateway> logger) : BackgroundService
{
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();

    public bool IsConnected => _client.IsConnected;

    public Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct) =>
        _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(o.Host, o.Port)
            .WithClientId(o.ClientId)
            .WithCleanSession(false) // keep QoS 1 messages queued while the api restarts
            .WithSessionExpiryInterval(3600)
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));
        if (!string.IsNullOrEmpty(o.Username))
            builder.WithCredentials(o.Username, o.Password);
        var clientOptions = builder.Build();

        var handlerList = handlers.ToList();
        _client.ApplicationMessageReceivedAsync += async e =>
        {
            var message = e.ApplicationMessage;
            var payload = message.Payload.IsSingleSegment ? message.Payload.First : message.Payload.ToArray();
            var receivedAt = time.GetUtcNow();
            foreach (var handler in handlerList)
            {
                try
                {
                    await handler.HandleAsync(message.Topic, payload, message.Retain, receivedAt);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Handler {Handler} failed on {Topic}", handler.GetType().Name, message.Topic);
                }
            }
        };

        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_client.IsConnected)
            {
                try
                {
                    var result = await _client.ConnectAsync(clientOptions, stoppingToken);
                    if (!result.IsSessionPresent)
                        await SubscribeAsync(handlerList, stoppingToken);
                    logger.LogInformation("MQTT connected to {Host}:{Port} (session present: {Session})", o.Host, o.Port, result.IsSessionPresent);
                    backoff = TimeSpan.FromSeconds(1);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("MQTT connect to {Host}:{Port} failed: {Error}. Retry in {Delay}s", o.Host, o.Port, ex.Message, backoff.TotalSeconds);
                    await Task.Delay(backoff, time, stoppingToken);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
                    continue;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
        }
    }

    private async Task SubscribeAsync(IEnumerable<IMqttMessageHandler> handlerList, CancellationToken ct)
    {
        var subscribe = new MqttClientSubscribeOptionsBuilder();
        foreach (var filter in handlerList.SelectMany(h => h.TopicFilters).Distinct())
            subscribe.WithTopicFilter(filter, MqttQualityOfServiceLevel.AtLeastOnce);
        await _client.SubscribeAsync(subscribe.Build(), ct);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_client.IsConnected)
            await _client.DisconnectAsync(cancellationToken: cancellationToken);
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }
}
