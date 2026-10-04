$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$cliProjectPath = Join-Path $repositoryRoot 'src\OpenIssue.Cli\OpenIssue.Cli.csproj'
$packageDirectory = Join-Path $repositoryRoot 'artifacts\packages'

& (Join-Path $PSScriptRoot 'build.ps1')

& dotnet pack $cliProjectPath --configuration Release --no-restore --output $packageDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet pack failed with exit code $LASTEXITCODE."
}

& (Join-Path $PSScriptRoot 'install.ps1')
