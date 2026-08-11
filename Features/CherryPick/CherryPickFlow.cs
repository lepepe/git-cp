using cp.Features.Update;
using Spectre.Console;
using static cp.Features.CherryPick.ConsolePrinter;

namespace cp.Features.CherryPick;

internal static class CherryPickFlow
{
    public static async Task<int> RunAsync()
    {
        UpdateService.CleanupStaleWindowsOldFile();
        var updateCheckTask = UpdateService.CheckForUpdateInBackgroundAsync(UpdateService.GetCurrentVersion());

        // ── Locate repo ───────────────────────────────────────────────────────────────

        var workDir = Directory.GetCurrentDirectory();
        var git = new GitService(workDir);

        if (!git.IsGitRepo())
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Not inside a git repository.");
            return 1;
        }

        // ── Source branch ─────────────────────────────────────────────────────────────

        var currentBranch = git.CurrentBranch();
        var allBranches = git.AllBranches();

        AnsiConsole.MarkupLine($"[grey]Current branch:[/] [bold]{Markup.Escape(currentBranch)}[/]");

        var branchPrompt = new SelectionPrompt<string>()
            .Title("Pick the [cornflowerblue]source branch[/] to cherry-pick from:")
            .PageSize(12)
            .EnableSearch()
            .SearchPlaceholderText("Type to search source branch...")
            .HighlightStyle(new Style(foreground: Color.CornflowerBlue))
            .AddChoices(allBranches);

        branchPrompt.SearchHighlightStyle = new Style(foreground: Color.Green, decoration: Decoration.Bold);

        var sourceBranch = AnsiConsole.Prompt(branchPrompt);

        // ── Load commits ──────────────────────────────────────────────────────────────

        List<CommitInfo> commits = [];

        AnsiConsole
            .Status()
            .Start(
                "Loading commits…",
                ctx =>
                {
                    ctx.Spinner(Spinner.Known.Dots);
                    commits = git.GetCommits(sourceBranch, limit: 60);
                }
            );

        if (commits.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No commits found on that branch.[/]");
            return 0;
        }

        // ── Display commit table

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[grey]Hash[/]").Centered())
            .AddColumn(new TableColumn("[grey]Date[/]").Centered())
            .AddColumn(new TableColumn("[grey]Author[/]"))
            .AddColumn(new TableColumn("[grey]Message[/]"));

        foreach (var c in commits)
            table.AddRow(
                $"[cornflowerblue]{Markup.Escape(c.ShortHash)}[/]",
                $"[grey]{Markup.Escape(c.Date)}[/]",
                Markup.Escape(c.Author.Length > 20 ? c.Author[..20] : c.Author),
                Markup.Escape(c.Message.Length > 70 ? c.Message[..70] + "…" : c.Message)
            );

        AnsiConsole.Write(table);

        // ── Multi-select commits ──────────────────────────────────────────────────────

        var selected = AnsiConsole.Prompt(
            new MultiSelectionPrompt<CommitInfo>()
                .Title(
                    "\nSelect [cornflowerblue]commits[/] to cherry-pick [grey](Space = toggle, Enter = confirm)[/]:"
                )
                .PageSize(15)
                .NotRequired()
                .UseConverter(c =>
                    $"[cornflowerblue]{c.ShortHash}[/] [grey]{c.Date}[/] {Markup.Escape(c.Author.Length > 18 ? c.Author[..18] : c.Author), -18} {Markup.Escape(c.Message.Length > 55 ? c.Message[..55] + "…" : c.Message)}"
                )
                .AddChoices(commits)
        );

        if (selected.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No commits selected. Exiting.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"\n[green]{selected.Count}[/] commit(s) selected.\n");

        // ── Target branch ─────────────────────────────────────────────────────────────

        var targetBranch = AnsiConsole.Prompt(
            new TextPrompt<string>("Enter the [cornflowerblue]target branch[/] name:").Validate(name =>
            {
                if (string.IsNullOrWhiteSpace(name))
                    return ValidationResult.Error("[red]Branch name cannot be empty.[/]");
                if (name.Contains(' '))
                    return ValidationResult.Error("[red]Branch name cannot contain spaces.[/]");
                return ValidationResult.Success();
            })
        );

        // ── Checkout / create target branch ──────────────────────────────────────────

        AnsiConsole.WriteLine();
        AnsiConsole.Write(
            new Rule(
                $"[cornflowerblue]Targeting branch:[/] [bold]{Markup.Escape(targetBranch)}[/]"
            ).RuleStyle("grey")
        );

