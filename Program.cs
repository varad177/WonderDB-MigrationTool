using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using WonderDB.MigrationTool.Audit;
using WonderDB.MigrationTool.Commands;
using WonderDB.MigrationTool.Connection;
using WonderDB.MigrationTool.Discovery;
using WonderDB.MigrationTool.Interactive;
using WonderDB.MigrationTool.Providers;

namespace WonderDB.MigrationTool;

/// <summary>
/// Entry point. Registers DI services and System.CommandLine commands.
/// No business logic lives here.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {// here The Application start

        var services = ConfigureServices(); // Dependency Injection container.
        // just creating the registry which can be requested later

        var rootCommand = new RootCommand(
            "WonderDB Migration Tool — Database migration CLI for .NET Clean Architecture projects by WonderBiz Technologies");

        rootCommand.AddCommand(MigrateCommand.Create(services));
        rootCommand.AddCommand(StatusCommand.Create(services));
        rootCommand.AddCommand(RollbackCommand.Create(services));
        rootCommand.AddCommand(GenerateCommand.Create(services));
        rootCommand.AddCommand(HistoryCommand.Create(services));
        rootCommand.AddCommand(ListProjectsCommand.Create(services));
        rootCommand.AddCommand(ListContextsCommand.Create(services));

        return await rootCommand.InvokeAsync(args);
    }

    /// <summary>
    /// Wire up all application services into the DI container.
    /// </summary>
    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Discovery
        services.AddSingleton<ProjectScanner>();
        services.AddSingleton<InfrastructureLoader>();
        services.AddSingleton<ConfigResolver>();
        services.AddSingleton<SchemaStore>();
        services.AddSingleton<ProviderDetector>();

        // Connection
        services.AddSingleton<ConnectionTester>();

        // Providers
        services.AddSingleton<EFCoreMigrationProvider>();
        services.AddSingleton<MongoMigrationProvider>();

        // Audit
        services.AddSingleton<AuditLogger>();

        // Interactive
        services.AddSingleton<PromptService>();

        return services.BuildServiceProvider();
    }
}
