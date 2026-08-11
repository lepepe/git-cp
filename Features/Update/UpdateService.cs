using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace cp.Features.Update;

public static class UpdateService
{
    private const string Repo = "lepepe/git-cp";
    private const string LatestReleaseApiUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("git-cp-update-check");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    // ── Version ──────────────────────────────────────────────────────────────

    public static string GetCurrentVersion()
    {
        var info = Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(info))
            return "0.0.0";

        var plusIndex = info.IndexOf('+');
        return plusIndex >= 0 ? info[..plusIndex] : info;
    }

    public static bool IsDevBuild(string version) =>
        version == "0.0.0" || !Version.TryParse(version, out _);

    // ── Platform ─────────────────────────────────────────────────────────────

    public static string? GetAssetNameForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
            return "git-cp-win-x64.exe";

        if (OperatingSystem.IsMacOS())
            return RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "git-cp-osx-arm64"
                : "git-cp-osx-x64";

        if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64)
            return "git-cp-linux-x64";

        return null;
    }

    // ── Passive check (background, cached) ────────────────────────────────────

    public static async Task<UpdateCheckResult?> CheckForUpdateInBackgroundAsync(
        string currentVersion,
        CancellationToken ct = default
    )
    {
        if (IsDevBuild(currentVersion))
            return null;

        try
        {
            var cache = await ReadCacheAsync(ct);
            if (cache is not null && DateTimeOffset.UtcNow - cache.LastCheckedUtc < CheckInterval)
            {
                return IsNewer(cache.LatestKnownVersion, currentVersion)
                    ? new UpdateCheckResult(NormalizeVersion(cache.LatestKnownVersion!))
                    : null;
            }

            var release = await FetchLatestReleaseAsync(ct);
            if (release is null)
                return null;

            await WriteCacheAsync(new UpdateCache(DateTimeOffset.UtcNow, release.TagName), ct);

            return IsNewer(release.TagName, currentVersion)
                ? new UpdateCheckResult(NormalizeVersion(release.TagName))
                : null;
        }
        catch
        {
            return null;
        }
    }

    // ── Explicit check (--update, bypasses cache interval) ────────────────────

    public static async Task<UpdateCheckOutcome> CheckForUpdateNowAsync(
        string currentVersion,
        CancellationToken ct = default
    )
    {
        var release = await FetchLatestReleaseAsync(ct);
        if (release is null)
            return new UpdateCheckOutcome(UpdateStatus.CheckFailed);

        try
        {
            await WriteCacheAsync(new UpdateCache(DateTimeOffset.UtcNow, release.TagName), ct);
        }
        catch
        {
            // Best-effort — a cache write failure shouldn't block the update flow.
        }

        var latestVersion = NormalizeVersion(release.TagName);

        if (!IsNewer(release.TagName, currentVersion))
            return new UpdateCheckOutcome(UpdateStatus.UpToDate, LatestVersion: latestVersion);

        var assetName = GetAssetNameForCurrentPlatform();
        var asset = assetName is null ? null : release.Assets.FirstOrDefault(a => a.Name == assetName);
        if (assetName is null || asset is null)
            return new UpdateCheckOutcome(UpdateStatus.CheckFailed, LatestVersion: latestVersion);

        var checksumAsset = release.Assets.FirstOrDefault(a => a.Name == "checksums.txt");

        return new UpdateCheckOutcome(
            UpdateStatus.UpdateAvailable,
            LatestVersion: latestVersion,
            DownloadUrl: asset.BrowserDownloadUrl,
            AssetName: assetName,
            ChecksumUrl: checksumAsset?.BrowserDownloadUrl
        );
    }

    // ── Apply (download → verify → swap) ──────────────────────────────────────

    public static async Task<UpdateApplyResult> ApplyUpdateAsync(
        string downloadUrl,
        string assetName,
        string? checksumUrl,
        CancellationToken ct = default
    )
    {
        var currentExePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExePath))
            return new UpdateApplyResult(false, "Could not determine the current executable's path.");

        var dir = Path.GetDirectoryName(currentExePath)!;
        var tempPath = Path.Combine(dir, $"git-cp.download.{Environment.ProcessId}.tmp");

        try
        {
            await DownloadToTempFileAsync(downloadUrl, tempPath, ct);

            if (checksumUrl is not null)
            {
                var verified = await VerifyChecksumAsync(tempPath, assetName, checksumUrl, ct);
                if (!verified)
                {
                    TryDelete(tempPath);
                    return new UpdateApplyResult(
                        false,
                        "Checksum verification failed — update aborted for safety."
                    );
                }
            }

            return OperatingSystem.IsWindows()
                ? ReplaceWindows(tempPath, currentExePath)
                : ReplaceUnix(tempPath, currentExePath);
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            return new UpdateApplyResult(false, $"Update failed: {ex.Message}");
        }
    }

    private static async Task DownloadToTempFileAsync(string url, string destinationPath, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var expectedLength = response.Content.Headers.ContentLength;

        await using (var httpStream = await response.Content.ReadAsStreamAsync(ct))
        await using (var fileStream = File.Create(destinationPath))
        {
            await httpStream.CopyToAsync(fileStream, ct);

            if (expectedLength is not null && fileStream.Length != expectedLength)
                throw new IOException(
                    $"Downloaded file size ({fileStream.Length}) does not match expected size ({expectedLength})."
                );
        }
    }

    private static async Task<bool> VerifyChecksumAsync(
        string filePath,
        string assetName,
        string checksumUrl,
        CancellationToken ct
    )
    {
        string checksumsText;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            checksumsText = await Http.GetStringAsync(checksumUrl, cts.Token);
        }
        catch
        {
            // No checksums reachable for this release — degrade to the size check already done.
            return true;
        }

        var expectedHash = checksumsText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[1].TrimStart('*') == assetName)
            .Select(parts => parts[0])
            .FirstOrDefault();

        if (expectedHash is null)
            return true; // Asset not listed in checksums.txt — degrade gracefully.

        await using var stream = File.OpenRead(filePath);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));

        return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    // ── Platform swap ──────────────────────────────────────────────────────────

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static UpdateApplyResult ReplaceUnix(string tempPath, string currentExePath)
    {
        try
        {
            MakeExecutable(tempPath);
            File.Move(tempPath, currentExePath, overwrite: true);
            return new UpdateApplyResult(
                true,
                "Updated successfully. The new version will be used the next time you run git cp."
            );
        }
        catch (Exception ex)
        {
            var newPath = currentExePath + ".new";
            try
            {
                if (File.Exists(newPath))
                    File.Delete(newPath);
                File.Move(tempPath, newPath);
            }
            catch
            {
                TryDelete(tempPath);
            }

            return new UpdateApplyResult(
                false,
                $"Couldn't replace the running binary automatically ({ex.Message}). "
                    + $"The downloaded update is at '{newPath}' — finish manually with: mv \"{newPath}\" \"{currentExePath}\""
            );
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void MakeExecutable(string path)
    {
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(
            path,
            mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
        );
    }

    private static UpdateApplyResult ReplaceWindows(string tempPath, string currentExePath)
    {
        var oldPath = currentExePath + ".old";
        try
        {
            File.Move(currentExePath, oldPath, overwrite: true);
            File.Move(tempPath, currentExePath);
            return new UpdateApplyResult(true, "Updated successfully. Re-run git cp to use the new version.");
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(false, $"Update failed while swapping the executable: {ex.Message}");
        }
    }

    /// <summary>Deletes a stale git-cp.exe.old left behind by a previous Windows update, if it's no longer locked.</summary>
    public static void CleanupStaleWindowsOldFile()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var currentExePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExePath))
            return;

        var oldPath = currentExePath + ".old";
        try
        {
            if (File.Exists(oldPath))
                File.Delete(oldPath);
        }
        catch
        {
            // Still locked by a previous instance — retry on a future launch.
        }
    }

    // ── GitHub API + cache ───────────────────────────────────────────────────

    private static async Task<GitHubRelease?> FetchLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            return await Http.GetFromJsonAsync<GitHubRelease>(LatestReleaseApiUrl, cts.Token);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeVersion(string tagName) => tagName.StartsWith('v') ? tagName[1..] : tagName;

    private static bool IsNewer(string? latestTagOrVersion, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(latestTagOrVersion))
            return false;
        if (!Version.TryParse(NormalizeVersion(latestTagOrVersion), out var latest))
            return false;
        if (!Version.TryParse(currentVersion, out var current))
            return false;

        return latest > current;
    }

    private static string GetCacheFilePath()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "git-cp", "update-cache.json");
        }

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configDir = !string.IsNullOrWhiteSpace(xdgConfig)
            ? xdgConfig
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return Path.Combine(configDir, "git-cp", "update-cache.json");
    }

    private static async Task<UpdateCache?> ReadCacheAsync(CancellationToken ct)
    {
        var path = GetCacheFilePath();
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<UpdateCache>(stream, cancellationToken: ct);
    }

    private static async Task WriteCacheAsync(UpdateCache cache, CancellationToken ct)
    {
        var path = GetCacheFilePath();
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
            await JsonSerializer.SerializeAsync(stream, cache, cancellationToken: ct);

        File.Move(tempPath, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}

public enum UpdateStatus
{
    UpToDate,
    UpdateAvailable,
    CheckFailed,
}

public record UpdateCheckResult(string LatestVersion);

public record UpdateCheckOutcome(
    UpdateStatus Status,
    string? LatestVersion = null,
    string? DownloadUrl = null,
    string? AssetName = null,
    string? ChecksumUrl = null
);

public record UpdateApplyResult(bool Success, string Message);
