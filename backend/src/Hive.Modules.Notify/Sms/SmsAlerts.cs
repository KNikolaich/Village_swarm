using System.Threading.Channels;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Notify.Sms;

/// <summary>An SMS waiting to go out: one text for every recipient.</summary>
public sealed record SmsAlert(string Key, string Text);

/// <summary>
/// Critical events by SMS (spec 9.6): the one channel that outlives the internet. Only <c>critical</c>, only from
/// the events this node speaks about, throttled; the sending runs in the background so ingest never waits for a modem.
/// </summary>
public sealed class SmsAlerts : BackgroundService, IIngestListener
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ISmsModem _modem;
    private readonly IOptions<SmsOptions> _options;
    private readonly IOptions<HiveOptions> _hive;
    private readonly NodeState _node;
    private readonly TimeProvider _time;
    private readonly ILogger<SmsAlerts> _logger;
    private readonly SmsThrottle _throttle;
    private readonly Channel<SmsAlert> _queue = Channel.CreateBounded<SmsAlert>(32);

    public SmsAlerts(IServiceScopeFactory scopes, ISmsModem modem, IOptions<SmsOptions> options, IOptions<HiveOptions> hive,
        NodeState node, TimeProvider time, ILogger<SmsAlerts> logger)
    {
        _scopes = scopes;
        _modem = modem;
        _options = options;
        _hive = hive;
        _node = node;
        _time = time;
        _logger = logger;
        _throttle = new SmsThrottle(options.Value.MaxPerHour, TimeSpan.FromMinutes(options.Value.DedupeMin));
    }

    public async Task OnIngestedAsync(IReadOnlyList<IngestItem> items, CancellationToken ct)
    {
        if (!_options.Value.Enabled)
            return;
        var events = items.OfType<DeviceEventReceived>().Where(e => e.Severity == EventSeverity.Critical && _node.Notifies(e.DeviceId)).ToList();
        if (events.Count == 0)
            return;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        foreach (var e in events)
        {
            if (!_throttle.TryTake($"{e.DeviceId}:{e.Type}", _time.GetUtcNow()))
            {
                _logger.LogInformation("SMS for {Type} from {Device} skipped: same alarm already sent, or the hourly cap", e.Type, e.DeviceId);
                continue;
            }
            var device = await db.Devices.AsNoTracking().Include(d => d.Zone).FirstOrDefaultAsync(d => d.DeviceId == e.DeviceId, ct);
            var place = device?.Zone?.Name ?? device?.Name ?? e.DeviceId;
            var alert = new SmsAlert($"{e.DeviceId}:{e.Type}", SmsText.Event(e.Type, place, _hive.Value.ToLocal(e.Ts)));
            if (!_queue.Writer.TryWrite(alert))
                _logger.LogWarning("SMS queue is full, dropped: {Text}", alert.Text);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation("SMS is not configured (Sms:Port, Sms:Recipients); critical events go to Telegram only");
            return;
        }
        await foreach (var alert in _queue.Reader.ReadAllAsync(stoppingToken))
            await SendAsync(alert, stoppingToken);
    }

    internal async Task SendAsync(SmsAlert alert, CancellationToken ct)
    {
        foreach (var number in _options.Value.Recipients.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            // The modem can be busy or registering on the network: a few tries a minute apart, then it is in the log.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await _modem.SendAsync(number, alert.Text, ct);
                    _logger.LogInformation("SMS sent to {Number}: {Text}", Mask(number), alert.Text);
                    break;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning("SMS to {Number} failed (try {Attempt}/3): {Error}", Mask(number), attempt, ex.Message);
                    if (attempt >= 3)
                        break;
                    await Task.Delay(RetryDelay, _time, ct);
                }
            }
        }
    }

    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromMinutes(1);

    private static string Mask(string number) => number.Length <= 4 ? "***" : $"***{number[^4..]}";
}
