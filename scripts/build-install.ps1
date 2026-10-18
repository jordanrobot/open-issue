$ErrorActionPreference = 'Stop'

$installScriptPath = Join-Path $PSScriptRoot 'install.ps1'
& $installScriptPath
