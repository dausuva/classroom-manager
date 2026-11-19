<#
.SYNOPSIS
Mutation test of the protocol library (src/Shared): Stryker.NET makes small deliberate breaks in the code and checks that the tests notice each one. Prints the score and can write docs/QUALITY.md.

.DESCRIPTION
Line coverage says which code the tests run; the mutation score says whether they would fail if that code were wrong. The library under test is src/Shared (the secure channel, protocol, keys, TLS profile), the security-critical part, configured in
tests/ClassroomManager.Shared.Tests/stryker-config.json. A run takes tens of minutes, so CI runs it on a schedule (.github/workflows/mutation.yml) and not on every change.
  -Report      a mutation-report.json of an earlier run: only read it, run nothing.
  -Floor       fail (exit 1) when the score is below this percentage (default: tests/mutation-floor.json).
  -WriteQuality  writes docs/QUALITY.md with the score, the numbers and the date.
Score = killed (including timeouts) / (killed + timeouts + survived + no coverage). Mutants that did not compile or were ignored are not counted. Runs under PowerShell 7 and Windows PowerShell 5.1; needs `dotnet tool restore`.
#>
[CmdletBinding()]
param(
    [string]$Report,
    [Nullable[double]]$Floor,
    [switch]$WriteQuality
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path $here -Parent
$testProject = Join-Path $root 'tests\ClassroomManager.Shared.Tests'

if (-not $Report) {
    Push-Location $testProject
    try {
        $saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try { & dotnet tool run dotnet-stryker -- --config-file stryker-config.json 2>&1 | Out-Host } finally { $ErrorActionPreference = $saved }
        if ($LASTEXITCODE -ne 0) { throw "Stryker failed with exit code $LASTEXITCODE." }
    } finally { Pop-Location }
    $latest = Get-ChildItem -LiteralPath (Join-Path $testProject 'StrykerOutput') -Directory | Sort-Object Name | Select-Object -Last 1
    $Report = Join-Path $latest.FullName 'reports\mutation-report.json'
}
if (-not (Test-Path -LiteralPath $Report)) { throw "No mutation report at $Report." }

$data = Get-Content -LiteralPath $Report -Raw | ConvertFrom-Json
$counts = @{}
foreach ($file in $data.files.PSObject.Properties) {
    foreach ($mutant in $file.Value.mutants) { $counts[$mutant.status] = 1 + [int]$counts[$mutant.status] }
}
$perFile = foreach ($file in $data.files.PSObject.Properties) {
    $fileCounts = @{}
    foreach ($mutant in $file.Value.mutants) { $fileCounts[$mutant.status] = 1 + [int]$fileCounts[$mutant.status] }
    $fileKilled = [int]$fileCounts['Killed'] + [int]$fileCounts['Timeout']
    $fileValid = $fileKilled + [int]$fileCounts['Survived'] + [int]$fileCounts['NoCoverage']
    if ($fileValid -ge 20) { [pscustomobject]@{ File = [IO.Path]::GetFileName($file.Name); Score = [Math]::Round(100.0 * $fileKilled / $fileValid); Mutants = $fileValid } }
}
function Count([string]$name) { if ($counts.ContainsKey($name)) { [int]$counts[$name] } else { 0 } }
$killed = (Count 'Killed') + (Count 'Timeout'); $survived = Count 'Survived'; $noCoverage = Count 'NoCoverage'
$valid = $killed + $survived + $noCoverage
if ($valid -eq 0) { throw 'The report holds no testable mutants.' }
$score = [Math]::Round(100.0 * $killed / $valid, 1)
Write-Host ("Mutation score {0}%: {1} killed, {2} survived, {3} without test coverage ({4} mutants counted; {5} did not compile or were ignored)." -f $score, $killed, $survived, $noCoverage, $valid, ((Count 'CompileError') + (Count 'Ignored')))

if ($env:GITHUB_STEP_SUMMARY) {
    "### Mutation score of src/Shared: $score %`n`nKilled $killed, survived $survived, no coverage $noCoverage." | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
}

if ($WriteQuality) {
    $version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $text = @"
# Quality measurements

Measured results, not promises. Each number says how it was measured and when. Re-run the commands to check them.

## Mutation score of the protocol library: $score %

The tests are good only if they fail when the code is wrong. [Stryker.NET](https://stryker-mutator.io/) makes small deliberate breaks ("mutants": a flipped comparison, a removed check, a changed constant) in ``src/Shared`` - the secure channel, the protocol, keys and the TLS profile - and runs the tests against each one. A mutant that makes a test fail is *killed*; one that no test notices *survived*.

| | Count |
|---|---|
| Killed (a test failed) | $killed |
| Survived (no test noticed) | $survived |
| No test runs this code | $noCoverage |
| **Score** | **$score %** |

## Where the tests are strongest and weakest

Files with at least 20 mutants: the six lowest and the four highest scores. The lowest are the next places to add tests.

| File | Score | Mutants |
|---|---|---|
$((@($perFile | Sort-Object Score | Select-Object -First 6) + @($perFile | Sort-Object Score -Descending | Select-Object -First 4) | ForEach-Object { "| ``$($_.File)`` | $($_.Score) % | $($_.Mutants) |" }) -join "`n")

Measured on version $version, $([DateTime]::UtcNow.ToString('yyyy-MM-dd')). Every survivor is a place where a test could be sharper; the list is in the HTML report of the run.

``````powershell
dotnet tool restore
powershell -File scripts/Test-Mutation.ps1 -WriteQuality     # tens of minutes
``````

CI repeats the run weekly (``.github/workflows/mutation.yml``) and fails when the score falls below the floor in ``tests/mutation-floor.json``.

## Other checks that run on every change

- More than 3,400 automated tests across the shared library, the Host and the Client.
- Line coverage per program with a floor that may not fall (``tests/coverage-floor.json``).
- The .NET security analyzers and every compiler warning are build errors.
- CodeQL scans the source for security problems; packages are restored from committed lock files.
"@
    [IO.File]::WriteAllText((Join-Path $root 'docs\QUALITY.md'), $text.Replace("`r`n", "`n"), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host 'docs\QUALITY.md written.'
}

if ($null -eq $Floor) {
    $floorFile = Join-Path $root 'tests\mutation-floor.json'
    if (Test-Path -LiteralPath $floorFile) { $Floor = [double](Get-Content -LiteralPath $floorFile -Raw | ConvertFrom-Json).sharedLibrary }
}
if ($null -ne $Floor -and $score -lt $Floor) { Write-Host "Below the floor of $Floor %." -ForegroundColor Red; exit 1 }
