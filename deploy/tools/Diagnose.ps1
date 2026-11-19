<#
.SYNOPSIS
  Collects a Classroom Manager troubleshooting report for this PC (client and/or host).

.DESCRIPTION
  Writes Diagnostics-<PC>-<date>.txt next to this script (e.g. on the USB stick), or to the Desktop
  if that folder is read-only. Configuration is summarized using an allowlist; keys are not exported.
  Reports still identify this PC and its users. Review the report before sharing it.

  Checks: Windows version, network profiles (Public blocks the firewall rules), IP addresses,
  client service and version, listening port, firewall rules, logon helper, settings status,
  hosts-file block, teacher app install and settings status. Raw logs are excluded by default.

.PARAMETER IncludeLogs
  Include recent raw client/update logs. These may contain credentials or personal data,
  including from older versions. Review and remove sensitive content before sharing.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\tools\Diagnose.ps1
#>
param([switch]$IncludeLogs)

$ErrorActionPreference = "Continue"
$lines = New-Object System.Collections.Generic.List[string]
function Add([string]$text) { $lines.Add($text) }
function Section([string]$title) { Add ""; Add "== $title ==" }
function Get-CmSettingsSummary([string]$Json) {
    # Never return a configuration value of arbitrary type or stringify the source object.
    # Unknown fields (including future secret-bearing fields) are deliberately omitted.
    try {
        if (-not $Json.TrimStart().StartsWith('{')) { throw 'Expected an object' }
        $config = ConvertFrom-Json -InputObject $Json -ErrorAction Stop
        if ($null -eq $config -or $config -isnot [pscustomobject]) { throw 'Expected an object' }
        $summary = [ordered]@{}
        foreach ($name in 'tcpPort', 'udpPort') {
            $value = $config.$name
            if (($value -is [int] -or $value -is [long]) -and $value -ge 1 -and $value -le 65535) {
                $summary[$name] = $value
            } else { $summary[$name] = '(missing or invalid)' }
        }
        $summary['useTls'] = if ($config.useTls -is [bool]) { $config.useTls } else { '(missing or invalid)' }
        $summary['primaryKeyConfigured'] = ($config.psk -is [string] -and -not [string]::IsNullOrEmpty($config.psk))
        foreach ($name in 'accessKeys', 'allowedRoots', 'allowedUpdateSignerCertificateSha256', 'computers') {
            $value = $config.$name
            $summary[$name + 'Count'] = if ($value -is [array]) { $value.Count } else { '(missing or invalid)' }
        }
        return ($summary | ConvertTo-Json -Compress)
    } catch {
        # Parser/IO exception messages can quote secret-bearing input. Do not echo them.
        return '(unavailable: invalid configuration)'
    }
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

Add "Classroom Manager diagnostics - $env:COMPUTERNAME - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Add "Run as administrator: $isAdmin (some checks need it)"
Add "Privacy: this report identifies the PC, users and network. Review before sharing."

Section "Windows"
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
if ($os) { Add "$($os.Caption) $($os.Version), last boot $($os.LastBootUpTime)" }
Add "Signed-in users: $((Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue).UserName)"

Section "Network"
foreach ($p in (Get-NetConnectionProfile -ErrorAction SilentlyContinue)) {
    $warn = $(if ($p.NetworkCategory -eq "Public") { "   <-- PUBLIC: Classroom Manager firewall rules do not apply" } else { "" })
    Add "$($p.InterfaceAlias): '$($p.Name)' $($p.NetworkCategory)$warn"
}
foreach ($a in (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -ne "127.0.0.1" })) {
    Add "$($a.InterfaceAlias): $($a.IPAddress)/$($a.PrefixLength)"
}

