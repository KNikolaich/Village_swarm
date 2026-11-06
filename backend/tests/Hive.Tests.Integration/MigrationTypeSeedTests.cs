using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

namespace Hive.Tests.Integration;

/// <summary>
/// A hornet that announces a type the hive does not know yet makes the hive add it (IngestWriter). The migration that later adds the same type must
/// not fail on it: it failed once on "car" and the hive never came up after an update.
/// </summary>
public sealed class MigrationTypeSeedTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").WithDatabase("hive").WithUsername("hive").WithPassword("hive").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    /// <summary>The context as the app builds it: identity with its schema version, snake case names.</summary>
    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<HiveDbContext>(o => o
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention());
        services.AddIdentity<HiveUser, IdentityRole<int>>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<HiveDbContext>();
        return services.BuildServiceProvider();
    }

    private HiveDbContext Db(ServiceProvider services) => services.CreateScope().ServiceProvider.GetRequiredService<HiveDbContext>();

    [Fact]
    public async Task A_device_type_that_is_there_already_does_not_stop_the_migration_that_adds_it()
    {
        // an old database: everything before the migrations that add device types
        using var services = Services();
        string before;
        {
            var db = Db(services);
            before = db.GetService<IMigrationsAssembly>().Migrations.Keys.Order().Last(id => string.CompareOrdinal(id, "20261024201846_InfraredTypes") < 0);
            await db.GetService<IMigrator>().MigrateAsync(before);
            // what the ingest made of hornets that announced these types
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO device_types (code, offline_after_s, title) VALUES
                ('irtx', 180, 'irtx'), ('monitor', 180, 'monitor'), ('car', 180, 'car'), ('some-own-type', 180, 'Своё название')
                """);
        }

        await Db(services).Database.MigrateAsync();   // no duplicate key

        {
            var db = Db(services);
            var titles = await db.DeviceTypes.AsNoTracking().ToDictionaryAsync(t => t.Code, t => t.Title);
            Assert.Equal("ИК-передатчик (управляет прибором)", titles["irtx"]);   // the placeholder title is replaced by the real one
            Assert.Equal("Машинка с камерой", titles["car"]);
            Assert.Equal("Монитор (экран с показаниями)", titles["monitor"]);
            Assert.Equal("ИК-приёмник (учит кнопки пульта)", titles["irrx"]);   // and the ones that were not there are added
            Assert.Equal("Своё название", titles["some-own-type"]);
        }
    }
}
