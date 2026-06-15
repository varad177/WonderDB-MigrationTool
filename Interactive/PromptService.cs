using Spectre.Console;
using WonderDB.MigrationTool.Discovery;

namespace WonderDB.MigrationTool.Interactive;

/// <summary>
/// Available migration modes for the interactive prompt.
/// </summary>
public enum MigrationMode
{
    MigrateAndUpdate,
    DryRun,
    Status,
    Rollback,
    GenerateMigration,
    ChangeDatabase,
    Exit
}

/// <summary>
/// Provides rich interactive prompts using Spectre.Console.
/// Used when the user runs 'wonderdb migrate' without flags.
/// </summary>
public class PromptService
{
    private readonly ProjectScanner _projectScanner;

    public PromptService(ProjectScanner projectScanner)
    {
        _projectScanner = projectScanner;
    }

    /// <summary>
    /// Ask the user for the Infrastructure project path, with auto-scan fallback.
    /// </summary>
    public string PromptForInfrastructurePath()
    {
        var input = AnsiConsole.Prompt(
            new TextPrompt<string>(
                "[blue]?[/] Enter path to Infrastructure folder (or press Enter to auto-scan workspace):")
                .AllowEmpty());

        if (!string.IsNullOrWhiteSpace(input))
            return Path.GetFullPath(input);

        // Auto-scan workspace
        AnsiConsole.MarkupLine("[blue]ℹ Scanning workspace for Infrastructure projects...[/]");

        var projects = _projectScanner.ScanWorkspace();

        if (projects.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]✗ No Infrastructure projects found in workspace.[/]");
            throw new InvalidOperationException(
                "No Infrastructure projects found. Provide the path explicitly with --infrastructure.");
        }

        if (projects.Count == 1)
        {
            AnsiConsole.MarkupLine($"[green]Auto-detected:[/] {projects[0].InfrastructurePath}");
            return projects[0].InfrastructurePath;
        }

