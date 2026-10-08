using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Hive.Modules.Telemetry.Ingest;

/// <summary>Writes one deduplicated batch in a single transaction (spec 6.3 IngestPipeline).</summary>
public sealed class IngestWriter(HiveDbContext db)
{
    public async Task WriteAsync(IReadOnlyList<IngestItem> items, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await UpdateDevicesAsync(items, ct);
        db.DeviceHealth.AddRange(items.OfType<DeviceHealthReceived>().Select(h => new DeviceHealth
        {
            DeviceId = h.DeviceId, Ts = h.Ts, UptimeS = h.UptimeS, Rssi = h.Rssi, HeapFree = h.HeapFree,
            Vbat = h.Vbat, ResetReason = h.ResetReason, ClockSkew = h.ClockSkew,
        }));
        db.DeviceLogs.AddRange(items.OfType<DeviceLogReceived>().Select(l => new DeviceLog
        {
            DeviceId = l.DeviceId, Ts = l.Ts, Level = l.Level, Msg = l.Msg, Ctx = l.Ctx,
        }));
        await db.SaveChangesAsync(ct);

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await InsertEventsAsync(connection, items.OfType<DeviceEventReceived>().ToList(), ct);
        await CopyTelemetryAsync(connection, items.OfType<TelemetryReceived>().ToList(), ct);

        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task UpdateDevicesAsync(IReadOnlyList<IngestItem> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.DeviceId).Distinct().ToList();
        var devices = await db.Devices.Where(d => ids.Contains(d.DeviceId)).ToDictionaryAsync(d => d.DeviceId, ct);
        var knownTypes = await db.DeviceTypes.Select(t => t.Code).ToHashSetAsync(ct);

        foreach (var item in items)
        {
            if (item is DeviceInfoReceived announced && !devices.ContainsKey(announced.DeviceId))
            {
                // Until provisioning (step 10) any hornet announcing itself is registered automatically.
                if (knownTypes.Add(announced.TypeCode))
                    db.DeviceTypes.Add(new DeviceType { Code = announced.TypeCode, Title = announced.TypeCode });
                var created = new Device { DeviceId = announced.DeviceId, TypeCode = announced.TypeCode, Name = announced.DeviceId, Status = DeviceStatus.Online };
                db.Devices.Add(created);
                devices[announced.DeviceId] = created;
            }

            if (!devices.TryGetValue(item.DeviceId, out var device))
                continue; // unknown device without info yet: its data is stored, the registry waits for info

            if (device.LastSeenAt is null || item.ReceivedAt > device.LastSeenAt)
                device.LastSeenAt = item.ReceivedAt;

            switch (item)
            {
                case DeviceStatusChanged status:
                    if (device.Status != DeviceStatus.Disabled)
                        device.Status = status.Online ? DeviceStatus.Online : DeviceStatus.Offline;
                    break;
                case DeviceInfoReceived info:
                    device.TypeCode = info.TypeCode;
                    device.Hw = info.Hw;
                    device.FwVersion = info.Fw;
                    device.Mac = info.Mac;
                    device.Ip = info.Ip;
                    device.Boot = info.Boot;
                    device.Info = info.Info;
                    break;
                case DeviceHealthReceived health:
                    device.Rssi = health.Rssi;
                    device.Boot = health.Boot;
                    MarkOnline(device);
                    break;
                case DeviceStateReceived state:
                    device.State = state.State;
                    break;
                case DeviceEventReceived { Type: "config_applied" } applied
                    when applied.Payload.RootElement.TryGetProperty("rev", out var rev) && rev.TryGetInt32(out var r):
                    device.ConfigAppliedRev = r;
                    break;
            }
        }
    }

    /// <summary>Fresh health means the hornet is alive even if a stale retained "offline" arrived earlier.</summary>
    private static void MarkOnline(Device device)
    {
        if (device.Status is DeviceStatus.Offline or DeviceStatus.Pending)
            device.Status = DeviceStatus.Online;
    }

    private static async Task InsertEventsAsync(NpgsqlConnection connection, List<DeviceEventReceived> events, CancellationToken ct)
    {
        if (events.Count == 0)
            return;
        await using var batch = new NpgsqlBatch(connection);
        foreach (var e in events)
        {
            batch.BatchCommands.Add(new NpgsqlBatchCommand("""
                INSERT INTO events (id, device_id, type, severity, ts, received_at, payload, armed, via_node)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                ON CONFLICT (id) DO NOTHING
                """)
            {
                Parameters =
                {
                    new() { Value = e.MessageId },
                    new() { Value = e.DeviceId },
                    new() { Value = e.Type },
                    new() { Value = e.Severity.ToString() },
                    new() { Value = e.Ts },
                    new() { Value = e.ReceivedAt },
                    new() { Value = e.Payload.RootElement.GetRawText(), NpgsqlDbType = NpgsqlDbType.Jsonb },
                    new() { Value = (object?)e.Armed ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean },
                    new() { Value = nameof(HiveNode.Home) },
                },
            });
        }
        await batch.ExecuteNonQueryAsync(ct);
    }

    private static async Task CopyTelemetryAsync(NpgsqlConnection connection, List<TelemetryReceived> telemetry, CancellationToken ct)
    {
        if (telemetry.Count == 0)
            return;
        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY telemetry (device_id, metric, ts, value, received_at) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var t in telemetry)
        {
            foreach (var (metric, value) in t.Values)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(t.DeviceId, NpgsqlDbType.Varchar, ct);
                await writer.WriteAsync(metric, NpgsqlDbType.Varchar, ct);
                await writer.WriteAsync(t.Ts, NpgsqlDbType.TimestampTz, ct);
                await writer.WriteAsync(value, NpgsqlDbType.Double, ct);
                await writer.WriteAsync(t.ReceivedAt, NpgsqlDbType.TimestampTz, ct);
            }
        }
        await writer.CompleteAsync(ct);
    }
}
