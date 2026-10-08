using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Hive.Simulator;

/// <summary>Simulator scenario loaded from YAML (see scenarios/*.yaml).</summary>
public sealed class Scenario
{
    public BrokerOptions Broker { get; set; } = new();
    public List<HornetSpec> Hornets { get; set; } = [];

    public static Scenario Load(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var scenario = deserializer.Deserialize<Scenario>(File.ReadAllText(path)) ?? new Scenario();
        scenario.BaseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return scenario;
    }

    /// <summary>Directory of the scenario file; relative paths in it resolve against this.</summary>
    [YamlIgnore]
    public string BaseDir { get; private set; } = Directory.GetCurrentDirectory();

    /// <summary>Expands <see cref="HornetSpec.Count"/>: guard-gate x3 becomes guard-gate1..guard-gate3.</summary>
    public IEnumerable<HornetSpec> ExpandHornets()
    {
        foreach (var spec in Hornets)
        {
            if (spec.Count <= 1)
            {
                yield return spec;
                continue;
            }
            for (var i = 1; i <= spec.Count; i++)
                yield return spec.WithId($"{spec.Id}{i}");
        }
    }
}

public sealed class BrokerOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class HornetSpec
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public int Count { get; set; } = 1;
    public string Fw { get; set; } = "0.0.1-sim";
    public int HealthIntervalS { get; set; } = 60;
    public int TeleIntervalS { get; set; } = 60;

    public MotionSpec Motion { get; set; } = new();
    public TemperatureSpec Temperature { get; set; } = new();
    public HeaterSpec Heater { get; set; } = new();

    /// <summary>Timeline of disruptions: offline periods and reboots.</summary>
    public List<ScriptStep> Script { get; set; } = [];

    public HornetSpec WithId(string id)
    {
        var copy = (HornetSpec)MemberwiseClone();
        copy.Id = id;
        copy.Count = 1;
        return copy;
    }
}

public sealed class MotionSpec
{
    /// <summary>Mean seconds between motion events (randomized +-50%); 0 disables.</summary>
    public int EverySeconds { get; set; } = 60;

    public bool Armed { get; set; } = true;
    public int Burst { get; set; } = 3;
    public int BurstIntervalMs { get; set; } = 700;

    /// <summary>Base URL of the hive photo ingest (POST {url}/api/ingest/photo); empty skips uploads.</summary>
    public string UploadUrl { get; set; } = "";

    /// <summary>Folder with test JPEGs, relative to the scenario file.</summary>
    public string PhotosDir { get; set; } = "photos";
}

public sealed class TemperatureSpec
{
    public double Base { get; set; } = 0;
    public double Amplitude { get; set; } = 5;
    public int PeriodS { get; set; } = 600;
    public double Noise { get; set; } = 0.1;
}

public sealed class HeaterSpec
{
    public double StartC { get; set; } = 12;
    public double OutsideC { get; set; } = -5;

    /// <summary>Degrees per minute the heater adds while on.</summary>
    public double HeatRateCPerMin { get; set; } = 0.5;

    /// <summary>Fraction of the inside/outside difference lost per minute.</summary>
    public double LossPerMin { get; set; } = 0.01;

    public double MinC { get; set; } = 5;
    public double MaxC { get; set; } = 25;
    public int MaxOnS { get; set; } = 21600;

    /// <summary>Seconds after start when the temperature sensor fails (0 = never).</summary>
    public int SensorFailAtS { get; set; }
}

public sealed class ScriptStep
{
    public int AtS { get; set; }

    /// <summary>offline | reboot</summary>
    public string Action { get; set; } = "";

    public int ForS { get; set; } = 30;
}