        if (git.BranchExists(targetBranch))
        {
            AnsiConsole.MarkupLine($"Branch [bold]{Markup.Escape(targetBranch)}[/] exists. Checking out…");
            var co = git.CheckoutExisting(targetBranch);
            if (!co.Success)
            {
                PrintError("Checkout failed", co);
                return 1;
            }
        }
        else
        {
            var create = AnsiConsole.Confirm(
                $"Branch [bold]{Markup.Escape(targetBranch)}[/] doesn't exist. Create it?"
            );
            if (!create)
                return 0;

            AnsiConsole.MarkupLine($"Creating [bold]{Markup.Escape(targetBranch)}[/]…");
            var cb = git.CheckoutNew(targetBranch);
            if (!cb.Success)
            {
                PrintError("Branch creation failed", cb);
                return 1;
            }
        }

        AnsiConsole.MarkupLine($"[green]✓[/] Now on [bold]{Markup.Escape(targetBranch)}[/]\n");

        // ── Cherry-pick loop ──────────────────────────────────────────────────────────

        // Apply oldest → newest so the history order is preserved
        var toApply = selected.ToList();
        toApply.Reverse();

        int applied = 0,
            skipped = 0;

        foreach (var commit in toApply)
        {
            AnsiConsole.Write(
                new Rule(
                    $"[grey]Cherry-picking[/] [cornflowerblue]{commit.ShortHash}[/] — {Markup.Escape(commit.Message)}"
                ).RuleStyle("grey")
            );

            var result = git.CherryPick(commit.Hash);

            if (result.Success)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Applied [cornflowerblue]{commit.ShortHash}[/]");
                applied++;
                continue;
            }

            switch (ConflictResolver.HandleFailure(git, commit, result))
            {
                case ConflictOutcome.Applied:
                    applied++;
                    break;
                case ConflictOutcome.Skipped:
                    skipped++;
                    break;
                case ConflictOutcome.AbortedAll:
                    goto Done;
                case ConflictOutcome.None:
                    break;
            }
        }

        Done:
        // ── Summary ───────────────────────────────────────────────────────────────────

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cornflowerblue]Summary[/]").RuleStyle("grey"));

        var summary = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("Result")
            .AddColumn("Count");

        summary.AddRow("[green]Applied[/]", $"[green]{applied}[/]");
        summary.AddRow("[yellow]Skipped[/]", $"[yellow]{skipped}[/]");
        summary.AddRow("Total selected", $"{selected.Count}");

        AnsiConsole.Write(summary);
        AnsiConsole.MarkupLine($"\n[grey]Branch:[/] [bold]{Markup.Escape(targetBranch)}[/]");

        // ── Push prompt ───────────────────────────────────────────────────────────────

        if (applied > 0 && git.RemoteExists("origin"))
        {
            AnsiConsole.WriteLine();
            var pushCommand = $"git push origin {targetBranch}";
            var doPush = AnsiConsole.Confirm(
                $"Push to remote? [grey]({Markup.Escape(pushCommand)})[/]",
                defaultValue: false
            );

            if (doPush)
            {
                GitResult pushResult;
                AnsiConsole
                    .Status()
                    .Start(
                        "Pushing…",
                        ctx =>
                        {
                            ctx.Spinner(Spinner.Known.Dots);
                            pushResult = git.Push("origin", targetBranch);

                            // Branch not yet tracked — retry with --set-upstream
                            if (!pushResult.Success && pushResult.Error.Contains("no upstream"))
                                pushResult = git.PushSetUpstream("origin", targetBranch);

                            if (pushResult.Success)
                                AnsiConsole.MarkupLine(
                                    $"[green]✓[/] Pushed [bold]{Markup.Escape(targetBranch)}[/] to origin."
                                );
                            else
                                PrintError("Push failed", pushResult);
                        }
                    );
            }
        }

        await Task.WhenAny(updateCheckTask, Task.Delay(150));
        if (updateCheckTask.IsCompletedSuccessfully && updateCheckTask.Result is { } updateInfo)
        {
            AnsiConsole.MarkupLine(
                $"\n[grey]A new version of git-cp is available: v{Markup.Escape(updateInfo.LatestVersion)} "
                    + $"(current: v{Markup.Escape(UpdateService.GetCurrentVersion())}). "
                    + $"Run 'git cp --update' to install it.[/]"
            );
        }

        AnsiConsole.MarkupLine("[green]Done![/]");

        return 0;
    }
}
