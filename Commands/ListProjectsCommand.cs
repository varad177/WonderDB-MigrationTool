using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using WonderDB.MigrationTool.Discovery;

namespace WonderDB.MigrationTool.Commands;

/// <summary>
/// The 'list-projects' command. Lists all discovered .NET projects in the workspace.
/// </summary>
public static class ListProjectsCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("list-projects", "List all discovered .NET projects in the workspace");

        var pathOption = new Option<string?>(
            "--path", "Base path to scan (defaults to /workspace or current directory)");

        command.AddOption(pathOption);

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var path = ctx.ParseResult.GetValueForOption(pathOption);

            try
            {
                await Task.Run(() => Execute(services, path));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                ctx.ExitCode = 1;
            }
        });

        return command;
    }

    private static void Execute(IServiceProvider services, string? basePath)
    {
        var scanner = services.GetRequiredService<ProjectScanner>();

        AnsiConsole.MarkupLine("[blue]ℹ Scanning workspace for .NET Infrastructure projects...[/]");

        var projects = scanner.ScanWorkspace(basePath);

        if (projects.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No Infrastructure projects found.[/]");
            AnsiConsole.MarkupLine(
                "[grey]Ensure your project follows Clean Architecture naming: " +
                "*.Infrastructure with a .csproj file.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[cyan]Discovered Projects[/]")
            .AddColumn(new TableColumn("[bold]#[/]").Centered())
            .AddColumn(new TableColumn("[bold]Project Name[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Infrastructure Path[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Solution Root[/]").LeftAligned());

        for (var i = 0; i < projects.Count; i++)
        {
            var project = projects[i];
            table.AddRow(
                $"[grey]{i + 1}[/]",
                $"[white]{Markup.Escape(project.Name)}[/]",
                Markup.Escape(project.InfrastructurePath),
                Markup.Escape(project.SolutionPath));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[green]Found {projects.Count} project(s).[/]");
    }
}
