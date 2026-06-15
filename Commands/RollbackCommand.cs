using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using WonderDB.MigrationTool.Audit;
using WonderDB.MigrationTool.Connection;
using WonderDB.MigrationTool.Discovery;
using WonderDB.MigrationTool.Providers;

namespace WonderDB.MigrationTool.Commands;

/// <summary>
/// The 'rollback' command. Reverts the database to a specific migration.
/// </summary>
public static class RollbackCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("rollback", "Revert to a specific migration");

        var toOption = new Option<string>(
            "--to", "Target migration name to rollback to")
        { IsRequired = true };
        var infrastructureOption = new Option<string?>(
            "--infrastructure", "Path to the Infrastructure project folder");
        var envOption = new Option<string?>(
            "--env", "Environment name (Development, Staging, Production)");
        var auditDbOption = new Option<bool>(
            "--audit-db", "Also write audit log to the target database");

        command.AddOption(toOption);
        command.AddOption(infrastructureOption);
        command.AddOption(envOption);
        command.AddOption(auditDbOption);

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var to = ctx.ParseResult.GetValueForOption(toOption)!;
            var infrastructure = ctx.ParseResult.GetValueForOption(infrastructureOption);
            var env = ctx.ParseResult.GetValueForOption(envOption);
            var auditDb = ctx.ParseResult.GetValueForOption(auditDbOption);

            try
            {
                await ExecuteAsync(services, to, infrastructure, env, auditDb);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                ctx.ExitCode = 1;
            }
        });

        return command;
    }

    private static async Task ExecuteAsync(
        IServiceProvider services, string targetMigration,
        string? infrastructurePath, string? env, bool auditDb)
    {
        if (string.IsNullOrEmpty(infrastructurePath))
        {
            var promptService = services.GetRequiredService<Interactive.PromptService>();
            infrastructurePath = promptService.PromptForInfrastructurePath();
        }
        else
        {
            infrastructurePath = Path.GetFullPath(infrastructurePath);
        }

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
        var auditLogger = services.GetRequiredService<AuditLogger>();

        var projectName = new DirectoryInfo(infrastructurePath).Name
            .Replace(".Infrastructure", string.Empty);

        // If multiple databases/contexts, prompt the user to select
        var promptSvc = services.GetRequiredService<Interactive.PromptService>();
        var dbName = promptSvc.SelectDatabase(connectionStrings, detector);
        var connectionString = connectionStrings[dbName];
        var providerType = detector.Detect(connectionString);

        var isConnected = await connectionTester.TestConnectionAsync(connectionString, providerType);
        if (!isConnected)
        {
            AnsiConsole.MarkupLine("[red]✗ Aborting — database is unreachable.[/]");
            return;
        }

        var selectedContexts = promptSvc.SelectDbContexts(dbContexts, providerType);

        var schemaStore = services.GetRequiredService<SchemaStore>();

        foreach (var ctxInfo in selectedContexts)
        {
            var savedSchema = schemaStore.Get(infrastructurePath, ctxInfo.ContextName);
            var schemaName = savedSchema ?? ctxInfo.SchemaName;

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

            IDbMigrationProvider provider = providerType == DbProviderType.MongoDB
                ? services.GetRequiredService<MongoMigrationProvider>()
                : services.GetRequiredService<EFCoreMigrationProvider>();

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[cyan]{ctxInfo.ContextName}[/]").LeftJustified());

            await provider.RollbackAsync(migrationContext, targetMigration);

            var entry = new AuditEntry
            {
                Project = projectName,
                Database = dbName,
                Context = ctxInfo.ContextName,
                MigrationName = targetMigration,
                Mode = "Rollback",
                Result = "Success"
            };

            await auditLogger.LogAsync(entry);

            if (auditDb)
            {
                try
                {
                    await auditLogger.WriteToDbAsync(connectionString, providerType, entry);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]⚠ Could not write audit to DB:[/] {Markup.Escape(ex.Message)}");
                }
            }
        }
    }
}
