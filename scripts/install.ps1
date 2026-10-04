$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$toolVersion = '1.0.0'
$packagePath = Join-Path $repositoryRoot "artifacts\packages\OpenIssue.Tool.$toolVersion.nupkg"
$nugetConfigPath = Join-Path $PSScriptRoot 'NuGet.Config'

if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "Local package not found at '$packagePath'. Run .\scripts\build-install.ps1 to build and install it."
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
