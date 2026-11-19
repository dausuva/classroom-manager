<#
  ClassroomSetup.psm1 - install / update / modify / repair / uninstall logic for the
  Classroom Manager Client (student PC) and Host (teacher PC).

  Used by Setup.ps1 (console / unattended), Setup-Gui.ps1 (window) and the older
  Install*.ps1 / Uninstall-Client.ps1 wrappers. Windows PowerShell 5.1 compatible.

  Every installed component also gets:
    * a "setup" folder next to its program files (a copy of these scripts), and
    * an entry in Settings > Apps (Add/Remove Programs) with Modify and Uninstall,
  so repair, settings changes and removal work later without the USB stick.
#>

# ---- Constants -----------------------------------------------------------------

$script:ClientService   = "ClassroomManagerClient"
$script:ClientDir       = Join-Path $env:ProgramFiles "ClassroomClient"
$script:ClientExeName   = "ClassroomClient.exe"
$script:ClientDataDir   = Join-Path $env:ProgramData "ClassroomManager"
$script:ClientRunValue  = "ClassroomManagerHelper"
$script:ClientArpKey    = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ClassroomManagerClient"
$script:ClientFwRules   = @("Classroom Manager TCP", "Classroom Manager UDP", "Classroom Manager ICMP")

$script:HostDefaultDir  = Join-Path $env:ProgramFiles "ClassroomHost"
$script:HostExeName     = "ClassroomHost.exe"
$script:HostStateKey    = "HKLM:\SOFTWARE\ClassroomManager\Host"
$script:HostArpKey      = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ClassroomManagerHost"
$script:HostRunValue    = "ClassroomManagerHost"
$script:HostFwRule      = "Classroom Manager Host"
$script:HostLinkName    = "Classroom Manager.lnk"
$script:HostOldLinkName = "Classroom Manager Host.lnk"   # the shortcut name before the app was shown as "Classroom Manager"
$script:MachineEnvKey   = "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment"

$script:RunKey          = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
$script:SetupFiles      = @("ClassroomSetup.psm1", "Setup.ps1", "Setup-Gui.ps1", "SetupText.lt.psd1")

# ---- Logging (the GUI swaps the sink for its log box) --------------------------

$script:LogSink = { param([string]$Message) Write-Host $Message }

function Set-CmLogger {
    param([Parameter(Mandatory)][scriptblock]$Sink)
    $script:LogSink = $Sink
}

function Write-CmLog {
    param([string]$Message)
    & $script:LogSink $Message
}

# ---- Shared helpers ------------------------------------------------------------

function Test-CmAdmin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-CmAdmin {
    if (-not (Test-CmAdmin)) { throw "Run this as Administrator." }
}

function Get-CmFileVersion {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    $raw = (Get-Item $Path).VersionInfo.FileVersion
    if (-not $raw) { return $null }
    try { return [version]($raw.Trim()) } catch { return $null }
}

function ConvertFrom-CmReleaseClaim {
    <# The release identity line a customer program carries in its version resource (T-218). $null: the program carries none.
       Valid=$false: it claims one but not in exactly the expected format. #>
    param([string]$Text)
    if (-not $Text -or -not $Text.StartsWith('cm-release/', [StringComparison]::Ordinal)) { return $null }
    $bad = [pscustomobject]@{ Valid = $false }
    $parts = $Text.Split('|')
    if ($parts.Count -ne 7 -or $parts[0] -ne 'cm-release/1' -or $parts[2] -ne 'customer') { return $bad }
    if ($parts[1] -notmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}$' -or $parts[5] -notmatch '^[1-9]\d{0,17}$') { return $bad }
    if (@($parts[1].Split('.') | Where-Object { [int]$_ -gt 65535 }).Count -gt 0) { return $bad }
    foreach ($field in @($parts[3], $parts[4], $parts[6])) { if ([string]::IsNullOrWhiteSpace($field) -or $field.Length -gt 64) { return $bad } }
    [pscustomobject]@{ Valid = $true; PublicVersion = [version]$parts[1]; Audience = $parts[3]; Variant = $parts[4]; Revision = [long]$parts[5]; ReleaseId = $parts[6] }
}

function Get-CmReleaseClaim {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    ConvertFrom-CmReleaseClaim ((Get-Item $Path).VersionInfo.LegalTrademarks)
}

function Test-CmReleaseOrder {
    <# Whether a program may replace the installed one when a release identity is involved. Handled=$false: neither has one, so the file-version rule decides.
       Problem: why not. -Force is "allow older/same": it skips the order, never a malformed claim, another school or another variant. #>
    param($Installed, [version]$InstalledVersion, $New, [version]$NewVersion, [switch]$Force)
    $none = [pscustomobject]@{ Handled = $false; Problem = $null }
    $fine = [pscustomobject]@{ Handled = $true; Problem = $null }
    function Stop([string]$Reason) { [pscustomobject]@{ Handled = $true; Problem = $Reason } }
    $installedIdentified = $Installed -and $Installed.Valid
    if ($New -and -not $New.Valid) { return Stop 'The new version claims a release identity that is not valid, so it was not installed.' }
    if (-not $New) {
        if ($installedIdentified -and -not $Force) { return Stop "The installed program is customer release $($Installed.PublicVersion) and the new one carries no release identity, so its order cannot be checked. Tick 'Allow older/same version' to install it anyway." }
        return $none
    }
    if ($installedIdentified) {
        if ($New.Audience -ne '*' -and $New.Audience -ne $Installed.Audience) { return Stop 'The new version was made for another school, so it was not installed.' }
        if ($New.Variant -ne $Installed.Variant) { return Stop 'The new version is for another kind of build, so it was not installed.' }
        if ($Force) { return $fine }
        if ($New.PublicVersion -gt $Installed.PublicVersion) { return $fine }
        if ($New.PublicVersion -lt $Installed.PublicVersion) { return Stop "The new version ($($New.PublicVersion)) is older than the installed one ($($Installed.PublicVersion)). Tick 'Allow older/same version' to roll back." }
        if ($New.Revision -gt $Installed.Revision) { return $fine }
        if ($New.Revision -eq $Installed.Revision) { return Stop "Release $($New.PublicVersion) number $($New.Revision) is already installed. Use Repair to reinstall the same files." }
        return Stop "Release number $($New.Revision) is older than the installed release number $($Installed.Revision). Tick 'Allow older/same version' to roll back."
    }
    # The installed program predates release identities: only its file version is known. The customer release of the same or a later public version replaces it.
    if (-not $Force -and $InstalledVersion) {
        $legacy = [version]::new($InstalledVersion.Major, $InstalledVersion.Minor, [Math]::Max($InstalledVersion.Build, 0))
        if ($New.PublicVersion -lt $legacy) { return Stop "The new version ($($New.PublicVersion)) is older than the installed program ($InstalledVersion). Tick 'Allow older/same version' to roll back." }
    }
    $fine
}

function Get-CmBundledAction {
    <# What the unattended installer does with a program that is already installed (T-218): Install, Update or Repair.
       The same release again is a repair. A different release is always an update, because Update-CmClient / Update-CmHost ask the release order and
       refuse an older one, another school or a program without identity: two releases of one public version share the file version 1.2.0.0, and the
       old rule (same file version = repair) would have put the older one over the newer one. Without identities the old rule is unchanged. #>
    param([bool]$Installed, [version]$InstalledVersion, [version]$SourceVersion, $InstalledClaim, $SourceClaim)
    if (-not $Installed) { return 'Install' }
    $bothIdentified = $InstalledClaim -and $InstalledClaim.Valid -and $SourceClaim -and $SourceClaim.Valid
    if ($bothIdentified) {
        if ($InstalledClaim.ReleaseId -eq $SourceClaim.ReleaseId -and $InstalledClaim.Revision -eq $SourceClaim.Revision) { return 'Repair' }
        return 'Update'
    }
    if ($InstalledClaim -or $SourceClaim) { return 'Update' }
    if ($SourceVersion -eq $InstalledVersion) { return 'Repair' }
    return 'Update'
}

function Get-CmCompany {
    param([string]$Path)
    $c = $null
    if (Test-Path $Path) { $c = (Get-Item $Path).VersionInfo.CompanyName }
    if ($c) { return $c }
    return "Dausuva Labs"
}

function Get-CmCertificateSha256 {
    param([Parameter(Mandatory)]$Certificate)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Certificate.RawData)) -replace '-', '') }
    finally { $sha.Dispose() }
}

