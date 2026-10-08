namespace Hive.Domain;

/// <summary>One-time code for adding (or replacing) a hornet (spec 5.3): 8 characters, valid 30 minutes.</summary>
public class EnrollmentCode
{
    public required string Code { get; set; }
    public required string DeviceId { get; set; }
    public required string TypeCode { get; set; }
    public required string Name { get; set; }
    public int? ZoneId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public string? UsedByMac { get; set; }
    public required string CreatedBy { get; set; }
}
