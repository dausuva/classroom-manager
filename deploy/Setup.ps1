<#
.SYNOPSIS
  Classroom Manager setup: install, update, modify, repair or uninstall the Client (student PC)
  or the Host (teacher PC). Interactive menu, or fully unattended with parameters.

.DESCRIPTION
  Actions
    Install    First install. Client needs a shared key (12+ characters).
    Update     Replace the program with a newer build; settings are kept. Refuses older/same
               versions unless -Force. Rolls back automatically if the new build fails to start.
    Modify     Client: shared key, ports, allowed folders.  Host: desktop shortcut, start with Windows,
               install folder on PATH.
    Repair     Re-applies files (with -SourceDir), permissions, firewall, service, shortcuts and the
               Apps & features entry, keeping settings.
    Uninstall  Removes everything. Client: -KeepData keeps settings/logs. Host: -RemoveSettings also
               deletes each teacher's saved settings (shared key, computer list).
    Status     Shows what is installed.

  Re-launches itself elevated when needed. A classroom-kit.json next to this script (the key, ports, allowed roots
  and named access keys; scripts\deploy.ps1 -Key writes one on request) supplies the defaults so a USB install needs
  no typing. Setup only reads that file: it never saves a key into it.

  Exit code 0 = success, 1 = failure (for GPO / Intune / imaging tools).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\deploy\Setup.ps1
  # menu

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\deploy\Setup.ps1 -Component Client -Action Install -Key "long-classroom-secret" -Quiet

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\deploy\Setup.ps1 -Component Client -Action Update -SourceDir E:\ClassroomManager\publish-client -Quiet

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\deploy\Setup.ps1 -Component Host -Action Uninstall -RemoveSettings -Quiet
#>
param(
    [ValidateSet("Client", "Host")][string]$Component,
    [ValidateSet("Install", "Update", "Modify", "Repair", "Uninstall", "Status")][string]$Action,
    [string]$SourceDir,
    [string]$Key,
    [int]$TcpPort,
    [int]$UdpPort,
    [string[]]$AllowedRoots,
    # Retired: TLS is always on. Kept so that older scripts that pass it still run; "Off" is ignored with a note.
    [ValidateSet("", "On", "Off")][string]$Tls = "",
    [string]$InstallDir,
    [ValidateSet("", "On", "Off")][string]$DesktopShortcut = "",
    [ValidateSet("", "On", "Off")][string]$StartWithWindows = "",
    # Host: put the install folder on the PATH of this PC, so ClassroomCli runs from any command prompt.
    # Install adds it unless this is Off; Modify leaves it alone unless this is given.
    [ValidateSet("", "On", "Off")][string]$AddToPath = "",
    [switch]$SetNetworkPrivate,
    [switch]$KeepData,
    [switch]$RemoveSettings,
    [switch]$Force,
    [switch]$Quiet,
    [string]$LogFile,
    # Which build to take from the kit when -SourceDir is not given: the one that includes .NET
    # (publish-client / publish-host) or the small one that uses the .NET Desktop Runtime on the PC.
    [ValidateSet("", "IncludesDotNet", "SharedRuntime")][string]$Build = "",
    # A shared-runtime build needs the .NET Desktop Runtime. When it is missing and the kit has no
    # runtime installer: Ask (interactive default), Allow the download, or Never (default with -Quiet).
    [ValidateSet("", "Ask", "Allow", "Never")][string]$RuntimeDownload = "",
    # Client Install / Modify: a JSON file of named access keys (see access-keys.json.example). They replace
    # the named keys on the PC. Install uses deploy\access-keys.json from the kit when this is not given.
    [string]$AccessKeysFile,
    # "lt" or "en" for the prompts; empty follows the Windows display language.
    [ValidateSet("", "en", "lt")][string]$Language = ""
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "ClassroomSetup.psm1") -Force
Set-CmLanguage $Language
# The text in the setup's language: T "Folder with {0}" $exeName
function T([string]$Text) { Get-CmText $Text $args }