function Test-CmSourceSignature {
    # Signed release folders contain SIGNING.json. Folders without the marker remain usable
    # as explicitly unsigned developer builds.
    param([string]$SourceDir, [string]$ExeName)
    $markerPath = Join-Path $SourceDir 'SIGNING.json'
    if (-not (Test-Path $markerPath)) {
        Write-CmLog 'No SIGNING.json in source: treating this as an unsigned developer build, not a release artifact.'
        return $null
    }
    try { $marker = Get-Content $markerPath -Raw | ConvertFrom-Json }
    catch { throw "Invalid release signature metadata: $markerPath" }
    $wanted = ([string]$marker.certificateSha256 -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($wanted.Length -ne 64) { throw 'SIGNING.json does not contain a valid certificateSha256 value.' }
    $exe = Join-Path $SourceDir $ExeName
    $sig = Get-AuthenticodeSignature -LiteralPath $exe
    if ($sig.Status -ne 'Valid' -or -not $sig.SignerCertificate) {
        throw "$ExeName does not have a valid, trusted Windows signature (status: $($sig.Status))."
    }
    $actual = Get-CmCertificateSha256 $sig.SignerCertificate
    if ($actual -ne $wanted) { throw "$ExeName signer fingerprint does not match SIGNING.json." }
    $subjectPattern = [string]$marker.subjectPattern
    if (-not $subjectPattern) { $subjectPattern = '^CN=Dausuva(?: |,|$)' }
    try { $subjectMatches = $sig.SignerCertificate.Subject -match $subjectPattern }
    catch { throw "SIGNING.json contains an invalid subjectPattern: $subjectPattern" }
    if (-not $subjectMatches) { throw "Unexpected signer subject: $($sig.SignerCertificate.Subject)" }
    Write-CmLog "Authenticode signature verified for $ExeName (certificate SHA-256 $actual)."
    $allowed = @($marker.allowedUpdateSignerCertificateSha256 | ForEach-Object {
        ([string]$_ -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    } | Where-Object { $_.Length -eq 64 } | Select-Object -Unique)
    if ($allowed -notcontains $actual) { $allowed += $actual }
    return $allowed
}

function Set-CmClientUpdateSigner {
    param([string[]]$CertificateSha256)
    $approved = @($CertificateSha256 | ForEach-Object {
        ([string]$_ -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    } | Where-Object { $_.Length -eq 64 } | Select-Object -Unique)
    if (-not $approved) { return }
    $path = Join-Path $script:ClientDataDir 'client.json'
    $cfg = Get-Content $path -Raw | ConvertFrom-Json
    $cfg | Add-Member -NotePropertyName allowedUpdateSignerCertificateSha256 -NotePropertyValue $approved -Force
    $cfg | ConvertTo-Json -Depth 8 | Set-Content $path -Encoding UTF8
    Write-CmLog "Pinned $($approved.Count) approved Authenticode signer(s) for in-app client updates."
}

# ---- .NET runtime (only for builds that do not carry their own) ------------------
#
# Every publish folder has a RUNTIME.json written by Publish-*.ps1. "self-contained" builds
# include .NET and need nothing. "shared-runtime" builds are small and use the .NET Desktop
# Runtime installed on the PC; setup installs it when it is missing, from Microsoft's installer
# in a "runtime" folder of the kit or, if the person running setup agrees, by downloading it.
# When the product moves to a new .NET version, change this block and <ClassroomNetVersion>
# in Directory.Build.props together.

# The program comes in two builds: x64 (the default) and x86 for 32-bit Windows. A build says which in its
# RUNTIME.json ("architecture"); a folder without that field is x64. The runtime to look for, its installer and
# its download follow the build, and Set-CmRuntimeArchitecture switches them.
$script:DotNetRuntime = @{
    Framework        = "Microsoft.WindowsDesktop.App"
    MajorVersion     = 10
    Architecture     = "x64"
    DisplayName      = ".NET 10 Desktop Runtime (x64)"
    InstallerPattern = "windowsdesktop-runtime-*-win-x64.exe"
    DownloadUrl      = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"
    # The runtime installer is signed "CN=.NET, O=Microsoft Corporation, ..." and other Microsoft
    # downloads "CN=Microsoft Corporation, O=Microsoft Corporation, ...": the organization is what counts.
    SignerPattern    = "(^|, )O=Microsoft Corporation(,|$)"
}

function Set-CmRuntimeArchitecture {
    # "x64" or "x86". Anything else is refused: a typo must not silently install the wrong runtime.
    param([Parameter(Mandatory)][string]$Architecture)
    $arch = $Architecture.Trim().ToLowerInvariant()
    if ($arch -notin @("x64", "x86")) { throw "Unknown architecture '$Architecture'. Use x64 or x86." }
    $major = $script:DotNetRuntime.MajorVersion
    $script:DotNetRuntime.Architecture     = $arch
    $script:DotNetRuntime.DisplayName      = ".NET $major Desktop Runtime ($arch)"
    $script:DotNetRuntime.InstallerPattern = "windowsdesktop-runtime-*-win-$arch.exe"
    $script:DotNetRuntime.DownloadUrl      = "https://aka.ms/dotnet/$major.0/windowsdesktop-runtime-win-$arch.exe"
}

function Get-CmOsArchitecture {
    # "x64" or "x86" for the Windows this runs on (not for this PowerShell: a 32-bit PowerShell on 64-bit Windows says x64).
    if ($env:PROCESSOR_ARCHITEW6432) { return $(if ($env:PROCESSOR_ARCHITEW6432 -eq "AMD64") { "x64" } else { $env:PROCESSOR_ARCHITEW6432.ToLowerInvariant() }) }
    return $(if ($env:PROCESSOR_ARCHITECTURE -eq "AMD64") { "x64" } elseif ($env:PROCESSOR_ARCHITECTURE -eq "x86") { "x86" } else { $env:PROCESSOR_ARCHITECTURE.ToLowerInvariant() })
}

function Get-CmSourceArchitecture {
    # The architecture a program folder was built for. No RUNTIME.json, or no field: x64 (every kit before the x86 build).
    param([string]$Dir)
    $marker = Join-Path $Dir "RUNTIME.json"
    if (-not (Test-Path $marker)) { return "x64" }
    try { $info = Get-Content $marker -Raw | ConvertFrom-Json } catch { throw "Invalid runtime description: $marker" }
    if ($info.architecture) { return ([string]$info.architecture).ToLowerInvariant() }
    return "x64"
}

function Assert-CmArchitecture {
    # Refuses a build that cannot run here, before anything is copied, with advice instead of a start-up failure.
    # x64 on 32-bit Windows cannot run. x86 on 64-bit Windows runs but sees the registry and System32 through the 32-bit
    # redirection, so Programs, Registry and Autoruns would show the wrong things: use the x64 build there.
    param([string]$Dir)
    $build = Get-CmSourceArchitecture $Dir
    $os = Get-CmOsArchitecture
    if ($build -eq "x64" -and $os -eq "x86") {
        throw "This is the 64-bit (x64) build, and this PC runs 32-bit Windows. Use the 32-bit (x86) build: the kit or installer with 'win-x86' in its name."
    }
    if ($build -eq "x86" -and $os -ne "x86") {
        throw "This is the 32-bit (x86) build for 32-bit Windows, and this PC runs 64-bit Windows. Use the 64-bit (x64) build: the kit or installer with 'win-x64' in its name."
    }
}

function Get-CmDotNetRoot {
    # Where the runtime of the current architecture lives. A 32-bit runtime on 64-bit Windows is under Program Files (x86); on 32-bit Windows Program Files is the 32-bit one.
    if ($script:DotNetRuntime.Architecture -eq "x86" -and ${env:ProgramFiles(x86)}) { return (Join-Path ${env:ProgramFiles(x86)} "dotnet") }
    return (Join-Path $env:ProgramFiles "dotnet")
}

# Asked before downloading; $null means "never download" (unattended runs must opt in).
$script:RuntimeDownloadPrompt = $null

function Get-CmDotNetRuntimeInfo { return $script:DotNetRuntime.Clone() }

function Set-CmRuntimeDownloadPrompt {
    param([scriptblock]$Prompt)
    $script:RuntimeDownloadPrompt = $Prompt
}

function Get-CmSourceRuntime {
    # What a program folder needs. No RUNTIME.json (kits built before this existed) means it includes .NET.
    param([string]$Dir)
    $marker = Join-Path $Dir "RUNTIME.json"
    if (-not (Test-Path $marker)) { return $null }
    try { $info = Get-Content $marker -Raw | ConvertFrom-Json } catch { throw "Invalid runtime description: $marker" }
    if ([string]$info.mode -ne "shared-runtime") { return $null }
    return $info
}

function Get-CmDesktopRuntimeVersions {
    param([string]$DotNetRoot = (Get-CmDotNetRoot))
    $shared = Join-Path $DotNetRoot ("shared\" + $script:DotNetRuntime.Framework)
    if (-not (Test-Path $shared)) { return @() }
    return @(Get-ChildItem $shared -Directory | ForEach-Object {
        # Preview builds are named like 10.0.0-rc.1; only released versions count.
        if ($_.Name -match '^\d+\.\d+\.\d+$') { [version]$_.Name }
    } | Sort-Object)
}

function Test-CmDesktopRuntime {
    # The apps accept the same or a newer major version (RollForward=Major in Directory.Build.props).
    param([int]$MajorVersion = $script:DotNetRuntime.MajorVersion, [string]$MinimumVersion = "", [string]$DotNetRoot = (Get-CmDotNetRoot))
    $minimum = $(if ($MinimumVersion -match '^\d+\.\d+\.\d+$') { [version]$MinimumVersion } else { [version]"$MajorVersion.0.0" })
    return [bool](Get-CmDesktopRuntimeVersions $DotNetRoot | Where-Object { $_ -ge $minimum })
}

function Find-CmRuntimeInstaller {
    # Looks beside the program folder and beside these scripts: <kit>\runtime, <source>\runtime.
    param([string]$SourceDir)
    $dirs = @()
    if ($SourceDir) { $dirs += (Join-Path $SourceDir "runtime"), (Join-Path (Split-Path $SourceDir -Parent) "runtime") }
    $dirs += (Join-Path (Split-Path $PSScriptRoot -Parent) "runtime"), (Join-Path $PSScriptRoot "runtime")
    foreach ($dir in ($dirs | Select-Object -Unique)) {
        if (-not (Test-Path $dir)) { continue }
        $found = Get-ChildItem $dir -File -Filter $script:DotNetRuntime.InstallerPattern |
            Where-Object { $_.VersionInfo.ProductMajorPart -ge $script:DotNetRuntime.MajorVersion } |
            Sort-Object { $_.VersionInfo.ProductVersion -as [version] } -Descending | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

function Test-CmMicrosoftSignature {
    # The runtime installer runs elevated, so it must carry a valid Microsoft signature wherever it came from.
    param([string]$Path)
    $sig = Get-AuthenticodeSignature -LiteralPath $Path
    if ($sig.Status -ne "Valid" -or -not $sig.SignerCertificate) {
        throw "$(Split-Path $Path -Leaf) does not have a valid Windows signature (status: $($sig.Status)). It was not run."
    }
    if ($sig.SignerCertificate.Subject -notmatch $script:DotNetRuntime.SignerPattern) {
        throw "$(Split-Path $Path -Leaf) is signed by '$($sig.SignerCertificate.Subject)', not Microsoft. It was not run."
    }
}

function Save-CmRuntimeDownload {
    $target = Join-Path $env:TEMP ("windowsdesktop-runtime-" + [guid]::NewGuid().ToString("N").Substring(0, 8) + "-win-" + $script:DotNetRuntime.Architecture + ".exe")
    Write-CmLog "Downloading the $($script:DotNetRuntime.DisplayName) from Microsoft..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $previous = $ProgressPreference
    $ProgressPreference = "SilentlyContinue"   # the progress bar makes Windows PowerShell downloads very slow
    try { Invoke-WebRequest -Uri $script:DotNetRuntime.DownloadUrl -OutFile $target -UseBasicParsing }
    catch { throw "The download failed: $($_.Exception.Message)" }
    finally { $ProgressPreference = $previous }
    return $target
}

function Install-CmDotNetRuntime {
    param([Parameter(Mandatory)][string]$InstallerPath)
    Assert-CmAdmin
    Test-CmMicrosoftSignature $InstallerPath
    Write-CmLog "Installing the $($script:DotNetRuntime.DisplayName) from $(Split-Path $InstallerPath -Leaf)..."
    $proc = Start-Process -FilePath $InstallerPath -ArgumentList "/install", "/quiet", "/norestart" -Wait -PassThru
    # 3010 and 1641 mean "installed, restart needed to finish".
    if ($proc.ExitCode -notin @(0, 3010, 1641)) { throw "The .NET runtime installer failed (exit code $($proc.ExitCode))." }
    if ($proc.ExitCode -ne 0) { Write-CmLog "Windows asks for a restart to finish installing the runtime." }
}

function Confirm-CmRuntime {
    # Call before copying program files: makes sure a shared-runtime build will be able to start.
    param([string]$Dir)
    Assert-CmArchitecture $Dir
    Set-CmRuntimeArchitecture (Get-CmSourceArchitecture $Dir)
    $need = Get-CmSourceRuntime $Dir
    if (-not $need) { return }
    $major = $(if ($need.majorVersion) { [int]$need.majorVersion } else { $script:DotNetRuntime.MajorVersion })
    $minimum = [string]$need.minimumVersion
    if (Test-CmDesktopRuntime -MajorVersion $major -MinimumVersion $minimum) {
        Write-CmLog "The $($script:DotNetRuntime.DisplayName) this build needs is installed."
        return
    }
    Write-CmLog "This build uses the $($script:DotNetRuntime.DisplayName), which is not installed on this PC."
    $installer = Find-CmRuntimeInstaller $Dir
    $downloaded = $null
    if (-not $installer) {
        $question = "This build needs the $($script:DotNetRuntime.DisplayName), which is not installed, and the setup kit has no 'runtime' folder with Microsoft's installer. Download it from Microsoft now (about 60 MB)?"
        if ($script:RuntimeDownloadPrompt -and (& $script:RuntimeDownloadPrompt $question)) {
            $installer = $downloaded = Save-CmRuntimeDownload
        } else {
            throw ("This build needs the $($script:DotNetRuntime.DisplayName). Put Microsoft's installer " +
                "($($script:DotNetRuntime.InstallerPattern)) in a 'runtime' folder next to the program folder, allow the download, " +
                "or install the build that includes .NET (the publish-client / publish-host folders).")
        }
    }
    try { Install-CmDotNetRuntime $installer }
    finally { if ($downloaded) { Remove-Item $downloaded -Force -ErrorAction SilentlyContinue } }
    if (-not (Test-CmDesktopRuntime -MajorVersion $major -MinimumVersion $minimum)) {
        throw "The runtime installer finished, but the $($script:DotNetRuntime.DisplayName) was still not found. Restart the PC and run setup again."
    }
    Write-CmLog "The $($script:DotNetRuntime.DisplayName) is installed."
}

function Resolve-CmSource {
    param([string]$SourceDir, [string]$ExeName)
    if (-not $SourceDir) { throw "Choose the folder that contains $ExeName (publish-client or publish-host)." }
    $SourceDir = $SourceDir.Trim().Trim('"')
    if (-not (Test-Path (Join-Path $SourceDir $ExeName))) { throw "$ExeName not found in `"$SourceDir`"." }
    return (Resolve-Path $SourceDir).Path
}

function Test-CmChecksums {
    # Verifies the source folder against its CHECKSUMS.sha256 (written by Publish-*.ps1), if present.
    param([string]$SourceDir)
    $sumFile = Join-Path $SourceDir "CHECKSUMS.sha256"
    if (-not (Test-Path $sumFile)) { Write-CmLog "No CHECKSUMS.sha256 in the source folder - skipping verification."; return }
    $bad = 0
    foreach ($line in Get-Content $sumFile) {
        if ($line -notmatch '^\s*([0-9a-fA-F]{64})\s+(.+)$') { continue }
        $full = Join-Path $SourceDir ($Matches[2] -replace '/', '\')
        if (-not (Test-Path $full)) { $bad++; Write-CmLog "  missing: $($Matches[2])"; continue }
        if ((Get-FileHash $full -Algorithm SHA256).Hash -ne $Matches[1].ToUpperInvariant()) { $bad++; Write-CmLog "  changed: $($Matches[2])" }
    }
    if ($bad -gt 0) { throw "The source files do not match CHECKSUMS.sha256 ($bad problem(s)). Copy the folder again from a trusted source." }
    Write-CmLog "Source files verified against CHECKSUMS.sha256."
}

function Copy-CmFiles {
    param([string]$From, [string]$To)
    New-Item -ItemType Directory -Force -Path $To | Out-Null
    Copy-Item (Join-Path $From "*") $To -Recurse -Force
}

function New-CmBackup {
    # Copies the current program folder aside so a failed update can be rolled back.
    param([string]$Dir)
    if (-not (Test-Path $Dir)) { return $null }
    $backup = Join-Path $env:TEMP ("ClassroomManager-backup-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    Copy-Item $Dir $backup -Recurse -Force
    return $backup
}

function Restore-CmBackup {
    param([string]$Backup, [string]$Dir, [switch]$Strict)
    if (-not $Backup -or -not (Test-Path $Backup)) { return }
    Write-CmLog "Rolling back to the previous version..."
    $removalErrors = if ($Strict) { 'Stop' } else { 'SilentlyContinue' }
    Remove-Item (Join-Path $Dir "*") -Recurse -Force -ErrorAction $removalErrors
    Copy-Item (Join-Path $Backup "*") $Dir -Recurse -Force
}

function Copy-CmSetupFolder {
    # Keeps a copy of these setup scripts with the installed program, for Apps & features.
    param([string]$InstallDir)
    $dest = Join-Path $InstallDir "setup"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    foreach ($f in $script:SetupFiles) {
        $src = Join-Path $PSScriptRoot $f
        if ((Test-Path $src) -and ((Resolve-Path $src).Path -ne (Join-Path $dest $f))) { Copy-Item $src $dest -Force }
    }
}

function Register-CmArpEntry {
    param([string]$Key, [string]$Component, [string]$DisplayName, [string]$InstallDir, [string]$Exe)
    $setupPs1 = Join-Path $InstallDir "setup\Setup.ps1"
    $setupGui = Join-Path $InstallDir "setup\Setup-Gui.ps1"
    $ps = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $version = Get-CmFileVersion $Exe
    $sizeKb = [int]((Get-ChildItem $InstallDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)

    New-Item -Path $Key -Force | Out-Null
    $values = @{
        DisplayName          = $DisplayName
        DisplayVersion       = $(if ($version) { $version.ToString() } else { "" })
        Publisher            = (Get-CmCompany $Exe)
        InstallLocation      = $InstallDir
        DisplayIcon          = $Exe
        UninstallString      = "`"$ps`" -NoProfile -ExecutionPolicy Bypass -File `"$setupPs1`" -Component $Component -Action Uninstall"
        QuietUninstallString = "`"$ps`" -NoProfile -ExecutionPolicy Bypass -File `"$setupPs1`" -Component $Component -Action Uninstall -Quiet"
        ModifyPath           = "`"$ps`" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$setupGui`" -Component $Component"
        InstallDate          = (Get-Date -Format "yyyyMMdd")
        URLInfoAbout         = "https://github.com/dausuva/classroom-manager"
        HelpLink             = "https://github.com/dausuva/classroom-manager/issues"
    }
    foreach ($k in $values.Keys) { Set-ItemProperty -Path $Key -Name $k -Value $values[$k] }
    Set-ItemProperty -Path $Key -Name "EstimatedSize" -Value $sizeKb -Type DWord
    Set-ItemProperty -Path $Key -Name "NoRepair" -Value 1 -Type DWord
}

# ---- Shared key: generate, check, read from the kit file ---------------------------

$script:KeyAlphabet = "23456789abcdefghjkmnpqrstuvwxyz"   # same as SharedKey.Alphabet in the apps

function New-CmKey {
    <# A random key like "k7mq2-xh9vd-3ptwa-e8fzn-4bjrs" (about 124 bits, no look-alike characters). #>
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $n = $script:KeyAlphabet.Length
    $limit = 256 - (256 % $n)          # reject bytes above this, so every character is equally likely
    $chars = New-Object System.Text.StringBuilder
    $buf = New-Object byte[] 1
    for ($i = 0; $i -lt 25; $i++) {
        if ($i -gt 0 -and $i % 5 -eq 0) { [void]$chars.Append('-') }
        do { $rng.GetBytes($buf) } while ($buf[0] -ge $limit)
        [void]$chars.Append($script:KeyAlphabet[$buf[0] % $n])
    }
    $rng.Dispose()
    return $chars.ToString()
}

function Test-CmKey {
    <# Returns $null when the key is acceptable, otherwise the reason (same rules as the apps). #>
    param([string]$Key)
    if ([string]::IsNullOrWhiteSpace($Key)) { return "The shared key is empty." }
    if ($Key -ne $Key.Trim()) { return "The shared key must not start or end with spaces." }
    if ($Key.Length -lt 12) { return "The shared key must be at least 12 characters." }
    if ($Key.Length -gt 256) { return "The shared key must be at most 256 characters." }
    if ($Key -match '[\x00-\x1F\x7F]') { return "The shared key must not contain control characters." }
    return $null
}

# ---- The kit file: classroom-kit.json ---------------------------------------------------
#
# One file that a person puts BESIDE the installer (or beside the setup scripts) to set up a PC without questions:
#   { "key": "<shared key, 12 to 256 characters>", "tcpPort": 5050, "udpPort": 5051, "allowedRoots": ["C:\\Users"],
#     "accessKeys": [ { "name": "Room 12 teacher", "role": "Teacher", "key": "..." } ] }
# Every entry is optional. The setup only READS it: nothing in Classroom Manager writes this file, generates a key into it or offers to
# "save the key on the kit" (T-226). It holds secrets; keep it off shared drives. A kit-settings.json (and access-keys.json) of an earlier
# version is still read when there is no classroom-kit.json, and is never written.

function Get-CmKitFilePath {
    <# The kit file in the folder of the setup scripts (classroom-kit.json, else a legacy kit-settings.json), or $null. #>
    param([string]$KitDeployDir)
    if (-not $KitDeployDir) { return $null }
    foreach ($name in "classroom-kit.json", "kit-settings.json") {
        $file = Join-Path $KitDeployDir $name
        if (Test-Path -LiteralPath $file -PathType Leaf) { return $file }
    }
    return $null
}

function Get-CmKit {
    <# The contents of the kit file as an object, or $null when there is none or it is not valid JSON. #>
    param([string]$KitDeployDir)
    $file = Get-CmKitFilePath $KitDeployDir
    if (-not $file) { return $null }
    try {
        $kit = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        if ($kit -is [System.Management.Automation.PSCustomObject]) { return $kit }
    } catch { }
    return $null
}

function Get-CmKitKey {
    <# The shared key in the kit file, or $null (also when the key in it is not acceptable). #>
    param([string]$KitDeployDir)
    $kit = Get-CmKit $KitDeployDir
    if ($kit -and $kit.PSObject.Properties["key"] -and -not (Test-CmKey $kit.key)) { return [string]$kit.key }
    return $null
}

function Get-CmKitAccessKeys {
    <# The named access keys of the kit ("accessKeys" in classroom-kit.json, else a legacy access-keys.json beside it), checked, in the
       form client.json stores them; $null when the kit has none. Throws when a key in it is not acceptable. #>
    param([string]$KitDeployDir)
    $kit = Get-CmKit $KitDeployDir
    if ($kit -and $kit.PSObject.Properties["accessKeys"] -and @($kit.accessKeys).Count -gt 0) { return , (Read-CmAccessKeyEntries @($kit.accessKeys)) }
    $legacy = Join-Path $KitDeployDir "access-keys.json"
    if ($KitDeployDir -and (Test-Path -LiteralPath $legacy -PathType Leaf)) { return , (Read-CmAccessKeys $legacy) }
    return $null
}

function Get-CmHostKeyPath { Join-Path $env:APPDATA "ClassroomManager\host.json" }

function Get-CmHostKey {
    <# The key saved in the teacher app's settings for the Windows user running setup, or $null. #>
    $file = Get-CmHostKeyPath
    if (-not (Test-Path $file)) { return $null }
    try {
        $s = Get-Content $file -Raw | ConvertFrom-Json
        if ($s.PSObject.Properties["psk"] -and $s.psk) { return [string]$s.psk }
    } catch { }
    return $null
}

function Set-CmHostKey {
    <# Writes the key into the teacher app's settings (host.json) for the Windows user running setup,
       keeping the saved computers, rooms and everything else. Close the teacher app first. #>
    param([Parameter(Mandatory)][string]$Key)
    $reason = Test-CmKey $Key
    if ($reason) { throw $reason }
    $file = Get-CmHostKeyPath
    # Only the "psk" value is replaced in the file text, so the computer list, rooms and schedules are
    # kept byte for byte (a PowerShell JSON round-trip could reshape them).
    $literal = (ConvertTo-Json $Key -Compress)
    if (Test-Path $file) {
        $json = [IO.File]::ReadAllText($file)
        $pskValue = [regex]'"psk"\s*:\s*"(?:[^"\\]|\\.)*"'
        $evaluator = [System.Text.RegularExpressions.MatchEvaluator]{ param($m) '"psk":' + $literal }
        if ($pskValue.IsMatch($json)) { $json = $pskValue.Replace($json, $evaluator, 1) }
        elseif ($json -match '^\s*\{\s*\}\s*$') { $json = '{"psk":' + $literal + '}' }
        else {
            $open = [regex]'^\s*\{'
            $json = $open.Replace($json, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) '{"psk":' + $literal + ',' }, 1)
        }
    } else {
        New-Item -ItemType Directory -Force -Path (Split-Path $file -Parent) | Out-Null
        $json = '{"psk":' + $literal + '}'
    }
    [IO.File]::WriteAllText($file, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-CmLog "Shared key saved in the teacher app's settings for Windows user $env:USERNAME."
}

function Get-CmPublicNetworks {
    try { @(Get-NetConnectionProfile -ErrorAction Stop | Where-Object { $_.NetworkCategory -eq "Public" }) }
    catch { @() }
}

function Set-CmNetworkPrivate {
    <# Switches every connected network that Windows marks "Public" to "Private", so the Domain/Private
       firewall rules apply. Only do this on a trusted school network. #>
    $public = Get-CmPublicNetworks
    if (-not $public) { Write-CmLog "No networks are set to Public."; return }
    foreach ($p in $public) {
        Set-NetConnectionProfile -InterfaceIndex $p.InterfaceIndex -NetworkCategory Private
        Write-CmLog "Network `"$($p.Name)`" ($($p.InterfaceAlias)) set to Private."
    }
}

function Write-CmNetworkWarning {
    foreach ($p in (Get-CmPublicNetworks)) {
        Write-CmLog "WARNING: network `"$($p.Name)`" is Public - the firewall rules only allow Domain/Private networks, so the connection will be blocked. Use the 'Set network to Private' option or tools\Set-NetworkPrivate.ps1."
    }
}

# =================================================================================
#  CLIENT (student PC)
# =================================================================================

function Get-CmClientStatus {
    $exe = Join-Path $script:ClientDir $script:ClientExeName
    $svc = Get-Service $script:ClientService -ErrorAction SilentlyContinue
    $cfgPath = Join-Path $script:ClientDataDir "client.json"
    $cfg = $null
    if (Test-Path $cfgPath) { try { $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json } catch { } }

    $roots = @("C:\Users"); $tcp = 5050; $udp = 5051; $hasKey = $false
    if ($cfg) {
        if ($cfg.PSObject.Properties["allowedRoots"] -and $cfg.allowedRoots) { $roots = @($cfg.allowedRoots) }
        if ($cfg.PSObject.Properties["tcpPort"] -and $cfg.tcpPort) { $tcp = [int]$cfg.tcpPort }
        if ($cfg.PSObject.Properties["udpPort"] -and $cfg.udpPort) { $udp = [int]$cfg.udpPort }
        $hasKey = [bool]($cfg.PSObject.Properties["psk"] -and $cfg.psk)
    }
    $version = Get-CmFileVersion $exe
    [pscustomobject]@{
        Component     = "Client"
        Installed     = (Test-Path $exe) -or [bool]$svc
        FilesPresent  = (Test-Path $exe)
        Version       = $version
        InstallDir    = $script:ClientDir
        ServiceState  = $(if ($svc) { $svc.Status.ToString() } else { "Not installed" })
        ConfigPresent = [bool]$cfg
        HasKey        = $hasKey
        TcpPort       = $tcp
        UdpPort       = $udp
        UseTls        = $true     # TLS cannot be turned off any more; a useTls line in client.json is ignored by the client
        AllowedRoots  = $roots
        HelperCount   = @(Get-Process ClassroomClient -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 }).Count
        PublicNetwork = [bool](Get-CmPublicNetworks)
    }
}

# ---- Language --------------------------------------------------------------------
# The setup window and console speak Lithuanian on a Lithuanian Windows and English elsewhere; the
# texts are in SetupText.lt.psd1 beside this module, keyed by the English text. Log lines stay English.
$script:Texts = @{}
$script:Language = "en"

function Set-CmLanguage {
    <# "lt" or "en"; empty follows the Windows display language. #>
    param([string]$Language = "")
    if (-not $Language) {
        $Language = $(if ([System.Globalization.CultureInfo]::CurrentUICulture.Name -like "lt*") { "lt" } else { "en" })
    }
    $script:Language = $Language
    $script:Texts = @{}
    $file = Join-Path $PSScriptRoot "SetupText.$Language.psd1"
    if ($Language -ne "en" -and (Test-Path $file)) {
        try { $script:Texts = Import-PowerShellDataFile $file -ErrorAction Stop } catch { $script:Texts = @{} }
    }
}

function Get-CmLanguage { $script:Language }

function Get-CmText {
    <# The text in the chosen language, with {0}, {1}... filled in. A text with no translation stays English. #>
    param([string]$Text, [object[]]$Values = @())
    $template = $(if ($script:Texts -and $script:Texts.ContainsKey($Text)) { $script:Texts[$Text] } else { $Text })
    if ($Values.Count -gt 0) { return ($template -f $Values) }
    return $template
}

Set-CmLanguage

function Format-CmClientStatus {
    param($Status)
    if (-not $Status.Installed) { return (Get-CmText "Client: not installed on this PC.") }
    $v = $(if ($Status.Version) { $Status.Version } else { Get-CmText "unknown version" })
    $lines = @(
        (Get-CmText "Client {0} in {1}" @($v, $Status.InstallDir)),
        (Get-CmText "Service: {0}; user helpers running: {1}" @($Status.ServiceState, $Status.HelperCount)),
        (Get-CmText "Ports: TCP {0}, UDP {1}; TLS: always on; shared key: {2}" @($Status.TcpPort, $Status.UdpPort,
            $(if ($Status.HasKey) { Get-CmText "set" } else { Get-CmText "MISSING" }))),
        (Get-CmText "Allowed folders: {0}" @($Status.AllowedRoots -join ', '))
    )
    if (-not $Status.FilesPresent) { $lines += Get-CmText "Program files are missing - run Repair with a source folder." }
    if ($Status.PublicNetwork) { $lines += Get-CmText "A network is set to Public - connections will be blocked by the firewall." }
    return ($lines -join "`r`n")
}

function Stop-CmClient {
    $svc = Get-Service $script:ClientService -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne "Stopped") {
        Write-CmLog "Stopping the client service..."
        Stop-Service $script:ClientService -Force -ErrorAction SilentlyContinue
        try { $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(20)) } catch { }
    }
    Get-Process ClassroomClient -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    Unregister-ScheduledTask -TaskName "ClassroomManagerClient" -Confirm:$false -ErrorAction SilentlyContinue
}

function Set-CmClientAcl {
    $staging = Join-Path $script:ClientDataDir "Staging"
    New-Item -ItemType Directory -Force -Path $script:ClientDataDir, $staging | Out-Null
    # SYSTEM + Administrators only, so students cannot read the key in client.json.
    # Users may list this folder and read Staging (uploaded installers / wallpapers).
    icacls $script:ClientDataDir /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" | Out-Null
    icacls $script:ClientDataDir /grant "*S-1-5-32-545:(RX)" | Out-Null
    icacls $staging /grant "*S-1-5-32-545:(OI)(CI)RX" | Out-Null
}

# ---- Named access keys ---------------------------------------------------------------
#
# Besides the lab's shared key, which always has the Admin role, a client accepts named keys that are
# limited to a role (Teacher or Admin). They are normally added from the teacher app (Access keys...),
# but a kit can carry them so that every PC is installed with them: put them as "accessKeys" in the classroom-kit.json next to
# these scripts (shaped like access-keys.json.example). That file holds secrets; keep it off shared drives.

function Read-CmAccessKeys {
    <# Reads and checks an access-keys.json. Returns the entries in the form client.json stores them. #>
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path $Path)) { throw "Access keys file not found: $Path" }
    # Through a variable first: Windows PowerShell hands a JSON array down the pipeline as one object,
    # so wrapping the pipeline itself in @() would give a single entry holding the whole list.
    try { $parsed = Get-Content $Path -Raw | ConvertFrom-Json } catch { throw "Access keys file is not valid JSON: $Path" }
    return , (Read-CmAccessKeyEntries @($parsed))
}

