using System.Security.Claims;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Modules.Users;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Password of the first admin when the users table is empty; generated and logged when not set.</summary>
    public string? BootstrapAdminPassword { get; set; }

    public string BootstrapAdminLogin { get; set; } = "admin";

    /// <summary>How long "remember me" keeps the session (phone at night should not ask for TOTP every time).</summary>
    public int SessionDays { get; set; } = 30;

    /// <summary>Requests to /api/auth/* per minute per client IP (on top of the per-account lockout).</summary>
    public int AuthRequestsPerMinute { get; set; } = 20;
}

/// <summary>Users, login with cookie + TOTP, roles (spec 6.5 /api/auth, /api/me; spec 12).</summary>
public static class UsersModule
{
    public const string Name = "users";
    public const string AuthRateLimit = "auth";

    public static IServiceCollection AddUsersModule(this IServiceCollection services, IConfiguration configuration)
    {
        var auth = configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));

        services.AddIdentity<HiveUser, IdentityRole<int>>(o =>
            {
                o.Stores.SchemaVersion = IdentitySchemaVersions.Version3; // passkeys table, used later
                o.Password.RequiredLength = 10;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireDigit = false; // length matters more; generated passwords are word-like
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.AllowedForNewUsers = true;
                o.User.RequireUniqueEmail = false;
            })
            .AddEntityFrameworkStores<HiveDbContext>()
            .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = "hive_session";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict; // with JSON-only APIs this is the CSRF protection
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromDays(auth.SessionDays);
            o.SlidingExpiration = true;
            // An API answers 401/403 instead of redirecting to a login page.
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        });
        services.ConfigureExternalCookie(o => o.Cookie.SameSite = SameSiteMode.Strict);

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(HiveRoles.CanControl, p => p.RequireRole(HiveRoles.Admin, HiveRoles.Member))
            .AddPolicy(HiveRoles.AdminOnly, p => p.RequireRole(HiveRoles.Admin));

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(AuthRateLimit, ctx => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = auth.AuthRequestsPerMinute, Window = TimeSpan.FromMinutes(1) }));
        });

        services.AddScoped<AuthService>();
        services.AddHostedService<AdminBootstrap>();
        return services;
    }

    public static IEndpointRouteBuilder MapUsersEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").AllowAnonymous().RequireRateLimiting(AuthRateLimit).WithTags("auth");
        auth.MapPost("/login", async Task<Results<Ok<LoginResult>, UnauthorizedHttpResult, ProblemHttpResult>> (LoginRequest request, AuthService s) =>
            await s.LoginAsync(request) switch
            {
                { Status: LoginStatus.Ok or LoginStatus.TotpRequired } r => TypedResults.Ok(r),
                { Status: LoginStatus.LockedOut } => TypedResults.Problem("Слишком много попыток, вход заблокирован на 15 минут", statusCode: StatusCodes.Status423Locked),
                _ => TypedResults.Unauthorized(),
            });
        auth.MapPost("/totp", async Task<Results<Ok<LoginResult>, UnauthorizedHttpResult, ProblemHttpResult>> (TotpLoginRequest request, AuthService s) =>
            await s.LoginTotpAsync(request) switch
            {
                { Status: LoginStatus.Ok } r => TypedResults.Ok(r),
                { Status: LoginStatus.LockedOut } => TypedResults.Problem("Слишком много попыток, вход заблокирован на 15 минут", statusCode: StatusCodes.Status423Locked),
                _ => TypedResults.Unauthorized(),
            });
        auth.MapPost("/logout", async (AuthService s) =>
        {
            await s.LogoutAsync();
            return TypedResults.NoContent();
        });

        var me = app.MapGroup("/api/me").WithTags("me");
        me.MapGet("", async Task<Results<Ok<MeDto>, UnauthorizedHttpResult>> (ClaimsPrincipal user, AuthService s) =>
            await s.GetMeAsync(user) is { } dto ? TypedResults.Ok(dto) : TypedResults.Unauthorized());
        me.MapPost("/password", async Task<Results<NoContent, ValidationProblem>> (ChangePasswordRequest request, ClaimsPrincipal user, AuthService s) =>
            await s.ChangePasswordAsync(user, request) is { } errors ? TypedResults.ValidationProblem(errors) : TypedResults.NoContent());
        me.MapPost("/totp/setup", async (ClaimsPrincipal user, AuthService s) => TypedResults.Ok(await s.SetupTotpAsync(user)));
        me.MapPost("/totp/enable", async Task<Results<Ok<RecoveryCodesDto>, ValidationProblem>> (TotpCodeRequest request, ClaimsPrincipal user, AuthService s) =>
            await s.EnableTotpAsync(user, request.Code) is { } codes
                ? TypedResults.Ok(codes)
                : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["Неверный код"] }));
        me.MapPost("/totp/disable", async Task<Results<NoContent, ValidationProblem>> (TotpCodeRequest request, ClaimsPrincipal user, AuthService s) =>
            await s.DisableTotpAsync(user, request.Code)
                ? TypedResults.NoContent()
                : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["Неверный код"] }));
        return app;
    }
}
