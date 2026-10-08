using Hive.Domain;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Hive.Infrastructure.Data;

/// <summary>PostgreSQL schema (spec 6.4). Table and column names are snake_case.</summary>
public class HiveDbContext(DbContextOptions<HiveDbContext> options)
    : IdentityDbContext<HiveUser, IdentityRole<int>, int>(options)
{
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<DeviceType> DeviceTypes => Set<DeviceType>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<TelemetryPoint> Telemetry => Set<TelemetryPoint>();
    public DbSet<DeviceEvent> Events => Set<DeviceEvent>();
    public DbSet<DeviceLog> DeviceLogs => Set<DeviceLog>();
    public DbSet<DeviceHealth> DeviceHealth => Set<DeviceHealth>();
    public DbSet<MediaItem> Media => Set<MediaItem>();
    public DbSet<DeviceCommand> Commands => Set<DeviceCommand>();
    public DbSet<Mode> Modes => Set<Mode>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<TgLink> TgLinks => Set<TgLink>();
    public DbSet<TgLinkCode> TgLinkCodes => Set<TgLinkCode>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        // Identity tables without the AspNet prefix: users, roles, user_roles...
        b.Entity<HiveUser>(e =>
        {
            e.ToTable("users");
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });
        b.Entity<IdentityRole<int>>().ToTable("roles");
        b.Entity<IdentityUserRole<int>>().ToTable("user_roles");
        b.Entity<IdentityUserClaim<int>>().ToTable("user_claims");
        b.Entity<IdentityUserLogin<int>>().ToTable("user_logins");
        b.Entity<IdentityUserToken<int>>().ToTable("user_tokens");
        b.Entity<IdentityRoleClaim<int>>().ToTable("role_claims");
        b.Entity<IdentityUserPasskey<int>>().ToTable("user_passkeys");

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
            e.Property(x => x.UploadTokenHash).HasMaxLength(64);
        });

        b.Entity<DeviceCommand>(e =>
        {
            e.ToTable("commands");
            e.HasKey(x => x.Cid);
            e.Property(x => x.Cid).HasMaxLength(26);
            e.Property(x => x.DeviceId).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(32);
            e.Property(x => x.IssuedBy).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.DeviceId, x.IssuedAt });
            e.HasIndex(x => x.Status);
        });

        b.Entity<Mode>(e =>
        {
            e.ToTable("modes");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(32);
            e.Property(x => x.Value).HasMaxLength(256);
            e.Property(x => x.UpdatedBy).HasMaxLength(64);
            e.HasData(new Mode { Key = Mode.Armed, Value = "false", UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) });
        });

        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_log");
            e.Property(x => x.Actor).HasMaxLength(64);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.Target).HasMaxLength(128);
            e.HasIndex(x => x.Ts);
        });

        b.Entity<TgLink>(e =>
        {
            e.ToTable("tg_links");
            e.HasIndex(x => x.ChatId).IsUnique();
            e.Property(x => x.Username).HasMaxLength(64);
            e.Property(x => x.NotifyLevel).HasConversion<string>().HasMaxLength(16);
            e.HasOne<HiveUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TgLinkCode>(e =>
        {
            e.ToTable("tg_link_codes");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(8);
            e.HasOne<HiveUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.Property(x => x.Channel).HasMaxLength(8);
            e.Property(x => x.Target).HasMaxLength(32);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.EventId).HasMaxLength(26);
            e.Property(x => x.DeviceIds).HasMaxLength(512);
            e.Property(x => x.DedupeKey).HasMaxLength(96);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => new { x.Target, x.DedupeKey, x.CreatedAt });
            e.HasIndex(x => x.EventId);
        });

        b.Entity<MediaItem>(e =>
        {
            e.ToTable("media");
            e.Property(x => x.Id).HasMaxLength(32);
            e.Property(x => x.EventId).HasMaxLength(26);
            e.Property(x => x.DeviceId).HasMaxLength(32);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Path).HasMaxLength(256);
            e.Property(x => x.ThumbPath).HasMaxLength(256);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.UploadedVia).HasMaxLength(8);
            e.HasIndex(x => x.Ts);
            e.HasIndex(x => new { x.DeviceId, x.Ts });
            e.HasIndex(x => x.EventId);
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
