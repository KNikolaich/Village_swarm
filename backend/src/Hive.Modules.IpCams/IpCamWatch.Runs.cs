using System.Text.Json;
using System.Threading.Channels;
using Hive.Contracts;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Modules.Devices;
using Hive.Modules.Media;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.IpCams;

/// <summary>What the watch knows about an NVR and its channels.</summary>
public sealed partial class IpCamWatch
{
    /// <summary>A channel of an NVR as the watch knows it.</summary>
    internal sealed class ChannelInfo(string deviceId, int channel, bool recordWhenDisarmed, string source, string? via, string quality)
    {
        public string Quality { get; } = quality;
        public string DeviceId { get; } = deviceId;
        public int Channel { get; } = channel;
        public bool RecordWhenDisarmed { get; } = recordWhenDisarmed;
        public string Source { get; } = source;

        /// <summary>The way the last frame came; set by whichever photo or command got one.</summary>
        public string? Via
        {
            get => Volatile.Read(ref _via);
            set => Volatile.Write(ref _via, value);
        }

        private string? _via = via;
    }

    /// <summary>The stream of one NVR and what is known of its health.</summary>
    internal sealed class NvrRun(int id, string name, DahuaClient client, Dictionary<int, ChannelInfo> channels, DateTimeOffset started)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;

        /// <summary>The NVR's own device in the swarm; null for a camera on its own.</summary>
        public string? DeviceId { get; init; }

        public string Vendor { get; init; } = CameraVendors.Dahua;

        /// <summary>ONVIF: where to subscribe for motion; null: the camera is only checked for life.</summary>
        public string? EventsUrl { get; init; }

        /// <summary>The login state last put in the NVR device's state (see NvrLogins).</summary>
        public string? LoginShown { get; set; }
        public DahuaClient Client { get; } = client;
        public Dictionary<int, ChannelInfo> Channels { get; } = channels;
        public CancellationTokenSource Stop { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;

        /// <summary>Channels whose camera the NVR reports lost (VideoLoss): offline whatever the stream does.</summary>
        public HashSet<int> Lost { get; } = [];

        public volatile bool Connected;

        /// <summary>When the stream opened last, and how often it was opened again after that (diagnostics).</summary>
        public DateTimeOffset? Since { get; private set; }

        public int Reconnects { get; private set; }
        public string? LastError { get; set; }

        public void Opened(DateTimeOffset now)
        {
            if (Since is not null)
                Reconnects++;
            Since = now;
            LastError = null;
        }
        private long _lastAlive = started.UtcTicks;

        /// <summary>The last line from the stream (written by the stream, read by the status check).</summary>
        public DateTimeOffset LastAlive => new(Interlocked.Read(ref _lastAlive), TimeSpan.Zero);

        public bool? Online { get; set; }
        public DateTimeOffset Refreshed { get; set; }

        public void Alive(DateTimeOffset now, bool connected)
        {
            Interlocked.Exchange(ref _lastAlive, now.UtcTicks);
            Connected = connected;
        }

        public bool IsLost(int channel)
        {
            lock (Lost)
                return Lost.Contains(channel);
        }
    }
}
