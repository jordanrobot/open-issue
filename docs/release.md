# Windows release and future platforms

Release artifacts are self-contained Windows x64 executables. Build them on a connected build host, then copy the publish directories to the target machine; running the release does not require an account, Git, .NET installation, or network access.

```powershell
dotnet restore FreeIssue.slnx --runtime win-x64 --locked-mode
dotnet publish src/FreeIssue.Cli/FreeIssue.Cli.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -o artifacts/win-x64/cli
dotnet publish src/FreeIssue.Desktop/FreeIssue.Desktop.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x64/desktop
```

Keep the SDK pin, NuGet package lock files, and publish commands with release records so restore and packaging use the reviewed versions. The desktop and CLI share the same core and on-disk Markdown format. Verify both executables on a clean Windows machine with networking disabled after publishing.

The core and CLI target `net10.0` and do not contain WPF dependencies. WPF is isolated in the Windows-only desktop project. This keeps a future UI replacement possible, but does not represent or promise a non-Windows desktop application. Evaluate any future frontend against the stable core/service and format before choosing a toolkit.

The only added runtime package is YamlDotNet, pinned and lock-file-resolved; it is MIT-licensed and used to parse and preserve YAML metadata.
