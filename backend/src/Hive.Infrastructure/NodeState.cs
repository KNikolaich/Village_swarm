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

    public NodeRole Role => _role;

    public void Set(NodeRole role) => _role = role;

    /// <summary>Observers do not run rules at all.</summary>
    public bool RulesRun => Role != NodeRole.Observer;

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
        Role != NodeRole.Observer || deviceId.StartsWith("hive-", StringComparison.Ordinal) && deviceId != $"hive-{Node}";
}
