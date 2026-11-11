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
/// The observer node only reads (spec 11.3): everything that would change the house is refused. Signing in and a person's own
/// settings (<c>/api/me</c>: password, linking a Telegram chat to this node's bot) stay open: they change nothing of the house.
/// </summary>
public sealed class ReadOnlyMiddleware(RequestDelegate next, NodeState node)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var changes = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);
        if (node.ReadOnly && changes && request.Path.StartsWithSegments("/api") && !NodeLocal(request.Path) && !OwnDevice(request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { title = "Этот узел наблюдает за домом: его данные меняются дома. Здесь можно менять только своих шершней" });
            return;
        }
        await next(context);
    }

    /// <summary>What belongs to this node alone and is never copied from home: sign-in, a person's own account, and this node's own weather and language settings.</summary>
    private static bool NodeLocal(PathString path) =>
        path.StartsWithSegments("/api/auth") || path.StartsWithSegments("/api/me")
        || path.StartsWithSegments("/api/settings/weather") || path.StartsWithSegments("/api/settings/language")
        || path.StartsWithSegments("/api/settings/alias") || path.StartsWithSegments("/api/settings/restart")
        // On the city the notes are the master copy: changed there, and by the dacha on behalf of its people.
        || path.StartsWithSegments("/api/notes") || path.StartsWithSegments("/api/replica/notes")
        // Home's node sends the city the photos it took: authenticated by the key of the two nodes, it changes nothing of the house's data.
        || path.StartsWithSegments("/api/ingest/replica")
        // The city's own hornets: they are added here (the wizard, the board trading its code for a login), their photos arrive here, and the
        // rules and zones made here belong to this node (what home sent is put back by the next catalog).
        || path.StartsWithSegments("/api/provision") || path.StartsWithSegments("/api/ingest/photo")
        || path.StartsWithSegments("/api/rules") || path.StartsWithSegments("/api/zones")
        // On the city this names the dacha's broker, for the hornets it makes itself.
        || path.StartsWithSegments("/api/failover");

    /// <summary>/api/devices/{id}/...: allowed for a device made on this node, never for a copy of the other node's.</summary>
    private bool OwnDevice(PathString path)
    {
        var parts = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts is ["api", "devices", var id, ..] && node.IsLocal(id);
    }
}
