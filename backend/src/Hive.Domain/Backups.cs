namespace Hive.Domain;

/// <summary>
/// One run of the backup container (spec 10): a database dump, the off-site copy (restic) or a restore check.
/// The container writes these rows itself (deploy/backup/lib.sh); the API only reads and watches them.
/// </summary>
public class BackupRun
{
    public long Id { get; set; }

    /// <summary>dump | offsite | verify</summary>
    public required string Kind { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public bool Ok { get; set; }

    /// <summary>Size of the dump, or of what the off-site copy added.</summary>
    public long? Bytes { get; set; }

    /// <summary>What was done, or why it failed (one line).</summary>
    public string? Detail { get; set; }
}
