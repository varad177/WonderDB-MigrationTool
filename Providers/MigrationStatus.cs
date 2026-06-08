namespace WonderDB.MigrationTool.Providers;

/// <summary>
/// Represents the status of a single migration — whether it has been applied or is still pending.
/// </summary>
public class MigrationStatus
{
    /// <summary>
    /// Unique identifier for the migration (e.g., "20240101120000_InitialCreate" or "v1").
    /// </summary>
    public string MigrationId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable migration name.
    /// </summary>
    public string MigrationName { get; set; } = string.Empty;

    /// <summary>
    /// True if this migration has been applied to the database.
    /// </summary>
    public bool IsApplied { get; set; }

    /// <summary>
    /// Timestamp when the migration was applied (null if pending or unknown).
    /// </summary>
    public DateTime? AppliedOn { get; set; }
}
