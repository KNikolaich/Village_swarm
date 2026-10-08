using System.Net.Http.Headers;
using Hive.Contracts.Messages;
using Hive.Simulator.Roles;

namespace Hive.Simulator;

/// <summary>Drives one <see cref="VirtualHornet"/> against a real broker: reconnects, scripted outages, photo uploads.</summary>
public sealed class HornetRunner(VirtualHornet hornet, IHornetLink link, HttpClient http, TimeProvider time)
{
    private static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan UploadRetry = TimeSpan.FromSeconds(30);

    private readonly Queue<ScriptStep> _script = new(hornet.Spec.Script.OrderBy(s => s.AtS));
    private DateTimeOffset _startedAt;
    private DateTimeOffset _offlineUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _nextConnectAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextUpload = DateTimeOffset.MinValue;
    private TimeSpan _backoff = TimeSpan.FromSeconds(1);

    public async Task RunAsync(CancellationToken ct)
    {
        link.MessageReceived += (topic, payload) =>
        {
            hornet.Receive(topic, payload);
            return Task.CompletedTask;
        };

        _startedAt = time.GetUtcNow();
        hornet.Start(_startedAt);
        Log($"start type={hornet.Role.Type} boot={hornet.Boot}");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = time.GetUtcNow();
                await RunScriptAsync(now, ct);
                await EnsureConnectedAsync(now, ct);

                hornet.Step(now);
                await PublishAsync(hornet.TakePending(link.IsConnected), ct);

                if (hornet.RebootRequested)
                    await RebootAsync(time.GetUtcNow(), "software", ct);

                if (link.IsConnected && now >= _nextUpload)
                    await UploadPhotosAsync(now, ct);

                await Task.Delay(StepInterval, time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (link.IsConnected)
                await link.DropAsync(CancellationToken.None); // LWT marks the hornet offline
            Log("stopped");
        }
    }

    private async Task RunScriptAsync(DateTimeOffset now, CancellationToken ct)
    {
        while (_script.TryPeek(out var step) && now - _startedAt >= TimeSpan.FromSeconds(step.AtS))
        {
            _script.Dequeue();
            switch (step.Action)
            {
                case "offline":
                    Log($"script: offline for {step.ForS}s");
                    _offlineUntil = now.AddSeconds(step.ForS);
                    if (link.IsConnected)
                        await link.DropAsync(ct);
                    break;
                case "reboot":
                    Log("script: reboot");
                    await RebootAsync(now, "panic", ct);
                    break;
                default:
                    Log($"script: unknown action '{step.Action}' ignored");
                    break;
            }
        }
    }

    private async Task RebootAsync(DateTimeOffset now, string reason, CancellationToken ct)
    {
        hornet.RebootRequested = false;
        if (link.IsConnected)
            await link.DropAsync(ct);
        hornet.Reboot(now, reason);
        _nextConnectAttempt = now.AddSeconds(2); // boot time
        Log($"rebooted boot={hornet.Boot} reason={reason}");
    }

    private async Task EnsureConnectedAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (link.IsConnected || now < _offlineUntil || now < _nextConnectAttempt)
            return;
        try
        {
            await link.ConnectAsync(hornet.Id, ct);
            _backoff = TimeSpan.FromSeconds(1);
            var flushed = hornet.OutboxCount;
            await PublishAsync(hornet.OnConnected(now).ToList(), ct);
            Log(flushed > 0 ? $"online, flushed {flushed} buffered messages" : "online");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Exponential backoff 1 -> 60 s with jitter (spec 4.5).
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
            _nextConnectAttempt = now + _backoff + jitter;
            Log($"connect failed ({ex.Message}), retry in {_backoff.TotalSeconds:0}s");
            _backoff = TimeSpan.FromSeconds(Math.Min(_backoff.TotalSeconds * 2, 60));
        }
    }

    private async Task PublishAsync(IReadOnlyList<Outgoing> messages, CancellationToken ct)
    {
        foreach (var message in messages)
            await link.PublishAsync(message.Topic, message.Payload, message.Retain, message.Qos1, ct);
    }

    private async Task UploadPhotosAsync(DateTimeOffset now, CancellationToken ct)
    {
        var uploads = hornet.Role.TakeUploads();
        if (uploads.Count == 0)
            return;

        var failed = new List<PhotoUpload>();
        foreach (var upload in uploads)
        {
            var (outcome, detail) = await PhotoUploader.UploadAsync(http, hornet.Id, upload, token: null, ct);
            switch (outcome)
            {
                case UploadOutcome.Retry:
                    failed.Add(upload);
                    hornet.EmitLog(HornetLogLevel.Warn, "photo upload failed, kept on SD", now, new { photo = upload.PhotoId, error = detail });
                    break;
                case UploadOutcome.Rejected:
                    hornet.EmitLog(HornetLogLevel.Error, "photo rejected by hive, dropped", now, new { photo = upload.PhotoId, error = detail });
                    break;
            }
        }

        if (failed.Count > 0)
        {
            hornet.Role.ReturnFailedUploads(failed);
            _nextUpload = now + UploadRetry;
            Log($"{failed.Count}/{uploads.Count} photo uploads failed, retry in {UploadRetry.TotalSeconds:0}s");
        }
        else
        {
            Log($"uploaded {uploads.Count} photos");
        }
    }

    private void Log(string message) => Console.WriteLine($"{time.GetLocalNow():HH:mm:ss} [{hornet.Id}] {message}");
}
