using Hive.Simulator.Roles;

namespace Hive.Tests.Unit.Simulator;

public class HeaterFailsafeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 10, 3, 0, 0, TimeSpan.Zero);

    private static HeaterFailsafe Create() => new(minC: 5, maxC: 25, maxOnS: 3600);

    [Fact]
    public void Turns_on_below_min_c_without_any_command()
    {
        var f = Create();
        Assert.True(f.Evaluate(4.0, T0));
        Assert.True(f.IsOn);
        Assert.Equal("min_c", f.LastReason);
    }

    [Fact]
    public void Forced_heating_keeps_running_until_hysteresis_band_is_left()
    {
        var f = Create();
        f.Evaluate(4.0, T0);
        f.Evaluate(5.5, T0.AddMinutes(1));
        Assert.True(f.IsOn);
        f.Evaluate(6.1, T0.AddMinutes(2));
        Assert.False(f.IsOn);
    }

    [Fact]
    public void Rejects_on_at_or_above_max_c()
    {
        var f = Create();
        Assert.NotNull(f.Command(on: true, forS: null, temperature: 25.0, T0));
        Assert.False(f.IsOn);
    }

    [Fact]
    public void Rejects_off_below_min_c()
    {
        var f = Create();
        f.Evaluate(3.0, T0);
        Assert.NotNull(f.Command(on: false, forS: null, temperature: 3.0, T0));
        Assert.True(f.IsOn);
    }

    [Fact]
    public void Switches_off_above_max_c_even_when_commanded_on()
    {
        var f = Create();
        Assert.Null(f.Command(on: true, forS: 600, temperature: 20, T0));
        Assert.True(f.Evaluate(25.5, T0.AddSeconds(10)));
        Assert.False(f.IsOn);
        Assert.Equal("max_c", f.LastReason);
    }

    [Fact]
    public void Command_for_s_expires_and_heater_switches_off()
    {
        var f = Create();
        f.Command(on: true, forS: 60, temperature: 15, T0);
        f.Evaluate(15, T0.AddSeconds(59));
        Assert.True(f.IsOn);
        f.Evaluate(15, T0.AddSeconds(60));
        Assert.False(f.IsOn);
    }

    [Fact]
    public void For_s_is_capped_by_max_on_s()
    {
        var f = Create();
        f.Command(on: true, forS: 100_000, temperature: 15, T0);
        f.Evaluate(15, T0.AddSeconds(3599));
        Assert.True(f.IsOn);
        f.Evaluate(15, T0.AddSeconds(3600));
        Assert.False(f.IsOn);
        Assert.Equal("max_on_s", f.LastReason);
    }

    [Fact]
    public void No_on_period_exceeds_max_on_s_even_when_forced_by_min_c()
    {
        var f = Create();
        var t = T0;
        var onSince = T0;
        var longest = TimeSpan.Zero;
        var wasOn = false;
        // Freezing room for 5 hours: forced heating must still be cut every max_on_s.
        for (var i = 0; i < 5 * 3600; i += 10, t = t.AddSeconds(10))
        {
            f.Evaluate(1.0, t);
            if (f.IsOn && !wasOn)
                onSince = t;
            if (f.IsOn)
                longest = TimeSpan.FromTicks(Math.Max(longest.Ticks, (t - onSince).Ticks));
            wasOn = f.IsOn;
        }
        Assert.True(longest <= TimeSpan.FromSeconds(3600), $"longest on-period {longest}");
    }

    [Fact]
    public void Rests_after_max_on_s_cut_off_then_resumes()
    {
        var f = Create();
        f.Evaluate(1.0, T0);
        f.Evaluate(1.0, T0.AddSeconds(3600));
        Assert.False(f.IsOn);
        Assert.NotNull(f.Command(on: true, forS: null, temperature: 1.0, T0.AddSeconds(3610)));
        f.Evaluate(1.0, T0.AddSeconds(3600) + HeaterFailsafe.Rest);
        Assert.True(f.IsOn);
    }

    [Fact]
    public void Sensor_loss_runs_anti_freeze_timer_cycle()
    {
        var f = Create();
        f.Evaluate(null, T0);
        Assert.True(f.IsOn);
        Assert.Equal("sensor_lost", f.LastReason);
        f.Evaluate(null, T0 + HeaterFailsafe.SafeOn);
        Assert.False(f.IsOn);
        f.Evaluate(null, T0 + HeaterFailsafe.SafeCycle);
        Assert.True(f.IsOn);
    }

    [Fact]
    public void Rejects_on_command_without_sensor()
    {
        var f = Create();
        Assert.NotNull(f.Command(on: true, forS: 60, temperature: null, T0));
    }
}
