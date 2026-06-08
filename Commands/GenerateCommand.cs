using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using WonderDB.MigrationTool.Audit;
using WonderDB.MigrationTool.Discovery;
using WonderDB.MigrationTool.Providers;

namespace WonderDB.MigrationTool.Commands;

/// <summary>
/// The 'generate' command. Scaffolds a new EF Core or MongoDB migration.
/// </summary>
public static class GenerateCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("generate", "Scaffold a new migration");

        var nameOption = new Option<string>(
            "--name", "Name for the new migration")
        { IsRequired = true };
        var infrastructureOption = new Option<string?>(
            "--infrastructure", "Path to the Infrastructure project folder");
        var envOption = new Option<string?>(
            "--env", "Environment name (Development, Staging, Production)");

        command.AddOption(nameOption);
        command.AddOption(infrastructureOption);
        command.AddOption(envOption);

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var name = ctx.ParseResult.GetValueForOption(nameOption)!;
            var infrastructure = ctx.ParseResult.GetValueForOption(infrastructureOption);
            var env = ctx.ParseResult.GetValueForOption(envOption);

            try
            {
                await ExecuteAsync(services, name, infrastructure, env);
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
        IServiceProvider services, string migrationName,
        string? infrastructurePath, string? env)
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
        var assembly = await loader.LoadAssemblyAsync(infrastructurePath);
        var dbContexts = loader.DiscoverDbContexts(assembly);

        if (dbContexts.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]⚠ No DbContexts found in the assembly.[/]");
            return;
        }

        var configResolver = services.GetRequiredService<ConfigResolver>();
        var config = configResolver.Resolve(infrastructurePath, env);
        var connectionStrings = configResolver.GetConnectionStrings(config);

        var detector = services.GetRequiredService<ProviderDetector>();

        // Determine provider type from connection strings
        DbProviderType providerType = DbProviderType.Unknown;
        string dbName = string.Empty;
        string connectionString = string.Empty;

        if (connectionStrings.Count > 0)
        {
            var promptSvc = services.GetRequiredService<Interactive.PromptService>();
            dbName = promptSvc.SelectDatabase(connectionStrings, detector);
            connectionString = connectionStrings[dbName];
            providerType = detector.Detect(connectionString);
        }

        var selectedContexts = dbContexts.Count > 1
            ? services.GetRequiredService<Interactive.PromptService>().SelectDbContexts(dbContexts)
            : dbContexts;

        var projectName = new DirectoryInfo(infrastructurePath).Name
            .Replace(".Infrastructure", string.Empty);
        var auditLogger = services.GetRequiredService<AuditLogger>();

        foreach (var ctxInfo in selectedContexts)
        {
            var migrationContext = new MigrationContext
            {
                DbContextType = ctxInfo.DbContextType,
                ConnectionString = connectionString,
                ProviderType = providerType,
                InfrastructurePath = infrastructurePath,
                ProjectName = projectName,
                DatabaseName = dbName,
                SchemaName = ctxInfo.SchemaName,
                InfrastructureAssembly = assembly
            };

            IDbMigrationProvider provider = providerType == DbProviderType.MongoDB
                ? services.GetRequiredService<MongoMigrationProvider>()
                : services.GetRequiredService<EFCoreMigrationProvider>();

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[cyan]{ctxInfo.DbContextType.Name}[/]").LeftJustified());

            await provider.GenerateAsync(migrationContext, migrationName);

            var entry = new AuditEntry
            {
                Project = projectName,
                Database = dbName,
                Context = ctxInfo.DbContextType.Name,
                MigrationName = migrationName,
                Mode = "Generate",
                Result = "Success"
            };
            await auditLogger.LogAsync(entry);
        }
    }
}