# ---- Elevate --------------------------------------------------------------------
if (-not (Test-CmAdmin)) {
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"")
    foreach ($p in $PSBoundParameters.GetEnumerator()) {
        if ($p.Value -is [switch]) { if ($p.Value.IsPresent) { $argList += "-$($p.Key)" } }
        elseif ($p.Value -is [array]) { $argList += "-$($p.Key)"; $argList += "`"$($p.Value -join ',')`"" }
        else { $argList += "-$($p.Key)"; $argList += "`"$($p.Value)`"" }
    }
    if (-not $Quiet) { $argList = @("-NoExit") + $argList }
    $proc = Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -PassThru -Wait
    exit $proc.ExitCode
}

# ---- Uninstalling from the installed copy: run from a temp copy so the folder can be deleted --
$programDir = Split-Path $PSScriptRoot -Parent
$runningFromInstall = (Split-Path $PSScriptRoot -Leaf) -eq "setup" -and
    ((Test-Path (Join-Path $programDir "ClassroomClient.exe")) -or (Test-Path (Join-Path $programDir "ClassroomHost.exe")))
if ($Action -eq "Uninstall" -and $runningFromInstall -and -not $env:CM_SETUP_TEMP) {
    $tmp = Join-Path $env:TEMP ("ClassroomManagerSetup-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    Copy-Item (Join-Path $PSScriptRoot "*") $tmp -Force
    $env:CM_SETUP_TEMP = "1"
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $tmp "Setup.ps1"))
    foreach ($p in $PSBoundParameters.GetEnumerator()) {
        if ($p.Value -is [switch]) { if ($p.Value.IsPresent) { $argList += "-$($p.Key)" } }
        else { $argList += "-$($p.Key)"; $argList += "$($p.Value)" }
    }
    & powershell.exe @argList
    $code = $LASTEXITCODE
    # The copy was made only so the installed folder could be deleted while its own script ran; nothing needs it now (T-227).
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    exit $code
}

if ($LogFile) { Start-Transcript -Path $LogFile -Append | Out-Null }

# ---- Defaults from a USB kit ---------------------------------------------------------
$kitRoot = Split-Path $PSScriptRoot -Parent
$kit = Get-CmKit $PSScriptRoot   # classroom-kit.json (or a legacy kit-settings.json) beside these scripts; read only
function KitValue($name) { if ($kit -and $kit.PSObject.Properties[$name]) { return $kit.$name } return $null }

if ($AllowedRoots) { $AllowedRoots = @($AllowedRoots | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }

function Ask([string]$Prompt, [string]$Default) {
    if ($Quiet) { return $Default }
    $suffix = $(if ($Default) { " [$Default]" } else { "" })
    $a = Read-Host "$Prompt$suffix"
    if ([string]::IsNullOrWhiteSpace($a)) { return $Default }
    return $a.Trim()
}

function AskSecret([string]$Prompt) {
    if ($Quiet) { return "" }
    $s = Read-Host $Prompt -AsSecureString
    return [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s))
}

function AskYes([string]$Prompt, [bool]$Default) {
    if ($Quiet) { return $Default }
    # Not in the text table: its keys ignore case, and these two differ only in case.
    $d = $(if ((Get-CmLanguage) -eq "lt") { $(if ($Default) { "T/n" } else { "t/N" }) } else { $(if ($Default) { "Y/n" } else { "y/N" }) })
    $a = Read-Host "$Prompt ($d)"
    if ([string]::IsNullOrWhiteSpace($a)) { return $Default }
    # English answers work in every language; Lithuanian adds t / taip.
    return ($a.Trim().ToLower() -in @("y", "yes", "t", "taip"))
}

Set-CmRuntimeDownloadPrompt {
    param([string]$Question)
    if ($RuntimeDownload -eq "Allow") { return $true }
    if ($RuntimeDownload -eq "Never" -or $Quiet) { return $false }
    Write-Host ""
    return (AskYes $Question $true)
}

