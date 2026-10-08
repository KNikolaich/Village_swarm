namespace Hive.Domain;

/// <summary>Role of a hive node in the home + city pair (spec 11.3).</summary>
public enum NodeRole
{
    Active,
    Standby,
    Observer,
}
