using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Spectre.Console;

namespace WonderDB.MigrationTool.Providers;

/// <summary>
/// Tracks which MongoDB migrations have been applied.
/// Stored in the _migration_history collection.
/// </summary>
public class MongoMigrationHistory
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("version")]
    public int Version { get; set; }

    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("appliedOn")]
    public DateTime AppliedOn { get; set; }
}

/// <summary>
/// MongoDB implementation of <see cref="IDbMigrationProvider"/>.
/// Uses MongoDB.Driver for collection and index management.
/// Migrations are versioned C# classes implementing IMongoMigration.
/// Applied migrations are tracked in the _migration_history collection.
/// </summary>
public class MongoMigrationProvider : IDbMigrationProvider
{
    private const string HistoryCollectionName = "_migration_history";

    public async Task MigrateAsync(MigrationContext context)
    {
        var (database, migrations) = await SetupMongoAsync(context);
        var applied = await GetAppliedVersionsAsync(database);
        var pending = migrations
            .Where(m => !applied.Contains(m.Version))
            .OrderBy(m => m.Version)
            .ToList();

        if (pending.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]✓ MongoDB is already up to date.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[yellow]Applying {pending.Count} pending MongoDB migration(s)...[/]");

        foreach (var migration in pending)
        {
            AnsiConsole.MarkupLine($"  [grey]→[/] v{migration.Version}: {migration.Name}");
            await migration.Up(database);

            var historyCollection = database.GetCollection<MongoMigrationHistory>(HistoryCollectionName);
            await historyCollection.InsertOneAsync(new MongoMigrationHistory
            {
                Version = migration.Version,
                Name = migration.Name,
                AppliedOn = DateTime.UtcNow
            });
        }

        AnsiConsole.MarkupLine("[green]✓ All MongoDB migrations applied successfully.[/]");
    }

    public async Task<IEnumerable<MigrationStatus>> GetStatusAsync(MigrationContext context)
    {
        var (database, migrations) = await SetupMongoAsync(context);
        var appliedHistory = await GetAppliedHistoryAsync(database);
        var appliedVersions = appliedHistory.Select(h => h.Version).ToHashSet();

        var statuses = new List<MigrationStatus>();

        foreach (var migration in migrations.OrderBy(m => m.Version))
        {
            var history = appliedHistory.FirstOrDefault(h => h.Version == migration.Version);
            statuses.Add(new MigrationStatus
            {
                MigrationId = $"v{migration.Version}",
                MigrationName = migration.Name,
                IsApplied = appliedVersions.Contains(migration.Version),
                AppliedOn = history?.AppliedOn
            });
        }

        return statuses;
    }

