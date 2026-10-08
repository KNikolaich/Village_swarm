namespace Hive.Simulator.Roles;

/// <summary>
/// Local heater safety rules that never depend on the server (spec 5.6). Reference model for the
/// firmware Failsafe module: below min_c heat regardless of commands, above max_c switch off,
/// no single on-period longer than max_on_s, timer-based anti-freeze when the sensor is lost.
/// </summary>
public sealed class HeaterFailsafe(double minC, double maxC, int maxOnS)
{
    /// <summary>Forced heating continues until min_c + this, to avoid flapping around min_c.</summary>
    public const double Hysteresis = 1.0;

    /// <summary>Pause after a max_on_s cut-off before the heater may run again.</summary>
    public static readonly TimeSpan Rest = TimeSpan.FromMinutes(1);

    /// <summary>Anti-freeze cycle without a sensor: 10 min on, 20 min off.</summary>
    public static readonly TimeSpan SafeOn = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan SafeCycle = TimeSpan.FromMinutes(30);

    private DateTimeOffset _onSince;
    private DateTimeOffset _commandUntil;
    private DateTimeOffset _restUntil = DateTimeOffset.MinValue;
    private DateTimeOffset? _sensorLostAt;
    private bool _commandedOn;
    private bool _forced;

    public double MinC { get; private set; } = minC;
    public double MaxC { get; private set; } = maxC;
    public int MaxOnS { get; private set; } = maxOnS;
    public bool IsOn { get; private set; }

    /// <summary>Why the last automatic change happened (null when the state followed a command).</summary>
    public string? LastReason { get; private set; }

    /// <summary>The server may narrow the limits, never disable them (spec 5.6).</summary>
    public void Configure(double minC, double maxC, int maxOnS)
    {
        MinC = minC;
        MaxC = maxC;
        MaxOnS = Math.Max(1, maxOnS);
    }

    /// <summary>Applies cmd/relay. Returns null when accepted, otherwise the rejection message.</summary>
    public string? Command(bool on, int? forS, double? temperature, DateTimeOffset now)
    {
        if (on)
        {
            if (temperature is null)
                return "failsafe: temperature sensor lost";
            if (temperature >= MaxC)
                return $"failsafe: {temperature:0.0} C is at or above max_c {MaxC:0.0}";
            if (now < _restUntil)
                return "failsafe: resting after max_on_s";
            _commandedOn = true;
            _commandUntil = now.AddSeconds(Math.Min(forS ?? MaxOnS, MaxOnS));
        }
        else
        {
            if (temperature < MinC)
                return $"failsafe: {temperature:0.0} C is below min_c {MinC:0.0}";
            _commandedOn = false;
        }
        LastReason = null;
        Set(on, now);
        return null;
    }

    /// <summary>Re-evaluates the rules; call every tick. Returns true when the heater state changed.</summary>
    public bool Evaluate(double? temperature, DateTimeOffset now)
    {
        var was = IsOn;

        if (IsOn && now - _onSince >= TimeSpan.FromSeconds(MaxOnS))
        {
            _commandedOn = false;
            _forced = false;
            _restUntil = now + Rest;
            Set(false, now, "max_on_s");
            return was != IsOn;
        }

        if (_commandedOn && now >= _commandUntil)
            _commandedOn = false; // for_s elapsed

        if (temperature is null)
        {
            _sensorLostAt ??= now;
            var inCycle = (now - _sensorLostAt.Value).Ticks % SafeCycle.Ticks;
            Set(inCycle < SafeOn.Ticks && now >= _restUntil, now, "sensor_lost");
            return was != IsOn;
        }
        _sensorLostAt = null;

        if (temperature > MaxC)
        {
            _commandedOn = false;
            _forced = false;
            Set(false, now, "max_c");
        }
        else if (now >= _restUntil && (temperature < MinC || (_forced && temperature < MinC + Hysteresis)))
        {
            _forced = true;
            Set(true, now, "min_c");
        }
        else
        {
            _forced = false;
            Set(_commandedOn, now, _commandedOn ? null : "idle");
        }
        return was != IsOn;
    }

    private void Set(bool on, DateTimeOffset now, string? reason = null)
    {
        if (on == IsOn)
            return;
        IsOn = on;
        if (on)
            _onSince = now;
        if (reason is not null)
            LastReason = reason;
    }
}
