<#
.SYNOPSIS
  Kept for compatibility: unattended client install (or reinstall over an existing one).
  Use Setup.ps1 for update / modify / repair / uninstall.

.EXAMPLE
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\Install-Client.ps1 -Key "your-long-shared-secret" -SourceDir .\publish-client
#>
param(
    [Parameter(Mandatory)][string]$Key,
    [Parameter(Mandatory)][string]$SourceDir,
    [int]$TcpPort = 5050,
    [int]$UdpPort = 5051,
    [string[]]$AllowedRoots = @("C:\Users")
)
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "ClassroomSetup.psm1") -Force
if (-not (Test-CmAdmin)) { throw "Run this script from an elevated (Administrator) PowerShell." }
if ((Get-CmClientStatus).Installed) {
    Set-CmClientConfig -Key $Key -TcpPort $TcpPort -UdpPort $UdpPort -AllowedRoots $AllowedRoots
    Repair-CmClient -SourceDir $SourceDir
} else {
    Install-CmClient -SourceDir $SourceDir -Key $Key -TcpPort $TcpPort -UdpPort $UdpPort -AllowedRoots $AllowedRoots
}
