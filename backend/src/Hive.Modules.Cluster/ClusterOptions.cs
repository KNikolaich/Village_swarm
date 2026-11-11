using System.Reflection;
using System.Text;
using System.Text.Json;
using Hive.Contracts;
using Hive.Contracts.Messages;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Cluster;

/// <summary>
/// Two nodes, one stream of events (spec 11): the home node and the city node run the same stack; the city watches home with
/// heartbeats and takes over the alarms when home is gone. Off unless <c>Cluster__Enabled</c> is set.
/// </summary>
public sealed class ClusterOptions
{
    public const string Section = "Cluster";

    public bool Enabled { get; set; }

    public int HeartbeatSeconds { get; set; } = 30;

    /// <summary>How often the node tells the other one how its board is doing (CPU, load, memory, disk).</summary>
    public int StatsEveryS { get; set; } = 60;

    /// <summary>The city is the master of the notes: it says so, and the dacha moves its notes there and asks the city for them. false: every hive keeps its own, as before.</summary>
    public bool NotesInCity { get; set; } = true;

    /// <summary>No heartbeat from home this long: a warning, and the city may take over if hornets reach it directly.</summary>
    public int HomeSilenceS { get; set; } = 180;

    /// <summary>No heartbeat this long: an alarm.</summary>
    public int HomeAlarmS { get; set; } = 900;

    /// <summary>How often the watchdog looks (seconds).</summary>
    public int TickS { get; set; } = 5;

    /// <summary>Home: how often it looks whether the catalog changed, and the longest it goes without publishing it (seconds).</summary>
    public int CatalogCheckS { get; set; } = 60;

    public int CatalogEveryS { get; set; } = 300;

    /// <summary>City only: the login home's Mosquitto bridge uses on the city broker (created when missing).</summary>
    public string BridgeUser { get; set; } = "bridge-home";

    public string? BridgePassword { get; set; }

    /// <summary>Home: how often the photo queue is looked at (seconds).</summary>
    public int ReplicaEveryS { get; set; } = 30;
}
