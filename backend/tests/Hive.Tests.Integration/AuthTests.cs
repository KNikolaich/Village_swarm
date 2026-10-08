using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Hive.Infrastructure.Identity;
using Hive.Modules.Users;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hive.Tests.Integration;

/// <summary>Login, TOTP, roles and lockout (build step 6).</summary>
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
    public async Task Repeated_wrong_passwords_lock_the_account()
    {
        await hive.CreateUserAsync("victim", "victim-password-1", HiveRoles.Member);
        var client = hive.App.CreateClient();
        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync("/api/auth/login", new { login = "victim", password = "nope-nope-nope" });

        var locked = await client.PostAsJsonAsync("/api/auth/login", new { login = "victim", password = "victim-password-1" });
        Assert.Equal((HttpStatusCode)423, locked.StatusCode);
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

    private sealed record LoginResultBody(string Status, MeDto? User);
}
