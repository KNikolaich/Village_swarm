using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hive.Infrastructure.Identity;
using Hive.Modules.Users;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hive.Tests.Integration;

/// <summary>Login, TOTP, the Telegram code on new devices, pauses after wrong passwords, recovery by email, roles.</summary>
[Collection(HiveCollection.Name)]
public sealed class AuthTests(HiveFixture hive)
{
    [Theory]
    [InlineData("/api/events")]
    [InlineData("/api/media?date=2026-10-08")]
    [InlineData("/api/devices")]
    [InlineData("/api/me")]
    public async Task Api_requires_a_session(string url)
    {
        var anonymous = hive.App.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Bootstrap_admin_logs_in_and_out()
    {
        var client = hive.App.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/login", new { login = "admin", password = "wrong-password" })).StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { login = "admin", password = HiveFixture.AdminPassword });
        var result = await login.Content.ReadFromJsonAsync<LoginResultBody>();
        if (result!.Status == "codeRequired") // admin already has Telegram when TelegramTests ran first
        {
            login = await client.PostAsJsonAsync("/api/auth/totp", new { code = hive.LastLoginCode() });
            result = await login.Content.ReadFromJsonAsync<LoginResultBody>();
        }
        Assert.Equal("ok", result!.Status);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("hive_session=", StringComparison.Ordinal) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));

        var me = await client.GetFromJsonAsync<MeDto>("/api/me");
        Assert.Equal(("admin", false), (me!.Login, me.TotpEnabled));
        Assert.Contains(HiveRoles.Admin, me.Roles);

        await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Totp_is_required_after_enabling_it()
    {
        await hive.CreateUserAsync("totp-user", "totp-password-1", HiveRoles.Member);
        var client = await hive.LoginAsync("totp-user", "totp-password-1");

        var setup = await (await client.PostAsync("/api/me/totp/setup", null)).Content.ReadFromJsonAsync<TotpSetupDto>();
        Assert.StartsWith("otpauth://totp/", setup!.AuthenticatorUri);
        var secret = setup.SharedKey.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/me/totp/enable", new { code = "000000" })).StatusCode);
        var enable = await client.PostAsJsonAsync("/api/me/totp/enable", new { code = Totp(secret) });
        var recovery = await enable.Content.ReadFromJsonAsync<RecoveryCodesDto>();
        Assert.Equal(10, recovery!.RecoveryCodes.Count);
        await client.PostAsync("/api/auth/logout", null);

        // Password alone is no longer enough.
        var fresh = hive.App.CreateClient();
        var step1 = await (await fresh.PostAsJsonAsync("/api/auth/login", new { login = "totp-user", password = "totp-password-1" })).Content.ReadFromJsonAsync<LoginResultBody>();
        Assert.Equal("totpRequired", step1!.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.PostAsJsonAsync("/api/auth/totp", new { code = "123456" })).StatusCode);

