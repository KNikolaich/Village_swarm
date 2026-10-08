using Hive.Infrastructure.Data;
using Hive.Infrastructure.Mqtt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Infrastructure;

public static class InfrastructureServices
{
    public const string ConnectionStringName = "Hive";

    public static IServiceCollection AddHiveInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} is not configured");

        services.AddDbContext<HiveDbContext>(o => o
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention());

        services.AddSingleton(TimeProvider.System);
        services.Configure<MqttOptions>(configuration.GetSection(MqttOptions.Section));
        services.AddSingleton<MqttGateway>();
        services.AddHostedService(sp => sp.GetRequiredService<MqttGateway>());
        return services;
    }
}
