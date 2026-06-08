using WonderDB.MigrationTool.Discovery;

namespace WonderDB.MigrationTool.Providers;

/// <summary>
/// Shared context object passed to every migration provider method.
/// Contains all information needed to connect to and migrate a specific database.
/// </summary>
public class MigrationContext
{
    /// <summary>
    /// The DbContext type discovered from the Infrastructure assembly (null for MongoDB).
    /// </summary>
    public Type? DbContextType { get; set; }

    /// <summary>
    /// Fully-qualified connection string for the target database.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Detected database provider type (SqlServer, PostgreSQL, MongoDB, SQLite).
    /// </summary>
    public DbProviderType ProviderType { get; set; }

    /// <summary>
    /// Absolute path to the Infrastructure project folder.
    /// </summary>
    public string InfrastructurePath { get; set; } = string.Empty;

    /// <summary>
    /// Name of the parent project (e.g., "OrderService").
    /// </summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// Logical database name from the connection string key (e.g., "OrderDb").
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Schema name derived from the DbContext (e.g., "orders").
    /// </summary>
    public string SchemaName { get; set; } = string.Empty;

    /// <summary>
    /// The loaded Infrastructure assembly (used for MongoDB migration discovery).
    /// </summary>
    public System.Reflection.Assembly? InfrastructureAssembly { get; set; }
}
