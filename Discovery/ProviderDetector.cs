using System.Text.RegularExpressions;

namespace WonderDB.MigrationTool.Discovery;

/// <summary>
/// Supported database provider types.
/// </summary>
public enum DbProviderType
{
    SqlServer,
    PostgreSQL,
    MongoDB,
    SQLite,
    Unknown
}

/// <summary>
/// Detects the database provider type from a connection string by matching known patterns.
/// </summary>
public class ProviderDetector
{
    /// <summary>
    /// Analyse a connection string and return the detected provider type.
    /// Detection priority: MongoDB → SQLite → PostgreSQL → SQL Server.
    /// </summary>
    public DbProviderType Detect(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return DbProviderType.Unknown;

        // MongoDB: starts with mongodb:// or mongodb+srv://
        if (connectionString.StartsWith("mongodb://", StringComparison.OrdinalIgnoreCase) ||
            connectionString.StartsWith("mongodb+srv://", StringComparison.OrdinalIgnoreCase))
        {
            return DbProviderType.MongoDB;
        }

        // SQLite: Data Source=<something>.db
        if (Regex.IsMatch(connectionString, @"Data\s+Source\s*=\s*.*\.db", RegexOptions.IgnoreCase))
        {
            return DbProviderType.SQLite;
        }

        // PostgreSQL: Host= keyword or Port= keyword (SQL Server uses "Server=...,1433" instead of Port=)
        if (Regex.IsMatch(connectionString, @"Host\s*=", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(connectionString, @"Port\s*=", RegexOptions.IgnoreCase))
        {
            return DbProviderType.PostgreSQL;
        }

        // SQL Server: Server= or Data Source= (without .db extension, already checked above)
        if (Regex.IsMatch(connectionString, @"Server\s*=", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(connectionString, @"Data\s+Source\s*=", RegexOptions.IgnoreCase))
        {
            return DbProviderType.SqlServer;
        }

        return DbProviderType.Unknown;
    }

    /// <summary>
    /// Returns a friendly display name for the provider type.
    /// </summary>
    public static string GetDisplayName(DbProviderType providerType)
    {
        return providerType switch
        {
            DbProviderType.SqlServer => "SQL Server",
            DbProviderType.PostgreSQL => "PostgreSQL",
            DbProviderType.MongoDB => "MongoDB",
            DbProviderType.SQLite => "SQLite",
            _ => "Unknown"
        };
    }
}
