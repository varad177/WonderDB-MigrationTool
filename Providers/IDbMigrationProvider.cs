namespace WonderDB.MigrationTool.Providers;

/// <summary>
/// Contract for all database migration providers.
/// Every provider (EF Core, MongoDB, etc.) must implement this interface.
/// </summary>
public interface IDbMigrationProvider
{
    /// <summary>
    /// Apply all pending migrations to the database.
    /// </summary>
    Task MigrateAsync(MigrationContext context);

    /// <summary>
    /// Get the status of all migrations (applied vs pending).
    /// </summary>
    Task<IEnumerable<MigrationStatus>> GetStatusAsync(MigrationContext context);

    /// <summary>
    /// Preview pending migrations without applying any changes.
    /// </summary>
    Task DryRunAsync(MigrationContext context);

    /// <summary>
    /// Revert the database to a specific migration.
    /// </summary>
    Task RollbackAsync(MigrationContext context, string targetMigration);

    /// <summary>
    /// Scaffold a new migration file.
    /// </summary>
    Task GenerateAsync(MigrationContext context, string migrationName);
}
