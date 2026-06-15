using System.Text.Json;
using Spectre.Console;

namespace WonderDB.MigrationTool.Discovery;

/// <summary>
/// Persists the schema name used per DbContext so that every operation
/// (Generate → Status → Migrate) uses a consistent schema automatically.
///
/// Stores data in {infrastructurePath}/.wonderdb-schemas.json
/// One entry per context name, e.g.:
///   { "InventorySqlDbContext": "hydrabad", "InventoryPgDbContext": "public" }
/// </summary>
public class SchemaStore
{
    private const string FileName = ".wonderdb-schemas.json";

    /// <summary>
    /// Save (or update) the schema for a given context.
    /// Called automatically after a successful Generate operation.
    /// </summary>
    public void Save(string infrastructurePath, string contextName, string schemaName)
    {
        var filePath = GetFilePath(infrastructurePath);
        var dict = Load(filePath);

        dict[contextName] = schemaName;

        var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Load the previously saved schema for a context.
    /// Returns null if no schema was ever saved (first time use).
    /// </summary>
    public string? Get(string infrastructurePath, string contextName)
    {
        var filePath = GetFilePath(infrastructurePath);
        var dict = Load(filePath);
        return dict.TryGetValue(contextName, out var schema) ? schema : null;
    }

    /// <summary>
    /// Returns all saved context → schema mappings for an infrastructure project.
    /// </summary>
    public Dictionary<string, string> GetAll(string infrastructurePath)
        => Load(GetFilePath(infrastructurePath));

    // ──────────────────────────────────────────────────────────────────────────

    private static string GetFilePath(string infrastructurePath)
        => Path.Combine(infrastructurePath, FileName);

    private static Dictionary<string, string> Load(string filePath)
    {
        if (!File.Exists(filePath))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(filePath);
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return raw is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(raw, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            AnsiConsole.MarkupLine("[yellow]⚠ Could not read .wonderdb-schemas.json — using defaults.[/]");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
