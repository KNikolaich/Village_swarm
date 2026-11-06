using System.Text.Json;
using Hive.Contracts.Messages;

namespace Hive.Simulator.Roles;

/// <summary>
/// irrx and irtx (infrared receiver and emitter on one ESP-01 pin, src/roles/ir.cpp): the receiver keeps the last code it saw in its
/// state ir_last, the emitter does cmd/ir. <see cref="Sent"/> lists the codes an emitter was told to send.
/// </summary>
public sealed class IrRole(bool emitter) : HornetRole
{
    private readonly List<string> _sent = [];

    public override string Type => emitter ? "irtx" : "irrx";
    public override string Hw => "esp-01";
    public override IReadOnlyList<string> Caps => [emitter ? "ir.tx" : "ir.rx"];
    public override IReadOnlyList<string> Commands => emitter ? ["ir"] : [];

    public IReadOnlyList<string> Sent => _sent;

    public override void Tick(DateTimeOffset now) { }

    /// <summary>A remote's button seen by the receiver, e.g. <c>"NEC 32 0x02FD807F"</c>.</summary>
    public void Press(string code, DateTimeOffset now) => Hornet.EmitState(new Dictionary<string, object> { ["ir_last"] = code }, now);

    public override CommandResult? HandleCommand(string name, JsonElement payload, DateTimeOffset now)
    {
        if (!emitter || name != "ir")
            return null;
        if (!payload.TryGetProperty("protocol", out var protocol) || protocol.GetString() is not { Length: >= 2 } p
            || !payload.TryGetProperty("bits", out var bits) || !bits.TryGetInt32(out var b) || b is < 1 or > 64
            || !payload.TryGetProperty("data", out var data) || data.GetString() is not { } d || !d.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return new CommandResult(AckStatus.Error, "bad infrared code");
        _sent.Add($"{p} {b} {d}");
        return CommandResult.Ok();
    }
}
