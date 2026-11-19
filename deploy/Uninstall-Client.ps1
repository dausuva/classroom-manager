<#
.SYNOPSIS
  Kept for compatibility: removes the Classroom Manager client from this PC. Run as Administrator.
  Same as: Setup.ps1 -Component Client -Action Uninstall -Quiet  (add -KeepData to keep settings).
#>
param([switch]$KeepData)
& (Join-Path $PSScriptRoot "Setup.ps1") -Component Client -Action Uninstall -Quiet -KeepData:$KeepData
exit $LASTEXITCODE
