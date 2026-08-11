using Spectre.Console;
using static cp.Features.CherryPick.ConsolePrinter;

namespace cp.Features.CherryPick;

internal enum ConflictOutcome
{
    Applied,
    Skipped,
    AbortedAll,
    None,
}

internal static class ConflictResolver
{
    public static ConflictOutcome HandleFailure(GitService git, CommitInfo commit, GitResult cherryPickResult)
    {
        var conflicted = git.ConflictedFiles();

        if (conflicted.Length == 0)
        {
            // Not a merge conflict — some other git error
            PrintError($"Cherry-pick failed for {commit.ShortHash}", cherryPickResult);
            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>().Title("What do you want to do?").AddChoices("Skip this commit", "Abort all")
            );

            if (action == "Abort all")
            {
                git.CherryPickAbort();
                return ConflictOutcome.AbortedAll;
            }
            git.CherryPickSkip();
            return ConflictOutcome.Skipped;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(
            new Panel(
                $"[red]Conflicts detected[/] in [bold]{Markup.Escape(commit.ShortHash)}[/] — {Markup.Escape(commit.Message)}"
            )
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Red)
        );

        // List conflicted files
        var fileTable = new Table()
            .Border(TableBorder.Simple)
            .BorderColor(Color.Red)
            .AddColumn("[red]Conflicted files[/]");
        foreach (var f in conflicted)
            fileTable.AddRow(Markup.Escape(f));
        AnsiConsole.Write(fileTable);

        // View / edit conflicted files
        var editor = git.ResolveEditor();
        var resolvedFiles = new HashSet<string>();
        var keepShowing = true;
        while (keepShowing)
        {
            var viewOptions = conflicted
                .SelectMany(f =>
                {
                    var check = resolvedFiles.Contains(f) ? "[green]✓[/] " : "   ";
                    return new[] { $"{check}View diff: {f}", $"{check}Edit in {editor}: {f}" };
                })
                .Append("Done — proceed to resolution")
                .ToList();

            var view = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Inspect or edit a conflicted file:")
                    .PageSize(12)
                    .HighlightStyle(new Style(foreground: Color.CornflowerBlue))
                    .AddChoices(viewOptions)
            );

            if (view == "Done — proceed to resolution")
            {
                keepShowing = false;
            }
            else if (view.Contains("View diff: "))
            {
                var file = view[(view.IndexOf("View diff: ") + "View diff: ".Length)..];
                var diff = git.GetConflictDiff(file);
                AnsiConsole.Write(new Rule($"[yellow]{Markup.Escape(file)}[/]").RuleStyle("yellow"));
                PrintColoredDiff(diff);
            }
            else if (view.Contains($"Edit in {editor}: "))
            {
                var editKey = $"Edit in {editor}: ";
                var file = view[(view.IndexOf(editKey) + editKey.Length)..];
                var filePath = Path.Combine(git.RepoPath, file);

                if (GitService.IsGuiEditor(editor))
                    AnsiConsole.MarkupLine(
                        $"[yellow]Tip:[/] GUI editors need [bold]--wait[/] in $EDITOR so the app blocks "
                            + $"until you close the file. Example: [grey]export EDITOR=\"{Markup.Escape(editor)} --wait\"[/]"
                    );

                AnsiConsole.MarkupLine($"[grey]Opening [bold]{Markup.Escape(file)}[/] in {Markup.Escape(editor)}…[/]");
                git.OpenInEditor(filePath);
                resolvedFiles.Add(file);
                AnsiConsole.MarkupLine($"[green]✓[/] Returned from editor. {Markup.Escape(file)} marked as resolved.");
            }
        }

        // Resolution choice
        var resolution = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("\nHow do you want to resolve the conflict?")
                .HighlightStyle(new Style(foreground: Color.CornflowerBlue))
                .AddChoices(
                    "I fixed it manually — stage & continue",
                    "Skip this commit",
                    "Abort all remaining cherry-picks"
                )
        );

        switch (resolution)
        {
            case "I fixed it manually — stage & continue":
                git.StageFiles(conflicted);

                var otherDirty = git.DirtyFiles().Except(conflicted).ToArray();
                if (otherDirty.Length > 0)
                {
                    AnsiConsole.MarkupLine(
                        "\n[yellow]Other modified files were found in your working tree — they are [bold]not[/] part of this conflict.[/]"
                    );
                    var extraFiles = AnsiConsole.Prompt(
                        new MultiSelectionPrompt<string>()
                            .Title(
                                "Select any you [cornflowerblue]also[/] want to include in this commit [grey](Space = toggle, Enter = confirm, none selected by default)[/]:"
                            )
                            .PageSize(15)
                            .NotRequired()
                            .UseConverter(Markup.Escape)
                            .AddChoices(otherDirty)
                    );
                    if (extraFiles.Count > 0)
                        git.StageFiles(extraFiles);
                }

                var cont = git.CherryPickContinue();
                if (cont.Success)
                {
                    AnsiConsole.MarkupLine("[green]✓[/] Continued successfully.");
                    return ConflictOutcome.Applied;
                }

                PrintError("Continue failed", cont);
                AnsiConsole.MarkupLine("[yellow]You may need to resolve more conflicts.[/]");
                return ConflictOutcome.None;

            case "Skip this commit":
                git.CherryPickSkip();
                AnsiConsole.MarkupLine($"[yellow]⊘[/] Skipped [cornflowerblue]{commit.ShortHash}[/]");
                return ConflictOutcome.Skipped;

            case "Abort all remaining cherry-picks":
                git.CherryPickAbort();
                AnsiConsole.MarkupLine("[red]Aborted.[/] Returning to original state.");
                return ConflictOutcome.AbortedAll;

            default:
                return ConflictOutcome.None;
        }
    }
}
