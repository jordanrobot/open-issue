$ErrorActionPreference = 'Stop'

$toolListJson = & dotnet tool list --global --format json
if ($LASTEXITCODE -ne 0) {
    throw "dotnet tool list failed with exit code $LASTEXITCODE."
}

$toolList = ($toolListJson -join [Environment]::NewLine) | ConvertFrom-Json
if ($toolList.data.packageId -notcontains 'OpenIssue.Tool') {
    Write-Host 'OpenIssue.Tool is not installed globally.'
    return
}

& dotnet tool uninstall --global OpenIssue.Tool
if ($LASTEXITCODE -ne 0) {
    throw "dotnet tool uninstall failed with exit code $LASTEXITCODE."
}
