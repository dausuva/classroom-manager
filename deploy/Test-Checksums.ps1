<#
.SYNOPSIS
  Verifies files in a folder against a CHECKSUMS.sha256 file, so you can
  confirm a copy of ClassroomClient/ClassroomHost you were given (or
  downloaded, or copied over the network) matches what was published -
  nothing added, removed or tampered with.

.DESCRIPTION
  Publish-Client.ps1 / Publish-Host.ps1 write CHECKSUMS.sha256 and a copy
  of this script into their output folder, so the folder is self-contained
  for verification: no need for the rest of the repo.

  Prints OK / FAILED / MISSING per file and exits with a non-zero code if
  anything doesn't match, so it can also be used as a gate in a script.

  No script on hand and just want to spot-check one file? Windows has a
  built-in equivalent with no dependencies:
      certutil -hashfile ClassroomClient.exe SHA256
  Compare its output to the matching line in CHECKSUMS.sha256.

.EXAMPLE
  powershell.exe -ExecutionPolicy Bypass -File .\Test-Checksums.ps1
  # run from inside a publish folder that already has CHECKSUMS.sha256

.EXAMPLE
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\Test-Checksums.ps1 -Path .\publish-client
#>
param(
    [string]$Path = ".",
    [string]$ChecksumFile
)

$ErrorActionPreference = "Stop"

$Path = (Resolve-Path $Path).Path
if (-not $ChecksumFile) { $ChecksumFile = Join-Path $Path "CHECKSUMS.sha256" }
if (-not (Test-Path $ChecksumFile)) { throw "Checksum file not found: $ChecksumFile" }

$ok = 0
$bad = 0
$missing = 0

foreach ($line in Get-Content $ChecksumFile) {
    if ($line -notmatch '^\s*([0-9a-fA-F]{64})\s+(.+)$') { continue }
    $expected = $Matches[1].ToLowerInvariant()
    $rel = $Matches[2] -replace '/', '\'
    $full = Join-Path $Path $rel

    if (-not (Test-Path $full)) {
        Write-Host "MISSING  $rel" -ForegroundColor Yellow
        $missing++
        continue
    }

    $actual = (Get-FileHash -Path $full -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -eq $expected) {
        Write-Host "OK       $rel" -ForegroundColor Green
        $ok++
    } else {
        Write-Host "FAILED   $rel" -ForegroundColor Red
        $bad++
    }
}

Write-Host ""
$summaryColor = "Green"
if ($bad -gt 0 -or $missing -gt 0) { $summaryColor = "Red" }
Write-Host "$ok OK, $bad failed, $missing missing." -ForegroundColor $summaryColor

if ($bad -gt 0 -or $missing -gt 0) { exit 1 }
exit 0   # explicit, so callers never see a stale exit code from an earlier program (e.g. robocopy's 1 = "copied")
