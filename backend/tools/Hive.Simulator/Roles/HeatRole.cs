using System.Text.Json;
using Hive.Contracts.Messages;

namespace Hive.Simulator.Roles;

/// <summary>heat: a room with a heater relay, simple thermal model and the local failsafe (spec 5.6).</summary>
public sealed class HeatRole(HeaterSpec heater, int teleIntervalS) : HornetRole
{
    private readonly HeaterFailsafe _failsafe = new(heater.MinC, heater.MaxC, heater.MaxOnS);
    private DateTimeOffset _lastTick;
    private DateTimeOffset _lastTele = DateTimeOffset.MinValue;
    private DateTimeOffset _bootAt;
    private int _teleIntervalS = teleIntervalS;
    private bool _publishedState;

    public double RoomC { get; private set; } = heater.StartC;
    public HeaterFailsafe Failsafe => _failsafe;

    public override string Type => "heat";
    public override string Hw => "esp32-devkit-relay";
    public override IReadOnlyList<string> Caps => ["relay.heater", "sensor.ds18b20"];
    public override IReadOnlyList<string> Metrics => ["temperature"];
    public override IReadOnlyList<string> Commands => ["relay"];

    /// <summary>Sensor reading, null after the scenario's sensor_fail_at_s.</summary>
    public double? Sensor(DateTimeOffset now) =>
        heater.SensorFailAtS > 0 && now - _bootAt >= TimeSpan.FromSeconds(heater.SensorFailAtS) ? null : RoomC;

    public override void OnBoot(DateTimeOffset now)
    {
        _bootAt = now;
        _lastTick = now;
        _publishedState = false;
    }

    public override void ApplyConfig(DeviceConfig config, DateTimeOffset now)
    {
        _teleIntervalS = config.TeleIntervalS ?? _teleIntervalS;
        if (config.Failsafe?.Heater is { } f)
            _failsafe.Configure(f.MinC, f.MaxC, f.MaxOnS);
    }

    public override void Tick(DateTimeOffset now)
    {
        var minutes = (now - _lastTick).TotalMinutes;
        _lastTick = now;
        RoomC += (_failsafe.IsOn ? heater.HeatRateCPerMin * minutes : 0)
                 - (RoomC - heater.OutsideC) * heater.LossPerMin * minutes;

        var sensor = Sensor(now);
        if (_failsafe.Evaluate(sensor, now) || !_publishedState)
        {
            PublishState(now);
            if (_publishedState && _failsafe.LastReason is "min_c" or "max_c" or "max_on_s" or "sensor_lost")
                EmitFailsafeEvent(now);
            _publishedState = true;
        }

        if (now - _lastTele >= TimeSpan.FromSeconds(_teleIntervalS))
        {
            _lastTele = now;
            if (sensor is { } t)
                Hornet.EmitTele("temperature", t, "C", now);
        }
    }

    public override CommandResult? HandleCommand(string name, JsonElement payload, DateTimeOffset now)
    {
        if (name != "relay")
            return null;
        if (!payload.TryGetProperty("channel", out var channel) || channel.GetString() != "heater")
            return new CommandResult(AckStatus.Error, "unknown channel");
        if (!payload.TryGetProperty("set", out var set) || set.GetString() is not ("on" or "off"))
            return new CommandResult(AckStatus.Error, "set must be on or off");

        int? forS = payload.TryGetProperty("for_s", out var f) ? f.GetInt32() : null;
        var rejection = _failsafe.Command(set.GetString() == "on", forS, Sensor(now), now);
        if (rejection is not null)
            return CommandResult.Rejected(rejection);

        PublishState(now);
        return CommandResult.Ok(State());
    }

    private Dictionary<string, object> State() => new() { ["heater"] = _failsafe.IsOn ? "on" : "off" };

    private void PublishState(DateTimeOffset now) => Hornet.EmitState(State(), now);

    private void EmitFailsafeEvent(DateTimeOffset now)
    {
        var e = Hornet.NextEnvelope(now);
        Hornet.EmitEvent("failsafe", new EventMessage
        {
            Id = e.Id, Ts = e.Ts, Seq = e.Seq, Boot = e.Boot,
            Severity = _failsafe.LastReason == "sensor_lost" ? Severity.Alarm : Severity.Warn,
            Extra = new Dictionary<string, JsonElement>
            {
                ["rule"] = JsonSerializer.SerializeToElement(_failsafe.LastReason),
                ["heater"] = JsonSerializer.SerializeToElement(_failsafe.IsOn ? "on" : "off"),
                ["temperature"] = JsonSerializer.SerializeToElement(Sensor(now) is { } t ? Math.Round(t, 2) : (double?)null),
            },
        });
    }
}
