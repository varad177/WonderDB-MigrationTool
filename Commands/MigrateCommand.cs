using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using WonderDB.MigrationTool.Audit;
using WonderDB.MigrationTool.Connection;
using WonderDB.MigrationTool.Discovery;
using WonderDB.MigrationTool.Interactive;
using WonderDB.MigrationTool.Providers;

namespace WonderDB.MigrationTool.Commands;

/// <summary>
/// The main 'migrate' command. Supports interactive, direct, batch, and dry-run modes.
/// </summary>
public static class MigrateCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("migrate", "Run database migrations");

        var infrastructureOption = new Option<string?>(
            "--infrastructure", "Path to the Infrastructure project folder");
        var allOption = new Option<bool>(
            "--all", "Batch-migrate all discovered Infrastructure projects in the workspace");
        var dryRunOption = new Option<bool>(
            "--dry-run", "Preview pending migrations without applying any changes");
        var envOption = new Option<string?>(
            "--env", "Environment name (Development, Staging, Production)");
        var auditDbOption = new Option<bool>(
            "--audit-db", "Also write audit log to the target database");
        var schemaOption = new Option<string?>(
            "--schema",
            "Target schema name for migration (overrides the auto-derived schema). " +
            "Use this for per-client schema isolation (e.g. --schema client_acme).");

        command.AddOption(infrastructureOption);
        command.AddOption(allOption);
        command.AddOption(dryRunOption);
        command.AddOption(envOption);
        command.AddOption(auditDbOption);
        command.AddOption(schemaOption);

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var infrastructure = ctx.ParseResult.GetValueForOption(infrastructureOption);
            var all = ctx.ParseResult.GetValueForOption(allOption);
            var dryRun = ctx.ParseResult.GetValueForOption(dryRunOption);
            var env = ctx.ParseResult.GetValueForOption(envOption);
            var auditDb = ctx.ParseResult.GetValueForOption(auditDbOption);
            var schema = ctx.ParseResult.GetValueForOption(schemaOption);

            try
            {
                if (all)
                {
                    await RunBatchMigrationAsync(services, env, dryRun, auditDb);
                }
                else if (!string.IsNullOrEmpty(infrastructure))
                {
                    await RunDirectMigrationAsync(services, infrastructure, env, dryRun, auditDb, schema);
                }
                else
                {
                    await RunInteractiveMigrationAsync(services, env, auditDb);
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                ctx.ExitCode = 1;
            }
        });

        return command;
    }

    /// <summary>
    /// Full interactive flow — prompts for path, database, context, and mode.
    /// </summary>
    private static async Task RunInteractiveMigrationAsync(
        IServiceProvider services, string? env, bool auditDb)
    {
        var promptService = services.GetRequiredService<PromptService>();
        var loader = services.GetRequiredService<InfrastructureLoader>();
        var configResolver = services.GetRequiredService<ConfigResolver>();
        var detector = services.GetRequiredService<ProviderDetector>();
        var connectionTester = services.GetRequiredService<ConnectionTester>();

        while (true)
        {
            var infrastructurePath = promptService.PromptForInfrastructurePath();
            var dbContexts = await loader.DiscoverDbContextsAsync(infrastructurePath);

            if (dbContexts.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]⚠ No DbContexts found in the project.[/]");
                continue;
            }

            var config = configResolver.Resolve(infrastructurePath, env);
            var connectionStrings = configResolver.GetConnectionStrings(config);

            if (connectionStrings.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]✗ No connection strings found in configuration.[/]");
                continue;
            }

            var projectName = new DirectoryInfo(infrastructurePath).Name
                .Replace(".Infrastructure", string.Empty);

            var dbName = promptService.SelectDatabase(connectionStrings, detector);
            var connectionString = connectionStrings[dbName];
            var providerType = detector.Detect(connectionString);

            var selectedContexts = promptService.SelectDbContexts(dbContexts, providerType);

            // Test connection
            var isConnected = await connectionTester.TestConnectionAsync(connectionString, providerType);
            if (!isConnected)
            {
                AnsiConsole.MarkupLine("[red]✗ Aborting — database is unreachable. Please select another.[/]");
                continue;
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]✓ Setup complete. Targeting '{dbName}' on '{projectName}'.[/]");

            var changeDb = false;
            while (!changeDb)
            {
                var mode = promptService.SelectMode();
                if (mode == MigrationMode.Exit)
                {
                    AnsiConsole.MarkupLine("[grey]Exiting WonderDB Migration Tool. Goodbye![/]");
                    return;
                }

                if (mode == MigrationMode.ChangeDatabase)
                {
                    changeDb = true;
                    continue;
                }

                foreach (var ctxInfo in selectedContexts)
                {
                    var provider = ResolveProvider(services, providerType);
                    var schemaStore = services.GetRequiredService<SchemaStore>();
                    var savedSchema = schemaStore.Get(infrastructurePath, ctxInfo.ContextName);

                    // Schema is relevant for applying/rolling back, but NOT for generating.
                    string schemaName;
                    if (mode == MigrationMode.GenerateMigration)
                    {
                        schemaName = "__WONDERDB_DYNAMIC_SCHEMA__";
                    }
                    else
                    {
                        schemaName = promptService.PromptForSchemaName(savedSchema ?? ctxInfo.SchemaName);
                    }

                    var migrationContext = new MigrationContext
                    {
                        ContextName = ctxInfo.ContextName,
                        ConnectionString = connectionString,
                        ProviderType = providerType,
                        InfrastructurePath = infrastructurePath,
                        ProjectName = projectName,
                        DatabaseName = dbName,
                        SchemaName = schemaName
                    };

                    AnsiConsole.WriteLine();
                    AnsiConsole.Write(new Rule($"[cyan]{ctxInfo.ContextName}[/] [grey](schema: {schemaName})[/]").LeftJustified());

                    try
                    {
                        switch (mode)
                        {
                            case MigrationMode.MigrateAndUpdate:
                                await provider.MigrateAsync(migrationContext);
                                await LogAuditAsync(services, migrationContext, "Migrate", "Success", auditDb);
                                break;

                            case MigrationMode.DryRun:
                                await provider.DryRunAsync(migrationContext);
                                await LogAuditAsync(services, migrationContext, "DryRun", "Success", auditDb);
                                break;

                            case MigrationMode.Status:
                                var statuses = await provider.GetStatusAsync(migrationContext);
                                RenderStatusTable(statuses, ctxInfo.ContextName);
                                break;

                            case MigrationMode.Rollback:
                                var target = promptService.PromptForRollbackTarget();
                                await provider.RollbackAsync(migrationContext, target);
                                await LogAuditAsync(services, migrationContext, "Rollback", "Success", auditDb, target);
                                break;

                            case MigrationMode.GenerateMigration:
                                var migName = promptService.PromptForMigrationName();
                                await provider.GenerateAsync(migrationContext, migName);
                                schemaStore.Save(infrastructurePath, ctxInfo.ContextName, schemaName);
                                await LogAuditAsync(services, migrationContext, "Generate", "Success", auditDb, migName);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"\n[red]✗ Operation failed:[/] {Markup.Escape(ex.Message)}");
                        AnsiConsole.MarkupLine("[yellow]The tool has caught this error and prevented a crash. Returning to main menu...[/]");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Direct migration with an explicit --infrastructure path.
    /// </summary>
    private static async Task RunDirectMigrationAsync(
        IServiceProvider services, string infrastructurePath, string? env, bool dryRun, bool auditDb,
        string? schemaOverride = null)
    {
        infrastructurePath = Path.GetFullPath(infrastructurePath);

        if (!Directory.Exists(infrastructurePath))
        {
            AnsiConsole.MarkupLine($"[red]✗ Directory not found:[/] {Markup.Escape(infrastructurePath)}");
            return;
        }

        var loader = services.GetRequiredService<InfrastructureLoader>();
        var dbContexts = await loader.DiscoverDbContextsAsync(infrastructurePath);

        if (dbContexts.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]⚠ No DbContexts found in the project.[/]");
            return;
        }

        var configResolver = services.GetRequiredService<ConfigResolver>();
        var config = configResolver.Resolve(infrastructurePath, env);
        var connectionStrings = configResolver.GetConnectionStrings(config);

        if (connectionStrings.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]✗ No connection strings found in configuration.[/]");
            return;
        }

        var detector = services.GetRequiredService<ProviderDetector>();
        var connectionTester = services.GetRequiredService<ConnectionTester>();
        var projectName = new DirectoryInfo(infrastructurePath).Name
            .Replace(".Infrastructure", string.Empty);

        foreach (var cs in connectionStrings)
        {
            var providerType = detector.Detect(cs.Value);
            var isConnected = await connectionTester.TestConnectionAsync(cs.Value, providerType);
            if (!isConnected)
            {
                AnsiConsole.MarkupLine(
                    $"[red]✗ Skipping '{cs.Key}' — database unreachable.[/]");
                continue;
            }

            var schemaStore = services.GetRequiredService<SchemaStore>();

            foreach (var ctxInfo in dbContexts)
            {
                var savedSchema = schemaStore.Get(infrastructurePath, ctxInfo.ContextName);
                // schemaOverride comes from --schema flag; fall back to saved schema, then derived
                var schemaName = schemaOverride ?? savedSchema ?? ctxInfo.SchemaName;

                var migrationContext = new MigrationContext
                {
                    ContextName = ctxInfo.ContextName,
                    ConnectionString = cs.Value,
                    ProviderType = providerType,
                    InfrastructurePath = infrastructurePath,
                    ProjectName = projectName,
                    DatabaseName = cs.Key,
                    SchemaName = schemaName
                };

                var provider = ResolveProvider(services, providerType);

                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule(
                    $"[cyan]{ctxInfo.ContextName} → {cs.Key}[/] [grey](schema: {schemaName})[/]").LeftJustified());

                if (dryRun)
                {
                    await provider.DryRunAsync(migrationContext);
                    await LogAuditAsync(services, migrationContext, "DryRun", "Success", auditDb);
                }
                else
                {
                    await provider.MigrateAsync(migrationContext);
                    await LogAuditAsync(services, migrationContext, "Migrate", "Success", auditDb);
                }
            }
        }
    }

    /// <summary>
    /// Batch migration — scans the workspace for all Infrastructure projects and migrates each one.
    /// Shows a summary table at the end.
    /// </summary>
    private static async Task RunBatchMigrationAsync(
        IServiceProvider services, string? env, bool dryRun, bool auditDb)
    {
        var scanner = services.GetRequiredService<ProjectScanner>();
        var projects = scanner.ScanWorkspace();

        if (projects.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]⚠ No Infrastructure projects found in workspace.[/]");
            return;
        }

        AnsiConsole.MarkupLine(
            $"[blue]ℹ Found {projects.Count} project(s). Starting parallel batch migration (max 3 concurrent)...[/]");

        var results = new System.Collections.Concurrent.ConcurrentBag<(string Project, string Status, string Details)>();

        // Limit concurrency to 3 projects at a time to avoid overwhelming the machine
        var semaphore = new SemaphoreSlim(3, 3);

        var tasks = projects.Select(async project =>
        {
            await semaphore.WaitAsync();
            try
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule($"[cyan]{project.Name}[/]").LeftJustified());

                await RunDirectMigrationAsync(services, project.InfrastructurePath, env, dryRun, auditDb);
                results.Add((project.Name, "✓ Success", dryRun ? "Dry run completed" : "Migrations applied"));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Failed:[/] {Markup.Escape(ex.Message)}");
                results.Add((project.Name, "✗ Failed", ex.Message));
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Summary table
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Batch Migration Summary[/]").LeftJustified());

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Project[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Status[/]").Centered())
            .AddColumn(new TableColumn("[bold]Details[/]").LeftAligned());

        foreach (var (proj, status, details) in results.OrderBy(r => r.Project))
        {
            var statusColor = status.Contains("Success") ? "green" : "red";
            table.AddRow(
                Markup.Escape(proj),
                $"[{statusColor}]{Markup.Escape(status)}[/]",
                Markup.Escape(details));
        }

        AnsiConsole.Write(table);
    }

    /// <summary>
    /// Resolve the correct migration provider based on the detected database type.
    /// </summary>
    private static IDbMigrationProvider ResolveProvider(IServiceProvider services, DbProviderType providerType)
    {
        return providerType switch
        {
            DbProviderType.MongoDB => services.GetRequiredService<MongoMigrationProvider>(),
            _ => services.GetRequiredService<EFCoreMigrationProvider>()
        };
    }

    /// <summary>
    /// Log an operation to the audit file (and optionally to the target DB).
    /// </summary>
    private static async Task LogAuditAsync(
        IServiceProvider services,
        MigrationContext context,
        string mode,
        string result,
        bool auditDb,
        string? migrationName = null)
    {
        var auditLogger = services.GetRequiredService<AuditLogger>();

        var entry = new AuditEntry
        {
            Project = context.ProjectName,
            Database = context.DatabaseName,
            Context = context.ContextName ?? "MongoDB",
            MigrationName = migrationName ?? "All",
            Mode = mode,
            Result = result
        };

        await auditLogger.LogAsync(entry);

        if (auditDb)
        {
            try
            {
                await auditLogger.WriteToDbAsync(context.ConnectionString, context.ProviderType, entry);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]⚠ Could not write audit to DB:[/] {Markup.Escape(ex.Message)}");
            }
        }
    }

    /// <summary>
    /// Render a Spectre.Console table showing migration statuses.
    /// </summary>
    internal static void RenderStatusTable(IEnumerable<MigrationStatus> statuses, string contextName)
    {
        var statusList = statuses.ToList();

        if (statusList.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No migrations found for {contextName}.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[cyan]{contextName}[/]")
            .AddColumn(new TableColumn("[bold]Migration[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Status[/]").Centered())
            .AddColumn(new TableColumn("[bold]Applied On[/]").RightAligned());

        foreach (var status in statusList)
        {
            var statusText = status.IsApplied
                ? "[green]Applied[/]"
                : "[yellow]Pending[/]";
            var appliedOn = status.AppliedOn?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

            table.AddRow(
                Markup.Escape(status.MigrationName),
                statusText,
                appliedOn);
        }

        AnsiConsole.Write(table);
    }
}
