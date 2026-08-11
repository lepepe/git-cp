using Spectre.Console;

namespace cp.Features.CherryPick;

internal static class ConsolePrinter
{
    public static void PrintError(string title, GitResult r)
    {
        AnsiConsole.Write(
            new Panel($"[red]{Markup.Escape(r.CombinedOutput.Trim())}[/]")
                .Header($"[red] {Markup.Escape(title)} [/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Red)
        );
    }

    public static void PrintColoredDiff(string diff)
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
}
