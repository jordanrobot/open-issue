param(
    [Parameter(Mandatory)]
    [int]$AppPid,
    [Parameter(Mandatory)]
    [string]$TestProjectRoot
)

$ErrorActionPreference = 'Stop'
$TestProjectRoot = (Resolve-Path -LiteralPath $TestProjectRoot).Path
$app = Get-Process -Id $AppPid
if (!$app.MainWindowTitle.Contains($TestProjectRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The app must be open on the specified disposable test project.'
}

function Invoke-Ui {
    & winapp ui @args -a $AppPid
    if ($LASTEXITCODE -ne 0) {
        throw "UI command failed: $args"
    }
}

function Test-EntryFocus {
    param([string]$Id, [string]$Text)

    Invoke-Ui set-value $Id ''
    Invoke-Ui focus $Id
    $expected = ''
    foreach ($character in $Text.ToCharArray()) {
        # Do not refocus between characters: each pause lets autosave finish.
        Invoke-Ui send-keys "$character" --verbatim --via send-input
        $expected += $character
        Start-Sleep -Milliseconds 300
        Invoke-Ui wait-for $Id -p HasKeyboardFocus --value True -t 3000
        Invoke-Ui wait-for $Id --value $expected -t 3000
    }

    Write-Host "PASS $Id retains focus across autosaves"
}

function Get-ListRowSelectors {
    $tree = (Invoke-Ui inspect IssueList --json -d 4 | Out-String) | ConvertFrom-Json
    $lists = @($tree.windows | ForEach-Object { $_.elements } |
        Where-Object { $_.automationId -eq 'IssueList' })
    if ($lists.Count -ne 1) {
        throw "Expected one issue list, found $($lists.Count)."
    }
    $rows = @($lists[0].children | Where-Object { $_.type -eq 'ListItem' })
    if ($rows.Count -eq 0 -or @($rows | Where-Object { !$_.selector }).Count -ne 0) {
        throw 'Expected visible issue rows with nonempty UIA selectors.'
    }
    return @($rows | Select-Object -ExpandProperty selector)
}

function Assert-ListRowsUnchanged {
    param([string[]]$Expected)

    $actual = @(Get-ListRowSelectors)
    if ([string]::Join('|', $Expected) -ne [string]::Join('|', $actual)) {
        throw 'List row containers were recreated or reordered unexpectedly.'
    }
}

$issuesPath = Join-Path $TestProjectRoot '.issues\open'
$existingFiles = @(Get-ChildItem -LiteralPath $issuesPath -Filter '*.md' | Select-Object -ExpandProperty FullName)
Invoke-Ui focus NewIssueButton
Invoke-Ui invoke NewIssueButton
Invoke-Ui wait-for IssueTitle -p IsEnabled --value True -t 5000
Invoke-Ui wait-for NewIssueButton -p HasKeyboardFocus --value True -t 3000
Write-Host 'PASS creating an issue does not move focus'

$newFiles = @(Get-ChildItem -LiteralPath $issuesPath -Filter '*.md' |
    Where-Object { $_.FullName -notin $existingFiles })
if ($newFiles.Count -ne 1) {
    throw "Expected one new test issue, found $($newFiles.Count)."
}

$rowSelectors = @(Get-ListRowSelectors)
Test-EntryFocus IssueTitle 'Focus regression'
Assert-ListRowsUnchanged $rowSelectors
Write-Host 'PASS title edits retain native list row containers'
Test-EntryFocus IssueTags 'focus'
Test-EntryFocus IssueBody 'Body'
Assert-ListRowsUnchanged $rowSelectors
Write-Host 'PASS tags and body edits retain native list row containers'

Invoke-Ui send-keys ' continues while saving' --verbatim --via send-input
Invoke-Ui wait-for IssueBody --value 'Body continues while saving' -t 3000
Start-Sleep -Milliseconds 700
Invoke-Ui wait-for IssueBody -p HasKeyboardFocus --value True -t 3000
Write-Host 'PASS rapid typing retains text and focus'

Test-EntryFocus SearchIssues 'Focus'
$filteredRowSelectors = @(Get-ListRowSelectors)
Invoke-Ui set-value SearchIssues 'Focu'
Start-Sleep -Milliseconds 700
Assert-ListRowsUnchanged $filteredRowSelectors
Write-Host 'PASS matching filter changes retain native list row containers'
Invoke-Ui focus IssueBody
Invoke-Ui send-keys enter --via send-input
Invoke-Ui send-keys tab --via send-input
Invoke-Ui send-keys 'Indented' --verbatim --via send-input
Start-Sleep -Milliseconds 700
Invoke-Ui wait-for IssueBody -p HasKeyboardFocus --value True -t 3000
Invoke-Ui wait-for IssueBody --value "Body continues while saving`r`tIndented" -t 3000
Write-Host 'PASS multiline and tab entry retain focus'

Invoke-Ui focus RefreshIssuesButton
Invoke-Ui invoke RefreshIssuesButton
Invoke-Ui wait-for RefreshIssuesButton -p HasKeyboardFocus --value True -t 3000
Invoke-Ui wait-for IssueTitle --value 'Focus regression' -t 3000
Assert-ListRowsUnchanged $filteredRowSelectors
Write-Host 'PASS explicit refresh retains focus and editor text'
Write-Host 'PASS explicit refresh retains native list row containers'

$deadline = [DateTime]::UtcNow.AddSeconds(5)
do {
    $saved = (Get-Content -LiteralPath $newFiles[0].FullName -Raw).Replace("`r`n", "`n").Replace("`r", "`n")
    $persisted = $saved.Contains('Focus regression') -and
        $saved.Contains('focus') -and
        $saved.Contains("Body continues while saving`n`tIndented")
    if (!$persisted) {
        Start-Sleep -Milliseconds 100
    }
} while (!$persisted -and [DateTime]::UtcNow -lt $deadline)
if (!$persisted) {
    throw 'The complete title, tags, and multiline body were not persisted.'
}
Write-Host 'PASS complete edits persist to the issue file'
