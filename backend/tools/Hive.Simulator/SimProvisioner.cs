using System.Net.Http.Json;
using System.Text.Json;

namespace Hive.Simulator;

/// <summary>Credentials a virtual hornet got from the hive, exactly as a real board would (spec 5.3).</summary>
public sealed record Enrollment(string? MqttUser, string? MqttPass, string UploadUrl, string UploadToken);

/// <summary>
/// Adds virtual hornets to a real hive the same way the "Add hornet" wizard does: an admin creates an
/// enrollment code, the hornet trades it for its MQTT login and upload token. Lets the simulator run
/// against a hive with dynamic security, and removes the hornets again afterwards.
/// </summary>
public sealed class SimProvisioner(HttpClient http)
{
    public async Task LoginAsync(string login, string password, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/api/auth/login", new { login, password }, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Login to the hive failed: HTTP {(int)response.StatusCode}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (body.GetProperty("status").GetString() != "ok")
            throw new InvalidOperationException("The hive account has TOTP enabled; use an account without it for the simulator");
    }

    public async Task<Enrollment> EnrollAsync(HornetSpec spec, CancellationToken ct)
    {
        using var codeResponse = await http.PostAsJsonAsync("/api/provision/codes",
            new { deviceId = spec.Id, type = spec.Type, name = $"Симулятор {spec.Id}" }, ct);
        if (!codeResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Code for {spec.Id}: HTTP {(int)codeResponse.StatusCode} {await codeResponse.Content.ReadAsStringAsync(ct)}");
        var code = (await codeResponse.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString();

        var mac = FakeMac(spec.Id);
        using var enrollResponse = await http.PostAsJsonAsync("/api/provision/enroll", new { code, mac, hw = "simulator", fw = spec.Fw }, ct);
        if (!enrollResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Enroll {spec.Id}: HTTP {(int)enrollResponse.StatusCode}");
        var e = await enrollResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
        var mqtt = e.GetProperty("mqtt");
        return new Enrollment(
            mqtt.GetProperty("user").GetString(),
            mqtt.GetProperty("pass").GetString(),
            e.GetProperty("uploadUrl").GetString()!,
            e.GetProperty("uploadToken").GetString()!);
    }

    /// <summary>Revokes the hornet's login and hides it (history stays), like DELETE in the UI.</summary>
    public async Task RemoveAsync(string deviceId, CancellationToken ct)
    {
        using var response = await http.DeleteAsync($"/api/devices/{deviceId}", ct);
        Console.WriteLine($"[{deviceId}] removed: HTTP {(int)response.StatusCode}");
    }

    private static string FakeMac(string id)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id));
        hash[0] = 0x02;
        return string.Join(':', hash.Take(6).Select(b => b.ToString("X2")));
    }
}
