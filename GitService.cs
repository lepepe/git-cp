using System.Diagnostics;
using System.Text;
using cp.Models;

namespace cp;

public class GitService
{
    public string RepoPath { get; }

    public GitService(string repoPath)
    {
        RepoPath = repoPath;
    }

    // ── Basic checks ────────────────────────────────────────────────────────

    public bool IsGitRepo()
    {
        var result = Run("rev-parse", "--is-inside-work-tree");
        return result.Success && result.Output.Trim() == "true";
    }

    public string CurrentBranch()
    {
        var result = Run("branch", "--show-current");
        return result.Success ? result.Output.Trim() : "HEAD";
    }

    public string[] AllBranches()
    {
        var result = Run("branch", "-a", "--format=%(refname:short)");
        if (!result.Success)
            return [];
        return result
            .Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(b => b.Trim().TrimStart('*').Trim())
            .Where(b => b.Length > 0)
            .Distinct()
            .ToArray();
    }

    // ── Commits ──────────────────────────────────────────────────────────────

    public List<CommitInfo> GetCommits(string branch, int limit = 50)
    {
        // format: <hash>|<short>|<author>|<date>|<subject>
        var result = Run(
            "log",
            branch,
            $"--max-count={limit}",
            "--pretty=format:%H|%h|%an|%ad|%s",
            "--date=short"
        );

        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            return [];

        var commits = new List<CommitInfo>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', 5);
            if (parts.Length == 5)
                commits.Add(new CommitInfo(parts[0], parts[1], parts[2], parts[3], parts[4]));
        }
        return commits;
    }

    // ── Branch operations ────────────────────────────────────────────────────

    public bool BranchExists(string branch)
    {
        var result = Run("branch", "--list", branch);
        return result.Success && !string.IsNullOrWhiteSpace(result.Output);
    }

    public GitResult CheckoutExisting(string branch) => Run("checkout", branch);

    public GitResult CheckoutNew(string branch) => Run("checkout", "-b", branch);

    // ── Cherry-pick ──────────────────────────────────────────────────────────

    public GitResult CherryPick(string hash) => Run("cherry-pick", hash);

    public GitResult CherryPickContinue() => Run("cherry-pick", "--continue");

    public GitResult CherryPickAbort() => Run("cherry-pick", "--abort");

    public GitResult CherryPickSkip() => Run("cherry-pick", "--skip");

    public string[] ConflictedFiles()
    {
        var result = Run("diff", "--name-only", "--diff-filter=U");
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            return [];
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    public string GetConflictDiff(string file)
    {
        var result = Run("diff", "--", file);
        return result.Output;
    }

    public GitResult StageAll() => Run("add", "-A");

    // ── Editor ───────────────────────────────────────────────────────────────

    public string ResolveEditor()
    {
        return Environment.GetEnvironmentVariable("VISUAL")
            ?? Environment.GetEnvironmentVariable("EDITOR")
            ?? (OperatingSystem.IsWindows() ? "notepad" : "vi");
    }

    public void OpenInEditor(string file)
    {
        var editor = ResolveEditor();

        if (!OperatingSystem.IsWindows())
        {
            // Spectre.Console leaves the terminal in raw/no-echo mode after
            // interactive prompts. Reset it to a sane state so that terminal
            // editors (nvim, vim, nano …) can initialise properly.
            using var stty = Process.Start(new ProcessStartInfo("stty", "sane")
            {
                UseShellExecute = false,
            });
            stty?.WaitForExit();
        }

        var psi = new ProcessStartInfo(editor)
        {
            WorkingDirectory = RepoPath,
            UseShellExecute  = false,
            // No stream redirection — the editor must own the terminal
        };
        psi.ArgumentList.Add(file);

        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
    }

    /// <summary>Returns true for known GUI editors that need --wait to block.</summary>
    public static bool IsGuiEditor(string editor) =>
        editor.StartsWith("code") || editor.StartsWith("subl") ||
        editor.StartsWith("atom") || editor.StartsWith("gedit") ||
        editor.StartsWith("kate");

    // ── Remote ───────────────────────────────────────────────────────────────

    public GitResult Push(string remote, string branch) => Run("push", remote, branch);

    public GitResult PushSetUpstream(string remote, string branch) =>
        Run("push", "--set-upstream", remote, branch);

    public bool RemoteExists(string remote = "origin")
    {
        var result = Run("remote", "get-url", remote);
        return result.Success;
    }

    // ── Runner ───────────────────────────────────────────────────────────────

    public GitResult Run(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // git cherry-pick --continue needs a terminal for the editor; skip the
        // editor by passing GIT_EDITOR=true (a no-op command that returns 0).
        psi.EnvironmentVariables["GIT_EDITOR"] = "true";

        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();

        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;

        return new GitResult(proc.ExitCode == 0, stdout, stderr, proc.ExitCode);
    }
}

public record GitResult(bool Success, string Output, string Error, int ExitCode)
{
    public string CombinedOutput =>
        string.IsNullOrWhiteSpace(Error) ? Output
        : string.IsNullOrWhiteSpace(Output) ? Error
        : $"{Output}\n{Error}";
}
