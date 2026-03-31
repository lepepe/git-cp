using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace cp;

public static class UpdateService
{
    private const string Repo = "lepepe/git-cp";
    private static readonly HttpClient Http;

    static UpdateService()
    {
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("git-cp-updater");
    }

    public static async Task CheckAndPromptAsync()
    {
        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version;
            if (current is null) return;

            var release = await Http.GetFromJsonAsync(
                $"https://api.github.com/repos/{Repo}/releases/latest",
                GithubJsonContext.Default.GithubRelease
            );
            if (release is null) return;

            var tag = release.TagName.TrimStart('v');
            if (!Version.TryParse(tag, out var latest)) return;
            if (latest <= current) return;

            AnsiConsole.MarkupLine(
                $"[yellow]Update available:[/] [cornflowerblue]{release.TagName}[/] " +
                $"(you have [grey]v{current.ToString(3)}[/])"
            );

            if (!AnsiConsole.Confirm("Install update now?", defaultValue: false))
                return;

            var assetName = GetAssetName();
            if (assetName is null)
            {
                AnsiConsole.MarkupLine("[yellow]Auto-update is not supported on this platform.[/]");
                return;
            }

            var asset = release.Assets.FirstOrDefault(a => a.Name == assetName);
            if (asset is null)
            {
                AnsiConsole.MarkupLine($"[red]Asset '[/][grey]{Markup.Escape(assetName)}[/][red]' not found in {Markup.Escape(release.TagName)}.[/]");
                return;
            }

            await DownloadAndReplaceAsync(asset.BrowserDownloadUrl, release.TagName);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Network unavailable or timeout — silently skip update check
        }
    }

    private static async Task DownloadAndReplaceAsync(string downloadUrl, string version)
    {
        var binaryPath = Environment.ProcessPath;
        if (binaryPath is null)
        {
            AnsiConsole.MarkupLine("[red]Could not determine current binary path.[/]");
            return;
        }

        var tmpPath = binaryPath + ".new";

        await AnsiConsole
            .Status()
            .StartAsync(
                $"Downloading {version}…",
                async ctx =>
                {
                    ctx.Spinner(Spinner.Known.Dots);
                    var bytes = await Http.GetByteArrayAsync(downloadUrl);
                    await File.WriteAllBytesAsync(tmpPath, bytes);
                }
            );

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows: can't overwrite a running exe directly.
                // Rename the current binary to .old first, then move the new one in.
                var oldPath = binaryPath + ".old";
                File.Move(binaryPath, oldPath, overwrite: true);
                File.Move(tmpPath, binaryPath);
            }
            else
            {
                // Linux / macOS: atomic overwrite works on running binaries (inode stays alive).
                File.Move(tmpPath, binaryPath, overwrite: true);

                var psi = new ProcessStartInfo("chmod") { UseShellExecute = false };
                psi.ArgumentList.Add("+x");
                psi.ArgumentList.Add(binaryPath);
                using var chmod = Process.Start(psi);
                chmod?.WaitForExit();
            }

            AnsiConsole.MarkupLine(
                $"[green]✓[/] Updated to [cornflowerblue]{version}[/]. " +
                "Restart git-cp to use the new version."
            );
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to replace binary:[/] {Markup.Escape(ex.Message)}");
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);
        }
    }

    private static string? GetAssetName() =>
        (OperatingSystem.IsLinux(), OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(),
         RuntimeInformation.ProcessArchitecture) switch
        {
            (true,  _,     _,     Architecture.Arm64) => "git-cp-linux-arm64",
            (true,  _,     _,     _)                  => "git-cp-linux-x64",
            (_,     true,  _,     Architecture.Arm64) => "git-cp-win-arm64.exe",
            (_,     true,  _,     _)                  => "git-cp-win-x64.exe",
            (_,     _,     true,  Architecture.Arm64) => "git-cp-osx-arm64",
            (_,     _,     true,  _)                  => "git-cp-osx-x64",
            _                                          => null,
        };
}

internal record GithubRelease(
    [property: JsonPropertyName("tag_name")]  string TagName,
    [property: JsonPropertyName("assets")]    GithubAsset[] Assets
);

internal record GithubAsset(
    [property: JsonPropertyName("name")]                  string Name,
    [property: JsonPropertyName("browser_download_url")]  string BrowserDownloadUrl
);

[JsonSerializable(typeof(GithubRelease))]
internal partial class GithubJsonContext : JsonSerializerContext { }
