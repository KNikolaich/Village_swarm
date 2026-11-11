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

public static class ClusterModule
{
    public const string Name = "cluster";

    public static IServiceCollection AddClusterModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ClusterOptions>(configuration.GetSection(ClusterOptions.Section));
        services.AddSingleton<HomeWatch>();
        services.AddSingleton<IMqttMessageHandler>(sp => sp.GetRequiredService<HomeWatch>());
        services.AddHostedService(sp => sp.GetRequiredService<HomeWatch>());
        services.AddSingleton<HeartbeatPublisher>();
        services.AddSingleton<IMqttMessageHandler>(sp => sp.GetRequiredService<HeartbeatPublisher>());
        services.AddHostedService(sp => sp.GetRequiredService<HeartbeatPublisher>());
        services.AddHostedService<BridgeLogin>();

        // Each node's board readings, for the other one to show.
        services.AddScoped<HiveAliasService>();

        // The city is the master of the notes: the dacha moves its notes over, then asks the city for them (and the assistant too).
        services.AddSingleton<NotesMasterState>();
        services.AddSingleton<NotesMasterClient>();
        services.AddSingleton<RemoteNoteMaster>();
        services.AddSingleton<Hive.Modules.Notes.INoteMaster>(sp => sp.GetRequiredService<RemoteNoteMaster>());
        services.AddHostedService<NotesMigrator>();
        services.AddHostedService<NodeStatsPublisher>();
        services.AddSingleton<NodeStatsReceiver>();
        services.AddSingleton<IMqttMessageHandler>(sp => sp.GetRequiredService<NodeStatsReceiver>());
        services.AddHostedService(sp => sp.GetRequiredService<NodeStatsReceiver>());

        // Events the city saw while home was down, back to home (spec 11.3).
        services.AddSingleton<ReplicaServer>();
        services.AddSingleton<IMqttMessageHandler>(sp => sp.GetRequiredService<ReplicaServer>());
        services.AddSingleton<ReplicaCatchUp>();
        services.AddSingleton<IMqttMessageHandler>(sp => sp.GetRequiredService<ReplicaCatchUp>());
        services.AddHostedService(sp => sp.GetRequiredService<ReplicaCatchUp>());
        services.AddHostedService<HornetCityLogins>();

        // The hornets the city made itself, told to home so that they are let in when carried to the dacha.
        services.AddHostedService<LocalsPublisher>();
        services.AddSingleton<LocalsReceiver>();
        services.AddSingleton<IMqttMessageHandler>(sp => sp.GetRequiredService<LocalsReceiver>());

        // Photos home to city (spec 7.3): the queue and sender at home, the receiver in the city.
        services.Configure<CityBrokerOptions>(configuration.GetSection(ClusterOptions.Section));
        services.AddSingleton<PhotoReplicaState>();
        services.AddHostedService<PhotoReplicator>();
        services.AddScoped<PhotoReplicaReceiver>();

        // The catalog of reference data, home to city (spec 11.2).
        services.AddSingleton<CatalogState>();
        services.AddScoped<CatalogBuilder>();
        services.AddScoped<CatalogApplier>();
        services.AddSingleton<CatalogReceiver>();
        services.AddSingleton<IMqttMessageHandler>(sp => sp.GetRequiredService<CatalogReceiver>());
        services.AddHostedService<CatalogPublisher>();
        return services;
    }

    public static IApplicationBuilder UseClusterReadOnly(this IApplicationBuilder app) => app.UseMiddleware<ReadOnlyMiddleware>();

    /// <summary>After authentication, before authorization: the city lets in the dacha's people by the key; the dacha, with its notes in the city, passes their requests on.</summary>
    public static IApplicationBuilder UseNotesMaster(this IApplicationBuilder app) => app.UseMiddleware<NotesMasterMiddleware>();

    public static IEndpointRouteBuilder MapClusterEndpoints(this IEndpointRouteBuilder app)
    {
        // Whether this node can tell people anything: a bot token, and at least one chat. The city has to be told this: alarms of the other node
        // come from here, and without a chat they go nowhere (the city does not poll the bot, so chats arrive with the catalog from home).
        app.MapGet("/api/cluster", async (HomeWatch watch, HiveDbContext db, HiveAliasService alias, NodeStatsReceiver stats, NotesMasterState notes,
                Microsoft.Extensions.Configuration.IConfiguration configuration, CancellationToken ct) =>
                TypedResults.Ok(watch.Describe() with
                {
                    TelegramConfigured = !string.IsNullOrWhiteSpace(configuration["Telegram:Token"]),
                    TelegramChats = await db.TgLinks.CountAsync(ct),
                    Alias = await alias.GetAsync(ct),
                    OtherAlias = stats.OtherAlias,
                    NotesIn = notes.Active ? "city" : "local",
                }))
            .WithTags("cluster");
        app.MapAliasEndpoints();
        app.MapNotesMasterEndpoints();
        app.MapPhotoReplicaEndpoints();
        return app;
    }
}
