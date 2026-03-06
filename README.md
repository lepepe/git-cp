# git-cp — Interactive Git Cherry-Pick Helper

A C# CLI tool that makes `git cherry-pick` simple and visual. Browse commits,
select one or many with a checkbox list, choose a target branch, and handle
conflicts — all without leaving your terminal.

Built with [Spectre.Console](https://spectreconsole.net/) on .NET 10.

---

## Features

- **Branch picker** — select any local or remote branch as the source
- **Commit table** — view the last 60 commits with hash, date, author, and message
- **Multi-select** — toggle commits with `Space`, confirm with `Enter`
- **Target branch** — type a new branch name (creates it) or an existing one (checks it out)
- **Conflict handling**
  - Lists every conflicted file
  - Shows a colour-coded diff per file (`+` green, `-` red, `@@` blue)
  - Lets you fix conflicts in your editor, then stage & continue
  - Or skip the commit, or abort the entire session
- **Summary** — applied / skipped count at the end

---

## Installation

> No .NET SDK needed on the target machine — the binaries are self-contained.

**Linux**

```bash
curl -fsSL https://raw.githubusercontent.com/lepepe/git-cp/main/install.sh | bash
```

**Windows** (PowerShell)

```powershell
irm https://raw.githubusercontent.com/lepepe/git-cp/main/install.ps1 | iex
```

Both scripts download the latest release binary from GitHub, place it in a
user-local directory, and add that directory to your `PATH` automatically.

After installation, restart your terminal and run `git cp` from inside any git repository.

---

## Prerequisites (for building from source)

| Requirement | Version  |
|-------------|----------|
| .NET SDK    | 10.0+    |
| git         | any      |

---

## Build & Run

```bash
# Clone or enter the project directory
cd git-cp

# Run directly (development)
dotnet run

# Build a Release binary
dotnet build -c Release
./bin/Release/net10.0/git-cp
```

### Single-file executables

**Linux (x64)**

```bash
# Build and install to ~/.local/bin in one step
make install
```

Or manually:

```bash
dotnet publish -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -o bin/publish/linux

cp bin/publish/linux/git-cp ~/.local/bin/git-cp
```

**Windows (x64)**

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o bin/publish/win

# Optionally put it on your PATH (run as Administrator)
copy bin\publish\win\git-cp.exe "C:\Windows\System32\git-cp.exe"
```

> Run `git-cp` or `git cp` from inside any git repository.

---

## Usage

```
$ git-cp
# or
$ git cp
```

The tool walks you through each step interactively:

```
Step 1  Pick the source branch to cherry-pick from
Step 2  A table shows the last 60 commits on that branch
Step 3  Select one or more commits (Space toggles, Enter confirms)
Step 4  Enter the name of the target/promotion branch
Step 5  The branch is created or checked out automatically
Step 6  Each commit is cherry-picked in chronological order
        On conflict → view diffs, fix, continue / skip / abort
Step 7  A summary shows how many commits were applied or skipped
```

### Conflict resolution options

| Choice | What happens |
|--------|-------------|
| I fixed it manually — stage & continue | Runs `git add -A && git cherry-pick --continue` |
| Skip this commit | Runs `git cherry-pick --skip` |
| Abort all remaining cherry-picks | Runs `git cherry-pick --abort` and exits |

---

## Releasing a new version

Push a semver tag — the Actions workflow builds both binaries and publishes a
GitHub Release automatically.

```bash
git tag v1.0.0
git push origin v1.0.0
```

The release will contain:

- `git-cp-linux-x64` — self-contained Linux binary
- `git-cp-win-x64.exe` — self-contained Windows binary

---

## Project Structure

```
git-cp/
├── .github/
│   └── workflows/
│       └── release.yml    # CI: build & publish binaries on tag push
├── cp.csproj              # Project file (.NET 10, Spectre.Console 0.54)
├── Makefile               # build + install to ~/.local/bin (Linux)
├── Program.cs             # App entry point — UI flow and cherry-pick loop
├── GitService.cs          # Thin wrapper around git CLI commands
├── install.sh             # One-liner installer for Linux
├── install.ps1            # One-liner installer for Windows
└── Models/
    └── CommitInfo.cs      # Record representing a single commit
```

---

## License

MIT