        var choices = projects
            .Select(p => $"{p.Name}  ({p.InfrastructurePath})")
            .ToList();

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[blue]?[/] Multiple projects found. Select one:")
                .PageSize(15)
                .HighlightStyle(new Style(Color.Cyan1))
                .AddChoices(choices));

        var selectedProject = projects.First(p => selected.Contains(p.InfrastructurePath));
        return selectedProject.InfrastructurePath;
    }

    /// <summary>
    /// Ask the user to select a database from the discovered connection strings.
    /// </summary>
    public string SelectDatabase(Dictionary<string, string> connectionStrings, ProviderDetector detector)
    {
        if (connectionStrings.Count == 0)
            throw new InvalidOperationException(
                "No connection strings found in appsettings.json. " +
                "Ensure the configuration file has a 'ConnectionStrings' section.");

        if (connectionStrings.Count == 1)
        {
            var single = connectionStrings.First();
            var providerType = detector.Detect(single.Value);
            AnsiConsole.MarkupLine(
                $"[green]Using database:[/] {single.Key}  ({ProviderDetector.GetDisplayName(providerType)})");
            return single.Key;
        }

        var choices = connectionStrings
            .Select(cs =>
            {
                var pt = detector.Detect(cs.Value);
                return $"{cs.Key}  ({ProviderDetector.GetDisplayName(pt)})";
            })
            .ToList();

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[blue]?[/] Select database:")
                .HighlightStyle(new Style(Color.Cyan1))
                .AddChoices(choices));

        return selected.Split("  ")[0].Trim();
    }

    /// <summary>
    /// Ask the user to select one or all DbContexts from the discovered list.
    /// </summary>
    public List<DbContextInfo> SelectDbContexts(List<DbContextInfo> contexts, DbProviderType providerType)
    {
        if (contexts.Count == 0)
            throw new InvalidOperationException(
                "No DbContext classes found in the Infrastructure assembly.");

        // Smart filtering based on provider type vs context name conventions
        var filteredContexts = contexts.Where(c => 
        {
            var name = c.ContextName.ToLowerInvariant();
            if (providerType == DbProviderType.SqlServer && (name.Contains("pg") || name.Contains("postgres")))
                return false;
            if (providerType == DbProviderType.PostgreSQL && (name.Contains("sql") && !name.Contains("pgsql")))
                return false;
            return true;
        }).ToList();

        // Fallback to all if filtering removed everything
        var listToUse = filteredContexts.Count > 0 ? filteredContexts : contexts;

        if (listToUse.Count == 1)
        {
            AnsiConsole.MarkupLine(
                $"[green]Using context:[/] {listToUse[0].ContextName}  (schema: {listToUse[0].SchemaName})");
            return listToUse;
        }

        var choices = new List<string> { "All" };
        choices.AddRange(
            listToUse.Select(c => $"{c.ContextName}   (schema: {c.SchemaName})"));

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[blue]?[/] Select DbContext / schema:")
                .HighlightStyle(new Style(Color.Cyan1))
                .AddChoices(choices));

        if (selected == "All")
            return listToUse;

        var contextName = selected.Split("   ")[0].Trim();
        return listToUse.Where(c => c.ContextName == contextName).ToList();
    }

    /// <summary>
    /// Ask the user which migration mode to run.
    /// </summary>
    public MigrationMode SelectMode()
    {
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("\n[blue]?[/] What would you like to do?")
                .HighlightStyle(new Style(Color.Cyan1))
                .AddChoices(
                    "Status / Diff",
                    "Generate Migration",
                    "Migrate & Update",
                    "Dry Run",
                    "Rollback",
                    "Change Project / Database",
                    "[grey]Exit[/]"));

        return selected switch
        {
            "Migrate & Update" => MigrationMode.MigrateAndUpdate,
            "Dry Run" => MigrationMode.DryRun,
            "Status / Diff" => MigrationMode.Status,
            "Rollback" => MigrationMode.Rollback,
            "Generate Migration" => MigrationMode.GenerateMigration,
            "Change Project / Database" => MigrationMode.ChangeDatabase,
            "[grey]Exit[/]" => MigrationMode.Exit,
            _ => MigrationMode.Exit
        };
    }

    /// <summary>
    /// Prompt for a new migration name (for Generate mode).
    /// </summary>
    public string PromptForMigrationName()
    {
        return AnsiConsole.Prompt(
            new TextPrompt<string>("[blue]?[/] Enter migration name:")
                .Validate(name =>
                {
                    if (string.IsNullOrWhiteSpace(name))
                        return ValidationResult.Error("[red]Migration name cannot be empty.[/]");
                    if (name.Contains(' '))
                        return ValidationResult.Error("[red]Migration name cannot contain spaces. Use PascalCase.[/]");
                    return ValidationResult.Success();
                }));
    }

    /// <summary>
    /// Prompt for the target migration name when rolling back.
    /// </summary>
    public string PromptForRollbackTarget()
    {
        return AnsiConsole.Prompt(
            new TextPrompt<string>("[blue]?[/] Enter target migration name to rollback to:")
                .Validate(name =>
                    string.IsNullOrWhiteSpace(name)
                        ? ValidationResult.Error("[red]Migration name cannot be empty.[/]")
                        : ValidationResult.Success()));
    }

    /// <summary>
    /// Prompt the user for a schema name to target during migration.
    /// Shows the auto-derived default (e.g. "inventory") — press Enter to accept.
    /// Used for client-level schema isolation (e.g. "client_acme").
    /// </summary>
    public string PromptForSchemaName(string defaultSchema)
    {
        return AnsiConsole.Prompt(
            new TextPrompt<string>($"[blue]?[/] Schema name [grey](default: {defaultSchema})[/]:")
                .DefaultValue(defaultSchema)
                .Validate(name =>
                {
                    if (string.IsNullOrWhiteSpace(name))
                        return ValidationResult.Error("[red]Schema name cannot be empty.[/]");
                    if (name.Contains(' '))
                        return ValidationResult.Error("[red]Schema name cannot contain spaces.[/]");
                    return ValidationResult.Success();
                }));
    }
}
