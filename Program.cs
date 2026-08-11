using cp.Features.CherryPick;
using cp.Features.Update;
using Spectre.Console;

AnsiConsole.Write(new FigletText("git-cp").Color(Color.CornflowerBlue));
AnsiConsole.MarkupLine("[grey]Interactive git cherry-pick helper[/]\n");

return args.Contains("--update") || args.Contains("-u")
    ? await UpdateFlow.RunAsync()
    : await CherryPickFlow.RunAsync();
