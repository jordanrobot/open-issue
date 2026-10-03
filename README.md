# Free Issue

Free Issue is a local-first issue and specification manager for Windows. Plain UTF-8 Markdown files in `.free-issue/issues/` are the source of truth; it needs no Git repository, account, network service, or database.

## Requirements

- .NET SDK 10.0.401 (pinned in `global.json`)
- Windows for the WPF desktop application; the core and CLI target portable .NET

## Build and verify

```powershell
dotnet build FreeIssue.slnx
dotnet run --project tests/FreeIssue.Tests
```

The desktop project targets Windows. On non-Windows build hosts, the project enables Windows targeting so the solution can still be compiled if the Windows desktop targeting pack can be restored.

## Start a project

```powershell
dotnet run --project src/FreeIssue.Cli -- init --project C:\work\my-project
dotnet run --project src/FreeIssue.Cli -- --project C:\work\my-project new --title "Document the API" --body-file issue.md
dotnet run --project src/FreeIssue.Cli -- --project C:\work\my-project list
dotnet run --project src/FreeIssue.Cli -- --project C:\work\my-project next --json
```

The `new` command opens the configured editor for missing body input in an interactive terminal. Redirected/scripted use requires `--title` and `--body-file`; use `--body-file -` to read the Markdown body from stdin. Issue commands discover the project by walking upward from the current directory, or accept `--project DIR`.

Run `free-issue help` (or `dotnet run --project src/FreeIssue.Cli -- help`) for the command reference. JSON read commands write only JSON to stdout. Malformed issue files are reported as diagnostics and do not hide valid issues. Exit codes are 0 for success, 1 for not found, 2 for invalid input/project or conflicts, and 3 for a detected concurrent edit.

## Windows desktop

Launch `src/FreeIssue.Desktop` from Visual Studio or publish it for Windows. It opens the project containing the current directory or lets you select `.free-issue/config.json`. The list and detail editor share the same application service and Markdown format as the CLI. External file changes trigger a refresh, and the window also refreshes on activation. Unsaved edits require an explicit save/discard decision.

## Format and agent use

See [the file format and storage contract](docs/format.md) for frontmatter, validation, relationships, and concurrency behavior. See [agent usage](docs/agent-usage.md) for safe scripted/agent workflows and [release instructions](docs/release.md) for offline Windows packaging. A starter body is in [docs/starter-issue.md](docs/starter-issue.md).

Back up or move a project by copying the `.free-issue` directory. The Markdown files are canonical; no search cache or database needs to be copied. Keep the configuration and issue files together when moving between directories.
