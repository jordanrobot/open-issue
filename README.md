# Open Issue

Open Issue is a local-first issue and specification manager. Issues are UTF-8 Markdown files stored under `.issues/`; no Git repository, account, network service, or database is required.

## Install

Requires the .NET 10 runtime. Install the CLI as a .NET global tool:

```sh
dotnet tool install --global OpenIssue.Tool
```

The installed command is `issue`. Update it with `dotnet tool update --global OpenIssue.Tool`. On Windows, `issue ui` launches the bundled WinUI 3 app with `winapp run`; the UI requires the Windows App SDK CLI (`winapp`), the .NET 10 Desktop Runtime, and Windows App Runtime.

To install the package built from this checkout:

```powershell
.\scripts\build-install.ps1
```

This packs the CLI and Windows desktop application to `artifacts/packages`, then installs the tool globally as `issue`. Pack the tool on Windows. To pack and install manually:

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
issue ui
issue new --title "Document the API" --body-file issue.md
issue list
issue show 1 --json
issue delete 12
issue next --json
```

The command discovers the nearest initialized `.issues/config.json` walking upward from the current directory, so it works anywhere inside that project. It operates only on that project’s `.issues` data. A nested initialized project takes precedence inside its own directory; a command cannot target another project with a path override.

Interactive `new` opens the configured editor when no body is supplied. In redirected/scripted use, provide `--title` and `--body-file`; use `--body-file -` to read Markdown from stdin. `issue --help` and `issue <command> --help` show command options.

JSON read commands write machine-readable output to stdout; errors have `error.code` and `error.message`. Invalid issue files are reported as diagnostics without hiding other valid issues. Exit codes: 0 success, 1 not found, 2 invalid input/project, 3 revision conflict.

## Build and verify

Build outputs and intermediates are kept under `artifacts/` (including per-project `bin/` and `obj/` trees and NuGet packages).

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

The CLI and core target portable .NET 10. The Windows desktop app uses WinUI 3 and the Windows App SDK. Build or package it with `src/OpenIssue.Desktop/OpenIssue.Desktop.csproj`; the desktop UI shares the core and storage contract with the CLI. The desktop UI defaults to System and offers Light, Dark, and Dracula, with the selection saved per user.

The desktop editor autosaves without disabling entry fields or moving keyboard focus. Autosave updates the issue's title in its existing list row without recreating the row. Search and status/priority filters are reapplied when you change a filter or refresh the list, not after each keystroke. Filter and refresh results retain surviving rows and only insert, remove, or move rows whose membership or order changes, avoiding a full-list redraw. The issue pane is a tree: drop between siblings to reorder, onto a different issue to reparent, or into open space to move an issue to the top level. A row's context menu offers Move to top level, Duplicate issue, Edit in external editor, Reveal in File Explorer, and Delete issue. The order is saved with the project. Creating an issue also leaves focus on the control you used.

For the native focus regression checks, launch the built desktop app on a disposable initialized project, then run `.\scripts\test-desktop-focus.ps1 -AppPid <desktop-process-id> -TestProjectRoot <disposable-project-path>`. This requires `winapp ui` and an unlocked interactive desktop. The script creates and edits a test issue; do not point it at your working issue store.

## Format and agent use

See [the file format and storage contract](docs/format.md), [safe CLI/agent workflows](docs/agent-usage.md), [Windows release instructions](docs/release.md), and [issue body guidance](docs/starter-issue.md).

Back up or move a project by copying its `.issues` directory. Numeric issue IDs are allocated within that project, immutable, and used for references.
