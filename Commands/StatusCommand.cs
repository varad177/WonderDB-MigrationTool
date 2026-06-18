using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using WonderDB.MigrationTool.Connection;
using WonderDB.MigrationTool.Discovery;
using WonderDB.MigrationTool.Providers;

namespace WonderDB.MigrationTool.Commands;

/// <summary>
/// The 'status' command. Shows applied vs pending migrations per DbContext.
/// </summary>
public static class StatusCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("status", "Show applied vs pending migrations per DbContext");

        var infrastructureOption = new Option<string?>(
            "--infrastructure", "Path to the Infrastructure project folder");
        var envOption = new Option<string?>(
            "--env", "Environment name (Development, Staging, Production)");

        command.AddOption(infrastructureOption);
        command.AddOption(envOption);

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var infrastructure = ctx.ParseResult.GetValueForOption(infrastructureOption);
            var env = ctx.ParseResult.GetValueForOption(envOption);

            try
            {
                await ExecuteAsync(services, infrastructure, env);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                ctx.ExitCode = 1;
            }
        });

        return command;
    }

    private static async Task ExecuteAsync(IServiceProvider services, string? infrastructurePath, string? env)
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

        foreach (var cs in connectionStrings)
        {
            var providerType = detector.Detect(cs.Value);
            var isConnected = await connectionTester.TestConnectionAsync(cs.Value, providerType);

            if (!isConnected)
            {
                AnsiConsole.MarkupLine($"[red]✗ Skipping '{cs.Key}' — database unreachable.[/]");
                continue;
            }

            foreach (var ctxInfo in dbContexts)
            {
                var migrationContext = new MigrationContext
                {
                    ContextName = ctxInfo.ContextName,
                    ConnectionString = cs.Value,
                    ProviderType = providerType,
                    InfrastructurePath = infrastructurePath,
                    DatabaseName = cs.Key,
                    SchemaName = ctxInfo.SchemaName,
                    DefaultSchemaName = ctxInfo.SchemaName
                };

                IDbMigrationProvider provider = providerType == DbProviderType.MongoDB
                    ? services.GetRequiredService<MongoMigrationProvider>()
                    : services.GetRequiredService<EFCoreMigrationProvider>();

                AnsiConsole.WriteLine();
                var statuses = await provider.GetStatusAsync(migrationContext);
                MigrateCommand.RenderStatusTable(statuses, ctxInfo.ContextName);
            }
        }
    }
}
