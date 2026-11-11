using Hive.Domain;
using Hive.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hive.Infrastructure;

/// <summary>A third-party secret the admin can enter in Settings instead of .env. <see cref="ConfigPath"/> is where the modules read it.</summary>
public sealed record SecretDef(string Key, string ConfigPath, string Group);

/// <summary>
/// The secrets kept in the database, encrypted with ASP.NET Data Protection (keys: DataProtection:KeysPath = HIVE_DATA/keys).
/// Boot secrets (PG_PASSWORD, MQTT_ADMIN_PASSWORD, CITY_MQTT_PASSWORD) stay in .env: they are needed before the database is reachable.
/// </summary>
public static class Secrets
{
    public const string RowPrefix = "secret.";
    public const string Purpose = "village-swarm.secrets.v1";

    public static readonly IReadOnlyList<SecretDef> All =
    [
        new("telegram.token", "Telegram:Token", "telegram"),
        new("telegram.proxypassword", "Telegram:Endpoints:0:ProxyPassword", "telegram"),
        new("assistant.apikey", "Assistant:ApiKey", "assistant"),
        new("smtp.password", "Smtp:Password", "smtp"),
    ];

    public static SecretDef? Find(string key) => All.FirstOrDefault(d => d.Key == key);

    /// <summary>
    /// Reads every stored secret and puts it over the configuration (the last source wins): a value set in Settings beats .env, an empty
    /// one falls back to .env. Call once after the migrations, before the options are first read. Takes effect after a restart.
    /// </summary>
    public static async Task ApplyAsync(IServiceProvider services, IConfigurationBuilder configuration, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<SecretStore>();
        var values = new Dictionary<string, string?>();
        foreach (var (def, value) in await store.ReadAllAsync(ct))
            if (value is not null)
                values[def.ConfigPath] = value;
        if (values.Count > 0)
            configuration.Add(new MemoryConfigurationSource { InitialData = values });
    }
}

public enum SecretSource { None, Env, Db, Unreadable }

public sealed class SecretStore(HiveDbContext db, IDataProtectionProvider protection, IConfiguration configuration, TimeProvider time, ILogger<SecretStore> log)
{
    private readonly IDataProtector _protector = protection.CreateProtector(Secrets.Purpose);

    public string Protect(string plain) => _protector.Protect(plain);

    /// <summary>The decrypted value, or null when the text cannot be decrypted (keys lost or replaced).</summary>
    public string? TryUnprotect(string cipher)
    {
        try { return _protector.Unprotect(cipher); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public async Task<List<(SecretDef Def, string? Value)>> ReadAllAsync(CancellationToken ct)
    {
        var rows = await db.Modes.AsNoTracking().Where(m => m.Key.StartsWith(Secrets.RowPrefix)).ToListAsync(ct);
        var result = new List<(SecretDef, string?)>();
        foreach (var row in rows)
        {
            if (Secrets.Find(row.Key[Secrets.RowPrefix.Length..]) is not { } def)
                continue;
            var value = TryUnprotect(row.Value);
            if (value is null)
                log.LogWarning("Secret {Key} in the database cannot be decrypted: the Data Protection keys (HIVE_DATA/keys) are not the ones it was saved with. Enter it again in Settings", def.Key);
            result.Add((def, value));
        }
        return result;
    }

    /// <summary>For the admin page: never the value, only whether it is set and where it comes from.</summary>
    public async Task<List<SecretStatus>> StatusAsync(CancellationToken ct)
    {
        var rows = (await db.Modes.AsNoTracking().Where(m => m.Key.StartsWith(Secrets.RowPrefix)).ToListAsync(ct)).ToDictionary(m => m.Key);
        return Secrets.All.Select(def =>
        {
            if (rows.TryGetValue(Secrets.RowPrefix + def.Key, out var row))
                return new SecretStatus(def.Key, def.Group, TryUnprotect(row.Value) is null ? SecretSource.Unreadable : SecretSource.Db, row.UpdatedAt, row.UpdatedBy);
            // Not in the database: .env (or an environment variable) may still provide it.
            return new SecretStatus(def.Key, def.Group, string.IsNullOrWhiteSpace(configuration[def.ConfigPath]) ? SecretSource.None : SecretSource.Env, null, null);
        }).ToList();
    }

    public async Task SetAsync(SecretDef def, string? value, string by, CancellationToken ct)
    {
        var key = Secrets.RowPrefix + def.Key;
        var row = await db.Modes.FirstOrDefaultAsync(m => m.Key == key, ct);
        var now = time.GetUtcNow();
        if (string.IsNullOrEmpty(value))
        {
            if (row is not null)
                db.Modes.Remove(row);
        }
        else if (row is null)
            db.Modes.Add(new Mode { Key = key, Value = Protect(value), UpdatedAt = now, UpdatedBy = by });
        else
        {
            row.Value = Protect(value);
            row.UpdatedAt = now;
            row.UpdatedBy = by;
        }
        // The audit row names the secret, never its value.
        Audit.Record(db, now, by, string.IsNullOrEmpty(value) ? "clear_secret" : "set_secret", def.Key, null);
        await db.SaveChangesAsync(ct);
    }
}

public sealed record SecretStatus(string Key, string Group, SecretSource Source, DateTimeOffset? UpdatedAt, string? UpdatedBy);
