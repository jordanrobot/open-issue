$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$cliProjectPath = Join-Path $repositoryRoot 'src\OpenIssue.Cli\OpenIssue.Cli.csproj'
$packageDirectory = Join-Path $repositoryRoot 'artifacts\packages'
$toolVersion = '1.0.0'
$packagePath = Join-Path $packageDirectory "OpenIssue.Tool.$toolVersion.nupkg"
$nugetConfigPath = Join-Path $PSScriptRoot 'NuGet.Config'

$buildScriptPath = Join-Path $PSScriptRoot 'build.ps1'
& $buildScriptPath

& dotnet pack $cliProjectPath --configuration Release --no-restore --output $packageDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet pack failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "dotnet pack succeeded but did not produce the expected package '$packagePath'."
}

$toolListJson = & dotnet tool list --global --format json
if ($LASTEXITCODE -ne 0) {
    throw "dotnet tool list failed with exit code $LASTEXITCODE."
}

$toolList = ($toolListJson -join [Environment]::NewLine) | ConvertFrom-Json
if ($toolList.data.packageId -contains 'OpenIssue.Tool') {
    Write-Host 'Replacing the existing global OpenIssue.Tool installation.'
    & dotnet tool uninstall --global OpenIssue.Tool
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool uninstall failed with exit code $LASTEXITCODE."
    }
}

& dotnet tool install OpenIssue.Tool --global --version $toolVersion --configfile $nugetConfigPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet tool install failed with exit code $LASTEXITCODE."
}

$userProfileDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
$dotnetCliHome = if ([string]::IsNullOrWhiteSpace($env:DOTNET_CLI_HOME)) { $userProfileDirectory } else { $env:DOTNET_CLI_HOME }
$globalToolsDirectory = Join-Path $dotnetCliHome '.dotnet\tools'
if (Test-Path -LiteralPath $globalToolsDirectory -PathType Container) {
    $env:PATH = "$globalToolsDirectory$([IO.Path]::PathSeparator)$env:PATH"
}

if (-not (Get-Command issue -ErrorAction SilentlyContinue)) {
    throw "The installed 'issue' command could not be found. Add the global .NET tools directory to PATH, then run 'issue skills sync'."
}

& issue skills sync
if ($LASTEXITCODE -ne 0) {
    throw "issue skills sync failed with exit code $LASTEXITCODE."
}
