using System.Text.Json;
using System.Threading.Channels;
using Hive.Contracts;
using Hive.Contracts.Messages;
using Hive.Contracts.Mqtt;
using Hive.Simulator.Roles;

namespace Hive.Simulator;

/// <summary>Envelope fields for one outgoing message (spec 4.3).</summary>
public readonly record struct Envelope(string Id, long Ts, long Seq, int Boot);

/// <summary>Result of a command, published as cmd/{name}/ack.</summary>
public sealed record CommandResult(AckStatus Status, string? Msg = null, IReadOnlyDictionary<string, object>? State = null)
{
    public static CommandResult Ok(IReadOnlyDictionary<string, object>? state = null) => new(AckStatus.Ok, State: state);
    public static CommandResult Rejected(string msg) => new(AckStatus.Rejected, msg);
}

/// <summary>Outgoing MQTT message. Events and acks (QoS 1) are buffered while offline; the rest is dropped.</summary>
public sealed record Outgoing(string Topic, string Payload, bool Retain, bool Qos1, bool Bufferable);

/// <summary>
/// One simulated hornet: envelope, LWT, outbox, commands with ttl and idempotency, retained config.
/// Behaviour that differs per device type lives in <see cref="HornetRole"/>.
/// All state changes happen on the caller of <see cref="Step"/>; incoming MQTT messages are queued.
/// </summary>
public sealed class VirtualHornet
{
    public const int OutboxLimit = 500; // camera roles buffer to SD; RAM-only roles would keep ~50 (spec 4.5)
    private const int AckCacheSize = 100;
    private static readonly TimeSpan ClockSyncDelay = TimeSpan.FromSeconds(5);

    private readonly Channel<(string Topic, string Payload)> _inbox = Channel.CreateUnbounded<(string, string)>();
    private readonly List<Outgoing> _pending = [];
    private readonly Queue<Outgoing> _outbox = new();
    private readonly LinkedList<(string Cid, Outgoing Ack)> _ackCache = new();
    private readonly Random _random;

    private long _seq;
    private DateTimeOffset _bootAt;
    private DateTimeOffset _lastHealth = DateTimeOffset.MinValue;
    private string _resetReason = "poweron";

    public VirtualHornet(HornetSpec spec, HornetRole role, int boot = 1, Random? random = null)
    {
        Spec = spec;
        Role = role;
        Boot = boot;
        _random = random ?? new Random();
        role.Attach(this);
    }

    public HornetSpec Spec { get; }
    public HornetRole Role { get; }
    public string Id => Spec.Id;
    public int Boot { get; private set; }
    public int HealthIntervalS { get; private set; }
    public DeviceConfig? Config { get; private set; }
    public Random Random => _random;
    public int OutboxCount => _outbox.Count;

    public void Start(DateTimeOffset now)
    {
        _bootAt = now;
        _seq = 0;
        HealthIntervalS = Config?.HealthIntervalS ?? Spec.HealthIntervalS;
        Role.OnBoot(now);
    }

    /// <summary>Queues an incoming message; it is handled on the next <see cref="Step"/>.</summary>
    public void Receive(string topic, string payload) => _inbox.Writer.TryWrite((topic, payload));

    /// <summary>Advances the hornet: handles incoming messages, runs the role, emits health.</summary>
    public void Step(DateTimeOffset now)
    {
        while (_inbox.Reader.TryRead(out var message))
            Handle(message.Topic, message.Payload, now);

        Role.Tick(now);

        if (now - _lastHealth >= TimeSpan.FromSeconds(HealthIntervalS))
        {
            _lastHealth = now;
            EmitHealth(now);
        }
    }

    /// <summary>Messages to send after a (re)connect: status, info, then the buffered outbox.</summary>
    public IEnumerable<Outgoing> OnConnected(DateTimeOffset now)
    {
        yield return new Outgoing(Topics.Status(Id), "online", Retain: true, Qos1: true, Bufferable: false);
        yield return Message(Topics.Info(Id), BuildInfo(now), retain: true, qos1: true);
        while (_outbox.TryDequeue(out var buffered))
            yield return buffered;
    }

