<#
.SYNOPSIS
  Writes SHA-256 checksums for every file in a folder (e.g. publish-client
  or publish-host), so anyone who receives it can verify nothing was
  altered on the way.

.DESCRIPTION
  Produces CHECKSUMS.sha256 in the target folder, one line per file:
      <sha256 hex>  <relative/path>
  This is the same format the `sha256sum` tool uses, so besides
  Test-Checksums.ps1 (Windows/PowerShell) it can also be checked with
  `sha256sum -c CHECKSUMS.sha256` from WSL, Linux or macOS.

  Publish-Client.ps1 and Publish-Host.ps1 call this automatically after a
  successful publish; run it by hand if you produced a folder some other
  way (e.g. Visual Studio's Publish) and want a checksum file for it.

.EXAMPLE
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\New-Checksums.ps1 -Path .\publish-client
#>
param(
    [Parameter(Mandatory)][string]$Path,
    [string]$OutFile = "CHECKSUMS.sha256"
)

$ErrorActionPreference = "Stop"

$Path = (Resolve-Path $Path).Path
$outPath = Join-Path $Path $OutFile

$files = Get-ChildItem -Path $Path -Recurse -File | Where-Object { $_.Name -ne $OutFile }
if (-not $files) { throw "No files found under $Path" }

$lines = foreach ($f in $files) {
    $hash = (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $rel = $f.FullName.Substring($Path.Length + 1) -replace '\\', '/'
    "$hash  $rel"
}

$lines | Sort-Object | Set-Content -Path $outPath -Encoding ASCII

Write-Host "Wrote $($files.Count) checksum(s) to $outPath" -ForegroundColor Green
