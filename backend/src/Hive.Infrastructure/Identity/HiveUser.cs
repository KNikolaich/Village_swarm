using Microsoft.AspNetCore.Identity;

namespace Hive.Infrastructure.Identity;

/// <summary>A person who can log in to the hive UI (spec 6.4 users). Login is <see cref="IdentityUser{TKey}.UserName"/>.</summary>
public class HiveUser : IdentityUser<int>
{
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Roles from spec 6.4. v1 has a single admin; the others are ready for family members and guests.</summary>
public static class HiveRoles
{
    public const string Admin = "admin";
    public const string Member = "member";
    public const string Viewer = "viewer";

    public static readonly string[] All = [Admin, Member, Viewer];

    /// <summary>Policy: may change things (ack alarms, arm, send commands). Viewers only look.</summary>
    public const string CanControl = "can-control";

    public const string AdminOnly = "admin-only";
}
