using System.Globalization;
using System.Text.RegularExpressions;
using Hive.Domain;
using Hive.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Infrastructure;

public enum ConfigKind { Text, Int, Number, Bool, Choice, TimeZone, Time, Url }

/// <summary>When a saved value starts to work: after the Hive restart (read once at start), or by itself (the backup container reads the database).</summary>
public enum ConfigApply { Restart, Auto }

/// <summary>
/// One non-secret, non-boot parameter the admin can set in Settings instead of .env. <see cref="ConfigPath"/> is where the modules read it
/// (a configuration path, or for <see cref="ConfigApply.Auto"/> the .env variable the value falls back to). <see cref="Default"/> is shown
/// when neither Settings nor .env set it; it mirrors the default in the options class.
/// </summary>
public sealed record ConfigDef(string Key, string ConfigPath, string Section, ConfigKind Kind, string Default, ConfigApply Apply = ConfigApply.Restart,
    string[]? Choices = null, double Min = double.MinValue, double Max = double.MaxValue);

/// <summary>
/// Plain (not encrypted) settings kept in the database (<c>modes</c>, keys <c>cfg.*</c>). Like <see cref="Secrets"/> they go over .env at start.
/// Boot and infrastructure values (PG_PASSWORD, MQTT_ADMIN_*, CITY_MQTT_PASSWORD, COMPOSE_FILE, images, ports, HIVE_HOSTS) stay in .env.
/// </summary>
public static class ConfigSettings
{
    public const string RowPrefix = "cfg.";

    /// <summary>Order of the sections on the page.</summary>
    public static readonly string[] Sections = ["general", "security", "monitoring", "cluster", "assistant", "mail", "backup"];

    public static readonly IReadOnlyList<ConfigDef> All =
    [
        new("timezone", "Hive:TimeZone", "general", ConfigKind.TimeZone, "Europe/Moscow"),

        new("auth.newdevicecode", "Auth:NewDeviceCode", "security", ConfigKind.Choice, "Always", Choices: ["Always", "External"]),

        new("sensors.air_warn_c", "Hive:Sensors:AirWarnC", "monitoring", ConfigKind.Number, "45", Min: 0, Max: 150),
        new("sensors.cpu_warn_c", "Hive:Sensors:CpuWarnC", "monitoring", ConfigKind.Number, "75", Min: 0, Max: 150),
        new("sensors.disk_warn_pct", "Hive:Sensors:DiskWarnPct", "monitoring", ConfigKind.Number, "85", Min: 1, Max: 100),

        new("cluster.notes_in_city", "Cluster:NotesInCity", "cluster", ConfigKind.Bool, "true"),

        new("assistant.model", "Assistant:Model", "assistant", ConfigKind.Text, "deepseek-flash"),
        new("assistant.baseurl", "Assistant:BaseUrl", "assistant", ConfigKind.Url, "https://api.deepseek.com"),
        new("assistant.maxrequestsperday", "Assistant:MaxRequestsPerDay", "assistant", ConfigKind.Int, "300", Min: 1, Max: 100000),

        new("smtp.host", "Smtp:Host", "mail", ConfigKind.Text, ""),
        new("smtp.port", "Smtp:Port", "mail", ConfigKind.Int, "587", Min: 1, Max: 65535),
        new("smtp.user", "Smtp:User", "mail", ConfigKind.Text, ""),
        new("smtp.from", "Smtp:From", "mail", ConfigKind.Text, ""),

        // The backup container reads these straight from the database (deploy/backup/loop.sh); .env BACKUP_* is the fallback.
        new("backup.at", "BACKUP_AT", "backup", ConfigKind.Time, "03:00", ConfigApply.Auto),
        new("backup.verify_day", "BACKUP_VERIFY_DAY", "backup", ConfigKind.Int, "7", ConfigApply.Auto, Min: 0, Max: 7),
    ];

