# Open Issue

Open Issue is a local-first issue and specification manager. Issues are UTF-8 Markdown files stored under `.issues/`; no Git repository, account, network service, or database is required.

## Install

Requires the .NET 10 runtime. Install the CLI as a .NET global tool:

```sh
dotnet tool install --global OpenIssue.Tool
```

The installed command is `issue`. Update it with `dotnet tool update --global OpenIssue.Tool`.

To install the package built from this checkout:

```powershell
.\scripts\build-install.ps1
```

This packs the CLI to `artifacts/packages` and installs it globally as `issue`. To pack and install manually:

```powershell
dotnet pack src/OpenIssue.Cli/OpenIssue.Cli.csproj -c Release -o artifacts/packages
dotnet tool install --global --configfile scripts/NuGet.Config OpenIssue.Tool --version 1.0.0
```

NuGet rejects `--add-source` when package source mapping is enabled. The supplied config maps `OpenIssue.Tool` to the local package feed, so you do not need to change your user-wide NuGet configuration.

## Initialize and use

Run `issue init` from the directory that should own its issue project. You can optionally provide a directory: `issue init /path/to/project`.

```sh
cd /path/to/project
issue init
issue new --title "Document the API" --body-file issue.md
issue list
issue show 1 --json
issue next --json
```

The command discovers the nearest initialized `.issues/config.json` walking upward from the current directory, so it works anywhere inside that project. It operates only on that project’s `.issues` data. A nested initialized project takes precedence inside its own directory; a command cannot target another project with a path override.

Interactive `new` opens the configured editor when no body is supplied. In redirected/scripted use, provide `--title` and `--body-file`; use `--body-file -` to read Markdown from stdin. `issue --help` and `issue <command> --help` show command options.

JSON read commands write machine-readable output to stdout; errors have `error.code` and `error.message`. Invalid issue files are reported as diagnostics without hiding other valid issues. Exit codes: 0 success, 1 not found, 2 invalid input/project, 3 revision conflict.

## Build and verify

```sh
dotnet build OpenIssue.slnx
dotnet run --project tests/OpenIssue.Tests
```

PowerShell helpers are also available from the repository root:

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\install.ps1
.\scripts\uninstall.ps1
.\scripts\build-install.ps1
```

`install.ps1` installs or replaces the global tool using the locally packed package; `uninstall.ps1` removes it.

The CLI and core target portable .NET 10. The Windows desktop app uses WPF and is Windows-only. Build or publish it with `src/OpenIssue.Desktop/OpenIssue.Desktop.csproj`; the desktop UI shares the core and storage contract with the CLI.

## Format and agent use

See [the file format and storage contract](docs/format.md), [safe CLI/agent workflows](docs/agent-usage.md), [Windows release instructions](docs/release.md), and the [starter issue template](docs/starter-issue.md).

Back up or move a project by copying its `.issues` directory. Numeric issue IDs are allocated within that project, immutable, and used for references.