        // A recovery code works once (the TOTP code was just used and cannot be replayed in the same window).
        var step2 = await fresh.PostAsJsonAsync("/api/auth/totp", new { code = recovery.RecoveryCodes[0] });
        Assert.Equal(HttpStatusCode.OK, step2.StatusCode);
        var me = await fresh.GetFromJsonAsync<MeDto>("/api/me");
        Assert.True(me!.TotpEnabled);
    }

    [Fact]
    public async Task Viewer_can_read_but_not_control()
    {
        await hive.CreateUserAsync("guest", "guest-password-1", HiveRoles.Viewer);
        var viewer = await hive.LoginAsync("guest", "guest-password-1");

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/events")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/events/01J9Z3K7Q8M4N5P6R7S8T9V0WX/ack", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/media/01J9Z3K7Q8M4N5P6R7S8T9V0WX-0/pin", null)).StatusCode);
    }

    [Fact]
    public async Task Wrong_passwords_pause_the_device_and_not_the_account()
    {
        await hive.CreateUserAsync("victim", "victim-password-1", HiveRoles.Member);
        var attacker = Device("10.9.0.1");
        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.PostAsJsonAsync("/api/auth/login", new { login = "victim", password = "nope-nope-nope" })).StatusCode);

        // The fifth wrong password starts an hour's pause; during it even the right password is not checked.
        var fifth = await attacker.PostAsJsonAsync("/api/auth/login", new { login = "victim", password = "nope-nope-nope" });
        Assert.Equal((HttpStatusCode)423, fifth.StatusCode);
        var until = (await fifth.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lockedUntil").GetDateTimeOffset();
        Assert.InRange(until - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));
        Assert.Equal((HttpStatusCode)423, (await attacker.PostAsJsonAsync("/api/auth/login", new { login = "victim", password = "victim-password-1" })).StatusCode);
        Assert.Equal((HttpStatusCode)423, (await attacker.PostAsJsonAsync("/api/auth/recover/confirm", new { login = "victim", code = "AAAAAAAA", newPassword = "whatever-1234" })).StatusCode);

        // The owner on another device is not locked out.
        var owner = Device("10.9.0.2");
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/auth/login", new { login = "victim", password = "victim-password-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task New_device_needs_the_code_from_Telegram()
    {
        const long chat = 7001;
        await hive.CreateUserAsync("tg-user", "tg-user-password", HiveRoles.Member);
        var home = Device("10.8.0.1");
        Assert.Equal("ok", (await LoginStepAsync(home, "tg-user", "tg-user-password")).Status); // no Telegram yet: the password is enough at home
        var link = await (await home.PostAsync("/api/me/telegram/code", null)).Content.ReadFromJsonAsync<JsonElement>();
        hive.Telegram.UserSays(chat, $"/link {link.GetProperty("code").GetString()}");
        await hive.Telegram.WaitForAsync(s => s.ChatId == chat && s.Text?.Contains("Чат привязан") == true, "link confirmation");

        var phone = Device("10.8.0.2", userAgent: "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Mobile Safari/537.36");
        Assert.Equal("codeRequired", (await LoginStepAsync(phone, "tg-user", "tg-user-password")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/me")).StatusCode);
        var message = hive.Telegram.Outbox.Last(s => s.ChatId == chat);
        Assert.Contains("Chrome · Android", message.Text);
        Assert.Contains("10.8.0.2", message.Text);

        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.PostAsJsonAsync("/api/auth/totp", new { code = "000000" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.PostAsJsonAsync("/api/auth/totp", new { code = hive.LastLoginCode() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/me")).StatusCode);

        // Known now: the password alone after logging out.
        await phone.PostAsync("/api/auth/logout", null);
        Assert.Equal("ok", (await LoginStepAsync(phone, "tg-user", "tg-user-password")).Status);
        var devices = await phone.GetFromJsonAsync<List<TrustedDeviceDto>>("/api/me/devices");
        var trusted = Assert.Single(devices!);
        Assert.True(trusted.Current);
        Assert.Equal("Chrome · Android", trusted.Name);

        // Another browser is new again; a forgotten device asks for the code again.
        Assert.Equal("codeRequired", (await LoginStepAsync(Device("10.8.0.3"), "tg-user", "tg-user-password")).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await phone.DeleteAsync($"/api/me/devices/{trusted.Id}")).StatusCode);
        await phone.PostAsync("/api/auth/logout", null);
        Assert.Equal("codeRequired", (await LoginStepAsync(phone, "tg-user", "tg-user-password")).Status);
    }

    [Fact]
    public async Task Login_from_outside_needs_a_second_factor()
    {
        await hive.CreateUserAsync("no-tg-user", "no-tg-password", HiveRoles.Viewer);
        var outside = Device("203.0.113.5", external: true);
        Assert.Equal("secondFactorMissing", (await LoginStepAsync(outside, "no-tg-user", "no-tg-password")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await outside.GetAsync("/api/me")).StatusCode);

        Assert.Equal("ok", (await LoginStepAsync(Device("10.7.0.1"), "no-tg-user", "no-tg-password")).Status);
    }

    [Fact]
    public async Task Forgotten_password_is_reset_with_the_code_from_the_letter()
    {
        await hive.CreateUserAsync("mail-user", "mail-user-password", HiveRoles.Member);
        var client = await hive.LoginAsync("mail-user", "mail-user-password");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/me/email", new { email = "kirill@example.org", currentPassword = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/me/email", new { email = "not an address", currentPassword = "mail-user-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/me/email", new { email = "kirill@example.org", currentPassword = "mail-user-password" })).StatusCode);
        Assert.Equal("kirill@example.org", (await client.GetFromJsonAsync<MeDto>("/api/me"))!.Email);
        Assert.Contains(Letters(), l => l.Contains("To: kirill@example.org", StringComparison.Ordinal) && l.Contains("«mail-user»", StringComparison.Ordinal));

        var stranger = Device("10.6.0.1");
        Assert.True((await stranger.GetFromJsonAsync<AuthOptionsDto>("/api/auth/options"))!.RecoveryByEmail);
        Assert.Equal(HttpStatusCode.NoContent, (await stranger.PostAsJsonAsync("/api/auth/recover", new { login = "nobody-here" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await stranger.PostAsJsonAsync("/api/auth/recover", new { login = "mail-user" })).StatusCode);
        var code = Letters().Select(l => Regex.Match(l, "Пароль восстановления для входа «mail-user»: ([A-Z2-9]{8})")).Single(m => m.Success).Groups[1].Value;

        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsJsonAsync("/api/auth/recover/confirm", new { login = "mail-user", code = "ZZZZZZZZ", newPassword = "brand-new-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await stranger.PostAsJsonAsync("/api/auth/recover/confirm", new { login = "mail-user", code, newPassword = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await stranger.PostAsJsonAsync("/api/auth/recover/confirm", new { login = "mail-user", code = code.ToLowerInvariant(), newPassword = "brand-new-password" })).StatusCode);
        // The code works once.
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsJsonAsync("/api/auth/recover/confirm", new { login = "mail-user", code, newPassword = "another-new-password" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Device("10.6.0.2").PostAsJsonAsync("/api/auth/login", new { login = "mail-user", password = "mail-user-password" })).StatusCode);
        Assert.Equal("ok", (await LoginStepAsync(Device("10.6.0.3"), "mail-user", "brand-new-password")).Status);
    }

    [Fact]
    public async Task OpenApi_document_describes_the_api()
    {
        var doc = await hive.App.CreateClient().GetStringAsync("/openapi/v1.json");
        foreach (var path in new[] { "/api/auth/login", "/api/events", "/api/events/{id}/ack", "/api/media", "/api/media/days", "/api/devices/{deviceId}", "/api/ingest/photo" })
            Assert.Contains($"\"{path}\"", doc);
        Assert.Contains("EventDto", doc);
    }

    /// <summary>RFC 6238 TOTP (SHA-1, 30 s, 6 digits) as authenticator apps compute it.</summary>
    private static string Totp(string base32Secret)
    {
        var key = Base32Decode(base32Secret);
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var message = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(message);
        var hash = HMACSHA1.HashData(key, message);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var output = new List<byte>();
        foreach (var c in input.TrimEnd('='))
        {
            value = (value << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }
        return [.. output];
    }

    /// <summary>A browser of its own: its own cookies and its own address (X-Forwarded-For, as Caddy sets it).</summary>
    private HttpClient Device(string ip, bool external = false, string? userAgent = null)
    {
        var client = hive.App.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
        if (external)
            client.DefaultRequestHeaders.Add(AuthService.ExternalHeader, "1");
        if (userAgent is not null)
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    private static async Task<LoginResultBody> LoginStepAsync(HttpClient client, string login, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { login, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResultBody>())!;
    }

    private string[] Letters() =>
        Directory.Exists(hive.MailRoot) ? Directory.GetFiles(hive.MailRoot, "*.eml").Select(File.ReadAllText).ToArray() : [];

    private sealed record LoginResultBody(string Status, MeDto? User);
}
