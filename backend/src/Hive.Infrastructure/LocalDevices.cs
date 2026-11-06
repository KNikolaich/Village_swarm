using Hive.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hive.Infrastructure;

/// <summary>Tells <see cref="NodeState"/> which devices were made on this node: read from the database until it answers (the schema may not be there at the very first start).</summary>
public sealed class LocalDevicesLoader(IServiceScopeFactory scopes, NodeState node, TimeProvider time, ILogger<LocalDevicesLoader> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The build runs the app to write the OpenAPI document; it must not need a database.
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider")
            return;
        for (var attempt = 0; attempt < 60 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
                node.LoadLocal(await db.Devices.AsNoTracking().Where(d => d.Local).Select(d => d.DeviceId).ToListAsync(stoppingToken));
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "The own devices of this node are not readable yet");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
