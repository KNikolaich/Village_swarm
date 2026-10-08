using System.Text;
using Hive.Contracts.Mqtt;
using MQTTnet;
using MQTTnet.Protocol;

namespace Hive.Simulator;

/// <summary>Network side of a virtual hornet. Abstracted so behaviour can be unit-tested without a broker.</summary>
public interface IHornetLink : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Connects with LWT <c>status = offline</c> (retained) and subscribes to the hornet topics.</summary>
    Task ConnectAsync(string deviceId, CancellationToken ct);

    /// <summary>Drops the connection the way a lost Wi-Fi or a crash would: the broker publishes the LWT.</summary>
    Task DropAsync(CancellationToken ct);

    Task PublishAsync(string topic, string payload, bool retain, bool qos1, CancellationToken ct);

    /// <summary>Raised for every incoming message: (topic, payload).</summary>
    event Func<string, string, Task>? MessageReceived;
}

public sealed class MqttHornetLink(BrokerOptions broker, string? username = null, string? password = null) : IHornetLink
{
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();

    public event Func<string, string, Task>? MessageReceived;

    public bool IsConnected => _client.IsConnected;

    public async Task ConnectAsync(string deviceId, CancellationToken ct)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(broker.Host, broker.Port)
            .WithClientId($"sim-{deviceId}")
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithWillTopic(Topics.Status(deviceId))
            .WithWillPayload("offline")
            .WithWillRetain()
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if ((username ?? broker.Username) is { } user)
            builder.WithCredentials(user, password ?? broker.Password);
        var options = builder.Build();

        _client.ApplicationMessageReceivedAsync -= OnMessage;
        _client.ApplicationMessageReceivedAsync += OnMessage;
        await _client.ConnectAsync(options, ct);

        var subscribe = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter($"{Topics.Device(deviceId)}/cmd/+", MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter(Topics.Config(deviceId), MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter($"{Topics.Prefix}/hive/#", MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _client.SubscribeAsync(subscribe, ct);
    }

    public Task DropAsync(CancellationToken ct) =>
        _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder()
            .WithReason(MqttClientDisconnectOptionsReason.DisconnectWithWillMessage)
            .Build(), ct);

    public Task PublishAsync(string topic, string payload, bool retain, bool qos1, CancellationToken ct) =>
        _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(qos1 ? MqttQualityOfServiceLevel.AtLeastOnce : MqttQualityOfServiceLevel.AtMostOnce)
            .Build(), ct);

    private Task OnMessage(MqttApplicationMessageReceivedEventArgs e) =>
        MessageReceived?.Invoke(e.ApplicationMessage.Topic, Encoding.UTF8.GetString(e.ApplicationMessage.Payload))
        ?? Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