    /// <summary>Takes the messages produced since the last call. Offline: bufferable ones go to the outbox.</summary>
    public IReadOnlyList<Outgoing> TakePending(bool connected)
    {
        var taken = _pending.ToList();
        _pending.Clear();
        if (connected)
            return taken;

        foreach (var message in taken.Where(m => m.Bufferable))
        {
            if (_outbox.Count >= OutboxLimit)
                _outbox.Dequeue();
            _outbox.Enqueue(message);
        }
        return [];
    }

    /// <summary>Simulated reboot: new boot number, seq restarts, clock unsynced for a few seconds.</summary>
    public void Reboot(DateTimeOffset now, string reason)
    {
        Boot++;
        _resetReason = reason;
        _lastHealth = DateTimeOffset.MinValue;
        _pending.Clear(); // RAM is lost; the SD outbox survives
        Start(now);
    }

    public Envelope NextEnvelope(DateTimeOffset now)
    {
        var synced = now - _bootAt >= ClockSyncDelay;
        return new Envelope(Ulid.New(now), synced ? now.ToUnixTimeMilliseconds() : 0, ++_seq, Boot);
    }

    public void Emit(string topic, object message, bool retain = false, bool qos1 = false) =>
        _pending.Add(Message(topic, message, retain, qos1));

    public void EmitEvent(string type, EventMessage message) =>
        Emit(Topics.Event(Id, type), message, qos1: true);

    public void EmitTele(string metric, double value, string? unit, DateTimeOffset now)
    {
        var e = NextEnvelope(now);
        Emit(Topics.Tele(Id, metric), new TeleMessage
        {
            Id = e.Id, Ts = e.Ts, Seq = e.Seq, Boot = e.Boot,
            Value = Math.Round(value, 2), Unit = unit, Sensor = "sim",
        });
    }

