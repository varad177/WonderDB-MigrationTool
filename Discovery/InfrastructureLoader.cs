using System.Diagnostics;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace WonderDB.MigrationTool.Discovery;

/// <summary>
/// Metadata about a DbContext discovered from source code scanning.
/// No assembly loading required — fully version-agnostic.
/// </summary>
public class DbContextInfo
{
    /// <summary>
    /// The simple class name of the DbContext (e.g., "AppDbContext").
    /// </summary>
    public string ContextName { get; set; } = string.Empty;

    /// <summary>
    /// Inferred schema name (derived from the context class name).
    /// </summary>
    public string SchemaName { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path to the Infrastructure project folder.
    /// </summary>
    public string ProjectPath { get; set; } = string.Empty;
}

/// <summary>
/// Discovers DbContext classes by scanning source code files in the Infrastructure project.
/// Does NOT load assemblies — making it fully version-agnostic and scalable.
/// </summary>
public class InfrastructureLoader
{
    // Regex to find classes that inherit from DbContext
    // Matches: "class SomeName : DbContext" or "class SomeName : SomeBase, ISomething" where SomeBase contains "DbContext"
    private static readonly Regex DbContextClassRegex = new(
        @"class\s+(\w+)\s*(?:<[^>]*>)?\s*:\s*([^{]+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Scan the Infrastructure project's source files to discover DbContext classes.
    /// This is a lightweight, version-agnostic alternative to assembly reflection.
    /// </summary>
    public Task<List<DbContextInfo>> DiscoverDbContextsAsync(string infrastructurePath)
    {
        var contexts = new List<DbContextInfo>();

        // Scan all .cs files in the project
        var csFiles = Directory.GetFiles(infrastructurePath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("bin", "")) && 
                        !f.Contains(Path.Combine("obj", "")) &&
                        !f.Contains("WonderDb_DesignTimeFactory"))
            .ToList();

        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);
            var matches = DbContextClassRegex.Matches(content);

            foreach (Match match in matches)
            {
                var className = match.Groups[1].Value.Trim();
                var baseTypes = match.Groups[2].Value.Trim();

                // Check if any base type contains "DbContext" and does NOT contain "IDesignTimeDbContextFactory"
                if (baseTypes.Contains("DbContext", StringComparison.OrdinalIgnoreCase) && 
                    !baseTypes.Contains("IDesignTimeDbContextFactory", StringComparison.OrdinalIgnoreCase))
                {
                    // Skip abstract classes
                    var lineStart = content.LastIndexOf('\n', match.Index) + 1;
                    var lineContent = content[lineStart..match.Index];
                    if (lineContent.Contains("abstract", StringComparison.OrdinalIgnoreCase))
                        continue;

                    contexts.Add(new DbContextInfo
                    {
                        ContextName = className,
                        SchemaName = DeriveSchemaName(className),
                        ProjectPath = infrastructurePath
                    });
                }
            }
        }

        return Task.FromResult(contexts);
    }

    /// <summary>
    /// Ensure the project builds successfully. Called before EF Core CLI operations.
    /// </summary>
    public async Task EnsureProjectBuildsAsync(string infrastructurePath)
    {
        var csprojFile = Directory.GetFiles(infrastructurePath, "*.csproj", SearchOption.TopDirectoryOnly)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No .csproj file found in '{infrastructurePath}'.");

        AnsiConsole.MarkupLine("[blue]ℹ Building project...[/]");
        await BuildProjectAsync(csprojFile);
    }

    /// <summary>
    /// Derive a schema name from the context type name by convention.
    /// "OrderDbContext" → "orders", "AuditContext" → "audit"
    /// </summary>
    private static string DeriveSchemaName(string contextName)
    {
        var name = contextName;

        if (name.EndsWith("DbContext", StringComparison.OrdinalIgnoreCase))
            name = name[..^9];
        else if (name.EndsWith("Context", StringComparison.OrdinalIgnoreCase))
            name = name[..^7];

        return name.ToLowerInvariant();
    }

    /// <summary>
    /// Invoke 'dotnet build' on the given project and throw on failure.
    /// </summary>
    private static async Task BuildProjectAsync(string csprojPath)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{csprojPath}\" -c Debug --nologo -v quiet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
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
            AnsiConsole.MarkupLine("[red]✗ Build failed![/]");

            if (!string.IsNullOrWhiteSpace(error))
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            if (!string.IsNullOrWhiteSpace(output))
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(output)}[/]");

            throw new InvalidOperationException(
                "dotnet build failed. Fix the compilation errors above and retry.");
        }

        AnsiConsole.MarkupLine("[green]✓ Build succeeded.[/]");
    }
}
