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
/// Which certificate of the city this node insists on. The city's certificate is self-signed (Caddy "tls internal") and is replaced every
/// few hours, so a fingerprint learned from it would refuse the city after the first renewal; therefore nothing is learned. Only a
/// fingerprint typed in Settings (<c>certSha256</c> in <c>failover.city</c>, for a certificate that does not rotate) is enforced.
/// Without it the way there is the WireGuard tunnel and the key made from the shared secret proves who asks.
/// </summary>
public sealed class CityPin(IServiceScopeFactory scopes, TimeProvider time)
{
    /// <summary>Where an earlier version kept a learned fingerprint: not read any more, only removed by <see cref="ForgetAsync"/>.</summary>
    public const string LearnedKey = "failover.city.pin";

    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
    private readonly object gate = new();

    private string? _url;
    private string? _explicit;
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

    /// <summary>Reads the city setting (at most every 30 seconds): call before talking to the city.</summary>
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
        var setting = await db.Modes.AsNoTracking().Where(m => m.Key == PhotoReplicator.UrlKey).Select(m => m.Value).FirstOrDefaultAsync(ct);
        Load(setting, now);
    }

    /// <summary>Takes the stored values in (tests call this instead of reading the database).</summary>
    internal void Load(string? citySetting, DateTimeOffset now)
    {
        string? explicitPin = null;
        if (citySetting is not null)
        {
            using var doc = JsonDocument.Parse(citySetting);
            if (doc.RootElement.TryGetProperty("certSha256", out var p) && p.ValueKind == JsonValueKind.String)
                explicitPin = Normalize(p.GetString());
        }
        lock (gate)
        {
            _url = PhotoReplicator.CityUrl(citySetting);
            _explicit = explicitPin;
            _loadedAt = now;
        }
    }

    /// <summary>Called while the TLS connection is set up: true to go on. With no typed fingerprint every certificate of the city is let in.</summary>
    public bool Accept(string fingerprint)
    {
        lock (gate)
        {
            _seen = fingerprint;
            var pinned = _explicit;
            return pinned is null || string.Equals(pinned, fingerprint, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Removes a fingerprint an earlier version had learned (it is not used any more).</summary>
    public async Task ForgetAsync(HiveDbContext db, CancellationToken ct)
    {
        await db.Modes.Where(m => m.Key == LearnedKey).ExecuteDeleteAsync(ct);
        lock (gate)
        {
            _seen = null;
        }
    }

    public CityPinDto Describe()
    {
        lock (gate)
        {
            var pinned = _explicit;
            // "Seen" is for a refusal: what the city showed instead of the typed fingerprint.
            return new CityPinDto(pinned, pinned is not null ? "settings" : "none", pinned is not null && _seen is not null && _seen != pinned ? _seen : null);
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
