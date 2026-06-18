namespace WonderDB.MigrationTool.Providers;

/// <summary>
/// Shared context object passed to every migration provider method.
/// Contains all information needed to connect to and migrate a specific database.
/// Uses string-based context identification — no assembly loading required.
/// </summary>
public class MigrationContext
{
    /// <summary>
    /// The simple class name of the DbContext (e.g., "AppDbContext").
    /// Used to pass --context to dotnet ef CLI.
    /// </summary>
    public string? ContextName { get; set; }

    /// <summary>
    /// Fully-qualified connection string for the target database.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Detected database provider type (SqlServer, PostgreSQL, MongoDB, SQLite).
    /// </summary>
    public Discovery.DbProviderType ProviderType { get; set; }

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
    /// The fallback default schema name detected from the DbContext (e.g., "orders").
    /// </summary>
    public string DefaultSchemaName { get; set; } = string.Empty;
}
