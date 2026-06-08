using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using WonderDB.MigrationTool.Audit;

namespace WonderDB.MigrationTool.Commands;

/// <summary>
/// The 'history' command. Displays the full audit log from wonderdb-audit.log.
/// </summary>
public static class HistoryCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("history", "Show full audit log of migration operations");

        var limitOption = new Option<int?>(
            "--limit", "Maximum number of entries to display");

        command.AddOption(limitOption);

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var limit = ctx.ParseResult.GetValueForOption(limitOption);

            try
            {
                await ExecuteAsync(services, limit);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                ctx.ExitCode = 1;
            }
        });

        return command;
    }

    private static async Task ExecuteAsync(IServiceProvider services, int? limit)
    {
        var auditLogger = services.GetRequiredService<AuditLogger>();
        var entries = await auditLogger.GetHistoryAsync(limit);

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No audit history found.[/]");
            AnsiConsole.MarkupLine("[grey]Run a migration to create the first audit entry.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[cyan]Migration Audit History[/]")
            .AddColumn(new TableColumn("[bold]Timestamp[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Project[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Database[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Context[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Migration[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Mode[/]").Centered())
            .AddColumn(new TableColumn("[bold]User[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Result[/]").Centered());

        foreach (var entry in entries)
        {
            var resultColor = entry.Result.Contains("Success", StringComparison.OrdinalIgnoreCase)
                ? "green"
                : "red";

            var modeColor = entry.Mode switch
            {
                "DryRun" => "yellow",
                "Rollback" => "yellow",
                "Generate" => "blue",
                _ => "green"
            };

            table.AddRow(
                $"[grey]{entry.Timestamp:yyyy-MM-dd HH:mm:ss}[/]",
                Markup.Escape(entry.Project),
                Markup.Escape(entry.Database),
                Markup.Escape(entry.Context),
                Markup.Escape(entry.MigrationName),
                $"[{modeColor}]{Markup.Escape(entry.Mode)}[/]",
                Markup.Escape(entry.ExecutedBy),
                $"[{resultColor}]{Markup.Escape(entry.Result)}[/]");
        }

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine($"\n[grey]Showing {entries.Count} entries.[/]");
    }
}