    private static readonly Regex TimeOfDay = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled);

    public static ConfigDef? Find(string key) => All.FirstOrDefault(d => d.Key == key);

    /// <summary>Null when the text is fine for the parameter, otherwise a short reason. Empty is always fine: it clears the setting.</summary>
    public static string? Validate(ConfigDef def, string value)
    {
        if (value.Length == 0)
            return null;
        if (value.Length > 300)
            return "too long";
        switch (def.Kind)
        {
            case ConfigKind.Text:
                return null;
            case ConfigKind.Int:
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i >= def.Min && i <= def.Max ? null : $"whole number {def.Min}..{def.Max}";
            case ConfigKind.Number:
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= def.Min && d <= def.Max ? null : $"number {def.Min}..{def.Max}";
            case ConfigKind.Bool:
                return value is "true" or "false" ? null : "true or false";
            case ConfigKind.Choice:
                return def.Choices!.Contains(value) ? null : "one of " + string.Join(", ", def.Choices!);
            case ConfigKind.Time:
                return TimeOfDay.IsMatch(value) ? null : "HH:mm";
            case ConfigKind.Url:
                return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? null : "http(s) address";
            case ConfigKind.TimeZone:
                try { TimeZoneInfo.FindSystemTimeZoneById(value); return null; }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return "unknown time zone (IANA id, e.g. Europe/Moscow)"; }
            default:
                return null;
        }
    }

    /// <summary>
    /// Puts every stored value over the configuration (the last source wins): Settings beats .env, a cleared one falls back to .env.
    /// Call once after the migrations, before the options are first read. Remembers what it applied (<see cref="AppliedConfig"/>)
    /// to tell the page which saves still wait for a restart.
    /// </summary>
    public static async Task ApplyAsync(IServiceProvider services, IConfigurationBuilder configuration, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var rows = await db.Modes.AsNoTracking().Where(m => m.Key.StartsWith(RowPrefix)).ToListAsync(ct);
        var values = new Dictionary<string, string?>();
        foreach (var row in rows)
            if (Find(row.Key[RowPrefix.Length..]) is { Apply: ConfigApply.Restart } def && row.Value.Length > 0 && Validate(def, row.Value) is null)
                values[def.ConfigPath] = row.Value;
        services.GetRequiredService<AppliedConfig>().Set(values);
        if (values.Count > 0)
            configuration.Add(new MemoryConfigurationSource { InitialData = values });
    }
}

/// <summary>The values <see cref="ConfigSettings.ApplyAsync"/> put over the configuration at this start (config path → value).</summary>
public sealed class AppliedConfig
{
    private IReadOnlyDictionary<string, string?> _values = new Dictionary<string, string?>();
    public void Set(IReadOnlyDictionary<string, string?> values) => _values = values;
    public string? Get(string configPath) => _values.GetValueOrDefault(configPath);
}

public enum ConfigSource { Db, Env, Default }

/// <summary>One parameter for the page. <see cref="Value"/> is what works now (Settings, else .env, else the default).</summary>
public sealed record ConfigItem(ConfigDef Def, string Value, ConfigSource Source, string? Stored, bool RestartPending, DateTimeOffset? UpdatedAt, string? UpdatedBy);

public sealed class ConfigStore(HiveDbContext db, IConfiguration configuration, AppliedConfig applied, TimeProvider time)
{
    public async Task<List<ConfigItem>> ListAsync(CancellationToken ct)
    {
        var rows = (await db.Modes.AsNoTracking().Where(m => m.Key.StartsWith(ConfigSettings.RowPrefix)).ToListAsync(ct)).ToDictionary(m => m.Key);
        return ConfigSettings.All.Select(def =>
        {
            rows.TryGetValue(ConfigSettings.RowPrefix + def.Key, out var row);
            var stored = row?.Value;
            var running = configuration[def.ConfigPath];
            var runningValue = string.IsNullOrWhiteSpace(running) ? def.Default : running;
            // Auto settings are read by their consumer from the database: the stored value is the effective one.
            var value = def.Apply == ConfigApply.Auto && stored is not null ? stored : runningValue;
            var source = stored is not null ? ConfigSource.Db : string.IsNullOrWhiteSpace(running) ? ConfigSource.Default : ConfigSource.Env;
            var pending = def.Apply == ConfigApply.Restart && stored != applied.Get(def.ConfigPath);
            return new ConfigItem(def, value, source, stored, pending, row?.UpdatedAt, row?.UpdatedBy);
        }).ToList();
    }

    /// <summary>Saves the value (the caller validated it); empty clears it so .env applies again.</summary>
    public async Task SetAsync(ConfigDef def, string value, string by, CancellationToken ct)
    {
        var key = ConfigSettings.RowPrefix + def.Key;
        var row = await db.Modes.FirstOrDefaultAsync(m => m.Key == key, ct);
        var now = time.GetUtcNow();
        if (value.Length == 0)
        {
            if (row is not null)
                db.Modes.Remove(row);
        }
        else if (row is null)
            db.Modes.Add(new Mode { Key = key, Value = value, UpdatedAt = now, UpdatedBy = by });
        else
        {
            row.Value = value;
            row.UpdatedAt = now;
            row.UpdatedBy = by;
        }
        Audit.Record(db, now, by, value.Length == 0 ? "clear_config" : "set_config", def.Key, new { value });
        await db.SaveChangesAsync(ct);
    }
}
