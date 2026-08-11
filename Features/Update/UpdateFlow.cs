using Spectre.Console;

namespace cp.Features.Update;

internal static class UpdateFlow
{
    public static async Task<int> RunAsync()
    {
        var currentVersion = UpdateService.GetCurrentVersion();
        AnsiConsole.MarkupLine($"[grey]Current version:[/] [bold]{Markup.Escape(currentVersion)}[/]\n");

        UpdateCheckOutcome outcome = null!;
        await AnsiConsole
            .Status()
            .StartAsync(
                "Checking for updates…",
                async ctx =>
                {
                    ctx.Spinner(Spinner.Known.Dots);
                    outcome = await UpdateService.CheckForUpdateNowAsync(currentVersion);
                }
            );

        switch (outcome.Status)
        {
            case UpdateStatus.CheckFailed:
                AnsiConsole.MarkupLine(
                    "[red]Could not check for updates.[/] GitHub may be unreachable, or no build is available for this platform."
                );
                return 1;

            case UpdateStatus.UpToDate:
                AnsiConsole.MarkupLine(
                    $"[green]✓[/] You're already on the latest version ([bold]v{Markup.Escape(outcome.LatestVersion ?? currentVersion)}[/])."
                );
                return 0;

            case UpdateStatus.UpdateAvailable:
                AnsiConsole.MarkupLine(
                    $"[cornflowerblue]A new version is available:[/] [bold]v{Markup.Escape(outcome.LatestVersion!)}[/] "
                        + $"[grey](current: v{Markup.Escape(currentVersion)})[/]"
                );

                if (!AnsiConsole.Confirm("Update now?", defaultValue: true))
                {
                    AnsiConsole.MarkupLine("[yellow]Update skipped.[/]");
                    return 0;
                }

                UpdateApplyResult result = null!;
                await AnsiConsole
                    .Status()
                    .StartAsync(
                        "Downloading…",
                        async ctx =>
                        {
                            ctx.Spinner(Spinner.Known.Dots);
                            result = await UpdateService.ApplyUpdateAsync(
                                outcome.DownloadUrl!,
                                outcome.AssetName!,
                                outcome.ChecksumUrl
                            );
                        }
                    );

                AnsiConsole.MarkupLine(
                    result.Success
                        ? $"[green]✓[/] {Markup.Escape(result.Message)}"
                        : $"[red]✗[/] {Markup.Escape(result.Message)}"
                );
                return result.Success ? 0 : 1;

            default:
                return 1;
        }
    }
}
