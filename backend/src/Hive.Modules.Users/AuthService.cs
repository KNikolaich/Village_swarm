using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Users;

public sealed record LoginRequest(string Login, string Password, bool RememberMe = true);

/// <summary>Second step: the code from Telegram, from the authenticator app, or a recovery code.</summary>
public sealed record TotpLoginRequest(string Code, bool RememberMe = true, bool RememberDevice = true);

public sealed record TotpCodeRequest(string Code);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record RecoverRequest(string Login);

public sealed record RecoverConfirmRequest(string Login, string Code, string NewPassword);

public sealed record SetEmailRequest(string? Email, string CurrentPassword);

public enum LoginStatus
{
    Ok,

    /// <summary>New device, the account has an authenticator app and Telegram could not take a code.</summary>
    TotpRequired,

    /// <summary>New device: a code went to the user's Telegram (the authenticator app works too when <see cref="LoginResult.TotpAllowed"/>).</summary>
    CodeRequired,

    Failed,
    LockedOut,

    /// <summary>A login from outside to an account with neither Telegram nor an authenticator app.</summary>
    SecondFactorMissing,
}

public sealed record LoginResult(LoginStatus Status, MeDto? User = null, bool TotpAllowed = false, DateTimeOffset? LockedUntil = null);

public enum RecoveryStatus
{
    Ok,
    Failed,
    LockedOut,
    Invalid,
}

public sealed record RecoveryResult(RecoveryStatus Status, DateTimeOffset? LockedUntil = null, Dictionary<string, string[]>? Errors = null);

public sealed record AuthOptionsDto(bool RecoveryByEmail);

public sealed record MeDto(int Id, string Login, string? DisplayName, IReadOnlyList<string> Roles, bool TotpEnabled, string? Email);

public sealed record TotpSetupDto(string SharedKey, string AuthenticatorUri);

public sealed record RecoveryCodesDto(IReadOnlyList<string> RecoveryCodes);

