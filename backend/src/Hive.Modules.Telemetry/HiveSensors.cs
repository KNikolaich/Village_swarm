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

    /// <summary>
    /// Root of the cgroup tree of the container the hive lives in (an LXC on Proxmox shares /proc/meminfo and the CPU counters
    /// with the whole host). Empty: not in a container, read /proc as is. Bind the container's /sys/fs/cgroup into the api
    /// and point this at it (see compose.city.yaml): the api's own cgroup would show the api's limit, not the container's.
    /// </summary>
    public string CgroupRoot { get; set; } = "";

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
public sealed class HiveSensorReader(string sysRoot, string procRoot = "/proc", string cgroupRoot = "", TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private (long Usec, long At)? _lastCpu;

    /// <summary>
    /// How busy the processor is, in percent of the cores the container has (0..100). In a container: the growth of the cgroup's
    /// CPU time since the previous call (null on the first call); otherwise the 1-minute load average divided by the cores.
    /// </summary>
    public double? CpuLoadPct(int cores)
    {
        if (cgroupRoot.Length > 0 && CgroupCpu() is { } cg)
        {
            var now = _clock.GetTimestamp();
            var last = _lastCpu;
            _lastCpu = (cg.Usec, now);
            if (last is not { } prev)
                return null;
            var seconds = _clock.GetElapsedTime(prev.At, now).TotalSeconds;
            if (seconds <= 0 || cg.Usec < prev.Usec)
                return null;
            var capacity = cg.Cores ?? cores;
            return capacity > 0 ? Math.Round(Math.Clamp(100.0 * (cg.Usec - prev.Usec) / 1e6 / seconds / capacity, 0, 100), 1) : null;
        }
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
        var host = HostMemory();
        if (cgroupRoot.Length > 0 && CgroupMemory() is { } cg)
        {
            // The container's limit, or the host's memory when the container has none.
            var totalMb = cg.LimitBytes is { } l && (host is null || l / 1048576.0 < host.Value.TotalMb) ? l / 1048576.0 : host?.TotalMb;
            if (totalMb is { } total)
                return (Math.Round(total, 0), Math.Round(Math.Min(cg.UsedBytes / 1048576.0, total), 0));
        }
        return host;
    }

    /// <summary>CPU time used (microseconds) and the cores the container may use (null: no quota), from cgroup v2 or v1.</summary>
    private (long Usec, double? Cores)? CgroupCpu()
    {
        try
        {
            var v2 = Path.Combine(cgroupRoot, "cpu.stat");
            if (File.Exists(v2))
            {
                long? usage = null;
                foreach (var line in File.ReadLines(v2))
                    if (line.StartsWith("usage_usec ", StringComparison.Ordinal) && long.TryParse(line.AsSpan(11), CultureInfo.InvariantCulture, out var u))
                        usage = u;
                if (usage is null)
                    return null;
                double? cores = null;
                var max = Path.Combine(cgroupRoot, "cpu.max");
                if (File.Exists(max) && File.ReadAllText(max).Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var quota, var period]
                    && long.TryParse(quota, CultureInfo.InvariantCulture, out var q) && long.TryParse(period, CultureInfo.InvariantCulture, out var p) && q > 0 && p > 0)
                    cores = (double)q / p;
                return (usage.Value, cores);
            }
            var ns = ReadLong(Path.Combine(cgroupRoot, "cpuacct", "cpuacct.usage"));
            if (ns is null)
                return null;
            var q1 = ReadLong(Path.Combine(cgroupRoot, "cpu", "cpu.cfs_quota_us"));
            var p1 = ReadLong(Path.Combine(cgroupRoot, "cpu", "cpu.cfs_period_us"));
            return (ns.Value / 1000, q1 is > 0 && p1 is > 0 ? (double)q1.Value / p1.Value : null);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Memory the container holds (without cache the kernel can drop, like docker stats) and its limit (null: none), v2 or v1.</summary>
    private (long UsedBytes, long? LimitBytes)? CgroupMemory()
    {
        try
        {
            string current, max, stat;
            if (File.Exists(Path.Combine(cgroupRoot, "memory.current")))
                (current, max, stat) = (Path.Combine(cgroupRoot, "memory.current"), Path.Combine(cgroupRoot, "memory.max"), Path.Combine(cgroupRoot, "memory.stat"));
            else
                (current, max, stat) = (Path.Combine(cgroupRoot, "memory", "memory.usage_in_bytes"), Path.Combine(cgroupRoot, "memory", "memory.limit_in_bytes"), Path.Combine(cgroupRoot, "memory", "memory.stat"));
            if (ReadLong(current) is not { } used)
                return null;
            long inactive = 0;
            if (File.Exists(stat))
                foreach (var line in File.ReadLines(stat))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && parts[0] is "inactive_file" or "total_inactive_file" && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var v))
                        inactive = v;
                }
            var limit = ReadLong(max); // "max" (v2) does not parse = no limit; v1 says a huge number
            return (Math.Max(0, used - inactive), limit is > 0 and < (1L << 60) ? limit : null);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static long? ReadLong(string file) =>
        File.Exists(file) && long.TryParse(File.ReadAllText(file).Trim(), CultureInfo.InvariantCulture, out var v) ? v : null;

    private (double TotalMb, double UsedMb)? HostMemory()
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

        var reader = new HiveSensorReader(o.SysRoot, o.ProcRoot, o.CgroupRoot);
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
