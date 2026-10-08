using System.Security.Claims;
using System.Text;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Hive.Modules.Users;

public sealed record LoginRequest(string Login, string Password, bool RememberMe = true);

public sealed record TotpLoginRequest(string Code, bool RememberMe = true, bool RememberDevice = false);

public sealed record TotpCodeRequest(string Code);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public enum LoginStatus
{
    Ok,
    TotpRequired,
    Failed,
    LockedOut,
}

public sealed record LoginResult(LoginStatus Status, MeDto? User = null);

public sealed record MeDto(int Id, string Login, string? DisplayName, IReadOnlyList<string> Roles, bool TotpEnabled);

public sealed record TotpSetupDto(string SharedKey, string AuthenticatorUri);

public sealed record RecoveryCodesDto(IReadOnlyList<string> RecoveryCodes);

/// <summary>Login flow on ASP.NET Identity: password, then TOTP when enabled. Session is the Identity cookie.</summary>
public sealed class AuthService(SignInManager<HiveUser> signIn, UserManager<HiveUser> users)
{
    private const string Issuer = "Village Swarm";

    public async Task<LoginResult> LoginAsync(LoginRequest request)
    {
        var result = await signIn.PasswordSignInAsync(request.Login, request.Password, request.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
            return new(LoginStatus.Ok, await MeAsync(await users.FindByNameAsync(request.Login)));
        if (result.RequiresTwoFactor)
            return new(LoginStatus.TotpRequired);
        return new(result.IsLockedOut ? LoginStatus.LockedOut : LoginStatus.Failed);
    }

    public async Task<LoginResult> LoginTotpAsync(TotpLoginRequest request)
    {
        var code = Normalize(request.Code);
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
            return new(LoginStatus.Failed);

        // Six digits: authenticator app. Anything else: a recovery code (stored as "xxxxx-xxxxx").
        var result = code.Length == 6 && code.All(char.IsAsciiDigit)
            ? await signIn.TwoFactorAuthenticatorSignInAsync(code, request.RememberMe, request.RememberDevice)
            : await signIn.TwoFactorRecoveryCodeSignInAsync(request.Code.Trim());
        if (result.Succeeded)
            return new(LoginStatus.Ok, await MeAsync(user));
        return new(result.IsLockedOut ? LoginStatus.LockedOut : LoginStatus.Failed);
    }

    public Task LogoutAsync() => signIn.SignOutAsync();

    public async Task<MeDto?> GetMeAsync(ClaimsPrincipal principal) =>
        await users.GetUserAsync(principal) is { } user ? await MeAsync(user) : null;

    public async Task<Dictionary<string, string[]>?> ChangePasswordAsync(ClaimsPrincipal principal, ChangePasswordRequest request)
    {
        var user = await users.GetUserAsync(principal) ?? throw new InvalidOperationException("No current user");
        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
            return Errors(result);
        await signIn.RefreshSignInAsync(user);
        return null;
    }

    /// <summary>Creates (or returns the pending) authenticator key; TOTP is enabled only after a code is confirmed.</summary>
    public async Task<TotpSetupDto> SetupTotpAsync(ClaimsPrincipal principal)
    {
        var user = await users.GetUserAsync(principal) ?? throw new InvalidOperationException("No current user");
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key) || !user.TwoFactorEnabled)
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
        }
        var label = Uri.EscapeDataString($"{Issuer}:{user.UserName}");
        var uri = $"otpauth://totp/{label}?secret={key}&issuer={Uri.EscapeDataString(Issuer)}&digits=6";
        return new TotpSetupDto(FormatKey(key!), uri);
    }

    public async Task<RecoveryCodesDto?> EnableTotpAsync(ClaimsPrincipal principal, string code)
    {
        var user = await users.GetUserAsync(principal) ?? throw new InvalidOperationException("No current user");
        var valid = await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, Normalize(code));
        if (!valid)
            return null;
        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        return new RecoveryCodesDto(codes?.ToList() ?? []);
    }

    public async Task<bool> DisableTotpAsync(ClaimsPrincipal principal, string code)
    {
        var user = await users.GetUserAsync(principal) ?? throw new InvalidOperationException("No current user");
        if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, Normalize(code)))
            return false;
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        return true;
    }

    private async Task<MeDto> MeAsync(HiveUser? user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new MeDto(user.Id, user.UserName!, user.DisplayName, (await users.GetRolesAsync(user)).ToList(), user.TwoFactorEnabled);
    }

    private static string Normalize(string code) => code.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

    private static string FormatKey(string key)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
            sb.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
        return sb.ToString().TrimEnd().ToLowerInvariant();
    }

    private static Dictionary<string, string[]> Errors(IdentityResult result) =>
        result.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
}
