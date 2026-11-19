<#
.SYNOPSIS
  Sets every network that Windows marks "Public" on this PC to "Private", so Classroom Manager's
  firewall rules (Domain/Private only) apply. Use only on a trusted school network.
#>
$ErrorActionPreference = "Stop"
Import-Module (Join-Path (Split-Path $PSScriptRoot -Parent) "ClassroomSetup.psm1") -Force
if (-not (Test-CmAdmin)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @("-NoProfile", "-NoExit", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"")
    exit
}
Set-CmNetworkPrivate
