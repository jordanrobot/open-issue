$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testProjectPath = Join-Path $repositoryRoot 'tests\OpenIssue.Tests\OpenIssue.Tests.csproj'

& dotnet restore $testProjectPath --locked-mode
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

& dotnet run --project $testProjectPath --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "dotnet run for OpenIssue.Tests failed with exit code $LASTEXITCODE."
}