/// <summary>
/// Login on ASP.NET Identity (session = the Identity cookie). A known device needs only the password; a new one
/// also a code from the user's Telegram (or the authenticator app). Wrong passwords and codes pause the device
/// (<see cref="LoginThrottlePolicy"/>). A forgotten password is reset with a code sent to the user's email.
/// </summary>
public sealed class AuthService(
    SignInManager<HiveUser> signIn,
    UserManager<HiveUser> users,
    HiveDbContext db,
    IHttpContextAccessor httpAccessor,
    LoginThrottleService throttle,
    TrustedDeviceService devices,
    IAuthNotifier notifier,
    IMailSender mail,
    TimeProvider time,
    IOptions<AuthOptions> options,
    ILogger<AuthService> logger)
{
    private const string Issuer = "Village Swarm";

    /// <summary>Set by Caddy on the site the router forwards from the internet (deploy/caddy/Caddyfile).</summary>
    public const string ExternalHeader = "X-Hive-External";

    public const string ChallengeCookie = "hive_2fa";

    private const int MaxCodeAttempts = 5;

    // No 0/O, 1/I: the code is read from a letter and typed on a phone.
    private const string RecoveryAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private HttpContext Http => httpAccessor.HttpContext ?? throw new InvalidOperationException("No HTTP request");

    public static bool IsExternal(HttpContext http) => http.Request.Headers.ContainsKey(ExternalHeader);

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var http = Http;
        var key = await throttle.KeyAsync(http, ct);
        if (await throttle.LockedUntilAsync(key, ct) is { } until)
            return new(LoginStatus.LockedOut, LockedUntil: until);

        var user = await users.FindByNameAsync(request.Login.Trim());
        var check = user is null ? SignInResult.Failed : await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: false);
        if (check.IsLockedOut)
            return new(LoginStatus.LockedOut, LockedUntil: user!.LockoutEnd);
        if (!check.Succeeded)
            return await FailedAsync(key, user, "неверный пароль", ct);

        if (await devices.TouchAsync(user!, http, ct))
            return await CompleteAsync(user!, key, request.RememberMe, trustDevice: false, ct);

        var external = IsExternal(http);
        if (options.Value.NewDeviceCode == NewDeviceCodeMode.External && !external)
            return await CompleteAsync(user!, key, request.RememberMe, trustDevice: false, ct);

        // New device: a code to the user's Telegram chats.
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var challenge = new AuthChallenge
        {
            Id = LoginThrottleService.Sha256(token),
            UserId = user!.Id,
            Kind = AuthChallengeKind.Login,
            ExpiresAt = time.GetUtcNow().AddMinutes(options.Value.CodeMinutes),
            RememberMe = request.RememberMe,
        };
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var sent = await notifier.SendAsync(user.Id,
            $"🔑 Код для входа в Улей: <code>{code}</code>\n" +
            $"Вход «{Html(user.UserName)}» с нового устройства: {Html(DeviceLine(http))}.\n" +
            $"Код действует {options.Value.CodeMinutes} минут. Если это не вы, никому его не сообщайте и смените пароль.", ct);
        if (sent)
            challenge.CodeHash = CodeHash(challenge.Id, code);

        if (!sent && !user.TwoFactorEnabled)
        {
            if (external)
                return new(LoginStatus.SecondFactorMissing);
            // At home without Telegram (not linked yet, or the bot is down): the password is enough, the device stays new.
            logger.LogWarning("Login of {Login} from a new device without a second factor (no Telegram chat got the code)", user.UserName);
            return await CompleteAsync(user, key, request.RememberMe, trustDevice: false, ct);
        }

        await db.AuthChallenges.Where(c => c.ExpiresAt < time.GetUtcNow()).ExecuteDeleteAsync(ct);
        db.AuthChallenges.Add(challenge);
        await db.SaveChangesAsync(ct);
        http.Response.Cookies.Append(ChallengeCookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth",
            MaxAge = TimeSpan.FromMinutes(options.Value.CodeMinutes),
        });
        return new(sent ? LoginStatus.CodeRequired : LoginStatus.TotpRequired, TotpAllowed: user.TwoFactorEnabled);
    }

    public async Task<LoginResult> LoginTotpAsync(TotpLoginRequest request, CancellationToken ct = default)
    {
        var http = Http;
        var key = await throttle.KeyAsync(http, ct);
        if (await throttle.LockedUntilAsync(key, ct) is { } until)
            return new(LoginStatus.LockedOut, LockedUntil: until);

        var challengeId = http.Request.Cookies.TryGetValue(ChallengeCookie, out var token) ? LoginThrottleService.Sha256(token) : null;
        var challenge = challengeId is null
            ? null
            : await db.AuthChallenges.FirstOrDefaultAsync(c => c.Id == challengeId && c.Kind == AuthChallengeKind.Login, ct);
        if (challenge is null || challenge.ExpiresAt < time.GetUtcNow())
            return new(LoginStatus.Failed);
        var user = await users.FindByIdAsync(challenge.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (user is null)
            return new(LoginStatus.Failed);

        var code = Normalize(request.Code);
        var ok = challenge.CodeHash.Length > 0 && FixedTimeEquals(CodeHash(challenge.Id, code), challenge.CodeHash);
        // Six digits: authenticator app. Anything else: a recovery code (stored as "xxxxx-xxxxx").
        if (!ok && user.TwoFactorEnabled)
            ok = code.Length == 6 && code.All(char.IsAsciiDigit)
                ? await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code)
                : (await users.RedeemTwoFactorRecoveryCodeAsync(user, request.Code.Trim())).Succeeded;

        if (!ok)
        {
            if (++challenge.Attempts >= MaxCodeAttempts)
                db.AuthChallenges.Remove(challenge);
            await db.SaveChangesAsync(ct);
            return await FailedAsync(key, user, "неверный код входа", ct);
        }

        db.AuthChallenges.Remove(challenge);
        await db.SaveChangesAsync(ct);
        http.Response.Cookies.Delete(ChallengeCookie, new CookieOptions { Path = "/api/auth" });
        return await CompleteAsync(user, key, challenge.RememberMe && request.RememberMe, request.RememberDevice, ct);
    }

    public Task LogoutAsync() => signIn.SignOutAsync();

    public AuthOptionsDto Describe() => new(mail.Configured);

    /// <summary>Sends a recovery code to the account's email. Says nothing about whether the login exists.</summary>
    public async Task<RecoveryResult> StartRecoveryAsync(RecoverRequest request, CancellationToken ct = default)
    {
        var key = await throttle.KeyAsync(Http, ct);
        if (await throttle.LockedUntilAsync(key, ct) is { } until)
            return new(RecoveryStatus.LockedOut, until);
        var user = await users.FindByNameAsync(request.Login.Trim());
        if (user?.Email is not { Length: > 0 } email || !mail.Configured)
            return new(RecoveryStatus.Ok);

        var now = time.GetUtcNow();
        var previous = await db.AuthChallenges.Where(c => c.UserId == user.Id && c.Kind == AuthChallengeKind.Recovery).ToListAsync(ct);
        // One letter a minute at most: the button is not a way to flood someone's mailbox.
        if (previous.Any(c => c.ExpiresAt - TimeSpan.FromMinutes(options.Value.RecoveryMinutes) > now - TimeSpan.FromMinutes(1)))
            return new(RecoveryStatus.Ok);
        db.AuthChallenges.RemoveRange(previous);

        var code = RandomNumberGenerator.GetString(RecoveryAlphabet, 8);
        var challenge = new AuthChallenge
        {
            Id = LoginThrottleService.Sha256(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))),
            UserId = user.Id,
            Kind = AuthChallengeKind.Recovery,
            ExpiresAt = now.AddMinutes(options.Value.RecoveryMinutes),
        };
        challenge.CodeHash = CodeHash(challenge.Id, code);
        try
        {
            await mail.SendAsync(email, "Улей: восстановление пароля",
                $"Пароль восстановления для входа «{user.UserName}»: {code}\n\n" +
                $"Введите его на странице входа вместе с новым паролем. Он действует {options.Value.RecoveryMinutes} минут.\n" +
                $"Запрос пришёл с устройства: {DeviceLine(Http)}.\n" +
                "Если вы не просили сменить пароль, просто удалите это письмо.", ct);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or IOException)
        {
            logger.LogError(ex, "Recovery letter to {Login} was not sent", user.UserName);
            return new(RecoveryStatus.Ok);
        }
        db.AuthChallenges.Add(challenge);
        await db.SaveChangesAsync(ct);
        return new(RecoveryStatus.Ok);
    }

    /// <summary>New password by the code from the letter. Forgets the account's trusted devices: next logins need the Telegram code.</summary>
    public async Task<RecoveryResult> ConfirmRecoveryAsync(RecoverConfirmRequest request, CancellationToken ct = default)
    {
        var key = await throttle.KeyAsync(Http, ct);
        if (await throttle.LockedUntilAsync(key, ct) is { } until)
            return new(RecoveryStatus.LockedOut, until);

        var user = await users.FindByNameAsync(request.Login.Trim());
        var challenge = user is null
            ? null
            : await db.AuthChallenges.Where(c => c.UserId == user.Id && c.Kind == AuthChallengeKind.Recovery && c.ExpiresAt > time.GetUtcNow())
                .OrderByDescending(c => c.ExpiresAt).FirstOrDefaultAsync(ct);
        var code = Normalize(request.Code).ToUpperInvariant();
        if (challenge is null || !FixedTimeEquals(CodeHash(challenge.Id, code), challenge.CodeHash))
        {
            if (challenge is not null && ++challenge.Attempts >= MaxCodeAttempts)
                db.AuthChallenges.Remove(challenge);
            await db.SaveChangesAsync(ct);
            var lockedUntil = await throttle.FailAsync(key, ct);
            return lockedUntil is { } l ? new(RecoveryStatus.LockedOut, l) : new(RecoveryStatus.Failed);
        }

        var reset = await users.ResetPasswordAsync(user!, await users.GeneratePasswordResetTokenAsync(user!), request.NewPassword);
        if (!reset.Succeeded)
            return new(RecoveryStatus.Invalid, Errors: Errors(reset));
        db.AuthChallenges.Remove(challenge);
        await db.SaveChangesAsync(ct);
        await devices.ForgetAllAsync(user!.Id, ct);
        await throttle.ResetAsync(key, ct);
        await notifier.SendAsync(user.Id,
            $"🔐 Пароль «{Html(user.UserName)}» сменён по коду из почты ({Html(DeviceLine(Http))}). " +
            "Все устройства забыты: при следующем входе Улей пришлёт код сюда.", ct);
        return new(RecoveryStatus.Ok);
    }

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

    /// <summary>Address for password recovery. A test letter goes out first, so a typo shows up now and not on the day it is needed.</summary>
    public async Task<Dictionary<string, string[]>?> SetEmailAsync(ClaimsPrincipal principal, SetEmailRequest request, CancellationToken ct = default)
    {
        var user = await users.GetUserAsync(principal) ?? throw new InvalidOperationException("No current user");
        if (!await users.CheckPasswordAsync(user, request.CurrentPassword))
            return new() { ["currentPassword"] = ["Неверный пароль"] };
        var email = request.Email?.Trim();
        if (string.IsNullOrEmpty(email))
        {
            await users.SetEmailAsync(user, null);
            return null;
        }
        if (!MailAddress.TryCreate(email, out _))
            return new() { ["email"] = ["Это не похоже на адрес почты"] };
        if (mail.Configured)
        {
            try
            {
                await mail.SendAsync(email, "Улей: адрес для восстановления пароля",
                    $"Этот адрес указан для восстановления пароля входа «{user.UserName}» в Улей. Отвечать на письмо не нужно.", ct);
            }
            catch (Exception ex) when (ex is SmtpException or InvalidOperationException or IOException or FormatException)
            {
                logger.LogWarning(ex, "Test letter to {Email} failed", email);
                return new() { ["email"] = [$"Письмо не отправилось: {ex.Message}"] };
            }
        }
        await users.SetEmailAsync(user, email);
        user.EmailConfirmed = true;
        await users.UpdateAsync(user);
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

    private async Task<LoginResult> CompleteAsync(HiveUser user, string throttleKey, bool rememberMe, bool trustDevice, CancellationToken ct)
    {
        await signIn.SignInAsync(user, isPersistent: rememberMe);
        if (trustDevice)
            await devices.TrustAsync(user, Http, ct);
        await throttle.ResetAsync(throttleKey, ct);
        return new(LoginStatus.Ok, await MeAsync(user));
    }

    private async Task<LoginResult> FailedAsync(string key, HiveUser? user, string what, CancellationToken ct)
    {
        var lockedUntil = await throttle.FailAsync(key, ct);
        if (lockedUntil is null)
            return new(LoginStatus.Failed);
        logger.LogWarning("Login paused until {Until} for {Key} after repeated failures ({What})", lockedUntil, key, what);
        if (user is not null)
        {
            var pause = lockedUntil.Value - time.GetUtcNow() > TimeSpan.FromHours(2) ? "сутки" : "час";
            await notifier.SendAsync(user.Id,
                $"⚠️ Вход «{Html(user.UserName)}»: {what} много раз подряд ({Html(DeviceLine(Http))}). " +
                $"Вход с этого устройства приостановлен на {pause}.", ct);
        }
        return new(LoginStatus.LockedOut, LockedUntil: lockedUntil);
    }

    private async Task<MeDto> MeAsync(HiveUser? user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new MeDto(user.Id, user.UserName!, user.DisplayName, (await users.GetRolesAsync(user)).ToList(), user.TwoFactorEnabled, user.Email);
    }

    private static string DeviceLine(HttpContext http)
    {
        var line = UserAgents.Describe(http.Request.Headers.UserAgent);
        if (LoginThrottleService.ClientIp(http) is { } ip)
            line += $", IP {ip}";
        return IsExternal(http) ? line + ", из интернета" : line;
    }

    /// <summary>Telegram's HTML needs only these escaped; WebUtility would also turn "·" into an entity.</summary>
    private static string Html(string? text) =>
        (text ?? "").Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string CodeHash(string challengeId, string code) => LoginThrottleService.Sha256(challengeId + ":" + code);

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

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
