using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure.Data;

namespace Hive.Infrastructure;

/// <summary>Adds an audit_log row to the current unit of work (saved with the caller's SaveChanges).</summary>
public static class Audit
{
    public static void Record(HiveDbContext db, DateTimeOffset now, string actor, string action, string? target = null, object? details = null) =>
        db.Audit.Add(new AuditEntry
        {
            Ts = now,
            Actor = actor,
            Action = action,
            Target = target,
            Details = details is null ? null : JsonSerializer.SerializeToDocument(details),
        });
}