    public void EmitState(IReadOnlyDictionary<string, object> state, DateTimeOffset now)
    {
        var e = NextEnvelope(now);
        Emit(Topics.State(Id), new StateMessage
        {
            Id = e.Id, Ts = e.Ts, Seq = e.Seq, Boot = e.Boot,
            State = state.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)),
        }, retain: true, qos1: true);
    }

    public void EmitLog(HornetLogLevel level, string msg, DateTimeOffset now, object? ctx = null)
    {
        var e = NextEnvelope(now);
        Emit(Topics.Log(Id), new LogMessage
        {
            Id = e.Id, Ts = e.Ts, Seq = e.Seq, Boot = e.Boot,
            Level = level, Msg = msg, Ctx = ctx is null ? null : JsonSerializer.SerializeToElement(ctx, ContractJson.Options),
        });
    }

    private Outgoing Message(string topic, object message, bool retain, bool qos1) =>
        new(topic, JsonSerializer.Serialize(message, message.GetType(), ContractJson.Options), retain, qos1, Bufferable: qos1 && !retain);

    private void Handle(string topic, string payload, DateTimeOffset now)
    {
        if (topic == Topics.Config(Id))
        {
            ApplyConfig(payload, now);
            return;
        }

        var cmdPrefix = Topics.Device(Id) + "/cmd/";
        if (topic.StartsWith(cmdPrefix, StringComparison.Ordinal) && !topic.EndsWith("/ack", StringComparison.Ordinal))
            HandleCommand(topic[cmdPrefix.Length..], payload, now);
        // vs/v1/hive/#: heartbeats and broadcasts are not simulated yet.
    }

    private void ApplyConfig(string payload, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(payload))
            return; // retained config cleared
        DeviceConfig config;
        try
        {
            config = ContractJson.Deserialize<DeviceConfig>(payload);
        }
        catch (JsonException ex)
        {
            EmitLog(HornetLogLevel.Error, $"bad config: {ex.Message}", now);
            return;
        }
        if (Config?.Rev == config.Rev)
            return;

        Config = config;
        HealthIntervalS = config.HealthIntervalS;
        Role.ApplyConfig(config, now);

        var e = NextEnvelope(now);
        EmitEvent("config_applied", new ConfigAppliedEvent { Id = e.Id, Ts = e.Ts, Seq = e.Seq, Boot = e.Boot, Rev = config.Rev });
    }

    private void HandleCommand(string name, string payload, DateTimeOffset now)
    {
        string cid;
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(payload).RootElement;
            cid = root.GetProperty("cid").GetString() ?? throw new JsonException("cid is null");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            EmitLog(HornetLogLevel.Warn, $"bad command {name}: {ex.Message}", now);
            return;
        }

        // Idempotency: a repeated cid is not executed again, the original ack is re-sent.
        var cached = _ackCache.FirstOrDefault(c => c.Cid == cid);
        if (cached.Ack is not null)
        {
            _pending.Add(cached.Ack);
            return;
        }

        var result = Execute(name, root, now);
        var e = NextEnvelope(now);
        var ack = Message(Topics.CmdAck(Id, name), new AckMessage
        {
            Id = e.Id, Ts = e.Ts, Seq = e.Seq, Boot = e.Boot,
            Cid = cid, Status = result.Status, Msg = result.Msg,
            State = result.State?.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)),
        }, retain: false, qos1: true);
        _pending.Add(ack);

        _ackCache.AddFirst((cid, ack));
        if (_ackCache.Count > AckCacheSize)
            _ackCache.RemoveLast();

        if (name == "reboot" && result.Status == AckStatus.Ok)
            RebootRequested = true;
    }

    /// <summary>Set by cmd/reboot; the runner drops the connection and calls <see cref="Reboot"/>.</summary>
    public bool RebootRequested { get; set; }

    private CommandResult Execute(string name, JsonElement root, DateTimeOffset now)
    {
        if (!root.TryGetProperty("ts", out var tsEl) || !root.TryGetProperty("ttl_s", out var ttlEl))
            return new CommandResult(AckStatus.Error, "ts and ttl_s are required");

        var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(tsEl.GetInt64()).AddSeconds(ttlEl.GetInt32());
        if (now > expiresAt)
            return new CommandResult(AckStatus.Expired, $"expired at {expiresAt:O}");

        return name switch
        {
            "reboot" => CommandResult.Ok(),
            "config_reload" => CommandResult.Ok(),
            _ => Role.HandleCommand(name, root, now) ?? new CommandResult(AckStatus.Unsupported, $"unknown command {name}"),
        };
    }

    private void EmitHealth(DateTimeOffset now)
    {
        var e = NextEnvelope(now);
        Emit(Topics.Health(Id), new HealthMessage
        {
            Id = e.Id, Ts = e.Ts, Seq = e.Seq, Boot = e.Boot,
            UptimeS = (long)(now - _bootAt).TotalSeconds,
            Rssi = _random.Next(-78, -55),
            HeapFree = _random.Next(120_000, 160_000),
            Vbat = Math.Round(4.9 + _random.NextDouble() * 0.2, 2),
            ResetReason = _resetReason,
            Outbox = _outbox.Count,
            Broker = BrokerNode.Home,
        });
    }

    private InfoMessage BuildInfo(DateTimeOffset now)
    {
        var e = NextEnvelope(now);
        return new InfoMessage
        {
            Id = e.Id, Ts = e.Ts, Seq = e.Seq, Boot = e.Boot,
            Type = Role.Type,
            Hw = Role.Hw,
            Fw = Spec.Fw,
            Mac = FakeMac(Id),
            Ip = $"192.168.20.{100 + (IdHash(Id)[6] % 100)}",
            Caps = Role.Caps,
            Metrics = Role.Metrics,
            Commands = ["reboot", "config_reload", .. Role.Commands],
        };
    }

    private static byte[] IdHash(string id) =>
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id));

    private static string FakeMac(string id)
    {
        var hash = IdHash(id);
        hash[0] = 0x02; // locally administered
        return string.Join(':', hash.Take(6).Select(b => b.ToString("X2")));
    }
}
