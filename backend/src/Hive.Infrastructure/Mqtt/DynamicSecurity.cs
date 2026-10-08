using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Contracts.Mqtt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.Mqtt;

/// <summary>
/// Mosquitto dynamic security (spec 4.1): every hornet gets its own login and a role that only allows its
/// own topics. Commands go to $CONTROL/dynamic-security/v1 over the api's broker connection, which must
/// belong to a client with the dynsec admin role.
/// </summary>
public sealed class DynamicSecurity(MqttGateway mqtt, DynamicSecurityResponses responses, IOptions<MqttOptions> options, ILogger<DynamicSecurity> logger)
{
    public const string ControlTopic = "$CONTROL/dynamic-security/v1";
    public const string ResponseTopic = ControlTopic + "/response";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public bool Enabled => options.Value.DynamicSecurity;

    public static string ClientName(string deviceId) => deviceId;
    public static string RoleName(string deviceId) => $"hornet-{deviceId}";

    /// <summary>
    /// Deny by default and give the api's own role full access to the contract topics. Idempotent,
    /// called once after connecting.
    /// </summary>
    public async Task EnsureDefaultsAsync(CancellationToken ct)
    {
        if (!Enabled)
            return;
        var prefix = Topics.Prefix;
        await SendAsync(
        [
            new JsonObject
            {
                ["command"] = "setDefaultACLAccess",
                ["acls"] = new JsonArray
                {
                    new JsonObject { ["acltype"] = "publishClientSend", ["allow"] = false },
                    new JsonObject { ["acltype"] = "publishClientReceive", ["allow"] = true },
                    new JsonObject { ["acltype"] = "subscribe", ["allow"] = false },
                    new JsonObject { ["acltype"] = "unsubscribe", ["allow"] = true },
                },
            },
            Acl("addRoleACL", options.Value.AdminRole, "publishClientSend", $"{prefix}/#", 0),
            Acl("addRoleACL", options.Value.AdminRole, "subscribePattern", $"{prefix}/#", 0),
        ], ct, ignoreErrors: true);
    }

    /// <summary>Creates (or recreates) the hornet's login and role. Returns the password.</summary>
    public async Task<string> ProvisionHornetAsync(string deviceId, string password, CancellationToken ct)
    {
        if (!Enabled)
            return password;
        var role = RoleName(deviceId);
        var dev = Topics.Device(deviceId);
        // Old login and role are removed first: replacing a board revokes the previous one (spec 5.3).
        await SendAsync([new JsonObject { ["command"] = "deleteClient", ["username"] = ClientName(deviceId) },
                         new JsonObject { ["command"] = "deleteRole", ["rolename"] = role }], ct, ignoreErrors: true);
        await SendAsync(
        [
            new JsonObject { ["command"] = "createRole", ["rolename"] = role },
            // Own topics only, except the ones the hive writes (cmd/+ and config): spec 4.2 ACL.
            Acl("addRoleACL", role, "publishClientSend", $"{dev}/#", 0, allow: true),
            Acl("addRoleACL", role, "publishClientSend", $"{dev}/cmd/+", 10, allow: false),
            Acl("addRoleACL", role, "publishClientSend", $"{dev}/config", 10, allow: false),
            Acl("addRoleACL", role, "subscribePattern", $"{dev}/cmd/#", 0, allow: true),
            Acl("addRoleACL", role, "subscribePattern", $"{dev}/config", 0, allow: true),
            Acl("addRoleACL", role, "subscribePattern", $"{Topics.Prefix}/hive/#", 0, allow: true),
            new JsonObject
            {
                ["command"] = "createClient",
                ["username"] = ClientName(deviceId),
                ["password"] = password,
                ["roles"] = new JsonArray { new JsonObject { ["rolename"] = role } },
            },
        ], ct);
        return password;
    }

    /// <summary>Removes the hornet's login, e.g. when it is deleted or factory reset (spec 5.3).</summary>
    public async Task RevokeHornetAsync(string deviceId, CancellationToken ct)
    {
        if (!Enabled)
            return;
        await SendAsync([new JsonObject { ["command"] = "deleteClient", ["username"] = ClientName(deviceId) },
                         new JsonObject { ["command"] = "deleteRole", ["rolename"] = RoleName(deviceId) }], ct, ignoreErrors: true);
    }

    private static JsonObject Acl(string command, string role, string type, string topic, int priority, bool allow = true) => new()
    {
        ["command"] = command,
        ["rolename"] = role,
        ["acltype"] = type,
        ["topic"] = topic,
        ["priority"] = priority,
        ["allow"] = allow,
    };

    private async Task SendAsync(JsonObject[] commands, CancellationToken ct, bool ignoreErrors = false)
    {
        var correlation = Guid.NewGuid().ToString("N");
        foreach (var c in commands)
            c["correlationData"] = correlation;
        var tcs = responses.Expect(correlation);
        try
        {
            var body = new JsonObject { ["commands"] = new JsonArray(commands.Select(c => (JsonNode)c).ToArray()) };
            await mqtt.PublishAsync(ControlTopic, body.ToJsonString(), retain: false, ct);
            var results = await tcs.Task.WaitAsync(Timeout, ct);
            foreach (var r in results)
            {
                var error = r?["error"]?.GetValue<string>();
                if (error is null)
                    continue;
                if (ignoreErrors)
                    logger.LogDebug("dynsec {Command}: {Error}", r!["command"], error);
                else
                    throw new InvalidOperationException($"Mosquitto dynsec {r!["command"]} failed: {error}");
            }
        }
        finally
        {
            responses.Forget(correlation);
        }
    }
}

/// <summary>Routes $CONTROL/dynamic-security/v1/response to the waiting command (correlationData).</summary>
public sealed class DynamicSecurityResponses(IOptions<MqttOptions> options, ILogger<DynamicSecurityResponses> logger) : IMqttMessageHandler
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonArray>> _pending = new();

    public IReadOnlyList<string> TopicFilters => options.Value.DynamicSecurity ? [DynamicSecurity.ResponseTopic] : [];

    public TaskCompletionSource<JsonArray> Expect(string correlation) =>
        _pending[correlation] = new TaskCompletionSource<JsonArray>(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Forget(string correlation) => _pending.TryRemove(correlation, out _);

    public ValueTask HandleAsync(string topic, ReadOnlyMemory<byte> payload, bool retained, DateTimeOffset receivedAt)
    {
        if (topic != DynamicSecurity.ResponseTopic)
            return ValueTask.CompletedTask;
        try
        {
            var responses = JsonNode.Parse(Encoding.UTF8.GetString(payload.Span))?["responses"]?.AsArray();
            var correlation = responses?.FirstOrDefault()?["correlationData"]?.GetValue<string>();
            if (correlation is not null && _pending.TryGetValue(correlation, out var tcs))
                tcs.TrySetResult(responses!);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Bad dynsec response: {Error}", ex.Message);
        }
        return ValueTask.CompletedTask;
    }
}
