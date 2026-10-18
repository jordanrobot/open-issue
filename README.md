# Open Issue

Open Issue is a local-first issue and specification manager. Issues are UTF-8 Markdown files stored under `.issues/open/` and `.issues/closed/`; no Git repository, account, network service, or database is required.

## Install

Requires the .NET 10 runtime. Install the CLI as a .NET global tool:

```sh
dotnet tool install --global OpenIssue.Tool
```

The installed command is `issue`. On Windows, `issue ui` launches the bundled WinUI 3 app with `winapp run`; the UI requires the Windows App SDK CLI (`winapp`), the .NET 10 Desktop Runtime, and Windows App Runtime.

The tool package includes the managed agent skills. Run `issue skills sync` after a direct tool install or update; it detects and synchronizes every supported harness found in the user profile: GitHub Copilot (`~/.copilot`), Codex (`~/.codex` or `~/.agents/skills`), and Claude Code (`~/.claude`). If none is detected, the command succeeds without installing skills or prompting. Subsequent syncs update all detected harnesses and remove previously managed copies for harnesses that are no longer detected. `issue skills uninstall` removes all tracked managed copies, including those for harnesses that are no longer detected.

.NET global tools do not run install, update, or uninstall hooks. Use `.\scripts\install.ps1` or `.\scripts\build-install.ps1` to build and install this checkout, `.\scripts\update.ps1` to update the global tool and its skills together, and `.\scripts\uninstall.ps1` to remove managed skills before uninstalling the tool. If using `dotnet tool uninstall` directly, run `issue skills uninstall` first.

To install the package built from this checkout:

```powershell
.\scripts\install.ps1
```

`install.ps1` builds and packs the current CLI and Windows desktop application to `artifacts/packages` before installing the tool globally as `issue`, so it does not install a stale local package. `build-install.ps1` remains as an alias. The install and update scripts synchronize every detected harness automatically. Pack the tool on Windows. To pack and install manually:

```powershell
dotnet pack src/OpenIssue.Cli/OpenIssue.Cli.csproj -c Release -o artifacts/packages
dotnet tool install --global --configfile scripts/NuGet.Config OpenIssue.Tool --version 1.0.0
```

NuGet rejects `--add-source` when package source mapping is enabled. The supplied config maps `OpenIssue.Tool` to the local package feed, so you do not need to change your user-wide NuGet configuration.

To remove the global tool installation:

```powershell
.\scripts\uninstall.ps1
```

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

Open and closed issues live in separate directories, and changing status moves the issue file between them. Existing issues in `.issues/issues/` are not migrated automatically; move them manually into the appropriate directory.

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

`install.ps1` and `build-install.ps1` build and pack the current checkout before installing or replacing the global tool; `uninstall.ps1` removes it.

The CLI and core target portable .NET 10. The Windows desktop app uses WinUI 3 and the Windows App SDK. Build or package it with `src/OpenIssue.Desktop/OpenIssue.Desktop.csproj`; the desktop UI shares the core and storage contract with the CLI. The desktop UI defaults to System and offers Light, Dark, and Dracula, with the selection saved per user.

The desktop editor autosaves without disabling entry fields or moving keyboard focus. Autosave updates the issue's title in its existing list row without recreating the row. Search and status/priority filters are reapplied when you change a filter or refresh the list, not after each keystroke. Filter and refresh results retain surviving rows and only insert, remove, or move rows whose membership or order changes, avoiding a full-list redraw. The issue pane can show the parent/child tree, group rows under collapsible tag headings, or group each issue under its prerequisites. Issues with multiple tags or dependencies appear in each matching group; untagged and dependency-free issues remain visible in their own groups. Drag-and-drop reordering and parent/child changes remain available in the parent/child view. A row's context menu offers Move to top level, Duplicate issue, Edit in external editor, Reveal in File Explorer, and Delete issue. The order is saved with the project. Creating an issue also leaves focus on the control you used.

For the native focus regression checks, launch the built desktop app on a disposable initialized project, then run `.\scripts\test-desktop-focus.ps1 -AppPid <desktop-process-id> -TestProjectRoot <disposable-project-path>`. This requires `winapp ui` and an unlocked interactive desktop. The script creates and edits a test issue; do not point it at your working issue store.

## Format and agent use

See [the file format and storage contract](docs/format.md), [safe CLI/agent workflows](docs/agent-usage.md), [Windows release instructions](docs/release.md), and [issue body guidance](docs/starter-issue.md).

Back up or move a project by copying its `.issues` directory. Numeric issue IDs are allocated within that project, immutable, and used for references.
