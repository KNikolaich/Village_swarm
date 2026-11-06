using Hive.Domain;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure;

/// <summary>The role of this node (Standby is the city taking over the alarms, spec 11.3), changed by the cluster module (heartbeats) and read by whatever must hold back on a copy.</summary>
public sealed class NodeState
{
    private volatile NodeRole _role;

    public NodeState(IOptions<HiveOptions> hive)
    {
        Node = hive.Value.Node;
        _role = hive.Value.Role.Equals("active", StringComparison.OrdinalIgnoreCase) ? NodeRole.Active : NodeRole.Observer;
    }

    public string Node { get; }

    // The devices made on this node (added through its own screen, not copied from the other one): the city keeps its own hornets
    // next to the copies of home's. Read often (every rule, notification and request), so a set in memory, kept up to date by the node.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _local = new(StringComparer.Ordinal);

    public bool IsLocal(string? deviceId) => deviceId is not null && _local.ContainsKey(deviceId);

    public void MarkLocal(string deviceId) => _local[deviceId] = 0;

    public void ForgetLocal(string deviceId) => _local.TryRemove(deviceId, out _);

    public void LoadLocal(IEnumerable<string> deviceIds)
    {
        foreach (var id in deviceIds)
            _local[id] = 0;
    }

    public NodeRole Role => _role;

    public void Set(NodeRole role) => _role = role;

    /// <summary>Observers do not run rules about the other node's devices; the rules of their own devices run (<see cref="RulesRunFor"/>).</summary>
    public bool RulesRun => Role != NodeRole.Observer;

    public bool RulesRunFor(string? deviceId) => RulesRun || IsLocal(deviceId);

    /// <summary>A rule may command a device on the active node, and on any node for the node's own devices.</summary>
    public bool MayCommand(string? deviceId) => RulesMayCommand || IsLocal(deviceId);

    /// <summary>Rules may command devices, set modes and take snapshots only on the active node; a standby node's rules just notify.</summary>
    public bool RulesMayCommand => Role == NodeRole.Active;

    /// <summary>The UI of an observer only reads: changes are made on the node that is in charge.</summary>
    public bool ReadOnly => Role == NodeRole.Observer;

    /// <summary>
    /// Whether a notification about this device goes out from here. An observer stays silent about everything the active node
    /// already reports (its copy of the events would otherwise tell the family everything twice) and speaks only about the
    /// active node itself, "hive-home".
    /// </summary>
    public bool Notifies(string deviceId) =>
        Role != NodeRole.Observer || IsLocal(deviceId) || deviceId.StartsWith("hive-", StringComparison.Ordinal) && deviceId != $"hive-{Node}";
}