function Read-CmAccessKeyEntries {
    <# Checks a list of named access keys (name, role, key) and returns them in the form client.json stores them. #>
    param([Parameter(Mandatory)][object[]]$Entries)
    $entries = @($Entries)
    $result = @()
    $names = @{}
    $keys = @{}
    foreach ($entry in $entries) {
        $name = [string]$entry.name
        $role = [string]$entry.role
        $key = [string]$entry.key
        if ([string]::IsNullOrWhiteSpace($name)) { throw "An access key has no name." }
        if ($name -ne $name.Trim()) { throw "The access key name '$name' must not start or end with spaces." }
        if ($name.Length -gt 40) { throw "The access key name '$name' is longer than 40 characters." }
        if ($name -match '[\x00-\x1F\x7F]') { throw "An access key name contains control characters." }
        if ($name -ieq "Lab key") { throw "'Lab key' is reserved for the lab's original shared key." }
        if ($role -notin @("Teacher", "Admin")) { throw "Access key '$name': the role must be Teacher or Admin." }
        if ($key -like "replace-with-*") { throw "Access key '$name' still has the placeholder key from the example file. Generate a real one." }
        $problem = Test-CmKey $key
        if ($problem) { throw "Access key '$name': $($problem -replace '^The shared key', 'the key')" }
        if ($names.ContainsKey($name.ToLowerInvariant())) { throw "Two access keys are named '$name'." }
        if ($keys.ContainsKey($key)) { throw "Access keys '$($keys[$key])' and '$name' use the same key; every key must be different." }
        $names[$name.ToLowerInvariant()] = $true
        $keys[$key] = $name
        # Role is stored capitalised the way the client writes it.
        $result += [ordered]@{ name = $name; role = $(if ($role -ieq "Admin") { "Admin" } else { "Teacher" }); psk = $key }
    }
    return , $result
}

