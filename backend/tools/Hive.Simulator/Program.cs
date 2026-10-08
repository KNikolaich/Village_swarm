using Hive.Simulator;
using Hive.Simulator.Roles;

// Swarm simulator (spec 14.2): N virtual hornets speaking the MQTT contract.
//   Hive.Simulator [scenario.yaml] [--host H] [--port P]
// Against a real hive with per-hornet logins (spec 5.3), the hornets enroll first and are removed on exit:
//   Hive.Simulator scenario.yaml --hive http://hive:5080 --login admin --password ... [--keep]

var scenarioPath = Path.Combine(AppContext.BaseDirectory, "scenarios", "default.yaml");
string? host = null, hive = null, login = null, password = null;
int? port = null;
var keep = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host": host = args[++i]; break;
        case "--port": port = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--hive": hive = args[++i]; break;
        case "--login": login = args[++i]; break;
        case "--password": password = args[++i]; break;
        case "--keep": keep = true; break;
        case "-h" or "--help":
            Console.WriteLine("Hive.Simulator [scenario.yaml] [--host localhost] [--port 1883] [--hive <api url> --login <admin> --password <pw> [--keep]]");
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
var specs = scenario.ExpandHornets().ToList();

SimProvisioner? provisioner = null;
var enrollments = new Dictionary<string, Enrollment>();
if (hive is not null)
{
    provisioner = new SimProvisioner(new HttpClient { BaseAddress = new Uri(hive), Timeout = TimeSpan.FromSeconds(20) });
    await provisioner.LoginAsync(login ?? "admin", password ?? "", cts.Token);
    foreach (var spec in specs)
    {
        enrollments[spec.Id] = await provisioner.EnrollAsync(spec, cts.Token);
        Console.WriteLine($"[{spec.Id}] enrolled");
    }
}

var runners = new List<Task>();
var links = new List<IHornetLink>();
foreach (var spec in specs)
{
    var enrollment = enrollments.GetValueOrDefault(spec.Id);
    if (enrollment is not null)
        spec.Motion.UploadUrl = enrollment.UploadUrl;
    var photosDir = Path.Combine(scenario.BaseDir, spec.Motion.PhotosDir);
    var hornet = new VirtualHornet(spec, HornetRole.Create(spec, photosDir));
    var link = new MqttHornetLink(scenario.Broker, enrollment?.MqttUser, enrollment?.MqttPass);
    links.Add(link);
    runners.Add(new HornetRunner(hornet, link, http, TimeProvider.System, enrollment?.UploadToken).RunAsync(cts.Token));
}

await Task.WhenAll(runners);
foreach (var link in links)
    await link.DisposeAsync();

if (provisioner is not null && !keep)
    foreach (var spec in specs)
        await provisioner.RemoveAsync(spec.Id, CancellationToken.None);
return 0;
