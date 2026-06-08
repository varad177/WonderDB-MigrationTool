using System.Text.Json;
using System.Text.Json.Serialization;
using WonderDB.MigrationTool.Discovery;

namespace WonderDB.MigrationTool.Audit;

/// <summary>
/// A single audit log entry recording a migration operation.
/// </summary>
public class AuditEntry
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("project")]
    public string Project { get; set; } = string.Empty;

    [JsonPropertyName("database")]
    public string Database { get; set; } = string.Empty;

    [JsonPropertyName("context")]
    public string Context { get; set; } = string.Empty;

    [JsonPropertyName("migrationName")]
    public string MigrationName { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("executedBy")]
    public string ExecutedBy { get; set; } = string.Empty;

    [JsonPropertyName("result")]
    public string Result { get; set; } = string.Empty;

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Logs every migration operation to a JSON-lines file (wonderdb-audit.log)
/// and optionally to a _migration_audit table in the target database.
/// </summary>
public class AuditLogger
{
    private const string AuditFileName = "wonderdb-audit.log";
    private readonly string _auditFilePath;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AuditLogger()
    {
        _auditFilePath = Path.Combine(Directory.GetCurrentDirectory(), AuditFileName);
    }

    /// <summary>
    /// Append an audit entry to the local JSON-lines log file.
    /// </summary>
    public async Task LogAsync(AuditEntry entry)
    {
        entry.Timestamp = DateTime.UtcNow;

        if (string.IsNullOrEmpty(entry.ExecutedBy))
            entry.ExecutedBy = Environment.UserName;

        var json = JsonSerializer.Serialize(entry, SerializerOptions);
        await File.AppendAllTextAsync(_auditFilePath, json + Environment.NewLine);
    }

    /// <summary>
    /// Read the full audit history from the log file, newest first.
    /// </summary>
    public async Task<List<AuditEntry>> GetHistoryAsync(int? limit = null)
    {
        if (!File.Exists(_auditFilePath))
            return new List<AuditEntry>();

        var lines = await File.ReadAllLinesAsync(_auditFilePath);
        var entries = new List<AuditEntry>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var entry = JsonSerializer.Deserialize<AuditEntry>(line, SerializerOptions);
                if (entry != null)
                    entries.Add(entry);
            }
            catch (JsonException)
            {
                // Skip malformed lines silently
            }
        }

        entries = entries.OrderByDescending(e => e.Timestamp).ToList();

        if (limit.HasValue && limit.Value > 0)
            entries = entries.Take(limit.Value).ToList();

        return entries;
    }

    /// <summary>
    /// Optionally write the audit entry to a _migration_audit table in the target database.
    /// This is triggered by the --audit-db flag.
    /// </summary>
    public async Task WriteToDbAsync(string connectionString, DbProviderType providerType, AuditEntry entry)
    {
        switch (providerType)
        {
            case DbProviderType.SqlServer:
                await WriteSqlServerAuditAsync(connectionString, entry);
                break;
            case DbProviderType.PostgreSQL:
                await WritePostgresAuditAsync(connectionString, entry);
                break;
            case DbProviderType.MongoDB:
                await WriteMongoAuditAsync(connectionString, entry);
                break;
            default:
                // SQLite / Unknown — skip DB audit silently
                break;
        }
    }

    private static async Task WriteSqlServerAuditAsync(string connectionString, AuditEntry entry)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();

        // Ensure the audit table exists
        await using var createCmd = connection.CreateCommand();
        createCmd.CommandText = """
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = '_migration_audit')
            CREATE TABLE _migration_audit (
                Id          INT IDENTITY(1,1) PRIMARY KEY,
                Timestamp   DATETIME2       NOT NULL,
                Project     NVARCHAR(500),
                DatabaseName NVARCHAR(500),
                Context     NVARCHAR(500),
                MigrationName NVARCHAR(500),
                Mode        NVARCHAR(100),
                ExecutedBy  NVARCHAR(200),
                Result      NVARCHAR(100),
                ErrorMessage NVARCHAR(MAX)
            )
            """;
        await createCmd.ExecuteNonQueryAsync();

        await using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO _migration_audit
                (Timestamp, Project, DatabaseName, Context, MigrationName, Mode, ExecutedBy, Result, ErrorMessage)
            VALUES
                (@ts, @proj, @db, @ctx, @mig, @mode, @user, @result, @err)
            """;
        insertCmd.Parameters.AddWithValue("@ts", entry.Timestamp);
        insertCmd.Parameters.AddWithValue("@proj", entry.Project);
        insertCmd.Parameters.AddWithValue("@db", entry.Database);
        insertCmd.Parameters.AddWithValue("@ctx", entry.Context);
        insertCmd.Parameters.AddWithValue("@mig", entry.MigrationName);
        insertCmd.Parameters.AddWithValue("@mode", entry.Mode);
        insertCmd.Parameters.AddWithValue("@user", entry.ExecutedBy);
        insertCmd.Parameters.AddWithValue("@result", entry.Result);
        insertCmd.Parameters.AddWithValue("@err", (object?)entry.ErrorMessage ?? DBNull.Value);
        await insertCmd.ExecuteNonQueryAsync();
    }

    private static async Task WritePostgresAuditAsync(string connectionString, AuditEntry entry)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var createCmd = connection.CreateCommand();
        createCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS _migration_audit (
                id              SERIAL PRIMARY KEY,
                timestamp       TIMESTAMPTZ     NOT NULL,
                project         VARCHAR(500),
                database_name   VARCHAR(500),
                context         VARCHAR(500),
                migration_name  VARCHAR(500),
                mode            VARCHAR(100),
                executed_by     VARCHAR(200),
                result          VARCHAR(100),
                error_message   TEXT
            )
            """;
        await createCmd.ExecuteNonQueryAsync();

        await using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO _migration_audit
                (timestamp, project, database_name, context, migration_name, mode, executed_by, result, error_message)
            VALUES
                (@ts, @proj, @db, @ctx, @mig, @mode, @user, @result, @err)
            """;
        insertCmd.Parameters.AddWithValue("@ts", entry.Timestamp);
        insertCmd.Parameters.AddWithValue("@proj", entry.Project);
        insertCmd.Parameters.AddWithValue("@db", entry.Database);
        insertCmd.Parameters.AddWithValue("@ctx", entry.Context);
        insertCmd.Parameters.AddWithValue("@mig", entry.MigrationName);
        insertCmd.Parameters.AddWithValue("@mode", entry.Mode);
        insertCmd.Parameters.AddWithValue("@user", entry.ExecutedBy);
        insertCmd.Parameters.AddWithValue("@result", entry.Result);
        insertCmd.Parameters.AddWithValue("@err", (object?)entry.ErrorMessage ?? DBNull.Value);
        await insertCmd.ExecuteNonQueryAsync();
    }

    private static async Task WriteMongoAuditAsync(string connectionString, AuditEntry entry)
    {
        var client = new MongoDB.Driver.MongoClient(connectionString);
        var url = new MongoDB.Driver.MongoUrl(connectionString);
        var databaseName = url.DatabaseName ?? "admin";
        var database = client.GetDatabase(databaseName);
        var collection = database.GetCollection<AuditEntry>("_migration_audit");
        await collection.InsertOneAsync(entry);
    }
}