function Write-CmClientConfig {
    # Merges the given values into client.json, keeping any other settings already in it.
    # -AccessKeys: $null leaves the named keys alone; a list (even an empty one) replaces them.
    # -Tls is kept only so that scripts written before TLS became mandatory still run: "Off" is refused with a log line, anything else is ignored.
    param([string]$Key, [int]$TcpPort, [int]$UdpPort, [string[]]$AllowedRoots, [string]$Tls, [object[]]$AccessKeys = $null)
    $path = Join-Path $script:ClientDataDir "client.json"
    $cfg = [ordered]@{ psk = ""; tcpPort = 5050; udpPort = 5051; allowedRoots = @("C:\Users") }
    if (Test-Path $path) {
        try {
            $old = Get-Content $path -Raw | ConvertFrom-Json
            foreach ($p in $old.PSObject.Properties) { $cfg[$p.Name] = $p.Value }
        } catch { Write-CmLog "Existing client.json could not be read; writing a new one." }
    }
    if ($Key)          { $cfg["psk"] = $Key }
    if ($TcpPort -gt 0) { $cfg["tcpPort"] = $TcpPort }
    if ($UdpPort -gt 0) { $cfg["udpPort"] = $UdpPort }
    if ($AllowedRoots) { $cfg["allowedRoots"] = @($AllowedRoots) }
    # TLS is always on. An old "useTls" line (false or true) is removed so that the file says nothing that is no longer true.
    $cfg.Remove("useTls")
    if ($Tls -eq "Off") { Write-CmLog "TLS cannot be turned off any more; the request to turn it off was ignored." }
    if (-not $cfg["psk"]) { throw "No shared key is set. Enter the classroom key (at least 12 characters)." }
    $keyProblem = Test-CmKey ([string]$cfg["psk"])
    if ($keyProblem) { throw $keyProblem }
    if ($null -ne $AccessKeys) {
        foreach ($named in $AccessKeys) {
            if ($named.psk -eq $cfg["psk"]) { throw "Access key '$($named.name)' is the same as the lab's shared key. A limited key must be different, or it would have full control." }
        }
        $cfg["accessKeys"] = @($AccessKeys)
        Write-CmLog "Named access keys: $(if ($AccessKeys.Count -eq 0) { 'none' } else { ($AccessKeys | ForEach-Object { "$($_.name) ($($_.role))" }) -join ', ' })."
    }

    Set-CmClientAcl   # write after the ACLs so the file inherits the restricted permissions
    ($cfg | ConvertTo-Json -Depth 5) | Set-Content $path -Encoding UTF8
    Write-CmLog "Settings saved (TCP $($cfg['tcpPort']), UDP $($cfg['udpPort']), TLS always on)."
    return $cfg
}

