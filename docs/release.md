# Windows release and future platforms

The standalone CLI release is a self-contained Windows x64 executable. The desktop UI is distributed as a signed WinUI 3 MSIX package. Build release artifacts on a connected Windows host.

```powershell
dotnet restore OpenIssue.slnx --locked-mode
dotnet pack src/OpenIssue.Cli/OpenIssue.Cli.csproj -c Release --no-restore -o artifacts/packages
dotnet publish src/OpenIssue.Cli/OpenIssue.Cli.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -o artifacts/win-x64/cli
winapp package src/OpenIssue.Desktop/OpenIssue.Desktop.csproj -c Release --arch x64 --self-contained --cert "$env:OPEN_ISSUE_SIGNING_CERT" --cert-password "$env:OPEN_ISSUE_SIGNING_CERT_PASSWORD" --output artifacts/win-x64/desktop/OpenIssue.Desktop.msix
```

Packing the CLI also builds the WinUI desktop application and includes its files in the tool package. Installed tools expose it with `issue ui`; the command uses the Windows App SDK CLI to register, launch, and unregister the packaged app, and requires `winapp`, the .NET 10 Desktop Runtime, and Windows App Runtime. Launch development builds with `winapp run`, not by starting the executable directly. The release MSIX must be signed with the release certificate whose publisher matches `Package.appxmanifest`.

Install the locally packed tool with `dotnet tool install --global --configfile scripts/NuGet.Config OpenIssue.Tool --version 1.0.0`. The config maps `OpenIssue.Tool` to the local `artifacts/packages` feed; this avoids `--add-source`, which NuGet does not allow when package source mapping is enabled. Once published to a NuGet feed, install it with `dotnet tool install --global OpenIssue.Tool`.

Keep the SDK pin, NuGet package lock files, and publish commands with release records so restore and packaging use the reviewed versions. The desktop and CLI share the same core and on-disk Markdown format. Verify the CLI executable and signed MSIX on a clean Windows machine with networking disabled after publishing.

The core and CLI target `net10.0` and do not reference WinUI APIs. WinUI 3 and the Windows App SDK are isolated in the Windows-only desktop project. The desktop UI shares the stable core/service and format with the CLI.

Runtime packages System.CommandLine and YamlDotNet are pinned and lock-file-resolved. System.CommandLine parses CLI commands/options; YamlDotNet reads and preserves issue metadata.