Section "Client (student PC)"
$exe = Join-Path $env:ProgramFiles "ClassroomClient\ClassroomClient.exe"
if (Test-Path $exe) { Add "Installed: $((Get-Item $exe).VersionInfo.FileVersion) at $exe" } else { Add "Not installed." }
$svc = Get-Service ClassroomManagerClient -ErrorAction SilentlyContinue
if ($svc) { Add "Service: $($svc.Status), start type $($svc.StartType)" }
$listen = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -in @(Get-Process ClassroomClient -ErrorAction SilentlyContinue).Id }
foreach ($l in $listen) { Add "Listening on TCP $($l.LocalAddress):$($l.LocalPort)" }
$helpers = @(Get-Process ClassroomClient -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 })
Add "User helpers running: $($helpers.Count) (sessions: $(($helpers | ForEach-Object { $_.SessionId }) -join ', '))"
$run = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue).ClassroomManagerHelper
Add "Logon helper entry: $(if ($run) { $run } else { 'MISSING' })"
foreach ($n in "Classroom Manager TCP", "Classroom Manager UDP", "Classroom Manager ICMP") {
    $r = Get-NetFirewallRule -DisplayName $n -ErrorAction SilentlyContinue
    Add "Firewall '$n': $(if ($r) { "enabled=$($r.Enabled) profiles=$($r.Profile)" } else { 'MISSING' })"
}
$cfgPath = Join-Path $env:ProgramData "ClassroomManager\client.json"
if (Test-Path $cfgPath) {
    try { Add "Settings: $(Get-CmSettingsSummary (Get-Content $cfgPath -Raw -ErrorAction Stop))" }
    catch { Add "Settings: client.json exists but could not be read." }
} elseif (Test-Path $exe) { Add "Settings: client.json MISSING" }
$hostsFile = Join-Path $env:SystemRoot "System32\drivers\etc\hosts"
if (Select-String -Path $hostsFile -Pattern "ClassroomManager block start" -SimpleMatch -Quiet -ErrorAction SilentlyContinue) {
    Add "Hosts file: contains a Classroom Manager website block"
}
Add "Raw logs: $(if ($IncludeLogs) { 'INCLUDED - may contain credentials or personal data; review before sharing.' } else { 'excluded (use -IncludeLogs only after reviewing the privacy risk).' })"
foreach ($log in $(if ($IncludeLogs) { @('client.log', 'update.log') } else { @() })) {
    $p = Join-Path $env:ProgramData "ClassroomManager\$log"
    if (Test-Path $p) {
        Add ""; Add "-- last 40 lines of $log --"
        Get-Content $p -Tail 40 -ErrorAction SilentlyContinue | ForEach-Object { Add $_ }
    }
}

Section "Host (teacher app)"
$hostDir = (Get-ItemProperty "HKLM:\SOFTWARE\ClassroomManager\Host" -ErrorAction SilentlyContinue).InstallDir
if (-not $hostDir) { $hostDir = Join-Path $env:ProgramFiles "ClassroomHost" }
$hostExe = Join-Path $hostDir "ClassroomHost.exe"
if (Test-Path $hostExe) { Add "Installed: $((Get-Item $hostExe).VersionInfo.FileVersion) at $hostExe" } else { Add "Not installed." }
$fw = Get-NetFirewallRule -DisplayName "Classroom Manager Host" -ErrorAction SilentlyContinue
if (Test-Path $hostExe) { Add "Firewall rule for the app: $(if ($fw) { 'present' } else { 'MISSING (discovery replies may be blocked)' })" }
$hostJson = Join-Path $env:APPDATA "ClassroomManager\host.json"
if (Test-Path $hostJson) {
    try {
        Add "Settings for ${env:USERNAME}: $(Get-CmSettingsSummary (Get-Content $hostJson -Raw -ErrorAction Stop))"
    } catch { Add "host.json could not be read." }
}

# ---- Write the report ------------------------------------------------------------------------
$name = "Diagnostics-$env:COMPUTERNAME-$(Get-Date -Format 'yyyyMMdd-HHmm').txt"
$out = Join-Path $PSScriptRoot $name
try { $lines | Set-Content -Path $out -Encoding UTF8 -ErrorAction Stop }
catch { $out = Join-Path ([Environment]::GetFolderPath("Desktop")) $name; $lines | Set-Content -Path $out -Encoding UTF8 }
$lines | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "Report saved to $out" -ForegroundColor Green
