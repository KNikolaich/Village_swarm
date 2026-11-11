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

/// <summary>Creates the login the home bridge uses, on the city's broker, when missing.</summary>
public sealed class BridgeLogin(MqttGateway mqtt, DynamicSecurity dynsec, IOptions<ClusterOptions> options, IOptions<HiveOptions> hive) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || hive.Value.Node != "city" || string.IsNullOrEmpty(options.Value.BridgePassword))
            return Task.CompletedTask;
        mqtt.Connected += Provision;
        // Not awaited: the broker's first answers can take ten seconds (the api has no permissions yet), and a host must start without waiting.
        if (mqtt.IsConnected)
            _ = Task.Run(async () =>
            {
                try
                {
                    await Provision(CancellationToken.None);
                }
                catch (Exception)
                {
                    // the next (re)connect of the api tries again
                }
            }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private Task Provision(CancellationToken ct) => dynsec.ProvisionBridgeAsync(options.Value.BridgeUser, options.Value.BridgePassword!, ct);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        mqtt.Connected -= Provision;
        return Task.CompletedTask;
    }
}
