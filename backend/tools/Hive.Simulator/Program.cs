using Hive.Simulator;
using Hive.Simulator.Roles;

// Swarm simulator (spec 14.2): N virtual hornets speaking the MQTT contract.
// Usage: dotnet run --project backend/tools/Hive.Simulator -- [scenario.yaml] [--host H] [--port P]

var scenarioPath = Path.Combine(AppContext.BaseDirectory, "scenarios", "default.yaml");
string? host = null;
int? port = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host": host = args[++i]; break;
        case "--port": port = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "-h" or "--help":
            Console.WriteLine("Hive.Simulator [scenario.yaml] [--host localhost] [--port 1883]");
            return 0;
        default: scenarioPath = args[i]; break;
    }
}

var scenario = Scenario.Load(scenarioPath);
if (host is not null)
    scenario.Broker.Host = host;
if (port is not null)
    scenario.Broker.Port = port.Value;

Console.WriteLine($"Scenario {Path.GetFullPath(scenarioPath)}, broker {scenario.Broker.Host}:{scenario.Broker.Port}. Ctrl+C to stop.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
var runners = new List<Task>();
var links = new List<IHornetLink>();
foreach (var spec in scenario.ExpandHornets())
{
    var photosDir = Path.Combine(scenario.BaseDir, spec.Motion.PhotosDir);
    var hornet = new VirtualHornet(spec, HornetRole.Create(spec, photosDir));
    var link = new MqttHornetLink(scenario.Broker);
    links.Add(link);
    runners.Add(new HornetRunner(hornet, link, http, TimeProvider.System).RunAsync(cts.Token));
}

await Task.WhenAll(runners);
foreach (var link in links)
    await link.DisposeAsync();
return 0;