function Resolve-SetupKey([string]$Given, [string]$ExistingHostKey) {
    # Order: a key passed on the command line > the key stored on this setup kit > the teacher app's
    # existing key (host install on a PC that already has one) > a newly generated random key.
    if ($Given) { return @{ Key = $Given; Source = "given" } }
    $kitKey = Get-CmKitKey $PSScriptRoot
    if ($kitKey) {
        if (AskYes (T "Use the shared key from classroom-kit.json (starts `"{0}...`")" $kitKey.Substring(0, 5)) $true) {
            return @{ Key = $kitKey; Source = "kit" }
        }
        return @{ Key = (AskSecret (T "Shared key (12+ characters, the same on the teacher app and every student PC)")); Source = "typed" }
    }
    if ($ExistingHostKey) {
        if (AskYes (T "Keep the shared key already saved in the teacher app for {0}" $env:USERNAME) $true) { return @{ Key = $ExistingHostKey; Source = "host" } }
    }
    # A silent run that generated a key would leave nobody knowing it (nothing is saved on the kit): it must be given.
    if ($Quiet) { throw (T "A silent install needs the shared key: pass -Key, or put a classroom-kit.json with a key beside the setup.") }
    $generated = New-CmKey
    Write-Host ""
    Write-Host (T "No shared key was given or found in classroom-kit.json. A new random key was generated - write it down, every PC needs the same key:") -ForegroundColor Cyan
    Write-Host "    $generated" -ForegroundColor Yellow
    $typed = Ask (T "Press Enter to use it, or type your own (12+ characters)") ""
    if ($typed) { return @{ Key = $typed; Source = "typed" } }
    return @{ Key = $generated; Source = "generated" }
}

function Show-KeyReminder {
    # Setup never saves the key on the kit (T-226); it only reminds the person who sets up to keep it.
    if ($Quiet) { return }
    Write-Host ""
    Write-Host (T "Keep this key safe (for example in a password manager): whoever has it can control every student PC.") -ForegroundColor Yellow
}

function Pick([string]$Title, [string[]]$Options) {
    Write-Host ""
    Write-Host $Title -ForegroundColor Cyan
    # Shown in the setup's language; the English option is returned, which is what the script compares.
    for ($i = 0; $i -lt $Options.Count; $i++) { Write-Host ("  [{0}] {1}" -f ($i + 1), (T $Options[$i])) }
    do { $n = Read-Host (T "Select 1-{0}" $Options.Count) } while (-not ($n -as [int]) -or [int]$n -lt 1 -or [int]$n -gt $Options.Count)
    return $Options[[int]$n - 1]
}

try {
    # ---- Component -----------------------------------------------------------------
    if (-not $Component) {
        if ($Quiet) { throw "-Component is required with -Quiet." }
        $c = Pick (T "Classroom Manager setup - which part?") @("Client - a student PC", "Host - the teacher PC")
        $Component = $(if ($c -like "Client*") { "Client" } else { "Host" })
    }

    $status = $(if ($Component -eq "Client") { Get-CmClientStatus } else { Get-CmHostStatus })
    Write-Host ""
    Write-Host $(if ($Component -eq "Client") { Format-CmClientStatus $status } else { Format-CmHostStatus $status })

    # ---- Action --------------------------------------------------------------------
    if (-not $Action) {
        if ($Quiet) { throw "-Action is required with -Quiet." }
        $choices = $(if ($status.Installed) { @("Update", "Modify", "Repair", "Uninstall", "Status") } else { @("Install", "Repair", "Status") })
        $Action = Pick (T "What do you want to do?") $choices
    }
    if ($Action -eq "Status") { exit 0 }

    $exeName = $(if ($Component -eq "Client") { "ClassroomClient.exe" } else { "ClassroomHost.exe" })
    $includesDotNet = Join-Path $kitRoot $(if ($Component -eq "Client") { "publish-client" } else { "publish-host" })
    $sharedRuntime = "$includesDotNet-shared-runtime"
    $hasIncluded = Test-Path (Join-Path $includesDotNet $exeName)
    $hasShared = Test-Path (Join-Path $sharedRuntime $exeName)
    if (-not $Build -and $hasIncluded -and $hasShared -and -not $Quiet -and -not $SourceDir -and $Action -in @("Install", "Update", "Repair")) {
        $runtimeName = (Get-CmDotNetRuntimeInfo).DisplayName
        $choice = Pick "This kit has two builds of the program:" @(
            "Includes .NET - larger, needs nothing else on this PC",
            "Uses the $runtimeName - smaller and faster to update; setup installs the runtime if it is missing")
        $Build = $(if ($choice -like "Includes*") { "IncludesDotNet" } else { "SharedRuntime" })
    }
    $defaultSource = $(if ($Build -eq "SharedRuntime" -or (-not $hasIncluded -and $hasShared)) { $sharedRuntime } else { $includesDotNet })
    if (-not (Test-Path (Join-Path $defaultSource $exeName))) { $defaultSource = "" }
    if ($Action -in @("Install", "Update") -and -not $SourceDir) { $SourceDir = Ask (T "Folder with {0}" $exeName) $defaultSource }
    if ($Action -eq "Repair" -and -not $SourceDir -and $defaultSource) {
        if (AskYes (T "Reinstall program files from {0}" $defaultSource) $true) { $SourceDir = $defaultSource }
    }

    if (-not $PSBoundParameters.ContainsKey("SetNetworkPrivate") -and $Action -in @("Install", "Repair") -and (Get-CmPublicNetworks)) {
        $SetNetworkPrivate = AskYes (T "This PC is on a network marked Public, which blocks Classroom Manager. Set it to Private") $false
    }

    # ---- Run -----------------------------------------------------------------------
    switch ("$Component/$Action") {
        "Client/Install" {
            $resolved = Resolve-SetupKey $Key $null
            $Key = $resolved.Key
            $problem = Test-CmKey $Key
            if ($problem) { throw $problem }
            if (-not $TcpPort) { $TcpPort = [int](Ask (T "TCP port") "$(if (KitValue 'tcpPort') { KitValue 'tcpPort' } else { 5050 })") }
            if (-not $UdpPort) { $UdpPort = [int](Ask (T "UDP port") "$(if (KitValue 'udpPort') { KitValue 'udpPort' } else { 5051 })") }
            if (-not $AllowedRoots) {
                $r = KitValue "allowedRoots"; if (-not $r) { $r = @("C:\Users") }
                $AllowedRoots = @((Ask (T "Folders the teacher may browse (comma-separated)") ($r -join ",")) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            }
            $namedKeys = $(if ($AccessKeysFile) { Read-CmAccessKeys $AccessKeysFile } else { $null })
            Install-CmClient -SourceDir $SourceDir -Key $Key -TcpPort $TcpPort -UdpPort $UdpPort -AllowedRoots $AllowedRoots -Tls $Tls -SetNetworkPrivate:$SetNetworkPrivate -AccessKeys $namedKeys
            Show-KeyReminder
        }
        "Client/Update"  { Update-CmClient -SourceDir $SourceDir -Force:$Force }
        "Client/Modify"  {
            $namedKeys = $(if ($AccessKeysFile) { Read-CmAccessKeys $AccessKeysFile } else { $null })
            if (-not $Quiet -and -not ($Key -or $TcpPort -or $UdpPort -or $AllowedRoots -or $AccessKeysFile)) {
                if (AskYes (T "Change the shared key") $false) { $Key = AskSecret (T "New shared key (12+ characters)") }
                $TcpPort = [int](Ask (T "TCP port") "$($status.TcpPort)")
                $UdpPort = [int](Ask (T "UDP port") "$($status.UdpPort)")
                $AllowedRoots = @((Ask (T "Folders the teacher may browse (comma-separated)") ($status.AllowedRoots -join ",")) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            }
            Set-CmClientConfig -Key $Key -TcpPort $TcpPort -UdpPort $UdpPort -AllowedRoots $AllowedRoots -Tls $Tls -AccessKeys $namedKeys
        }
        "Client/Repair"  { Repair-CmClient -SourceDir $SourceDir -SetNetworkPrivate:$SetNetworkPrivate }
        "Client/Uninstall" {
            if (-not $Quiet -and -not $PSBoundParameters.ContainsKey("KeepData")) {
                $KeepData = AskYes (T "Keep settings and logs for a later reinstall") $false
            }
            if (-not $Quiet -and -not (AskYes (T "Remove the Classroom Manager Client from this PC") $true)) { throw (T "Cancelled.") }
            Uninstall-CmClient -KeepData:$KeepData
        }
        "Host/Install" {
            $desk = $true; $start = $false; $onPath = $true
            if ($DesktopShortcut) { $desk = ($DesktopShortcut -eq "On") } else { $desk = AskYes (T "Desktop shortcut") $true }
            if ($StartWithWindows) { $start = ($StartWithWindows -eq "On") } else { $start = AskYes (T "Start the teacher app when Windows starts") $false }
            if ($AddToPath) { $onPath = ($AddToPath -eq "On") } else { $onPath = AskYes (T "Add ClassroomCli to PATH, to run it from any command prompt") $true }
            if (-not $InstallDir) { $InstallDir = Ask (T "Install folder") "$env:ProgramFiles\ClassroomHost" }
            Install-CmHost -SourceDir $SourceDir -InstallDir $InstallDir -DesktopShortcut $desk -StartWithWindows $start -AddToPath $onPath -SetNetworkPrivate:$SetNetworkPrivate

            # Shared key for the teacher app: saved for the Windows user running setup (not on the kit).
            $existing = Get-CmHostKey
            if ($Quiet -and -not $Key -and -not $existing -and -not (Get-CmKitKey $PSScriptRoot)) {
                # A silent Host install has no one to ask and nothing worth inventing: the key is set in the Host (Settings) afterwards.
                Write-CmLog "No shared key was given, so none was set for the teacher app. Enter it in the Host under Settings."
                break
            }
            $resolved = Resolve-SetupKey $Key $existing
            $problem = Test-CmKey $resolved.Key
            if ($problem) { throw $problem }
            if ($existing -and $existing -ne $resolved.Key) {
                if (AskYes (T "The teacher app for {0} already has a different shared key. Replace it (student PCs set up with the old key will then show 'Wrong shared key')" $env:USERNAME) $false) {
                    Set-CmHostKey $resolved.Key
                } else {
                    Write-CmLog "Kept the teacher app's existing key. Use Rotate key... in the app to change keys on student PCs."
                    $resolved = @{ Key = $existing; Source = "host" }
                }
            } elseif (-not $existing) {
                Set-CmHostKey $resolved.Key
            }
            Show-KeyReminder
        }
        "Host/Update"  { Update-CmHost -SourceDir $SourceDir -Force:$Force }
        "Host/Modify"  {
            if (-not $Quiet -and -not ($DesktopShortcut -or $StartWithWindows -or $AddToPath)) {
                $DesktopShortcut = $(if (AskYes (T "Desktop shortcut") $status.DesktopShortcut) { "On" } else { "Off" })
                $StartWithWindows = $(if (AskYes (T "Start with Windows") $status.StartWithWindows) { "On" } else { "Off" })
                $AddToPath = $(if (AskYes (T "ClassroomCli on PATH") $status.OnPath) { "On" } else { "Off" })
            }
            Set-CmHostOptions -DesktopShortcut $DesktopShortcut -StartWithWindows $StartWithWindows -AddToPath $AddToPath
        }
        "Host/Repair"  { Repair-CmHost -SourceDir $SourceDir -SetNetworkPrivate:$SetNetworkPrivate }
        "Host/Uninstall" {
            if (-not $Quiet -and -not $PSBoundParameters.ContainsKey("RemoveSettings")) {
                $RemoveSettings = AskYes (T "Also delete saved teacher settings (shared key, computer list) for all users") $false
            }
            if (-not $Quiet -and -not (AskYes (T "Remove the Classroom Manager teacher app from this PC") $true)) { throw (T "Cancelled.") }
            Uninstall-CmHost -RemoveSettings:$RemoveSettings
        }
    }
    Write-Host ""
    Write-Host (T "Done.") -ForegroundColor Green
    $code = 0
}
catch {
    Write-Host ""
    Write-Host (T "FAILED: {0}" $_.Exception.Message) -ForegroundColor Red
    $code = 1
}
finally {
    if ($LogFile) { Stop-Transcript | Out-Null }
}
exit $code
