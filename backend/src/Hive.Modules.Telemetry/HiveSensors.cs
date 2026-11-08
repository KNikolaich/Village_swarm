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

    /// <summary>procfs root (load average, memory); tests point it at a fake tree.</summary>
    public string ProcRoot { get; set; } = "/proc";

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
public sealed class HiveSensorReader(string sysRoot, string procRoot = "/proc")
{
    /// <summary>The 1-minute load average divided by the number of cores, in percent: how busy the processor is.</summary>
    public double? CpuLoadPct(int cores)
    {
        try
        {
            var file = Path.Combine(procRoot, "loadavg");
            if (!File.Exists(file))
                return null;
            var first = File.ReadAllText(file).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var load) && cores > 0
                ? Math.Round(100.0 * load / cores, 1)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Total and used memory in megabytes (used = total minus what the system calls available), from /proc/meminfo.</summary>
    public (double TotalMb, double UsedMb)? Memory()
    {
        try
        {
            var file = Path.Combine(procRoot, "meminfo");
            if (!File.Exists(file))
                return null;
            long? total = null, available = null;
            foreach (var line in File.ReadLines(file))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    total = Kilobytes(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    available = Kilobytes(line);
            }
            return total is { } t && available is { } a ? (Math.Round(t / 1024.0, 0), Math.Round((t - a) / 1024.0, 0)) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static long? Kilobytes(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) ? kb : null;
    }

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

    public static double? DiskUsedPct(string path) => Disk(path)?.UsedPct;

    /// <summary>Size and free space of the disk (mount) that holds <paramref name="path"/>.</summary>
    public static DiskSpace? Disk(string path)
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
            return new DiskSpace(drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>How big a disk is and how much of it is free, in bytes.</summary>
public sealed record DiskSpace(long Total, long Free)
{
    public double UsedPct => Math.Round(100.0 * (Total - Free) / Total, 1);
}

/// <summary>Reads the disk that holds a path; replaced in tests with a small pretend disk.</summary>
public interface IDiskSpaceReader
{
    DiskSpace? Read(string path);
}

public sealed class SystemDiskSpaceReader : IDiskSpaceReader
{
    public DiskSpace? Read(string path) => HiveSensorReader.Disk(path);
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
    HiveStats stats,
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

        var reader = new HiveSensorReader(o.SysRoot, o.ProcRoot);
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
        if (HiveSensorReader.Disk(options.Value.DiskPath) is { } disk)
        {
            values.Add(("disk_used_pct", disk.UsedPct));
            values.Add(("disk_total_gb", Math.Round(disk.Total / 1073741824.0, 1)));
            values.Add(("disk_free_gb", Math.Round(disk.Free / 1073741824.0, 1)));
        }
        if (reader.CpuLoadPct(Environment.ProcessorCount) is { } load)
            values.Add(("cpu_load_pct", load));
        if (reader.Memory() is { } memory)
        {
            values.Add(("mem_total_mb", memory.TotalMb));
            values.Add(("mem_used_mb", memory.UsedMb));
            values.Add(("mem_used_pct", Math.Round(100.0 * memory.UsedMb / Math.Max(1, memory.TotalMb), 1)));
        }
        if (values.Count == 0)
            return;
        stats.Update(now, values);

        queue.Enqueue(new TelemetryReceived(DeviceId, Ulid.New(now), now, now, values));
        foreach (var (metric, value) in values)
        {
            // Only some readings have a limit (air, CPU temperature, disk use); the rest are just shown.
            if (!_watches.TryGetValue(metric, out var watch) || watch.Update(value) is not { } over)
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
