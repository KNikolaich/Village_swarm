namespace Hive.Simulator.Roles;

/// <summary>
/// meteo: outdoor temperature as a sine wave with noise, humidity in antiphase. With config hw.ntc_pin an NTC thermistor
/// indoors too (temperature_ntc): about 22 C, smoothed like the firmware's, moved by hw.ntc_offset.
/// </summary>
public sealed class MeteoRole(TemperatureSpec temperature, int teleIntervalS) : HornetRole
{
    private DateTimeOffset _lastTele = DateTimeOffset.MinValue;
    private int _teleIntervalS = teleIntervalS;
    private bool _ntc;
    private double _ntcOffset;

    public override string Type => "meteo";
    public override string Hw => "esp32c3-supermini";
    public override IReadOnlyList<string> Caps => ["sensor.sht45"];
    public override IReadOnlyList<string> Metrics => _ntc ? ["temperature", "humidity", "temperature_ntc"] : ["temperature", "humidity"];

    public static double Temperature(TemperatureSpec spec, DateTimeOffset now) =>
        spec.Base + spec.Amplitude * Math.Sin(2 * Math.PI * now.ToUnixTimeSeconds() / Math.Max(spec.PeriodS, 1));

    public override void ApplyConfig(Hive.Contracts.Messages.DeviceConfig config, DateTimeOffset now)
    {
        _teleIntervalS = config.TeleIntervalS ?? _teleIntervalS;
        _ntc = config.Hw?.NtcPin is not null;
        _ntcOffset = config.Hw?.NtcOffset ?? 0;
    }

    public override void Tick(DateTimeOffset now)
    {
        if (now - _lastTele < TimeSpan.FromSeconds(_teleIntervalS))
            return;
        _lastTele = now;

        var noise = (Hornet.Random.NextDouble() - 0.5) * 2 * temperature.Noise;
        var t = Temperature(temperature, now) + noise;
        var phase = (t - temperature.Base) / Math.Max(temperature.Amplitude, 0.1);
        var humidity = Math.Clamp(75 - 15 * phase + noise * 10, 0, 100);

        Hornet.EmitTele("temperature", t, "C", now);
        Hornet.EmitTele("humidity", humidity, "%", now);
        if (_ntc)
            Hornet.EmitTele("temperature_ntc", 22 + noise * 0.1 + _ntcOffset, "C", now);
    }
}
