using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Mqtt;
using Microsoft.EntityFrameworkCore;

namespace Hive.Modules.Devices;

/// <summary>One infrared code: <c>NEC</c>, 32 bits, <c>0x02FD807F</c>. What Tasmota reports when it sees it and sends back.</summary>
public sealed record IrCode(string Protocol, int Bits, string Data);

/// <summary>The remote's buttons learned for one Tasmota IR device, by the name the family gave them.</summary>
public sealed record IrButtonsDto(IReadOnlyDictionary<string, IrCode> Buttons);

/// <summary>
/// Buttons are kept in the device's config under "ir": a Tasmota device has no retained config of ours, and an irtx hornet's
/// firmware does not read them (the hive sends the code with each command).
/// </summary>
public sealed class IrButtonService(HiveDbContext db, TimeProvider time)
{
    public const int MaxButtons = 40;
    public const int MaxNameLength = 32;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static string? Validate(IReadOnlyDictionary<string, IrCode> buttons)
    {
        if (buttons.Count > MaxButtons)
            return $"не больше {MaxButtons} кнопок";
        foreach (var (name, code) in buttons)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
                return $"имя кнопки: от 1 до {MaxNameLength} символов";
            if (!TasmotaCommands.IsValidIr(code.Protocol, code.Bits, code.Data))
                return $"кнопка «{name}»: неверный код (протокол, число бит 1-64, данные вида 0x20DF10EF)";
        }
        return null;
    }

    /// <summary>The buttons stored in a device's config.</summary>
    public static Dictionary<string, IrCode> Read(JsonDocument? config)
    {
        var result = new Dictionary<string, IrCode>();
        if (config is null || !config.RootElement.TryGetProperty("ir", out var ir) || !ir.TryGetProperty("buttons", out var buttons))
            return result;
        foreach (var b in buttons.EnumerateObject())
            if (b.Value.Deserialize<IrCode>(Web) is { } code)
                result[b.Name] = code;
        return result;
    }

    /// <summary>The command payload (protocol, bits, data) of a named button, or null when there is no such button.</summary>
    public static JsonObject? Resolve(JsonDocument? config, string button) =>
        Read(config).TryGetValue(button, out var c)
            ? new JsonObject { ["button"] = button, ["protocol"] = c.Protocol, ["bits"] = c.Bits, ["data"] = c.Data }
            : null;

    /// <summary>Devices whose remote can be learned: Tasmota ones with infrared, and our infrared transmitters.</summary>
    private static bool HasButtons(Device d) => d.Driver == Drivers.Tasmota || d.TypeCode == "irtx";

    public async Task<IrButtonsDto?> GetAsync(string deviceId, CancellationToken ct)
    {
        var device = (await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct)) is { } found && HasButtons(found) ? found : null;
        return device is null ? null : new IrButtonsDto(Read(device.Config));
    }

    /// <summary>False when there is no such Tasmota device.</summary>
    public async Task<bool> SetAsync(string deviceId, IReadOnlyDictionary<string, IrCode> buttons, string by, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null || !HasButtons(device))
            return false;
        var config = device.Config is null ? new JsonObject() : JsonNode.Parse(device.Config.RootElement.GetRawText())!.AsObject();
        var stored = new JsonObject();
        foreach (var (name, code) in buttons)
            stored[name.Trim()] = new JsonObject { ["protocol"] = code.Protocol.ToUpperInvariant(), ["bits"] = code.Bits, ["data"] = code.Data };
        config["ir"] = new JsonObject { ["buttons"] = stored };
        device.Config = JsonDocument.Parse(config.ToJsonString());
        Audit.Record(db, time.GetUtcNow(), by, "ir_buttons", deviceId, new { count = buttons.Count });
        await db.SaveChangesAsync(ct);
        return true;
    }
}