    public async Task DryRunAsync(MigrationContext context)
    {
        var (database, migrations) = await SetupMongoAsync(context);
        var applied = await GetAppliedVersionsAsync(database);
        var pending = migrations
            .Where(m => !applied.Contains(m.Version))
            .OrderBy(m => m.Version)
            .ToList();

        if (pending.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No pending MongoDB migrations.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[yellow]Dry run — {pending.Count} pending MongoDB migration(s):[/]");
        foreach (var migration in pending)
        {
            AnsiConsole.MarkupLine($"  [yellow]→[/] v{migration.Version}: {migration.Name}");
        }

        AnsiConsole.MarkupLine("\n[yellow]No changes were applied (dry run mode).[/]");
    }

    public async Task RollbackAsync(MigrationContext context, string targetMigration)
    {
        var (database, migrations) = await SetupMongoAsync(context);

        int targetVersion;
        if (!int.TryParse(targetMigration.TrimStart('v', 'V'), out targetVersion))
        {
            // Try to find by name
            var target = migrations.FirstOrDefault(m =>
                m.Name.Equals(targetMigration, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                throw new InvalidOperationException(
                    $"MongoDB migration '{targetMigration}' not found. " +
                    $"Use 'wonderdb status' to see available migrations.");
            targetVersion = target.Version;
        }

        var appliedHistory = await GetAppliedHistoryAsync(database);
        var toRollback = appliedHistory
            .Where(h => h.Version > targetVersion)
            .OrderByDescending(h => h.Version)
            .ToList();

        if (toRollback.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to rollback.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[yellow]Rolling back {toRollback.Count} MongoDB migration(s)...[/]");

        var historyCollection = database.GetCollection<MongoMigrationHistory>(HistoryCollectionName);

        foreach (var history in toRollback)
        {
            var migration = migrations.FirstOrDefault(m => m.Version == history.Version);
            if (migration != null)
            {
                AnsiConsole.MarkupLine($"  [grey]←[/] v{migration.Version}: {migration.Name}");
                await migration.Down(database);
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]⚠[/] v{history.Version}: {history.Name} (migration class not found, removing history only)");
            }

            await historyCollection.DeleteOneAsync(
                Builders<MongoMigrationHistory>.Filter.Eq(h => h.Version, history.Version));
        }

        AnsiConsole.MarkupLine($"[green]✓ Rolled back to v{targetVersion}.[/]");
    }

    public Task GenerateAsync(MigrationContext context, string migrationName)
    {
        var migrationsDir = Path.Combine(context.InfrastructurePath, "Migrations", "Mongo");
        Directory.CreateDirectory(migrationsDir);

        // Determine the next version number from existing migration files
        var existingFiles = Directory.GetFiles(migrationsDir, "M*.cs");
        var nextVersion = existingFiles.Length + 1;

        var className = $"M{nextVersion:D4}_{SanitizeName(migrationName)}";
        var filePath = Path.Combine(migrationsDir, $"{className}.cs");

        // Infer a namespace from the project name
        var projectName = Path.GetFileName(context.InfrastructurePath);
        var ns = $"{projectName}.Migrations.Mongo";

        var template = $$"""
            using MongoDB.Driver;
            using WonderDB.MigrationTool.Providers;

            namespace {{ns}};

            /// <summary>
            /// MongoDB migration: {{migrationName}}
            /// </summary>
            public class {{className}} : IMongoMigration
            {
                public int Version => {{nextVersion}};
                public string Name => "{{migrationName}}";

                public async Task Up(IMongoDatabase database)
                {
                    // Create collections, indexes, or seed data here.
                    // Example:
                    //   await database.CreateCollectionAsync("my_collection");
                    //   var collection = database.GetCollection<BsonDocument>("my_collection");
                    //   var indexKeys = Builders<BsonDocument>.IndexKeys.Ascending("fieldName");
                    //   await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(indexKeys));
                    await Task.CompletedTask;
                }

                public async Task Down(IMongoDatabase database)
                {
                    // Reverse the changes made in Up().
                    // Example:
                    //   await database.DropCollectionAsync("my_collection");
                    await Task.CompletedTask;
                }
            }
            """;

        File.WriteAllText(filePath, template);

        AnsiConsole.MarkupLine($"[green]✓ MongoDB migration generated:[/] {Markup.Escape(filePath)}");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Build a MongoClient and discover IMongoMigration classes from the loaded assembly.
    /// </summary>
    private static Task<(IMongoDatabase database, List<IMongoMigration> migrations)> SetupMongoAsync(
        MigrationContext context)
    {
        var client = new MongoClient(context.ConnectionString);
        var url = new MongoUrl(context.ConnectionString);
        var databaseName = url.DatabaseName
                           ?? context.DatabaseName
                           ?? "default";
        var database = client.GetDatabase(databaseName);

        var migrations = DiscoverMongoMigrations(context);

        return Task.FromResult((database, migrations));
    }

    /// <summary>
    /// Scan the Infrastructure assembly for concrete classes implementing IMongoMigration.
    /// </summary>
    private static List<IMongoMigration> DiscoverMongoMigrations(MigrationContext context)
    {
        var migrations = new List<IMongoMigration>();
        var assembly = context.InfrastructureAssembly ?? context.DbContextType?.Assembly;

        if (assembly == null)
            return migrations;

        var migrationInterface = typeof(IMongoMigration);

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray()!;
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface || !migrationInterface.IsAssignableFrom(type))
                continue;

            try
            {
                var instance = (IMongoMigration)Activator.CreateInstance(type)!;
                migrations.Add(instance);
            }
            catch
            {
                // Skip types that cannot be instantiated
            }
        }

        return migrations.OrderBy(m => m.Version).ToList();
    }

    private static async Task<HashSet<int>> GetAppliedVersionsAsync(IMongoDatabase database)
    {
        var history = await GetAppliedHistoryAsync(database);
        return history.Select(h => h.Version).ToHashSet();
    }

    private static async Task<List<MongoMigrationHistory>> GetAppliedHistoryAsync(IMongoDatabase database)
    {
        try
        {
            var collection = database.GetCollection<MongoMigrationHistory>(HistoryCollectionName);
            return await collection
                .Find(FilterDefinition<MongoMigrationHistory>.Empty)
                .SortBy(h => h.Version)
                .ToListAsync();
        }
        catch (MongoException)
        {
            return new List<MongoMigrationHistory>();
        }
    }

    /// <summary>
    /// Remove characters that are invalid in C# identifiers.
    /// </summary>
    private static string SanitizeName(string name)
    {
        return new string(name
            .Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')
            .ToArray());
    }
}
