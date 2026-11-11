using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Modules.Cluster;

/// <summary>What the Settings card shows of the city's certificate pin.</summary>
public sealed record CityPinDto(string? Pinned, string Source, string? Seen);

/// <summary>
/// Which certificate of the city this node trusts. The city's certificate is self-signed, so it is not checked against any authority;
/// instead its SHA-256 fingerprint is pinned: either typed in Settings (<c>certSha256</c> in <c>failover.city</c>) or, when that is empty,
/// learned on the first connection (trust on first use) and kept in <see cref="LearnedKey"/> together with the address it was learned for.
/// A different certificate is refused (the key made from the shared secret still proves who asks, but only the pin proves who answers).
/// </summary>
public sealed class CityPin(IServiceScopeFactory scopes, TimeProvider time)
{
    public const string LearnedKey = "failover.city.pin";

    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
    private readonly object gate = new();

    private string? _url;
    private string? _explicit;
    private string? _learned;
    private string? _learnedUrl;
    private string? _seen;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    /// <summary>"AA:BB:..", "aabb.." and spaces all mean the same 64 hex digits; anything else is not a fingerprint.</summary>
    public static string? Normalize(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            return null;
        var hex = new string(fingerprint.Where(c => c is not (':' or ' ' or '-')).ToArray()).ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexStringLower(new X509Certificate2(certificate).GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256));

    /// <summary>Reads the setting and the learned pin (at most every 30 seconds): call before talking to the city.</summary>
    public async Task EnsureFreshAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            if (now - _loadedAt < Fresh)
                return;
        }
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var rows = await db.Modes.AsNoTracking().Where(m => m.Key == PhotoReplicator.UrlKey || m.Key == LearnedKey).ToDictionaryAsync(m => m.Key, m => m.Value, ct);
        Load(rows.GetValueOrDefault(PhotoReplicator.UrlKey), rows.GetValueOrDefault(LearnedKey), now);
    }

    /// <summary>Takes the stored values in (tests call this instead of reading the database).</summary>
    internal void Load(string? citySetting, string? learnedJson, DateTimeOffset now)
    {
        string? explicitPin = null;
        string? learned = null, learnedUrl = null;
        if (citySetting is not null)
        {
            using var doc = JsonDocument.Parse(citySetting);
            if (doc.RootElement.TryGetProperty("certSha256", out var p) && p.ValueKind == JsonValueKind.String)
                explicitPin = Normalize(p.GetString());
        }
        if (learnedJson is not null)
        {
            using var doc = JsonDocument.Parse(learnedJson);
            learned = doc.RootElement.TryGetProperty("sha256", out var s) ? Normalize(s.GetString()) : null;
            learnedUrl = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
        }
        lock (gate)
        {
            _url = PhotoReplicator.CityUrl(citySetting);
            _explicit = explicitPin;
            // A pin learned for another address says nothing about this one.
            _learned = learned;
            _learnedUrl = learnedUrl;
            _loadedAt = now;
        }
    }

    private string? Effective => _explicit ?? (_learned is not null && _learnedUrl == _url ? _learned : null);

    /// <summary>Called while the TLS connection is set up: true to go on. With no pin the first certificate seen is remembered.</summary>
    public bool Accept(string fingerprint)
    {
        bool learnNow = false;
        string? url;
        bool ok;
        lock (gate)
        {
            _seen = fingerprint;
            var pinned = Effective;
            ok = pinned is null || string.Equals(pinned, fingerprint, StringComparison.OrdinalIgnoreCase);
            url = _url;
            if (pinned is null && url is not null)
            {
                _learned = fingerprint;
                _learnedUrl = url;
                learnNow = true;
            }
        }
        if (learnNow)
            _ = Task.Run(() => PersistAsync(url!, fingerprint));
        return ok;
    }

    private async Task PersistAsync(string url, string fingerprint)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
            var value = JsonSerializer.Serialize(new { url, sha256 = fingerprint });
            var row = await db.Modes.FirstOrDefaultAsync(m => m.Key == LearnedKey);
            if (row is null)
                db.Modes.Add(new Mode { Key = LearnedKey, Value = value, UpdatedAt = time.GetUtcNow(), UpdatedBy = "city-pin" });
            else
            {
                row.Value = value;
                row.UpdatedAt = time.GetUtcNow();
            }
            await db.SaveChangesAsync();
        }
        catch (Exception)
        {
            // Not stored: the next start learns again (and refuses a different one only if this one was stored).
        }
    }

    /// <summary>Forgets what was learned: the next connection pins whatever certificate the city shows then (the city got a new one).</summary>
    public async Task ForgetAsync(HiveDbContext db, CancellationToken ct)
    {
        await db.Modes.Where(m => m.Key == LearnedKey).ExecuteDeleteAsync(ct);
        lock (gate)
        {
            _learned = null;
            _learnedUrl = null;
            _seen = null;
        }
    }

    public CityPinDto Describe()
    {
        lock (gate)
        {
            var pinned = Effective;
            return new CityPinDto(pinned, _explicit is not null ? "settings" : pinned is not null ? "learned" : "none", _seen is not null && _seen != pinned ? _seen : null);
        }
    }
}

/// <summary>
/// How one node speaks HTTP to the other. The city's certificate is self-signed and the way there is the router's tunnel, so no authority
/// is asked; the certificate has to be the one pinned (<see cref="CityPin"/>), and what proves who is asking is the key made from the secret
/// the two nodes share (see PhotoReplicaPolicy, NotesMasterPolicy).
/// </summary>
public static class CityHttp
{
    public static HttpMessageHandler Handler(TimeSpan connectTimeout, CityPin pin) => new SocketsHttpHandler
    {
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null && pin.Accept(CityPin.Fingerprint(certificate)),
        },
        ConnectTimeout = connectTimeout,
        // A kept connection is not asked again, so a changed pin takes hold only on a new one.
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
}
