using System.Text.Json.Serialization;
using Hive.Api.Live;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Devices;
using Hive.Modules.Events;
using Hive.Modules.Media;
using Hive.Modules.Telemetry;
using Hive.Modules.Telemetry.Ingest;
using Hive.Modules.Users;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
    // Numbers are numbers: keeps the OpenAPI types (and the generated TypeScript) as `number`, not `number | string`.
    o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.Services.AddHiveInfrastructure(builder.Configuration);
builder.Services.AddUsersModule(builder.Configuration);
builder.Services.AddTelemetryModule();
builder.Services.AddDevicesModule();
builder.Services.AddEventsModule();
builder.Services.AddMediaModule(builder.Configuration);

builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
builder.Services.AddSingleton<IIngestListener, LiveNotifier>();
builder.Services.AddOpenApi();

var app = builder.Build();

// `Hive.Api --migrate` applies migrations and exits (install/update scripts, spec 13.4).
// Database:MigrateOnStartup does the same on every start (default in Development).
var migrateOnly = args.Contains("--migrate");
if (migrateOnly || app.Configuration.GetValue("Database:MigrateOnStartup", app.Environment.IsDevelopment()))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<HiveDbContext>().Database.MigrateAsync();
    if (migrateOnly)
        return;
}

// OpenAPI document for the generated TypeScript client (npm run gen:api) and Swagger UI.
if (app.Environment.IsDevelopment() || app.Configuration.GetValue("OpenApi:Enabled", false))
{
    app.MapOpenApi().AllowAnonymous();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/openapi/v1.json", "Village Swarm API");
        o.RoutePrefix = "swagger";
    });
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapUsersEndpoints();
app.MapTelemetryEndpoints();
app.MapDevicesEndpoints();
app.MapEventsEndpoints();
app.MapMediaEndpoints();
app.MapHub<LiveHub>(LiveHub.Path);

app.MapGet("/healthz", () => TypedResults.Ok(new { status = "ok" })).AllowAnonymous().ExcludeFromDescription();

app.MapGet("/readyz", async (HiveDbContext db, MqttGateway mqtt, CancellationToken ct) =>
{
    var dbOk = await db.Database.CanConnectAsync(ct);
    var checks = new { database = dbOk ? "ok" : "down", mqtt = mqtt.IsConnected ? "ok" : "down" };
    return dbOk && mqtt.IsConnected
        ? Results.Ok(new { status = "ok", checks })
        : Results.Json(new { status = "degraded", checks }, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous().ExcludeFromDescription();

app.Run();

public partial class Program;
