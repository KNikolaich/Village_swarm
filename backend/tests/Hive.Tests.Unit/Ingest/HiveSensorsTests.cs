using Hive.Domain;
using Hive.Infrastructure;
using Hive.Modules.Telemetry;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hive.Tests.Unit.Ingest;

public sealed class HiveSensorsTests : IDisposable
{
    private readonly string _sys = Directory.CreateTempSubdirectory("hive-sys-").FullName;

    public void Dispose() => Directory.Delete(_sys, recursive: true);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_sys, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text + "\n");
    }

    [Fact]
    public void Reads_ds18b20_and_cpu_from_sysfs()
    {
        Write("bus/w1/devices/28-0000057a1b2c/temperature", "30187");
        Write("bus/w1/devices/w1_bus_master1/temperature", "99999"); // not a thermometer
        Write("class/thermal/thermal_zone0/temp", "51540");

        var reader = new HiveSensorReader(_sys);

        Assert.Equal(30.19, reader.AirC());
        Assert.Equal(51.54, reader.CpuC());
    }

    [Fact]
    public void Missing_or_power_on_reading_is_no_value()
    {
        var reader = new HiveSensorReader(_sys);
        Assert.Null(reader.AirC());
        Assert.Null(reader.CpuC());

        Write("bus/w1/devices/28-0000057a1b2c/temperature", "85000"); // DS18B20 that has not converted yet
        Assert.Null(reader.AirC());
    }

    [Fact]
    public void Limit_has_hysteresis()
    {
        var watch = new LimitWatch(45, 3);
        Assert.Null(watch.Update(44.9));
        Assert.True(watch.Update(45.2));
        Assert.Null(watch.Update(46));
        Assert.Null(watch.Update(43)); // still within the hysteresis
        Assert.False(watch.Update(41.9));
        Assert.Null(watch.Update(40));
    }

    [Fact]
    public void Overheat_becomes_a_warn_event_once_and_clears_with_info()
    {
        Write("bus/w1/devices/28-0000057a1b2c/temperature", "47500");
        var queue = new IngestQueue();
        var sensors = new HiveSensors(queue,
            Options.Create(new HiveSensorOptions { SysRoot = _sys, DiskPath = _sys, AirWarnC = 45 }),
            Options.Create(new HiveOptions { Node = "home" }), TimeProvider.System, NullLogger<HiveSensors>.Instance);
        var reader = new HiveSensorReader(_sys);

        sensors.Sample(reader);
        sensors.Sample(reader);
        Write("bus/w1/devices/28-0000057a1b2c/temperature", "38000");
        sensors.Sample(reader);

        var items = Drain(queue);
        var tele = items.OfType<TelemetryReceived>().ToList();
        Assert.Equal(3, tele.Count);
        Assert.All(tele, t => Assert.Equal("hive-home", t.DeviceId));
        Assert.Contains(("temperature", 47.5), tele[0].Values);
        Assert.Contains(tele[0].Values, v => v.Metric == "disk_used_pct");

        var events = items.OfType<DeviceEventReceived>().Where(e => e.Type.StartsWith("overheat", StringComparison.Ordinal)).ToList();
        Assert.Equal([("overheat", EventSeverity.Warn), ("overheat_cleared", EventSeverity.Info)], events.Select(e => (e.Type, e.Severity)));
        Assert.Equal(47.5, events[0].Payload.RootElement.GetProperty("value").GetDouble());
    }

    private static List<IngestItem> Drain(IngestQueue queue)
    {
        var items = new List<IngestItem>();
        while (queue.Reader.TryRead(out var item))
            items.Add(item);
        return items;
    }
}
