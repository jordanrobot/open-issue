$ErrorActionPreference = 'Stop'

& dotnet tool update --global OpenIssue.Tool
if ($LASTEXITCODE -ne 0) {
    throw "dotnet tool update failed with exit code $LASTEXITCODE."
}

$userProfileDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
$dotnetCliHome = if ([string]::IsNullOrWhiteSpace($env:DOTNET_CLI_HOME)) { $userProfileDirectory } else { $env:DOTNET_CLI_HOME }
$globalToolsDirectory = Join-Path $dotnetCliHome '.dotnet\tools'
if (Test-Path -LiteralPath $globalToolsDirectory -PathType Container) {
    $env:PATH = "$globalToolsDirectory$([IO.Path]::PathSeparator)$env:PATH"
}

if (-not (Get-Command issue -ErrorAction SilentlyContinue)) {
    throw "The updated 'issue' command could not be found. Add the global .NET tools directory to PATH, then run 'issue skills sync'."
}

& issue skills sync
if ($LASTEXITCODE -ne 0) {
    throw "issue skills sync failed with exit code $LASTEXITCODE."
}
