using cp;
using cp.Models;
using Spectre.Console;

// ── Banner ────────────────────────────────────────────────────────────────────

AnsiConsole.Write(new FigletText("git-cp").Color(Color.CornflowerBlue));
AnsiConsole.MarkupLine("[grey]Interactive git cherry-pick helper[/]\n");

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

    // ── Conflict handling ──────────────────────────────────────────────────

    var conflicted = git.ConflictedFiles();

    if (conflicted.Length == 0)
    {
        // Not a merge conflict — some other git error
        PrintError($"Cherry-pick failed for {commit.ShortHash}", result);
        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("What do you want to do?")
                .AddChoices("Skip this commit", "Abort all")
        );

        if (action == "Abort all")
        {
            git.CherryPickAbort();
            break;
        }
        git.CherryPickSkip();
        skipped++;
        continue;
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
    bool keepShowing = true;
    while (keepShowing)
    {
        var viewOptions = conflicted
            .SelectMany(f =>
            {
                var check = resolvedFiles.Contains(f) ? "[green]✓[/] " : "   ";
                return new[]
                {
                    $"{check}View diff: {f}",
                    $"{check}Edit in {editor}: {f}",
                };
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
                    $"[yellow]Tip:[/] GUI editors need [bold]--wait[/] in $EDITOR so the app blocks " +
                    $"until you close the file. Example: [grey]export EDITOR=\"{Markup.Escape(editor)} --wait\"[/]");

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
            git.StageAll();
            var cont = git.CherryPickContinue();
            if (cont.Success)
            {
                AnsiConsole.MarkupLine("[green]✓[/] Continued successfully.");
                applied++;
            }
            else
            {
                PrintError("Continue failed", cont);
                AnsiConsole.MarkupLine("[yellow]You may need to resolve more conflicts.[/]");
            }
            break;

        case "Skip this commit":
            git.CherryPickSkip();
            AnsiConsole.MarkupLine($"[yellow]⊘[/] Skipped [cornflowerblue]{commit.ShortHash}[/]");
            skipped++;
            break;

        case "Abort all remaining cherry-picks":
            git.CherryPickAbort();
            AnsiConsole.MarkupLine("[red]Aborted.[/] Returning to original state.");
            goto Done;
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

AnsiConsole.MarkupLine("[green]Done![/]");

return 0;

// ── Helpers ───────────────────────────────────────────────────────────────────

static void PrintError(string title, GitResult r)
{
    AnsiConsole.Write(
        new Panel($"[red]{Markup.Escape(r.CombinedOutput.Trim())}[/]")
            .Header($"[red] {Markup.Escape(title)} [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Red)
    );
}

static void PrintColoredDiff(string diff)
{
    if (string.IsNullOrWhiteSpace(diff))
    {
        AnsiConsole.MarkupLine("[grey](empty diff)[/]");
        return;
    }

    foreach (var line in diff.Split('\n'))
    {
        var escaped = Markup.Escape(line);
        if (line.StartsWith('+'))
            AnsiConsole.MarkupLine($"[green]{escaped}[/]");
        else if (line.StartsWith('-'))
            AnsiConsole.MarkupLine($"[red]{escaped}[/]");
        else if (line.StartsWith('@'))
            AnsiConsole.MarkupLine($"[cornflowerblue]{escaped}[/]");
        else
            AnsiConsole.WriteLine(line);
    }
}
