using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.IpCams;

/// <summary>
/// What a Dahua NVR says when it refuses a login: <c>Error {"ErrorCode":268632081,"Result":false,"RmLock":1320,"RmLogin":0}</c>.
/// 0x10030011 is "user locked", RmLock the seconds until it unlocks, RmLogin the attempts left before it locks.
/// </summary>
public sealed record NvrAuthError(int Status, long? Code, int? RmLock, int? RmLogin)
{
    public const long LockedCode = 0x10030011;

    public bool Locked => Code == LockedCode || RmLogin == 0 || RmLock > 0;

    public static NvrAuthError Parse(int status, string? body)
    {
        var start = body?.IndexOf('{', StringComparison.Ordinal) ?? -1;
        if (start < 0)
            return new NvrAuthError(status, null, null, null);
        try
        {
            using var json = JsonDocument.Parse(body![start..]);
            var root = json.RootElement;
            return new NvrAuthError(status, Long(root, "ErrorCode"), (int?)Long(root, "RmLock"), (int?)Long(root, "RmLogin"));
        }
        catch (JsonException)
        {
            return new NvrAuthError(status, null, null, null);
        }
    }

    private static long? Long(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : null;

    public override string ToString()
    {
        var parts = new List<string> { $"HTTP {Status.ToString(CultureInfo.InvariantCulture)}" };
        if (Code is { } c)
            parts.Add($"code 0x{c:X8}");
        if (RmLogin is { } left)
            parts.Add($"{left} attempts left");
        if (RmLock is { } s && s > 0)
            parts.Add($"locked for {s} s");
        return string.Join(", ", parts);
    }
}

/// <summary>Why the hive does not talk to an NVR right now. Kind: locked (by the NVR), paused (wrong login twice), wait (after a refusal).</summary>
public sealed record NvrBlock(string Kind, DateTimeOffset? Until, int? AttemptsLeft)
{
    public string Message(DateTimeOffset now) => Kind switch
    {
        NvrLogins.Locked => $"the NVR locked the login, {Minutes(now)} min left",
        NvrLogins.Paused => "the NVR refused the login twice; re-enter it in the settings",
        _ => $"the NVR refused the login{(AttemptsLeft is { } n ? $" ({n} attempts left)" : "")}, next try in {Seconds(now)} s",
    };

    private int Minutes(DateTimeOffset now) => (int)Math.Ceiling(Math.Max(0, ((Until ?? now) - now).TotalMinutes));
    private int Seconds(DateTimeOffset now) => (int)Math.Ceiling(Math.Max(0, ((Until ?? now) - now).TotalSeconds));
}

/// <summary>State of an NVR's login for the settings page: ok, locked, paused or wait (see <see cref="NvrBlock"/>).</summary>
public sealed record NvrLoginDto(string State, DateTimeOffset? Until, int? AttemptsLeft);

/// <summary>
/// Keeps the hive from locking the family out of their NVR: Dahua locks an account after a few wrong logins (for half an hour),
/// and every request with a stale password, the event stream's reconnects included, counts. Once the NVR says it is locked,
/// nothing goes to it until it unlocks (plus 30 s); a refused login waits a minute before the same password is tried again,
/// and after two refusals in a row the hive stops until someone saves the login again. RTSP uses the same account, so the
/// ffmpeg fallback asks here too.
/// </summary>
public sealed class NvrLogins(IOptions<IpCamOptions> options, TimeProvider time, ILogger<NvrLogins> logger)
{
    public const string Locked = "locked";
    public const string Paused = "paused";
    public const string Wait = "wait";
    private static readonly TimeSpan UnlockMargin = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lockedUntil = new();
    private readonly ConcurrentDictionary<string, Login> _logins = new();

    private sealed record Login(string PasswordHash, int Failures, DateTimeOffset? RetryAt, int? AttemptsLeft);

    private static string Device(NvrEndpoint e) => $"{e.Host}:{e.Port.ToString(CultureInfo.InvariantCulture)}";
    private static string Account(NvrEndpoint e) => $"{Device(e)}\n{e.Username}";
    private static string Hash(string password) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    /// <summary>Null when a request may go to the NVR now.</summary>
    public NvrBlock? Blocked(NvrEndpoint e)
    {
        var now = time.GetUtcNow();
        if (_lockedUntil.TryGetValue(Device(e), out var until))
        {
            if (until > now)
                return new NvrBlock(Locked, until, 0);
            _lockedUntil.TryRemove(Device(e), out _);
        }
        if (!_logins.TryGetValue(Account(e), out var login) || login.PasswordHash != Hash(e.Password))
            return null; // another password: the user is trying a new one
        if (login.Failures >= 2)
            return new NvrBlock(Paused, null, login.AttemptsLeft);
        return login.RetryAt > now ? new NvrBlock(Wait, login.RetryAt, login.AttemptsLeft) : null;
    }

    public void Failed(NvrEndpoint e, NvrAuthError error)
    {
        var now = time.GetUtcNow();
        if (error.Locked)
        {
            var until = now + TimeSpan.FromSeconds(Math.Max(error.RmLock ?? 0, 0)) + UnlockMargin;
            if (!_lockedUntil.TryGetValue(Device(e), out var known) || known < now)
                logger.LogWarning("NVR {Host}: the login {User} is locked ({Error}); no requests until {Until:HH:mm:ss} UTC",
                    Device(e), e.Username, error, until);
            _lockedUntil[Device(e)] = until;
            return;
        }
        var hash = Hash(e.Password);
        var login = _logins.AddOrUpdate(Account(e),
            _ => new Login(hash, 1, now.AddSeconds(options.Value.AuthRetryS), error.RmLogin),
            (_, old) => new Login(hash, old.PasswordHash == hash ? old.Failures + 1 : 1, now.AddSeconds(options.Value.AuthRetryS), error.RmLogin));
        if (login.Failures >= 2)
            logger.LogWarning("NVR {Host}: the login {User} was refused twice ({Error}); stopped until it is saved again", Device(e), e.Username, error);
        else
            logger.LogWarning("NVR {Host}: the login {User} was refused ({Error}); next try in {Seconds} s", Device(e), e.Username, error, options.Value.AuthRetryS);
    }

    public void Succeeded(NvrEndpoint e)
    {
        if (_logins.TryRemove(Account(e), out _))
            logger.LogInformation("NVR {Host}: the login {User} works again", Device(e), e.Username);
    }

    /// <summary>The login was saved again: refusals of the old one are forgotten (a lock by the NVR stays until it ends).</summary>
    public void Forget(string host, int port)
    {
        var prefix = $"{host}:{port.ToString(CultureInfo.InvariantCulture)}\n";
        foreach (var key in _logins.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            _logins.TryRemove(key, out _);
    }

    public string Explain(NvrBlock block) => block.Message(time.GetUtcNow());

    public NvrLoginDto State(NvrEndpoint e) =>
        Blocked(e) is { } b ? new NvrLoginDto(b.Kind, b.Until, b.AttemptsLeft) : new NvrLoginDto("ok", null, null);
}
