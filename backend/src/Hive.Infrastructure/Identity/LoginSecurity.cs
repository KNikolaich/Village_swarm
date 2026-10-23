namespace Hive.Infrastructure.Identity;

/// <summary>
/// A browser that passed the second factor for a user: next logins from it need only the password.
/// The browser holds a random token in the hive_device cookie; only its SHA-256 is stored.
/// </summary>
public class TrustedDevice
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string TokenHash { get; set; } = "";

    /// <summary>"Chrome · Android" from the User-Agent, for the list in settings and the Telegram notice.</summary>
    public string? Name { get; set; }

    public string? LastIp { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public enum AuthChallengeKind
{
    /// <summary>Second step of a login from a new device: a code sent to the user's Telegram chats.</summary>
    Login,

    /// <summary>Forgotten password: a code sent to the user's email.</summary>
    Recovery,
}

/// <summary>A one-time code waiting to be typed. Id is the SHA-256 of a random token (login: the hive_2fa cookie).</summary>
public class AuthChallenge
{
    public string Id { get; set; } = "";
    public int UserId { get; set; }
    public AuthChallengeKind Kind { get; set; }

    /// <summary>SHA-256 of Id + code; empty when only the authenticator app can answer.</summary>
    public string CodeHash { get; set; } = "";

    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public bool RememberMe { get; set; }
}

/// <summary>Wrong passwords and codes from one device (or one IP for unknown devices) and how long it waits.</summary>
public class LoginThrottle
{
    public string Key { get; set; } = "";
    public int Failures { get; set; }
    public DateTimeOffset LastFailureAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}

/// <summary>Messages about sign-ins to the user's own Telegram chats (implemented by the notify module).</summary>
public interface IAuthNotifier
{
    /// <summary>Sends HTML text to every chat linked to the user; false when none got it.</summary>
    Task<bool> SendAsync(int userId, string html, CancellationToken ct);
}

public sealed class NoAuthNotifier : IAuthNotifier
{
    public Task<bool> SendAsync(int userId, string html, CancellationToken ct) => Task.FromResult(false);
}
