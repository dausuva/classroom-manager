<#
.SYNOPSIS
  Kept for compatibility: installs the Client or Host. Use Setup.ps1 for every action
  (install, update, modify, repair, uninstall).

.EXAMPLE
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\Install.ps1 -Component Client -Key "a-long-random-classroom-secret" -SourceDir .\publish-client
#>
param(
    [ValidateSet("Client", "Host")][string]$Component,
    [string]$Key,
    [string]$SourceDir,
    [int]$TcpPort,
    [int]$UdpPort,
    [string[]]$AllowedRoots,
    [string]$HostInstallDir
)
$fwd = @{ Action = "Install" }
if ($Component)      { $fwd.Component = $Component }
if ($Key)            { $fwd.Key = $Key }
if ($SourceDir)      { $fwd.SourceDir = $SourceDir }
if ($TcpPort)        { $fwd.TcpPort = $TcpPort }
if ($UdpPort)        { $fwd.UdpPort = $UdpPort }
if ($AllowedRoots)   { $fwd.AllowedRoots = $AllowedRoots }
if ($HostInstallDir) { $fwd.InstallDir = $HostInstallDir }
& (Join-Path $PSScriptRoot "Setup.ps1") @fwd
exit $LASTEXITCODE
