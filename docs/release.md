# Windows release and future platforms

Release artifacts are self-contained Windows x64 executables. Build them on a connected build host, then copy the publish directories to the target machine; running the release does not require an account, Git, .NET installation, or network access.

```powershell
dotnet restore OpenIssue.slnx --runtime win-x64 --locked-mode
dotnet pack src/OpenIssue.Cli/OpenIssue.Cli.csproj -c Release --no-restore -o artifacts/packages
dotnet publish src/OpenIssue.Cli/OpenIssue.Cli.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -o artifacts/win-x64/cli
dotnet publish src/OpenIssue.Desktop/OpenIssue.Desktop.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x64/desktop
```

Keep the SDK pin, NuGet package lock files, and publish commands with release records so restore and packaging use the reviewed versions. The desktop and CLI share the same core and on-disk Markdown format. Verify both executables on a clean Windows machine with networking disabled after publishing.

The core and CLI target `net10.0` and do not contain WPF dependencies. WPF is isolated in the Windows-only desktop project. This keeps a future UI replacement possible, but does not represent or promise a non-Windows desktop application. Evaluate any future frontend against the stable core/service and format before choosing a toolkit.

Runtime packages System.CommandLine and YamlDotNet are pinned and lock-file-resolved. System.CommandLine parses CLI commands/options; YamlDotNet reads and preserves issue metadata.
