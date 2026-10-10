using System.Globalization;
using System.Text.Json;
using Hive.Contracts;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Telemetry;

public sealed class HiveSensorOptions
{
    public const string Section = "Hive:Sensors";

    /// <summary>On the RPi only (compose sets it): the hive shows up as device hive-{node} with its own readings.</summary>
    public bool Enabled { get; set; }

    public int IntervalS { get; set; } = 60;

    /// <summary>sysfs root; tests point it at a fake tree.</summary>
    public string SysRoot { get; set; } = "/sys";

    /// <summary>Disk whose usage is watched (the media root lives on it).</summary>
    public string DiskPath { get; set; } = "/srv/hive/media";

    /// <summary>Air around the board (DS18B20 on 1-Wire): above this the hive is overheating.</summary>
    public double AirWarnC { get; set; } = 45;

    public double CpuWarnC { get; set; } = 75;

    public double DiskWarnPct { get; set; } = 85;

    /// <summary>A warning clears only after the value drops this far below the limit (no flapping).</summary>
    public double Hysteresis { get; set; } = 3;
}

/// <summary>Reads the hive's own sensors from sysfs: DS18B20 via the w1-gpio overlay and the SoC thermal zone.</summary>
public sealed class HiveSensorReader(string sysRoot)
{
    /// <summary>First DS18B20 (family 28) in °C, or null when there is none or it reports an error.</summary>
    public double? AirC()
    {
        var devices = Path.Combine(sysRoot, "bus", "w1", "devices");
        if (!Directory.Exists(devices))
            return null;
        foreach (var dir in Directory.EnumerateDirectories(devices, "28-*").Order(StringComparer.Ordinal))
        {
            var file = Path.Combine(dir, "temperature");
            if (TryReadMilli(file, out var c) && c is > -55 and < 125 && c != 85) // 85 °C is the DS18B20 power-on value
                return c;
        }
        return null;
    }

    public double? CpuC() =>
        TryReadMilli(Path.Combine(sysRoot, "class", "thermal", "thermal_zone0", "temp"), out var c) ? c : null;

    private static bool TryReadMilli(string file, out double celsius)
    {
        celsius = 0;
        try
        {
            if (!File.Exists(file))
                return false;
            var text = File.ReadAllText(file).Trim();
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milli))
                return false;
            celsius = Math.Round(milli / 1000.0, 2);
            return true;
        }
        catch (IOException)
        {
            return false; // the 1-Wire driver returns EIO when a read fails
        }
    }

    public static double? DiskUsedPct(string path)
    {
        try
        {
            // The mount point with the longest matching prefix holds the path (/ or a separate /srv).
            var full = Path.GetFullPath(path);
            var drive = DriveInfo.GetDrives()
                .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                .MaxBy(d => d.RootDirectory.FullName.Length);
            if (drive is null || drive.TotalSize == 0)
                return null;
            return Math.Round(100.0 * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize, 1);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Limit with hysteresis: reports when a value goes over the limit and when it is back to normal.</summary>
public sealed class LimitWatch(double limit, double hysteresis)
{
    public bool Over { get; private set; }

    /// <summary>true: just went over; false: just recovered; null: nothing changed.</summary>
    public bool? Update(double value)
    {
        if (!Over && value >= limit)
            return Over = true;
        if (Over && value <= limit - hysteresis)
            return Over = false;
        return null;
    }
}

/// <summary>
/// The hive watches itself (spec 3.1 "summer under the roof +40 °C", 7.4 disk control): air temperature at the
/// board, CPU temperature and disk usage become telemetry of device hive-{node}; going over a limit is a
/// warn event (Telegram, silent), coming back is an info event.
/// </summary>
public sealed class HiveSensors(
    IngestQueue queue,
    IOptions<HiveSensorOptions> options,
    IOptions<HiveOptions> hive,
    TimeProvider time,
    ILogger<HiveSensors> logger) : BackgroundService
{
    public const string TypeCode = "hive";

    private readonly Dictionary<string, LimitWatch> _watches = new()
    {
        ["temperature"] = new LimitWatch(options.Value.AirWarnC, options.Value.Hysteresis),
        ["cpu_temperature"] = new LimitWatch(options.Value.CpuWarnC, options.Value.Hysteresis),
        ["disk_used_pct"] = new LimitWatch(options.Value.DiskWarnPct, options.Value.Hysteresis),
    };

    public string DeviceId => $"hive-{hive.Value.Node}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled)
            return;

        var reader = new HiveSensorReader(o.SysRoot);
        Announce(reader);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, o.IntervalS)), time);
        do
        {
            try
            {
                Sample(reader);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Hive sensor read failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void Announce(HiveSensorReader reader)
    {
        var now = time.GetUtcNow();
        var info = JsonSerializer.SerializeToDocument(new
        {
            air_sensor = reader.AirC() is not null,
            limits = new { air_c = options.Value.AirWarnC, cpu_c = options.Value.CpuWarnC, disk_pct = options.Value.DiskWarnPct },
        });
        var version = typeof(HiveSensors).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        queue.Enqueue(new DeviceInfoReceived(DeviceId, Ulid.New(now), now, now, TypeCode, "raspberry-pi", version, "", null, 0, info));
        queue.Enqueue(new DeviceStatusChanged(DeviceId, true, now));
        if (reader.AirC() is null)
            logger.LogInformation("No DS18B20 at {Root}/bus/w1/devices: the hive reports CPU and disk only", options.Value.SysRoot);
    }

    /// <summary>One reading of every sensor; internal for tests.</summary>
    internal void Sample(HiveSensorReader reader)
    {
        var now = time.GetUtcNow();
        var values = new List<(string Metric, double Value)>();
        if (reader.AirC() is { } air)
            values.Add(("temperature", air));
        if (reader.CpuC() is { } cpu)
            values.Add(("cpu_temperature", cpu));
        if (HiveSensorReader.DiskUsedPct(options.Value.DiskPath) is { } disk)
            values.Add(("disk_used_pct", disk));
        if (values.Count == 0)
            return;

        queue.Enqueue(new TelemetryReceived(DeviceId, Ulid.New(now), now, now, values));
        foreach (var (metric, value) in values)
        {
            var watch = _watches[metric];
            if (watch.Update(value) is not { } over)
                continue;
            var type = metric == "disk_used_pct" ? "disk_full" : "overheat";
            var limit = metric switch
            {
                "temperature" => options.Value.AirWarnC,
                "cpu_temperature" => options.Value.CpuWarnC,
                _ => options.Value.DiskWarnPct,
            };
            var payload = JsonSerializer.SerializeToDocument(new { metric, value, limit });
            queue.Enqueue(new DeviceEventReceived(DeviceId, Ulid.New(now), now, now,
                over ? type : type + "_cleared", over ? EventSeverity.Warn : EventSeverity.Info, null, payload));
            if (over)
                logger.LogWarning("Hive {Metric} {Value} is over the limit {Limit}", metric, value, limit);
        }
    }
}