function Set-CmClientFirewall {
    param([int]$TcpPort, [int]$UdpPort)
    foreach ($n in $script:ClientFwRules) { Remove-NetFirewallRule -DisplayName $n -ErrorAction SilentlyContinue }
    New-NetFirewallRule -DisplayName "Classroom Manager TCP" -Direction Inbound -Protocol TCP -LocalPort $TcpPort `
        -Action Allow -Profile Domain,Private | Out-Null
    New-NetFirewallRule -DisplayName "Classroom Manager UDP" -Direction Inbound -Protocol UDP -LocalPort $UdpPort `
        -Action Allow -Profile Domain,Private | Out-Null
    # Ping replies, so the host can tell "PC is on but the client is not reachable" from "PC is off".
    New-NetFirewallRule -DisplayName "Classroom Manager ICMP" -Direction Inbound -Protocol ICMPv4 -IcmpType 8 `
        -Action Allow -Profile Domain,Private | Out-Null
    Write-CmLog "Firewall rules set (TCP $TcpPort, UDP $UdpPort, ping; Domain and Private networks)."
}

function Register-CmClientService {
    $exe = Join-Path $script:ClientDir $script:ClientExeName
    $imagePath = "`"$exe`" --service"
    if (Get-Service $script:ClientService -ErrorAction SilentlyContinue) {
        # Re-point an existing service instead of deleting it (a delete can stay "pending" until reboot).
        Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$($script:ClientService)" -Name ImagePath -Value $imagePath
        Set-Service $script:ClientService -StartupType Automatic
    } else {
        New-Service -Name $script:ClientService -BinaryPathName $imagePath -DisplayName "Classroom Manager Client" `
            -Description "Lets the teacher's Classroom Manager host manage this computer." -StartupType Automatic | Out-Null
    }
    sc.exe failure $script:ClientService reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
    Write-CmLog "Service registered (automatic start, restarts on failure)."
}

function Set-CmHelperRunKey {
    $exe = Join-Path $script:ClientDir $script:ClientExeName
    Set-ItemProperty -Path $script:RunKey -Name $script:ClientRunValue -Value "`"$exe`""
}

function Start-CmClient {
    Start-Service $script:ClientService
    $svc = Get-Service $script:ClientService
    try { $svc.WaitForStatus("Running", [TimeSpan]::FromSeconds(20)) } catch { }
    if ((Get-Service $script:ClientService).Status -ne "Running") {
        throw "The client service did not start. See $($script:ClientDataDir)\client.log."
    }
    Write-CmLog "Service running. User helpers start within about a minute (or at next sign-in)."
}

function Complete-CmClientRegistration {
    Copy-CmSetupFolder $script:ClientDir
    Register-CmArpEntry -Key $script:ClientArpKey -Component "Client" -DisplayName "Classroom Manager Client" `
        -InstallDir $script:ClientDir -Exe (Join-Path $script:ClientDir $script:ClientExeName)
}

function Install-CmClient {
    param(
        [Parameter(Mandatory)][string]$SourceDir,
        [string]$Key,
        [int]$TcpPort = 5050,
        [int]$UdpPort = 5051,
        [string[]]$AllowedRoots = @("C:\Users"),
        [ValidateSet("", "On", "Off")][string]$Tls = "On",
        [switch]$SetNetworkPrivate,
        # Named keys to install with the client; when omitted, the "accessKeys" of classroom-kit.json beside these scripts are used if present.
        [object[]]$AccessKeys = $null
    )
    Assert-CmAdmin
    if ($null -eq $AccessKeys) {
        $kitKeys = Get-CmKitAccessKeys $PSScriptRoot
        if ($kitKeys) {
            $AccessKeys = $kitKeys
            Write-CmLog "Installing $($AccessKeys.Count) named access key(s) from the kit file beside the setup."
        }
    }
    $SourceDir = Resolve-CmSource $SourceDir $script:ClientExeName
    Write-CmLog "Installing Classroom Manager Client $(Get-CmFileVersion (Join-Path $SourceDir $script:ClientExeName))..."
    Test-CmChecksums $SourceDir
    $updateSigner = Test-CmSourceSignature $SourceDir $script:ClientExeName
    Confirm-CmRuntime $SourceDir

    Stop-CmClient
    Copy-CmFiles $SourceDir $script:ClientDir
    Write-CmLog "Program files copied to $($script:ClientDir)."
    $cfg = Write-CmClientConfig -Key $Key -TcpPort $TcpPort -UdpPort $UdpPort -AllowedRoots $AllowedRoots -Tls $Tls -AccessKeys $AccessKeys
    Set-CmClientUpdateSigner $updateSigner
    Set-CmClientFirewall -TcpPort $cfg["tcpPort"] -UdpPort $cfg["udpPort"]
    Register-CmClientService
    Set-CmHelperRunKey
    Complete-CmClientRegistration
    if ($SetNetworkPrivate) { Set-CmNetworkPrivate }
    Start-CmClient
    Write-CmNetworkWarning
    Write-CmLog "Client installed. Enter the same shared key in the teacher app (Settings)."
}

function Update-CmClient {
    param([Parameter(Mandatory)][string]$SourceDir, [switch]$Force)
    Assert-CmAdmin
    $status = Get-CmClientStatus
    if (-not $status.Installed) { throw "The client is not installed. Use Install." }
    $SourceDir = Resolve-CmSource $SourceDir $script:ClientExeName
    $newVersion = Get-CmFileVersion (Join-Path $SourceDir $script:ClientExeName)
    $order = Test-CmReleaseOrder -Installed (Get-CmReleaseClaim (Join-Path $script:ClientDir $script:ClientExeName)) -InstalledVersion $status.Version `
        -New (Get-CmReleaseClaim (Join-Path $SourceDir $script:ClientExeName)) -NewVersion $newVersion -Force:$Force
    if ($order.Problem) { throw $order.Problem }
    if ($status.Version -and $newVersion -and -not $Force -and -not $order.Handled) {
        if ($newVersion -lt $status.Version) { throw "The source ($newVersion) is older than the installed client ($($status.Version)). Tick 'Allow older/same version' to roll back." }
        if ($newVersion -eq $status.Version) { throw "Version $newVersion is already installed. Use Repair to reinstall the same files." }
    }
    Write-CmLog "Updating client $($status.Version) -> $newVersion (settings are kept)..."
    Test-CmChecksums $SourceDir
    $updateSigner = Test-CmSourceSignature $SourceDir $script:ClientExeName
    Confirm-CmRuntime $SourceDir   # before the running client is stopped: a build that cannot start must not replace it

    Stop-CmClient
    $backup = New-CmBackup $script:ClientDir
    try {
        Copy-CmFiles $SourceDir $script:ClientDir
        Register-CmClientService
        Set-CmHelperRunKey
        Start-CmClient
        Set-CmClientUpdateSigner $updateSigner
    } catch {
        Write-CmLog "Update failed: $($_.Exception.Message)"
        Stop-CmClient
        Restore-CmBackup $backup $script:ClientDir
        Start-Service $script:ClientService -ErrorAction SilentlyContinue
        throw
    } finally {
        if ($backup) { Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Complete-CmClientRegistration
    Write-CmLog "Client updated to $newVersion."
}

function Set-CmClientConfig {
    <# Modify: change the shared key, ports, allowed folders or named access keys. Empty/0/$null values stay unchanged. #>
    param([string]$Key, [int]$TcpPort, [int]$UdpPort, [string[]]$AllowedRoots, [ValidateSet("", "On", "Off")][string]$Tls = "", [object[]]$AccessKeys = $null)
    Assert-CmAdmin
    if (-not (Get-CmClientStatus).Installed) { throw "The client is not installed. Use Install." }
    $cfg = Write-CmClientConfig -Key $Key -TcpPort $TcpPort -UdpPort $UdpPort -AllowedRoots $AllowedRoots -Tls $Tls -AccessKeys $AccessKeys
    Set-CmClientFirewall -TcpPort $cfg["tcpPort"] -UdpPort $cfg["udpPort"]
    Stop-CmClient
    Start-CmClient
    if ($Key) { Write-CmLog "Shared key changed - enter the same key in the teacher app (Settings)." }
}

function Repair-CmClient {
    <# Fix: reinstalls files (when a source is given) and re-applies folder permissions, firewall rules,
       service registration, the logon helper and the Apps entry. Keeps settings. #>
    param([string]$SourceDir, [switch]$SetNetworkPrivate)
    Assert-CmAdmin
    $exe = Join-Path $script:ClientDir $script:ClientExeName
    if ($SourceDir) {
        $SourceDir = Resolve-CmSource $SourceDir $script:ClientExeName
        Test-CmChecksums $SourceDir
        $updateSigner = Test-CmSourceSignature $SourceDir $script:ClientExeName
    } elseif (-not (Test-Path $exe)) {
        throw "Program files are missing. Choose the publish-client folder as the source to repair."
    }
    # A removed runtime is one of the things Repair has to be able to fix.
    Confirm-CmRuntime $(if ($SourceDir) { $SourceDir } else { $script:ClientDir })
    $cfgPath = Join-Path $script:ClientDataDir "client.json"
    if (-not (Test-Path $cfgPath)) { throw "Settings (client.json) are missing. Use Modify and enter the shared key, or Install again." }

    Stop-CmClient
    if ($SourceDir) { Copy-CmFiles $SourceDir $script:ClientDir; Set-CmClientUpdateSigner $updateSigner; Write-CmLog "Program files reinstalled." }
    $cfg = Write-CmClientConfig          # re-validates the key and re-applies the folder permissions
    Set-CmClientFirewall -TcpPort $cfg["tcpPort"] -UdpPort $cfg["udpPort"]
    Register-CmClientService
    Set-CmHelperRunKey
    Complete-CmClientRegistration
    if ($SetNetworkPrivate) { Set-CmNetworkPrivate }
    Start-CmClient
    Write-CmNetworkWarning
    Write-CmLog "Client repaired."
}

function Uninstall-CmClient {
    param([switch]$KeepData)
    Assert-CmAdmin
    Write-CmLog "Removing Classroom Manager Client..."
    Stop-CmClient
    Invoke-CmClientCleanup
    if (Get-Service $script:ClientService -ErrorAction SilentlyContinue) { sc.exe delete $script:ClientService | Out-Null }
    Remove-ItemProperty -Path $script:RunKey -Name $script:ClientRunValue -ErrorAction SilentlyContinue
    foreach ($n in $script:ClientFwRules) { Remove-NetFirewallRule -DisplayName $n -ErrorAction SilentlyContinue }
    Remove-Item $script:ClientArpKey -Recurse -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    Remove-Item $script:ClientDir -Recurse -Force -ErrorAction SilentlyContinue
    if ($KeepData) {
        Write-CmLog "Kept settings, logs and certificate in $($script:ClientDataDir) (a later install reuses them)."
    } else {
        Remove-Item $script:ClientDataDir -Recurse -Force -ErrorAction SilentlyContinue
        # What the per-user helper kept for each student (wallpaper and lock screen state and pictures).
        # The teacher app keeps its settings in AppData\Roaming, which is not touched here.
        foreach ($p in (Get-ChildItem (Join-Path $env:SystemDrive "Users") -Directory -ErrorAction SilentlyContinue)) {
            Remove-Item (Join-Path $p.FullName "AppData\Local\ClassroomManager") -Recurse -Force -ErrorAction SilentlyContinue
        }
        Write-CmLog "Removed settings, logs, certificate, staged files and each user's Classroom Manager files."
    }
    Remove-CmHostsBlock
    if (Test-Path $script:ClientDir) { Write-CmLog "Some files in $($script:ClientDir) were in use; they go after a restart or can be deleted by hand." }
    Write-CmLog "Client removed."
}

function Invoke-CmClientCleanup {
    # Lets the Client put back what would otherwise stay changed after it is gone: devices and
    # policies an exam turned off, and the browsers' secure DNS setting. An older Client does not
    # know the switch and would start as the per-user helper, so it is given a time limit.
    $exe = Join-Path $script:ClientDir $script:ClientExeName
    if (-not (Test-Path $exe)) { return }
    try {
        $p = Start-Process -FilePath $exe -ArgumentList "--uninstall-cleanup" -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit(20000)) {
            $p.Kill()
            Write-CmLog "The installed Client is too old to undo exam device controls by itself; check the camera and USB settings by hand if an exam was running."
        } elseif ($p.ExitCode -eq 0) {
            Write-CmLog "Exam device controls and browser DNS settings were put back."
        }
    } catch { Write-CmLog "Clean-up before removal did not run: $($_.Exception.Message)" }
    Get-Process ClassroomClient -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

function Remove-CmHostsBlock {
    # Removes the website block the client wrote into the hosts file (between its own marker lines only).
    $hosts = Join-Path $env:SystemRoot "System32\drivers\etc\hosts"
    if (-not (Test-Path $hosts)) { return }
    $start = "# >>> ClassroomManager block start - do not edit between these lines >>>"
    $end   = "# <<< ClassroomManager block end <<<"
    $lines = Get-Content $hosts
    if (-not ($lines | Where-Object { $_.Trim() -eq $start })) { return }
    $keep = New-Object System.Collections.Generic.List[string]
    $inBlock = $false
    foreach ($l in $lines) {
        $t = $l.Trim()
        if ($t -eq $start) { $inBlock = $true; continue }
        if ($t -eq $end) { $inBlock = $false; continue }
        if (-not $inBlock) { $keep.Add($l) }
    }
    Set-Content -Path $hosts -Value $keep -Encoding ASCII
    ipconfig /flushdns | Out-Null
    Write-CmLog "Removed the Classroom Manager website block from the hosts file."
}

# =================================================================================
#  HOST (teacher PC)
# =================================================================================

function Get-CmHostDir {
    $dir = $null
    if (Test-Path $script:HostStateKey) { $dir = (Get-ItemProperty $script:HostStateKey -ErrorAction SilentlyContinue).InstallDir }
    if (-not $dir) { $dir = $script:HostDefaultDir }
    return $dir
}

function Get-CmHostLinks {
    @(
        (Join-Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs" $script:HostLinkName),
        (Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) $script:HostLinkName)
    )
}

function Get-CmHostOldLinks {
    @(
        (Join-Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs" $script:HostOldLinkName),
        (Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) $script:HostOldLinkName)
    )
}

function Get-CmHostStatus {
    $dir = Get-CmHostDir
    $exe = Join-Path $dir $script:HostExeName
    $links = Get-CmHostLinks
    $run = (Get-ItemProperty $script:RunKey -ErrorAction SilentlyContinue).PSObject.Properties[$script:HostRunValue]
    [pscustomobject]@{
        Component        = "Host"
        Installed        = (Test-Path $exe)
        Version          = (Get-CmFileVersion $exe)
        InstallDir       = $dir
        Running          = [bool](Get-Process ClassroomHost -ErrorAction SilentlyContinue)
        DesktopShortcut  = ((Test-Path $links[1]) -or (Test-Path (Get-CmHostOldLinks)[1]))
        StartWithWindows = [bool]$run
        OnPath           = $(try { Test-CmPathContains (Get-CmRawPath) $dir } catch { $false })
        FirewallRule     = [bool](Get-NetFirewallRule -DisplayName $script:HostFwRule -ErrorAction SilentlyContinue)
    }
}

function Format-CmHostStatus {
    param($Status)
    if (-not $Status.Installed) { return (Get-CmText "Host: not installed on this PC.") }
    $v = $(if ($Status.Version) { $Status.Version } else { Get-CmText "unknown version" })
    $yes = Get-CmText "yes"; $no = Get-CmText "no"
    return (@(
        $(if ($Status.Running) { Get-CmText "Host {0} in {1} (running now)" @($v, $Status.InstallDir) } else { Get-CmText "Host {0} in {1}" @($v, $Status.InstallDir) }),
        (Get-CmText "Desktop shortcut: {0}; start with Windows: {1}; on PATH: {2}; firewall rule: {3}" @(
            $(if ($Status.DesktopShortcut) { $yes } else { $no }), $(if ($Status.StartWithWindows) { $yes } else { $no }),
            $(if ($Status.OnPath) { $yes } else { $no }), $(if ($Status.FirewallRule) { $yes } else { Get-CmText "MISSING" }))),
        (Get-CmText "Teacher settings (shared key, computers, rooms) are stored per Windows user in %AppData%\ClassroomManager.")
    ) -join "`r`n")
}

function Stop-CmHost {
    $p = Get-Process ClassroomHost -ErrorAction SilentlyContinue
    if ($p) {
        Write-CmLog "Closing the running teacher app..."
        $p | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    }
}

function Set-CmHostShortcuts {
    param([string]$Dir, [bool]$Desktop)
    $exe = Join-Path $Dir $script:HostExeName
    $links = Get-CmHostLinks
    foreach ($old in (Get-CmHostOldLinks)) { Remove-Item $old -Force -ErrorAction SilentlyContinue }
    $shell = New-Object -ComObject WScript.Shell
    $targets = @($links[0])
    if ($Desktop) { $targets += $links[1] } else { Remove-Item $links[1] -Force -ErrorAction SilentlyContinue }
    foreach ($linkPath in $targets) {
        $s = $shell.CreateShortcut($linkPath)
        $s.TargetPath = $exe
        $s.WorkingDirectory = $Dir
        $s.IconLocation = "$exe,0"
        $s.Description = "Classroom Manager teacher console"
        $s.Save()
    }
    Write-CmLog "Start Menu shortcut set$(if ($Desktop) { '; Desktop shortcut set' } else { '; no Desktop shortcut' })."
}

function Set-CmHostStartup {
    param([string]$Dir, [bool]$Enabled)
    if ($Enabled) {
        Set-ItemProperty -Path $script:RunKey -Name $script:HostRunValue -Value "`"$(Join-Path $Dir $script:HostExeName)`""
        Write-CmLog "The teacher app starts when anyone signs in."
    } else {
        Remove-ItemProperty -Path $script:RunKey -Name $script:HostRunValue -ErrorAction SilentlyContinue
    }
}

function Set-CmHostFirewall {
    # Lets discovery replies from student PCs reach the teacher app without a first-run firewall prompt.
    param([string]$Dir)
    Remove-NetFirewallRule -DisplayName $script:HostFwRule -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $script:HostFwRule -Direction Inbound -Program (Join-Path $Dir $script:HostExeName) `
        -Action Allow -Profile Domain,Private | Out-Null
    Write-CmLog "Firewall rule set for the teacher app (Domain and Private networks)."
}

# ---- PATH: the install folder holds ClassroomCli -----------------------------------
#
# The machine PATH is read and written through the registry without expanding it, so entries such
# as %SystemRoot%\system32 stay as they are. The functions that work out the new value take and
# return plain text, and the registry key is a parameter, so both can be tried on a scratch key.

function Get-CmPathEntryKey {
    # The form two PATH entries are compared in: no quotes, variables expanded, no trailing backslash.
    param([string]$Entry)
    if (-not $Entry) { return "" }
    return [Environment]::ExpandEnvironmentVariables($Entry.Trim().Trim('"')).TrimEnd('\').ToLowerInvariant()
}

function Test-CmPathContains {
    param([string]$PathValue, [string]$Dir)
    $wanted = Get-CmPathEntryKey $Dir
    if (-not $wanted -or -not $PathValue) { return $false }
    return [bool](@($PathValue -split ';' | Where-Object { (Get-CmPathEntryKey $_) -eq $wanted }).Count)
}

function Add-CmPathEntry {
    # Returns the PATH text with $Dir at the end, unchanged when it is already there.
    param([string]$PathValue, [string]$Dir)
    if (Test-CmPathContains $PathValue $Dir) { return $PathValue }
    if (-not $PathValue) { return $Dir }
    return $PathValue.TrimEnd(';') + ';' + $Dir
}

function Remove-CmPathEntry {
    # Returns the PATH text without $Dir; every other entry keeps its text and its place.
    param([string]$PathValue, [string]$Dir)
    if (-not (Test-CmPathContains $PathValue $Dir)) { return $PathValue }
    $wanted = Get-CmPathEntryKey $Dir
    return (@($PathValue -split ';' | Where-Object { (Get-CmPathEntryKey $_) -ne $wanted }) -join ';')
}

function Get-CmRawPath {
    param([string]$Key = $script:MachineEnvKey)
    $item = Get-Item -LiteralPath $Key -ErrorAction Stop
    return [string]$item.GetValue("Path", "", [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
}

function Set-CmPathEntry {
    <# Adds $Dir to, or removes it from, the Path value under $Key. Returns $true when the value changed. #>
    param([Parameter(Mandatory)][string]$Dir, [bool]$Enabled, [string]$Key = $script:MachineEnvKey)
    $item = Get-Item -LiteralPath $Key -ErrorAction Stop
    $current = Get-CmRawPath $Key
    # An empty machine PATH means it could not be read properly; writing then would wipe it.
    if (-not $current -and $Key -eq $script:MachineEnvKey) { throw "The PATH of this PC could not be read; it was left unchanged." }
    $new = $(if ($Enabled) { Add-CmPathEntry $current $Dir } else { Remove-CmPathEntry $current $Dir })
    if ($new -ceq $current) { return $false }
    $kind = "ExpandString"
    if ($item.GetValueNames() -contains "Path") { $kind = "$($item.GetValueKind('Path'))" }
    Set-ItemProperty -LiteralPath $Key -Name "Path" -Value $new -Type $kind
    return $true
}

function Send-CmEnvironmentChanged {
    # Tells running programs (Explorer above all) that the environment changed, so a command prompt
    # opened afterwards sees the new PATH without signing out.
    try {
        if (-not ("ClassroomSetup.Native" -as [type])) {
            Add-Type -Namespace ClassroomSetup -Name Native -MemberDefinition '
                [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
                public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);'
        }
        $result = [UIntPtr]::Zero
        # HWND_BROADCAST, WM_SETTINGCHANGE, SMTO_ABORTIFHUNG
        [void][ClassroomSetup.Native]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, "Environment", 2, 2000, [ref]$result)
    } catch { }
}

function Set-CmHostPath {
    param([string]$Dir, [bool]$Enabled)
    try {
        $changed = Set-CmPathEntry -Dir $Dir -Enabled $Enabled
    } catch {
        Write-CmLog "PATH was not changed: $($_.Exception.Message)"
        return
    }
    if ($changed) { Send-CmEnvironmentChanged }
    if ($Enabled) { Write-CmLog "$Dir is on the PATH of this PC: ClassroomCli runs from any command prompt opened from now on." }
    elseif ($changed) { Write-CmLog "$Dir removed from the PATH of this PC." }
}

function Complete-CmHostRegistration {
    param([string]$Dir)
    New-Item -Path $script:HostStateKey -Force | Out-Null
    Set-ItemProperty -Path $script:HostStateKey -Name InstallDir -Value $Dir
    Copy-CmSetupFolder $Dir
    Register-CmArpEntry -Key $script:HostArpKey -Component "Host" -DisplayName "Classroom Manager" `
        -InstallDir $Dir -Exe (Join-Path $Dir $script:HostExeName)
}

function Install-CmHost {
    param(
        [Parameter(Mandatory)][string]$SourceDir,
        [string]$InstallDir,
        [bool]$DesktopShortcut = $true,
        [bool]$StartWithWindows = $false,
        [bool]$AddToPath = $true,
        [switch]$SetNetworkPrivate
    )
    Assert-CmAdmin
    $SourceDir = Resolve-CmSource $SourceDir $script:HostExeName
    if (-not $InstallDir) { $InstallDir = Get-CmHostDir }
    Write-CmLog "Installing Classroom Manager Host $(Get-CmFileVersion (Join-Path $SourceDir $script:HostExeName)) to $InstallDir..."
    Test-CmChecksums $SourceDir
    [void](Test-CmSourceSignature $SourceDir $script:HostExeName)
    Confirm-CmRuntime $SourceDir
    Stop-CmHost
    Copy-CmFiles $SourceDir $InstallDir
    Set-CmHostShortcuts $InstallDir $DesktopShortcut
    Set-CmHostStartup $InstallDir $StartWithWindows
    Set-CmHostPath $InstallDir $AddToPath
    Set-CmHostFirewall $InstallDir
    Complete-CmHostRegistration $InstallDir
    if ($SetNetworkPrivate) { Set-CmNetworkPrivate }
    Write-CmNetworkWarning
    Write-CmLog "Host installed. Start it and enter the classroom shared key in Settings."
}

function Assert-CmHostStartupProbeSupport {
    param([string]$Dir)
    $metadata = Join-Path $Dir 'RUNTIME.json'
    $runtime = if (Test-Path -LiteralPath $metadata) { Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json } else { $null }
    if (-not $runtime -or -not $runtime.PSObject.Properties['hostStartupProbe'] -or $runtime.hostStartupProbe -ne 1) {
        throw (Get-CmText "This Host source does not support verified startup. Use a newer release; no program files were replaced.")
    }
}

function New-CmHostProbeProcess {
    param([string]$Executable, [string]$ExpectedVersion)
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = "--startup-probe $ExpectedVersion"
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = 'Hidden'
    [Diagnostics.Process]::Start($start)
}

function Test-CmHostStartup {
    param([string]$Dir, [version]$ExpectedVersion)
    if (-not $ExpectedVersion -or $ExpectedVersion.Revision -lt 0) {
        throw (Get-CmText "The Host startup probe requires an exact four-part file version.")
    }
    $process = New-CmHostProbeProcess (Join-Path $Dir $script:HostExeName) $ExpectedVersion.ToString()
    try {
        if (-not $process.WaitForExit(30000)) {
            # This process was created solely for the probe. Never stop another Host.
            $process.Kill()
            [void]$process.WaitForExit(5000)
            throw (Get-CmText "The replacement Host startup probe timed out.")
        }
        if ($process.ExitCode -ne 0) {
            throw (Get-CmText "The replacement Host failed its expected-version startup probe.")
        }
    }
    finally { $process.Dispose() }
}

function Update-CmHost {
    param([Parameter(Mandatory)][string]$SourceDir, [switch]$Force)
    # Copy/restore cmdlet failures must enter the recovery path even when the caller
    # uses PowerShell's default Continue preference.
    $ErrorActionPreference = 'Stop'
    Assert-CmAdmin
    $status = Get-CmHostStatus
    if (-not $status.Installed) { throw "The teacher app is not installed. Use Install." }
    $SourceDir = Resolve-CmSource $SourceDir $script:HostExeName
    $newVersion = Get-CmFileVersion (Join-Path $SourceDir $script:HostExeName)
    $order = Test-CmReleaseOrder -Installed (Get-CmReleaseClaim (Join-Path (Get-CmHostDir) $script:HostExeName)) -InstalledVersion $status.Version `
        -New (Get-CmReleaseClaim (Join-Path $SourceDir $script:HostExeName)) -NewVersion $newVersion -Force:$Force
    if ($order.Problem) { throw $order.Problem }
    if ($status.Version -and $newVersion -and -not $Force -and -not $order.Handled) {
        if ($newVersion -lt $status.Version) { throw "The source ($newVersion) is older than the installed app ($($status.Version)). Tick 'Allow older/same version' to roll back." }
        if ($newVersion -eq $status.Version) { throw "Version $newVersion is already installed. Use Repair to reinstall the same files." }
    }
    Write-CmLog "Updating the teacher app $($status.Version) -> $newVersion (teacher settings are kept)..."
    Test-CmChecksums $SourceDir
    [void](Test-CmSourceSignature $SourceDir $script:HostExeName)
    Confirm-CmRuntime $SourceDir
    Assert-CmHostStartupProbeSupport $SourceDir
    if (-not $newVersion -or $newVersion.Revision -lt 0) {
        throw (Get-CmText "The Host startup probe requires an exact four-part file version.")
    }
    Stop-CmHost
    $backup = New-CmBackup $status.InstallDir
    if (-not $backup -or -not (Test-Path -LiteralPath $backup -PathType Container)) {
        throw (Get-CmText "The previous teacher app could not be backed up. No program files were replaced.")
    }
    $removeBackup = $false
    try {
        Copy-CmFiles $SourceDir $status.InstallDir
        Test-CmHostStartup $status.InstallDir $newVersion
        $removeBackup = $true
    }
    catch {
        $updateFailure = $_
        try {
            if (-not (Test-Path -LiteralPath $backup -PathType Container)) {
                throw (Get-CmText "The recovery backup is missing.")
            }
            Restore-CmBackup $backup $status.InstallDir -Strict
            $removeBackup = $true
        }
        catch {
            # Preserve the only known-good image. Do not erase it in finally or
            # report a failed restore as a successful update.
            Write-CmLog (Get-CmText "CRITICAL: teacher app rollback failed; recovery backup retained at {0}." @($backup))
            throw (Get-CmText "Teacher app update and rollback failed. Preserve the recovery backup at {0} and repair the installation before starting it." @($backup))
        }
        throw $updateFailure
    }
    finally {
        if ($removeBackup) { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Set-CmHostFirewall $status.InstallDir
    Complete-CmHostRegistration $status.InstallDir
    Write-CmLog "Teacher app updated to $newVersion."
}

function Set-CmHostOptions {
    <# Modify: desktop shortcut, start-with-Windows and the install folder on PATH. "" = leave unchanged. #>
    param([ValidateSet("", "On", "Off")][string]$DesktopShortcut = "", [ValidateSet("", "On", "Off")][string]$StartWithWindows = "",
          [ValidateSet("", "On", "Off")][string]$AddToPath = "")
    Assert-CmAdmin
    $status = Get-CmHostStatus
    if (-not $status.Installed) { throw "The teacher app is not installed. Use Install." }
    if ($DesktopShortcut) { Set-CmHostShortcuts $status.InstallDir ($DesktopShortcut -eq "On") }
    if ($StartWithWindows) { Set-CmHostStartup $status.InstallDir ($StartWithWindows -eq "On") }
    if ($AddToPath) { Set-CmHostPath $status.InstallDir ($AddToPath -eq "On") }
    Write-CmLog "Options saved. The shared key and ports are changed inside the app (Settings)."
}

function Repair-CmHost {
    param([string]$SourceDir, [switch]$SetNetworkPrivate)
    Assert-CmAdmin
    $status = Get-CmHostStatus
    $dir = $status.InstallDir
    if ($SourceDir) {
        $SourceDir = Resolve-CmSource $SourceDir $script:HostExeName
        Test-CmChecksums $SourceDir
        [void](Test-CmSourceSignature $SourceDir $script:HostExeName)
        Confirm-CmRuntime $SourceDir
        Stop-CmHost
        Copy-CmFiles $SourceDir $dir
        Write-CmLog "Program files reinstalled."
    } elseif (-not $status.Installed) {
        throw "Program files are missing. Choose the publish-host folder as the source to repair."
    } else {
        Confirm-CmRuntime $dir
    }
    Set-CmHostShortcuts $dir ($status.DesktopShortcut -or -not $status.Installed)
    if ($status.StartWithWindows) { Set-CmHostStartup $dir $true }
    # A first install through Repair gets the default; otherwise PATH stays as it was found.
    if (-not $status.Installed) { Set-CmHostPath $dir $true }
    Set-CmHostFirewall $dir
    Complete-CmHostRegistration $dir
    if ($SetNetworkPrivate) { Set-CmNetworkPrivate }
    Write-CmNetworkWarning
    Write-CmLog "Teacher app repaired."
}

function Uninstall-CmHost {
    param([switch]$RemoveSettings)
    Assert-CmAdmin
    $dir = Get-CmHostDir
    Write-CmLog "Removing Classroom Manager Host..."
    Stop-CmHost
    foreach ($l in @(Get-CmHostLinks) + @(Get-CmHostOldLinks)) { Remove-Item $l -Force -ErrorAction SilentlyContinue }
    Remove-ItemProperty -Path $script:RunKey -Name $script:HostRunValue -ErrorAction SilentlyContinue
    Remove-NetFirewallRule -DisplayName $script:HostFwRule -ErrorAction SilentlyContinue
    Set-CmHostPath $dir $false
    Remove-Item $script:HostArpKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $script:HostStateKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    if ($RemoveSettings) {
        # host.json holds the shared key and the computer list, one copy per Windows user.
        $profiles = Get-ChildItem (Join-Path $env:SystemDrive "Users") -Directory -ErrorAction SilentlyContinue
        foreach ($p in $profiles) {
            $d = Join-Path $p.FullName "AppData\Roaming\ClassroomManager"
            if (Test-Path (Join-Path $d "host.json")) {
                Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue
                Write-CmLog "Removed teacher settings for $($p.Name)."
            }
        }
    } else {
        Write-CmLog "Teacher settings in %AppData%\ClassroomManager were kept (they hold the shared key and computer list)."
    }
    Write-CmLog "Teacher app removed."
}

Export-ModuleMember -Function `
    Set-CmLanguage, Get-CmLanguage, Get-CmText, `
    Set-CmLogger, Write-CmLog, Test-CmAdmin, Set-CmNetworkPrivate, Get-CmPublicNetworks, `
    New-CmKey, Test-CmKey, Get-CmKitFilePath, Get-CmKit, Get-CmKitKey, Get-CmKitAccessKeys, Get-CmHostKey, Set-CmHostKey, `
    Get-CmClientStatus, Format-CmClientStatus, Install-CmClient, Update-CmClient, Set-CmClientConfig, Repair-CmClient, Uninstall-CmClient, `
    Get-CmHostStatus, Format-CmHostStatus, Install-CmHost, Update-CmHost, Set-CmHostOptions, Repair-CmHost, Uninstall-CmHost, `
    Get-CmDotNetRuntimeInfo, Set-CmRuntimeArchitecture, Get-CmOsArchitecture, Get-CmSourceArchitecture, Assert-CmArchitecture, Get-CmDotNetRoot, Set-CmRuntimeDownloadPrompt, Get-CmSourceRuntime, Get-CmDesktopRuntimeVersions, Test-CmDesktopRuntime, `
    Find-CmRuntimeInstaller, Test-CmMicrosoftSignature, Install-CmDotNetRuntime, Confirm-CmRuntime, Read-CmAccessKeys, Read-CmAccessKeyEntries, `
    Test-CmPathContains, Add-CmPathEntry, Remove-CmPathEntry, Get-CmRawPath, Set-CmPathEntry, `
    ConvertFrom-CmReleaseClaim, Get-CmReleaseClaim, Test-CmReleaseOrder, Get-CmBundledAction
