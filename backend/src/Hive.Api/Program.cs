using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Devices;
using Hive.Modules.Events;
using Hive.Modules.Media;
using Hive.Modules.Telemetry;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHiveInfrastructure(builder.Configuration);
builder.Services.AddTelemetryModule();
builder.Services.AddDevicesModule();
builder.Services.AddEventsModule();
builder.Services.AddMediaModule(builder.Configuration);

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

app.MapDevicesEndpoints();
app.MapEventsEndpoints();
app.MapMediaEndpoints();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/readyz", async (HiveDbContext db, MqttGateway mqtt, CancellationToken ct) =>
{
    var dbOk = await db.Database.CanConnectAsync(ct);
    var checks = new { database = dbOk ? "ok" : "down", mqtt = mqtt.IsConnected ? "ok" : "down" };
    return dbOk && mqtt.IsConnected
        ? Results.Ok(new { status = "ok", checks })
        : Results.Json(new { status = "degraded", checks }, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();

public partial class Program;
