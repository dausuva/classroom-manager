<#
.SYNOPSIS
  Installs the .NET 10 Desktop Runtime silently with winget (x64 on 64-bit Windows, x86 on 32-bit Windows), for
  student PCs that will run the shared-runtime build of Classroom Manager.

.DESCRIPTION
  Written to be pushed from the teacher app: Run command... > load this file (PowerShell, elevated),
  with the timeout raised to about 900 seconds. It also works from an elevated PowerShell prompt or
  a deployment tool. It needs internet access on the PC it runs on, because winget downloads the
  runtime from Microsoft there.

  The script does nothing when the runtime is already installed, so it is safe to run on a whole
  room repeatedly. It finishes with one line starting with OK or FAILED and exits 0 or 1.

  Settings are plain variables below, not parameters, because the teacher app sends the script as
  text and a parameter block cannot be used that way.

  Without internet access on the student PCs, install the runtime from the kit instead: setup does
  that by itself from the kit's runtime folder (see README.md, "Two builds").
#>

# ---- Settings ---------------------------------------------------------------------
$MajorVersion = 10
$PackageId    = "Microsoft.DotNet.DesktopRuntime.$MajorVersion"
$Reinstall    = $false   # $true runs winget even when the runtime is already present (repair)
# "auto" follows the Windows of the PC it runs on; set "x64" or "x86" to force one.
$Architecture = "auto"
# ------------------------------------------------------------------------------------

if ($Architecture -eq "auto") {
    # PROCESSOR_ARCHITEW6432 is set only for a 32-bit PowerShell on 64-bit Windows, and then names the real architecture.
    $real = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    $Architecture = if ($real -eq "x86") { "x86" } else { "x64" }
}

$ErrorActionPreference = "Stop"
# Progress records would otherwise come back to the teacher app as unreadable XML.
$ProgressPreference = "SilentlyContinue"

function Get-DesktopRuntimeVersions {
    $shared = Join-Path $env:ProgramFiles "dotnet\shared\Microsoft.WindowsDesktop.App"
    if (-not (Test-Path $shared)) { return @() }
    return @(Get-ChildItem $shared -Directory | ForEach-Object {
        # Preview builds are named like 10.0.0-rc.1; only released versions count.
        if ($_.Name -match '^\d+\.\d+\.\d+$') { [version]$_.Name }
    } | Where-Object { $_.Major -eq $MajorVersion } | Sort-Object)
}

function Find-Winget {
    $command = Get-Command winget.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    # The SYSTEM account has no "winget" alias on its PATH; the program itself is in WindowsApps.
    $apps = Join-Path $env:ProgramFiles "WindowsApps"
    $found = Get-ChildItem $apps -Directory -Filter "Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe" -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.Name -replace '^Microsoft\.DesktopAppInstaller_([\d.]+)_.*$', '$1') } -Descending |
        ForEach-Object { Join-Path $_.FullName "winget.exe" } | Where-Object { Test-Path $_ } | Select-Object -First 1
    return $found
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Administrator rights are needed. In the teacher app, run this elevated (as SYSTEM), not as the student."
    }

    $before = Get-DesktopRuntimeVersions
    if ($before -and -not $Reinstall) {
        Write-Output "OK $($env:COMPUTERNAME): .NET $MajorVersion Desktop Runtime $($before[-1]) is already installed."
        exit 0
    }

    $winget = Find-Winget
    if (-not $winget) {
        throw "winget was not found on this PC (it comes with App Installer from the Microsoft Store). Install the runtime from the setup kit's runtime folder instead."
    }

    # --scope machine: for every user. --source winget: skip the Store source, which needs a signed-in user.
    $arguments = @(
        "install", "--id", $PackageId, "--exact", "--source", "winget", "--scope", "machine", "--architecture", $Architecture,
        "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
    )
    if ($Reinstall) { $arguments += "--force" }
    $output = & $winget @arguments 2>&1 | Out-String
    $code = $LASTEXITCODE

    $after = Get-DesktopRuntimeVersions
    if (-not $after) {
        # winget draws progress bars with characters that clutter a log; keep only its last readable lines.
        $tail = ($output -split "`r?`n" | Where-Object { $_ -match '[A-Za-z]{3}' } | Select-Object -Last 4) -join " | "
        throw "winget finished with exit code $code but the runtime is still not installed. $tail"
    }
    Write-Output "OK $($env:COMPUTERNAME): .NET $MajorVersion Desktop Runtime $($after[-1]) installed (winget exit code $code)."
    exit 0
}
catch {
    Write-Output "FAILED $($env:COMPUTERNAME): $($_.Exception.Message)"
    exit 1
}
