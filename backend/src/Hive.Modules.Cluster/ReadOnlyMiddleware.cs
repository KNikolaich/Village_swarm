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
/// What stays open is marked on the routes themselves with <c>.NodeLocal()</c> (see <see cref="NodeLocalAttribute"/>).
/// </summary>
public sealed class ReadOnlyMiddleware(RequestDelegate next, NodeState node)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var changes = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);
        if (node.ReadOnly && changes && request.Path.StartsWithSegments("/api") && !context.GetEndpoint().IsNodeLocal() && !OwnDevice(request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { title = "Этот узел наблюдает за домом: его данные меняются дома. Здесь можно менять только своих шершней" });
            return;
        }
        await next(context);
    }

    /// <summary>/api/devices/{id}/...: allowed for a device made on this node, never for a copy of the other node's.</summary>
    private bool OwnDevice(PathString path)
    {
        var parts = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts is ["api", "devices", var id, ..] && node.IsLocal(id);
    }
}
