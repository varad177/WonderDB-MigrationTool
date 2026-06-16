using System.Diagnostics;
using System.Text.RegularExpressions;
using Spectre.Console;
using WonderDB.MigrationTool.Discovery;

namespace WonderDB.MigrationTool.Providers;

/// <summary>
/// EF Core implementation of <see cref="IDbMigrationProvider"/>.
/// Fully CLI-based â€” runs 'dotnet ef' as a subprocess.
/// No assembly loading, no EF Core NuGet dependency required.
/// Works across .NET versions and inside Docker containers.
/// </summary>
public class EFCoreMigrationProvider : IDbMigrationProvider
{
    // Session-level cache: tracks which .csproj paths were already restored this session.
    // Avoids redundant dotnet restore calls during batch migrations.
    private static readonly HashSet<string> _restoredProjects = new(StringComparer.OrdinalIgnoreCase);
    public async Task MigrateAsync(MigrationContext context)
    {
        var tempFactoryPath = Path.Combine(context.InfrastructurePath, "WonderDb_DesignTimeFactory.cs");
        try
        {
            await WriteDesignTimeFactoryAsync(context, tempFactoryPath);
            await EnsureRestoredAsync(context);

            var args = BuildEfArgs("database update", context);
            var (exitCode, output, error) = await RunDotnetEfAsync(args, context, streamOutput: true);

            if (exitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]âœ— Database update failed![/]");
                PrintOutput(output, error);
                throw new InvalidOperationException("Failed to apply migrations.");
            }

            AnsiConsole.MarkupLine("[green]âœ“ All migrations applied successfully.[/]");
        }
        finally
        {
            CleanupTempFactory(tempFactoryPath);
        }
    }

    public async Task<IEnumerable<MigrationStatus>> GetStatusAsync(MigrationContext context)
    {
        var tempFactoryPath = Path.Combine(context.InfrastructurePath, "WonderDb_DesignTimeFactory.cs");
        try
        {
            await WriteDesignTimeFactoryAsync(context, tempFactoryPath);
            await EnsureRestoredAsync(context);

            var args = BuildEfArgs("migrations list", context);
            var (exitCode, output, error) = await RunDotnetEfAsync(args, context);

            if (exitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]âœ— Failed to list migrations![/]");
                // Print the real EF output so the user can see what went wrong
                if (!string.IsNullOrWhiteSpace(error))
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(error.Trim())}[/]");
                if (!string.IsNullOrWhiteSpace(output))
                    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(output.Trim())}[/]");
                return Enumerable.Empty<MigrationStatus>();
            }

            var statuses = new List<MigrationStatus>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("Build") || trimmed.StartsWith("info:"))
                    continue;

                // EF CLI marks pending migrations with "(Pending)" suffix
                var isPending = trimmed.Contains("(Pending)");
                var migrationName = trimmed.Replace("(Pending)", "").Trim();

                if (!string.IsNullOrEmpty(migrationName))
                {
                    statuses.Add(new MigrationStatus
                    {
                        MigrationId = migrationName,
                        MigrationName = migrationName,
                        IsApplied = !isPending,
                        AppliedOn = null
                    });
                }
            }

            return statuses;
        }
        finally
        {
            CleanupTempFactory(tempFactoryPath);
        }
    }

    public async Task DryRunAsync(MigrationContext context)
    {
        var tempFactoryPath = Path.Combine(context.InfrastructurePath, "WonderDb_DesignTimeFactory.cs");
        try
        {
            await WriteDesignTimeFactoryAsync(context, tempFactoryPath);
            await EnsureRestoredAsync(context);

            // List pending migrations
            var args = BuildEfArgs("migrations list", context);
            var (exitCode, output, error) = await RunDotnetEfAsync(args, context);

            if (exitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]âœ— Failed to list migrations![/]");
                PrintOutput(output, error);
                return;
            }

            var pendingMigrations = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Contains("(Pending)"))
                .Select(l => l.Replace("(Pending)", "").Trim())
                .ToList();

            if (pendingMigrations.Count == 0)
            {
                AnsiConsole.MarkupLine("[green]No pending migrations. Database is up to date.[/]");
                return;
            }

            AnsiConsole.MarkupLine($"[yellow]Dry run â€” {pendingMigrations.Count} pending migration(s):[/]");
            foreach (var migration in pendingMigrations)
            {
                AnsiConsole.MarkupLine($"  [yellow]â†’[/] {migration}");
            }

            // Try to generate SQL script for preview
            try
            {
                var scriptArgs = BuildEfArgs("migrations script --idempotent", context);
                var (scriptExit, scriptOutput, scriptError) = await RunDotnetEfAsync(scriptArgs, context);

                if (scriptExit == 0 && !string.IsNullOrWhiteSpace(scriptOutput))
                {
                    // Filter out build output lines
                    var sqlLines = scriptOutput
                        .Split('\n')
                        .Where(l => !l.TrimStart().StartsWith("Build") && !l.TrimStart().StartsWith("info:"))
                        .ToList();
                    var sql = string.Join('\n', sqlLines).Trim();

                    if (!string.IsNullOrWhiteSpace(sql))
                    {
                        AnsiConsole.MarkupLine("\n[blue]Generated SQL:[/]");
                        var panel = new Panel(Markup.Escape(sql))
                        {
                            Border = BoxBorder.Rounded,
                            Header = new PanelHeader("[blue]SQL Preview[/]")
                        };
                        AnsiConsole.Write(panel);
                    }
                }
            }
            catch
            {
                AnsiConsole.MarkupLine("[yellow]âš  Could not generate SQL preview.[/]");
            }

            AnsiConsole.MarkupLine("\n[yellow]No changes were applied (dry run mode).[/]");
        }
        finally
        {
            CleanupTempFactory(tempFactoryPath);
        }
    }

    public async Task RollbackAsync(MigrationContext context, string targetMigration)
    {
        var tempFactoryPath = Path.Combine(context.InfrastructurePath, "WonderDb_DesignTimeFactory.cs");
        try
        {
            await WriteDesignTimeFactoryAsync(context, tempFactoryPath);
            await EnsureRestoredAsync(context);

            AnsiConsole.MarkupLine(
                $"[yellow]Rolling back to migration:[/] [white]{targetMigration}[/]");

            var args = BuildEfArgs($"database update {targetMigration}", context);
            var (exitCode, output, error) = await RunDotnetEfAsync(args, context, streamOutput: true);

            if (exitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]âœ— Rollback failed![/]");
                throw new InvalidOperationException($"Failed to rollback to '{targetMigration}'.");
            }

            AnsiConsole.MarkupLine(
                $"[green]âœ“ Successfully rolled back to '{targetMigration}'.[/]");
        }
        finally
        {
            CleanupTempFactory(tempFactoryPath);
        }
    }

    public async Task GenerateAsync(MigrationContext context, string migrationName)
    {
        AnsiConsole.MarkupLine(
            $"[blue]Generating migration '[white]{migrationName}[/]' " +
            $"for [white]{context.ContextName ?? "DbContext"}[/]...[/]");

        var tempFactoryPath = Path.Combine(context.InfrastructurePath, "WonderDb_DesignTimeFactory.cs");
        try
        {
            await WriteDesignTimeFactoryAsync(context, tempFactoryPath);
            await EnsureRestoredAsync(context);

            // Output dir: Migrations/{Provider}/{ContextName}
            var outputDir = GetMigrationOutputDir(context.ProviderType, context.ContextName);
            var args = BuildEfArgs($"migrations add {migrationName} --output-dir {outputDir}", context);
            var (exitCode, output, error) = await RunDotnetEfAsync(args, context, streamOutput: true);

            if (exitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]âœ— Migration generation failed![/]");
                PrintOutput(output, error);
                throw new InvalidOperationException("Failed to generate migration.");
            }

            AnsiConsole.MarkupLine(
                $"[green]✓ Migration '{migrationName}' generated in '[white]{outputDir}[/]'.[/]");

            // MAGIC TRICK: Make the migration dynamically schema-aware AND restore original Context Type!
            if (!string.IsNullOrWhiteSpace(context.SchemaName))
            {
                var envReplacer = $"System.Environment.GetEnvironmentVariable(\"WONDERDB_SCHEMA\") ?? \"{context.SchemaName}\"";
                var csFiles = Directory.GetFiles(Path.Combine(context.InfrastructurePath, outputDir), "*.cs");
                
                // We must use the fully-qualified original context name so it compiles without missing 'using' directives
                var contextFullName = FindContextFullName(context.InfrastructurePath, context.ContextName ?? "DbContext");

                foreach (var file in csFiles)
                {
                    var text = File.ReadAllText(file);
                    var modified = false;

                    if (text.Contains($"\"{context.SchemaName}\""))
                    {
                        text = text.Replace($"\"{context.SchemaName}\"", envReplacer);
                        modified = true;
                    }

                    if (text.Contains("WonderDbSchemaContext"))
                    {
                        // The DbContext attribute must be fully qualified
                        text = text.Replace("typeof(WonderDbSchemaContext)", $"typeof({contextFullName})");
                        
                        // The class name and other identifiers must use the short name
                        var shortName = contextFullName.Contains(".") ? contextFullName.Split('.').Last() : contextFullName;
                        text = text.Replace("WonderDbSchemaContext", shortName);
                        modified = true;
                    }

                    // Remove the invalid using statement
                    if (text.Contains("using WonderDB.Temp;"))
                    {
                        text = text.Replace("using WonderDB.Temp;", "");
                        modified = true;
                    }

                    if (modified)
                    {
                        File.WriteAllText(file, text);
                    }
                }
            }

            // Invalidate restore cache for this project so the next operation
            // sees the newly generated migration files in a fresh build.
            var infraCsproj = Directory.GetFiles(context.InfrastructurePath, "*.csproj",
                SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (infraCsproj != null)
                _restoredProjects.Remove(Path.GetFullPath(infraCsproj));
        }
        finally
        {
            CleanupTempFactory(tempFactoryPath);
        }
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Migration output directory helper
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Returns the relative output directory for migration files.
    /// Structure: Migrations/{ProviderFolder}/{ContextName}
    /// e.g., Migrations/PostgreSql/InventoryPgDbContext
    /// </summary>
    private static string GetMigrationOutputDir(DbProviderType providerType, string? contextName)
    {
        var providerFolder = providerType switch
        {
            DbProviderType.SqlServer  => "SqlServer",
            DbProviderType.PostgreSQL => "PostgreSql",
            DbProviderType.SQLite     => "SQLite",
            DbProviderType.MongoDB    => "MongoDB",
            _                         => "Other"
        };

        // IMPORTANT: We must NOT use the context class name directly as the last
        // folder segment. EF Core derives the migration namespace from the output
        // directory, so a folder named "InventorySqlDbContext" creates a namespace
        // segment "InventorySqlDbContext" that collides with the class of the same
        // name â†’ CS0118 compiler error.  Append "_Mig" to make it a unique name.
        var contextFolder = string.IsNullOrWhiteSpace(contextName)
            ? "Default_Mig"
            : $"{contextName}_Mig";

        // Use forward slashes so dotnet ef accepts the path on all platforms
        return $"Migrations/{providerFolder}/{contextFolder}";
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Private helpers
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Writes a temporary IDesignTimeDbContextFactory to the Infrastructure project.
    /// When a schema override is requested, we also emit a thin DbContext SUBCLASS
    /// that overrides OnModelCreating to call HasDefaultSchema — this is the ONLY
    /// way to make EF Core actually create tables in a different schema.
    /// </summary>
    private async Task WriteDesignTimeFactoryAsync(MigrationContext context, string tempFactoryPath)
    {
        var contextFullName = FindContextFullName(
            context.InfrastructurePath,
            context.ContextName ?? "DbContext");

        var schemaName = context.SchemaName;
        var hasSchema  = !string.IsNullOrWhiteSpace(schemaName);
        var connStr    = context.ConnectionString.Replace("\"", "\"\"");

        string factoryCode;

        var isGenerating = schemaName == "__WONDERDB_DYNAMIC_SCHEMA__";

        if (isGenerating)
        {
            var historyTableOption = context.ProviderType switch
            {
                DbProviderType.PostgreSQL =>
                    $"UseNpgsql(@\"{connStr}\", opt => opt.MigrationsHistoryTable(\"__EFMigrationsHistory\", \"{schemaName}\"))",
                DbProviderType.SqlServer =>
                    $"UseSqlServer(@\"{connStr}\", opt => opt.MigrationsHistoryTable(\"__EFMigrationsHistory\", \"{schemaName}\"))",
                _ =>
                    $"UseSqlite(@\"{connStr}\")"
            };

            // Emit a subclass that calls HasDefaultSchema before base.OnModelCreating.
            // This forces EF Core to output 'schema: "__WONDERDB_DYNAMIC_SCHEMA__"' into the C# files.
            factoryCode = $@"
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WonderDB.Temp
{{
    internal class WonderDbSchemaContext : {contextFullName}
    {{
        public WonderDbSchemaContext(DbContextOptions<{contextFullName}> options)
            : base(options) {{ }}

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {{
            modelBuilder.HasDefaultSchema(""{schemaName}"");
            base.OnModelCreating(modelBuilder);
        }}
    }}

    public class WonderDbTempFactory : IDesignTimeDbContextFactory<WonderDbSchemaContext>
    {{
        public WonderDbSchemaContext CreateDbContext(string[] args)
        {{
            var builder = new DbContextOptionsBuilder<{contextFullName}>();
            builder.{historyTableOption};
            return new WonderDbSchemaContext(builder.Options);
        }}
    }}
}}";
        }
        else if (hasSchema)
        {
            var historyTableOption = context.ProviderType switch
            {
                DbProviderType.PostgreSQL =>
                    $"UseNpgsql(@\"{connStr}\", opt => opt.MigrationsHistoryTable(\"__EFMigrationsHistory\", \"{schemaName}\"))",
                DbProviderType.SqlServer =>
                    $"UseSqlServer(@\"{connStr}\", opt => opt.MigrationsHistoryTable(\"__EFMigrationsHistory\", \"{schemaName}\"))",
                _ =>
                    $"UseSqlite(@\"{connStr}\")"
            };

            // For Status/Diff/Migrate runtime, DO NOT subclass!
            // The C# migrations are dynamically schema-aware. We just configure the History table.
            factoryCode = $@"
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WonderDB.Temp
{{
    public class WonderDbTempFactory : IDesignTimeDbContextFactory<{contextFullName}>
    {{
        public {contextFullName} CreateDbContext(string[] args)
        {{
            var builder = new DbContextOptionsBuilder<{contextFullName}>();
            builder.{historyTableOption};
            return new {contextFullName}(builder.Options);
        }}
    }}
}}";
        }
        else
        {
            // No schema override — create the real context directly
            var providerMethod = context.ProviderType switch
            {
                DbProviderType.PostgreSQL => "UseNpgsql",
                DbProviderType.SQLite     => "UseSqlite",
                _                         => "UseSqlServer"
            };

            factoryCode = $@"
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WonderDB.Temp
{{
    public class WonderDbTempFactory : IDesignTimeDbContextFactory<{contextFullName}>
    {{
        public {contextFullName} CreateDbContext(string[] args)
        {{
            var builder = new DbContextOptionsBuilder<{contextFullName}>();
            builder.{providerMethod}(@""{connStr}"");
            return new {contextFullName}(builder.Options);
        }}
    }}
}}";
        }
        await File.WriteAllTextAsync(tempFactoryPath, factoryCode);
    }

    /// <summary>
    /// Run 'dotnet restore' on the Infrastructure project (and startup project if found).
    /// This is CRITICAL for cross-OS Docker scenarios: when a project is restored on Windows,
    /// the obj/project.assets.json contains Windows-specific fallback paths like
    /// 'C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages'.
    /// Running restore inside the container regenerates these with Linux-compatible paths.
    /// </summary>
    private async Task EnsureRestoredAsync(MigrationContext context)
    {
        // Restore the Infrastructure project (skip if already fresh this session)
        var infraCsproj = Directory.GetFiles(context.InfrastructurePath, "*.csproj", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        if (infraCsproj != null)
            await RestoreIfNeededAsync(infraCsproj,
                Directory.GetParent(context.InfrastructurePath)?.FullName ?? context.InfrastructurePath,
                "Infrastructure");

        // Also restore the startup project if found
        var startupProject = FindStartupProject(context.InfrastructurePath);
        if (startupProject != null)
        {
            var startupCsproj = Directory.GetFiles(startupProject, "*.csproj", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();

            if (startupCsproj != null)
                await RestoreIfNeededAsync(startupCsproj,
                    Directory.GetParent(startupProject)?.FullName ?? startupProject,
                    "startup");
        }
    }

    /// <summary>
    /// Runs 'dotnet restore' only when:
    ///  (a) the project hasn't been restored in this session, AND
    ///  (b) obj/project.assets.json is missing or older than the .csproj.
    /// Both conditions must be true to trigger a real restore.
    /// </summary>
    private static async Task RestoreIfNeededAsync(string csprojPath, string workingDir, string label)
    {
        var key = Path.GetFullPath(csprojPath);

        // Check session cache first
        if (_restoredProjects.Contains(key))
        {
            AnsiConsole.MarkupLine($"[grey]â„¹ Skipping restore ({label}) â€” already done this session.[/]");
            return;
        }

        // Check assets.json freshness
        var assetsJson = Path.Combine(
            Path.GetDirectoryName(csprojPath)!, "obj", "project.assets.json");

        if (File.Exists(assetsJson))
        {
            var assetTime  = File.GetLastWriteTimeUtc(assetsJson);
            var csprojTime = File.GetLastWriteTimeUtc(csprojPath);
            if (assetTime >= csprojTime)
            {
                AnsiConsole.MarkupLine($"[grey]â„¹ Skipping restore ({label}) â€” assets already up to date.[/]");
                _restoredProjects.Add(key);
                return;
            }
        }

        AnsiConsole.MarkupLine($"[blue]â„¹ Restoring {label} project dependencies...[/]");
        var (exitCode, _, error) = await RunProcessAsync("dotnet", $"restore \"{csprojPath}\"", workingDir);

        if (exitCode != 0)
            AnsiConsole.MarkupLine($"[yellow]âš  Restore warning for {label} project: {Markup.Escape(error.Trim())}[/]");
        else
            _restoredProjects.Add(key);
    }

    /// <summary>
    /// Build the 'dotnet ef' argument string, including --project and --startup-project.
    /// </summary>
    private string BuildEfArgs(string efCommand, MigrationContext context)
    {
        var args = $"ef {efCommand} " +
                   $"--project \"{context.InfrastructurePath}\"";

        var startupProject = FindStartupProject(context.InfrastructurePath);
        if (startupProject != null)
        {
            args += $" --startup-project \"{startupProject}\"";
        }

        if (!string.IsNullOrEmpty(context.ContextName))
        {
            args += $" --context \"{context.ContextName}\"";
        }

        return args;
    }

    /// <summary>
    /// Execute a 'dotnet ef' command and capture stdout/stderr.
    /// </summary>
    private async Task<(int ExitCode, string Output, string Error)> RunDotnetEfAsync(
        string args, MigrationContext context, bool streamOutput = false)
    {
        var workingDir = Directory.GetParent(context.InfrastructurePath)?.FullName
                         ?? context.InfrastructurePath;

        var envVars = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(context.SchemaName))
        {
            envVars["WONDERDB_SCHEMA"] = context.SchemaName;
        }

        return await RunProcessAsync("dotnet", args, workingDir, streamOutput, envVars);
    }

    /// <summary>
    /// Execute a process and capture stdout/stderr.
    /// </summary>
    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        string fileName, string arguments, string workingDirectory, bool streamOutput = false, Dictionary<string, string>? envVars = null)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            }
        };

        if (envVars != null)
        {
            foreach (var kvp in envVars)
            {
                process.StartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
            }
        }

        if (streamOutput)
        {
            var output = new System.Text.StringBuilder();
            var error = new System.Text.StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    output.AppendLine(e.Data);
                    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(e.Data)}[/]");
                }
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    error.AppendLine(e.Data);
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(e.Data)}[/]");
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            return (process.ExitCode, output.ToString(), error.ToString());
        }
        else
        {
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var output = await outputTask;
            var error = await errorTask;
            return (process.ExitCode, output, error);
        }
    }

    /// <summary>
    /// Scan the Infrastructure project's source code to find the fully-qualified name
    /// (namespace + class) of the DbContext. Returns e.g. "IAM.Infrastructure.Persistence.AppDbContext".
    /// </summary>
    private static string FindContextFullName(string infrastructurePath, string contextName)
    {
        var csFiles = Directory.GetFiles(infrastructurePath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("bin", "")) &&
                        !f.Contains(Path.Combine("obj", "")) &&
                        !f.Contains("WonderDb_DesignTimeFactory") &&
                        !f.Contains("WonderDbTempFactory"))
            .ToList();

        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);

            // Check if this file contains the context class definition
            if (!Regex.IsMatch(content, $@"class\s+{Regex.Escape(contextName)}\s*[:<{{]"))
                continue;

            // Extract namespace â€” supports both block and file-scoped namespaces
            // File-scoped: "namespace Some.Namespace;"
            // Block: "namespace Some.Namespace { ... }"
            var nsMatch = Regex.Match(content, @"namespace\s+([\w.]+)\s*[;{]");
            if (nsMatch.Success)
                return $"{nsMatch.Groups[1].Value}.{contextName}";
        }

        // Fallback: derive from project name
        var projectName = Path.GetFileNameWithoutExtension(
            Directory.GetFiles(infrastructurePath, "*.csproj").FirstOrDefault() ?? "");
        return $"{projectName}.Persistence.{contextName}";
    }

    /// <summary>
    /// Look for a sibling API/Web project to use as the EF Core startup project.
    /// </summary>
    private static string? FindStartupProject(string infrastructurePath)
    {
        var parentDir = Directory.GetParent(infrastructurePath);
        if (parentDir == null)
            return null;

        var candidates = parentDir.GetDirectories()
            .Where(d =>
                d.Name.EndsWith(".API", StringComparison.OrdinalIgnoreCase) ||
                d.Name.EndsWith(".Web", StringComparison.OrdinalIgnoreCase) ||
                d.Name.EndsWith(".WebAPI", StringComparison.OrdinalIgnoreCase) ||
                d.Name.EndsWith(".Host", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return candidates
            .FirstOrDefault(d => d.GetFiles("*.csproj").Length > 0)?
            .FullName;
    }

    private static void CleanupTempFactory(string tempFactoryPath)
    {
        try
        {
            if (File.Exists(tempFactoryPath))
                File.Delete(tempFactoryPath);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private static void PrintOutput(string output, string error)
    {
        if (!string.IsNullOrWhiteSpace(error))
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error.Trim())}[/]");
        if (!string.IsNullOrWhiteSpace(output))
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(output.Trim())}[/]");
    }
}
