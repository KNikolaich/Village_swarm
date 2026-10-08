using Hive.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hive.Infrastructure.Data;

/// <summary>PostgreSQL schema (spec 6.4). Table and column names are snake_case.</summary>
public class HiveDbContext(DbContextOptions<HiveDbContext> options) : DbContext(options)
{
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<DeviceType> DeviceTypes => Set<DeviceType>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<TelemetryPoint> Telemetry => Set<TelemetryPoint>();
    public DbSet<DeviceEvent> Events => Set<DeviceEvent>();
    public DbSet<DeviceLog> DeviceLogs => Set<DeviceLog>();
    public DbSet<DeviceHealth> DeviceHealth => Set<DeviceHealth>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Zone>(e =>
        {
            e.ToTable("zones");
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<DeviceType>(e =>
        {
            e.ToTable("device_types");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(32);
            e.Property(x => x.Title).HasMaxLength(64);
            e.HasData(
                new DeviceType { Code = "guard-cam", Title = "Камера с датчиком движения", OfflineAfterS = 180 },
                new DeviceType { Code = "meteo", Title = "Метеодатчик", OfflineAfterS = 180 },
                new DeviceType { Code = "heat", Title = "Обогрев", OfflineAfterS = 180 },
                new DeviceType { Code = "leak", Title = "Датчик протечки с краном", OfflineAfterS = 180 },
                new DeviceType { Code = "plant", Title = "Растения", OfflineAfterS = 600 });
        });

        b.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.HasIndex(x => x.DeviceId).IsUnique();
            e.Property(x => x.DeviceId).HasMaxLength(32);
            e.Property(x => x.TypeCode).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Mac).HasMaxLength(17);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<DeviceType>().WithMany().HasForeignKey(x => x.TypeCode).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        // Partitioned by month in raw SQL (see the InitialSchema migration); EF only reads it.
        b.Entity<TelemetryPoint>(e =>
        {
            e.ToTable("telemetry", t => t.ExcludeFromMigrations());
            e.HasNoKey();
        });

        b.Entity<DeviceEvent>(e =>
        {
            e.ToTable("events");
            e.Property(x => x.Id).HasMaxLength(26);
            e.Property(x => x.DeviceId).HasMaxLength(32);
            e.Property(x => x.Type).HasMaxLength(32);
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ViaNode).HasConversion<string>().HasMaxLength(8);
            e.HasIndex(x => x.Ts);
            e.HasIndex(x => new { x.DeviceId, x.Ts });
        });

        b.Entity<DeviceLog>(e =>
        {
            e.ToTable("device_logs");
            e.Property(x => x.DeviceId).HasMaxLength(32);
            e.Property(x => x.Level).HasMaxLength(8);
            e.HasIndex(x => new { x.DeviceId, x.Ts });
        });

        b.Entity<DeviceHealth>(e =>
        {
            e.ToTable("device_health");
            e.Property(x => x.DeviceId).HasMaxLength(32);
            e.Property(x => x.ResetReason).HasMaxLength(32);
            e.HasIndex(x => new { x.DeviceId, x.Ts });
        });
    }
}
