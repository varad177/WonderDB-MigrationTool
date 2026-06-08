using MongoDB.Driver;

namespace WonderDB.MigrationTool.Providers;

/// <summary>
/// Contract for versioned MongoDB migrations.
/// Each migration class implements Up() and Down() to define forward and rollback logic.
/// </summary>
public interface IMongoMigration
{
    /// <summary>
    /// Unique version number for ordering. Lower versions run first.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Human-readable name describing the migration.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Apply the migration — create collections, indexes, seed data, etc.
    /// </summary>
    Task Up(IMongoDatabase database);

    /// <summary>
    /// Reverse the migration — drop collections, remove indexes, etc.
    /// </summary>
    Task Down(IMongoDatabase database);
}
