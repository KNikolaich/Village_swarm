using System.Text.Json;
using Hive.Contracts.Messages;

namespace Hive.Simulator.Roles;

/// <summary>guard-cam (spec 5.4): random PIR motion, photo burst, HTTP upload, event/motion; arm and snapshot commands.</summary>
public sealed class GuardCamRole(MotionSpec motion, PhotoLibrary photos) : HornetRole
{
    private readonly List<PhotoUpload> _uploads = [];
    private DateTimeOffset _nextMotion;
    private DateTimeOffset _lastMotion = DateTimeOffset.MinValue;

    public bool Armed { get; private set; } = motion.Armed;
    public int CooldownS { get; private set; } = 20;
    public bool RecordWhenDisarmed { get; private set; }
    public int Burst { get; private set; } = motion.Burst;
    public string UploadUrl { get; private set; } = motion.UploadUrl;

    public override string Type => "guard-cam";
    public override string Hw => "esp32cam-aithinker";
    public override IReadOnlyList<string> Caps => ["camera", "motion.pir", "led.ir", "sd"];
    public override IReadOnlyList<string> Commands => ["arm", "snapshot"];

    public override void OnBoot(DateTimeOffset now) => ScheduleNextMotion(now);

    public override void Tick(DateTimeOffset now)
    {
        if (motion.EverySeconds <= 0 || now < _nextMotion)
            return;
        ScheduleNextMotion(now);
        TriggerMotion(now);
    }

    /// <summary>PIR rising edge. Public so tests and scripted scenarios can fire it directly.</summary>
    public void TriggerMotion(DateTimeOffset now)
    {
        if (now - _lastMotion < TimeSpan.FromSeconds(CooldownS))
            return; // anti-bounce
        _lastMotion = now;

        var e = Hornet.NextEnvelope(now);
        var photoIds = Armed || RecordWhenDisarmed ? Shoot(e.Id, e.Ts, Burst) : [];
        Hornet.EmitEvent("motion", new MotionEvent
        {
            Id = e.Id, Ts = e.Ts, Seq = e.Seq, Boot = e.Boot,
            Source = MotionSource.Pir,
            Armed = Armed,
            Severity = Armed ? Severity.Alarm : Severity.Info,
            Photos = photoIds,
            PhotoStatus = photoIds.Count == 0 ? PhotoStatus.None
                : string.IsNullOrEmpty(UploadUrl) ? PhotoStatus.Buffered : PhotoStatus.Uploading,
        });
    }

    public override void ApplyConfig(DeviceConfig config, DateTimeOffset now)
    {
        if (config.Guard is { } guard)
        {
            Armed = guard.Armed ?? Armed;
            CooldownS = guard.CooldownS ?? CooldownS;
            RecordWhenDisarmed = guard.RecordWhenDisarmed ?? RecordWhenDisarmed;
        }
        Burst = config.Camera?.Burst ?? Burst;
        UploadUrl = config.Upload?.Primary ?? UploadUrl;
    }

    public override CommandResult? HandleCommand(string name, JsonElement payload, DateTimeOffset now)
    {
        switch (name)
        {
            case "arm":
                if (!payload.TryGetProperty("armed", out var armed) || armed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return new CommandResult(AckStatus.Error, "armed (bool) is required");
                Armed = armed.GetBoolean();
                return CommandResult.Ok(new Dictionary<string, object> { ["armed"] = Armed });

            case "snapshot":
                var count = payload.TryGetProperty("count", out var c) ? Math.Clamp(c.GetInt32(), 1, 10) : 1;
                var e = Hornet.NextEnvelope(now);
                var ids = Shoot(e.Id, e.Ts, count);
                Hornet.EmitEvent("snapshot", new EventMessage
                {
                    Id = e.Id, Ts = e.Ts, Seq = e.Seq, Boot = e.Boot,
                    Severity = Severity.Info,
                    Extra = new Dictionary<string, JsonElement> { ["photos"] = JsonSerializer.SerializeToElement(ids) },
                });
                return CommandResult.Ok();

            default:
                return null;
        }
    }

    public override IReadOnlyList<PhotoUpload> TakeUploads()
    {
        if (string.IsNullOrEmpty(UploadUrl) || _uploads.Count == 0)
            return [];
        var taken = _uploads.ToList();
        _uploads.Clear();
        return taken;
    }

    public override void ReturnFailedUploads(IEnumerable<PhotoUpload> failed) => _uploads.AddRange(failed);

    private List<string> Shoot(string eventId, long ts, int count)
    {
        var ids = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var photoId = $"{eventId}-{i}";
            ids.Add(photoId);
            if (photos.Count > 0)
                _uploads.Add(new PhotoUpload(photoId, eventId, ts + i * motion.BurstIntervalMs, photos.Next(), UploadUrl));
        }
        return ids;
    }

    private void ScheduleNextMotion(DateTimeOffset now) =>
        _nextMotion = now.AddSeconds(motion.EverySeconds * (0.5 + Hornet.Random.NextDouble()));
}
