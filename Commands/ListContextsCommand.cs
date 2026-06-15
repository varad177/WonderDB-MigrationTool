using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using WonderDB.MigrationTool.Discovery;

namespace WonderDB.MigrationTool.Commands;

/// <summary>
/// The 'list-contexts' command. Lists all DbContexts found in the Infrastructure assembly.
/// </summary>
public static class ListContextsCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("list-contexts", "List all DbContexts found in the Infrastructure assembly");

        var infrastructureOption = new Option<string?>(
            "--infrastructure", "Path to the Infrastructure project folder");

        command.AddOption(infrastructureOption);

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var infrastructure = ctx.ParseResult.GetValueForOption(infrastructureOption);

            try
            {
                await ExecuteAsync(services, infrastructure);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                ctx.ExitCode = 1;
            }
        });

        return command;
    }

    private static async Task ExecuteAsync(IServiceProvider services, string? infrastructurePath)
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
            AnsiConsole.MarkupLine("[yellow]No DbContext classes found in the project.[/]");
            AnsiConsole.MarkupLine(
                "[grey]Ensure your Infrastructure project contains classes that inherit from " +
                "Microsoft.EntityFrameworkCore.DbContext.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[cyan]Discovered DbContexts[/]")
            .AddColumn(new TableColumn("[bold]#[/]").Centered())
            .AddColumn(new TableColumn("[bold]DbContext[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Schema[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Namespace[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Assembly[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Project Path[/]").LeftAligned());

        for (var i = 0; i < dbContexts.Count; i++)
        {
            var ctx = dbContexts[i];
            table.AddRow(
                $"[grey]{i + 1}[/]",
                $"[white]{Markup.Escape(ctx.ContextName)}[/]",
                Markup.Escape(ctx.SchemaName),
                "—",
                Markup.Escape(ctx.ProjectPath));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[green]Found {dbContexts.Count} DbContext(s).[/]");
    }
}
