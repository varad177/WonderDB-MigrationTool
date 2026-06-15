using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using Spectre.Console;

namespace WonderDB.MigrationTool.Connection;

/// <summary>
/// Tests database connectivity before running any migration operations.
/// Fails fast with clear, colored error messages if the database is unreachable.
/// </summary>
public class ConnectionTester
{
    private const int TimeoutSeconds = 10;

    /// <summary>
    /// Ping the target database. Returns true on success, false on failure.
    /// All errors are caught and displayed as friendly Spectre.Console messages.
    /// </summary>
    public async Task<bool> TestConnectionAsync(string connectionString, Discovery.DbProviderType providerType)
    {
        try
        {
            return providerType switch
            {
                Discovery.DbProviderType.SqlServer => await TestSqlServerAsync(connectionString),
                Discovery.DbProviderType.PostgreSQL => await TestPostgreSQLAsync(connectionString),
                Discovery.DbProviderType.MongoDB => await TestMongoDbAsync(connectionString),
                Discovery.DbProviderType.SQLite => TestSQLite(connectionString),
                _ => throw new NotSupportedException(
                    $"Connection testing is not supported for provider '{providerType}'.")
            };
        }
        catch (NotSupportedException)
        {
            throw; // Re-throw known unsupported provider errors
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Connection failed:[/] {Markup.Escape(ex.Message)}");
            return false;
        }
    }

    private static async Task<bool> TestSqlServerAsync(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            ConnectTimeout = TimeoutSeconds
        };

        await using var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync();

            // Run a trivial query to confirm the connection is usable
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync();

            AnsiConsole.MarkupLine("[green]✓ SQL Server connection successful.[/]");
            return true;
        }
        catch (SqlException ex) when (ex.Number == 4060)
        {
            // Error 4060: Cannot open database requested by the login.
            AnsiConsole.MarkupLine("[green]✓ SQL Server reachable (target database will be created).[/]");
            return true;
        }
    }

    private static async Task<bool> TestPostgreSQLAsync(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = TimeoutSeconds
        };

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync();

            AnsiConsole.MarkupLine("[green]✓ PostgreSQL connection successful.[/]");
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == "3D000")
        {
            // Error 3D000: database does not exist
            AnsiConsole.MarkupLine("[green]✓ PostgreSQL reachable (target database will be created).[/]");
            return true;
        }
    }

    private static async Task<bool> TestMongoDbAsync(string connectionString)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ConnectTimeout = TimeSpan.FromSeconds(TimeoutSeconds);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(TimeoutSeconds);

        var client = new MongoClient(settings);
        var database = client.GetDatabase("admin");
        await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));

        AnsiConsole.MarkupLine("[green]✓ MongoDB connection successful.[/]");
        return true;
    }

    private static bool TestSQLite(string connectionString)
    {
        var match = Regex.Match(
            connectionString,
            @"Data\s+Source\s*=\s*(.+?)(?:;|$)",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            AnsiConsole.MarkupLine("[red]✗ Could not parse SQLite connection string.[/]");
            return false;
        }

        var filePath = match.Groups[1].Value.Trim();

        if (filePath == ":memory:")
        {
            AnsiConsole.MarkupLine("[green]✓ SQLite in-memory database ready.[/]");
            return true;
        }

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            AnsiConsole.MarkupLine($"[red]✗ SQLite database directory not found:[/] {Markup.Escape(directory)}");
            return false;
        }

        AnsiConsole.MarkupLine("[green]✓ SQLite database path is accessible.[/]");
        return true;
    }
}
