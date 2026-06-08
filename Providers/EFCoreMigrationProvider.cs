using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using WonderDB.MigrationTool.Discovery;

namespace WonderDB.MigrationTool.Providers;

/// <summary>
/// EF Core implementation of <see cref="IDbMigrationProvider"/>.
/// Supports SQL Server and PostgreSQL. Creates DbContext instances at runtime
/// via reflection and uses the standard EF Core migration APIs.
/// </summary>
public class EFCoreMigrationProvider : IDbMigrationProvider
{
    public async Task MigrateAsync(MigrationContext context)
    {
        using var dbContext = CreateDbContext(context);

        var pending = (await dbContext.Database.GetPendingMigrationsAsync()).ToList();

        if (pending.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]✓ Database is already up to date.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[yellow]Applying {pending.Count} pending migration(s)...[/]");
        foreach (var migration in pending)
        {
            AnsiConsole.MarkupLine($"  [grey]→[/] {migration}");
        }

        await dbContext.Database.MigrateAsync();

        AnsiConsole.MarkupLine("[green]✓ All migrations applied successfully.[/]");
    }

    public async Task<IEnumerable<MigrationStatus>> GetStatusAsync(MigrationContext context)
    {
        using var dbContext = CreateDbContext(context);

        var applied = (await dbContext.Database.GetAppliedMigrationsAsync()).ToList();
        var pending = (await dbContext.Database.GetPendingMigrationsAsync()).ToList();

        var statuses = new List<MigrationStatus>();

        foreach (var migration in applied)
        {
            statuses.Add(new MigrationStatus
            {
                MigrationId = migration,
                MigrationName = migration,
                IsApplied = true,
                AppliedOn = null
            });
        }

        foreach (var migration in pending)
        {
            statuses.Add(new MigrationStatus
            {
                MigrationId = migration,
                MigrationName = migration,
                IsApplied = false,
                AppliedOn = null
            });
        }

        return statuses;
    }

    public async Task DryRunAsync(MigrationContext context)
    {
        using var dbContext = CreateDbContext(context);

        var pending = (await dbContext.Database.GetPendingMigrationsAsync()).ToList();

        if (pending.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No pending migrations. Database is up to date.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[yellow]Dry run — {pending.Count} pending migration(s):[/]");
        foreach (var migration in pending)
        {
            AnsiConsole.MarkupLine($"  [yellow]→[/] {migration}");
        }

        // Generate SQL script for review
        try
        {
            var migrator = dbContext.GetInfrastructure().GetRequiredService<IMigrator>();
            var lastApplied = (await dbContext.Database.GetAppliedMigrationsAsync()).LastOrDefault();
            var sql = migrator.GenerateScript(
                fromMigration: lastApplied,
                toMigration: pending.Last());

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
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]⚠ Could not generate SQL preview:[/] {Markup.Escape(ex.Message)}");
        }

        AnsiConsole.MarkupLine("\n[yellow]No changes were applied (dry run mode).[/]");
    }

    public async Task RollbackAsync(MigrationContext context, string targetMigration)
    {
        using var dbContext = CreateDbContext(context);

        AnsiConsole.MarkupLine(
            $"[yellow]Rolling back to migration:[/] [white]{targetMigration}[/]");

        var migrator = dbContext.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);

        AnsiConsole.MarkupLine(
            $"[green]✓ Successfully rolled back to '{targetMigration}'.[/]");
    }

    public async Task GenerateAsync(MigrationContext context, string migrationName)
    {
        AnsiConsole.MarkupLine(
            $"[blue]Generating migration '[white]{migrationName}[/]' " +
            $"for [white]{context.DbContextType?.Name ?? "DbContext"}[/]...[/]");

        var args = $"ef migrations add {migrationName} " +
                   $"--project \"{context.InfrastructurePath}\" " +
                   $"--context {context.DbContextType?.Name ?? "DbContext"} " +
                   $"--output-dir Migrations";

        // Find the startup project (API or Web sibling) for EF tooling
        var startupProject = FindStartupProject(context.InfrastructurePath);
        if (startupProject != null)
        {
            args += $" --startup-project \"{startupProject}\"";
        }

        var workingDir = Directory.GetParent(context.InfrastructurePath)?.FullName
                         ?? context.InfrastructurePath;

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir
            }
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            AnsiConsole.MarkupLine("[red]✗ Migration generation failed![/]");
            if (!string.IsNullOrWhiteSpace(error))
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            if (!string.IsNullOrWhiteSpace(output))
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(output)}[/]");
            throw new InvalidOperationException("Failed to generate migration.");
        }

        AnsiConsole.MarkupLine($"[green]✓ Migration '{migrationName}' generated successfully.[/]");
        if (!string.IsNullOrWhiteSpace(output))
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(output)}[/]");
    }

    /// <summary>
    /// Create a DbContext instance at runtime using the provider-specific options builder.
    /// Assumes the target DbContext has a constructor accepting DbContextOptions&lt;T&gt;.
    /// </summary>
    private static DbContext CreateDbContext(MigrationContext context)
    {
        if (context.DbContextType == null)
            throw new InvalidOperationException("DbContext type is required for EF Core operations.");

        var optionsBuilderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(context.DbContextType);
        var optionsBuilder = (DbContextOptionsBuilder)Activator.CreateInstance(optionsBuilderType)!;

        switch (context.ProviderType)
        {
            case DbProviderType.SqlServer:
                optionsBuilder.UseSqlServer(context.ConnectionString);
                break;
            case DbProviderType.PostgreSQL:
                optionsBuilder.UseNpgsql(context.ConnectionString);
                break;
            default:
                throw new NotSupportedException(
                    $"EF Core provider for '{ProviderDetector.GetDisplayName(context.ProviderType)}' " +
                    $"is not supported. Supported: SQL Server, PostgreSQL.");
        }

        return (DbContext)Activator.CreateInstance(context.DbContextType, optionsBuilder.Options)!;
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

        // Prefer a project that has a .csproj file
        return candidates
            .FirstOrDefault(d => d.GetFiles("*.csproj").Length > 0)?
            .FullName;
    }
}
