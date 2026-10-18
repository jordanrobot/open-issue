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

$localApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$skillStatePath = Join-Path $localApplicationData 'OpenIssue\agent-skills.json'
if (Test-Path -LiteralPath $skillStatePath -PathType Leaf) {
    $userProfileDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $dotnetCliHome = if ([string]::IsNullOrWhiteSpace($env:DOTNET_CLI_HOME)) { $userProfileDirectory } else { $env:DOTNET_CLI_HOME }
    $globalToolsDirectory = Join-Path $dotnetCliHome '.dotnet\tools'
    if (Test-Path -LiteralPath $globalToolsDirectory -PathType Container) {
        $env:PATH = "$globalToolsDirectory$([IO.Path]::PathSeparator)$env:PATH"
    }

    if (-not (Get-Command issue -ErrorAction SilentlyContinue)) {
        throw "The installed 'issue' command could not be found, so its managed skills cannot be removed."
    }

    & issue skills uninstall
    if ($LASTEXITCODE -ne 0) {
        throw "issue skills uninstall failed with exit code $LASTEXITCODE."
    }
}

& dotnet tool uninstall --global OpenIssue.Tool
if ($LASTEXITCODE -ne 0) {
    throw "dotnet tool uninstall failed with exit code $LASTEXITCODE."
}
