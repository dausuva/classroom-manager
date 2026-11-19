<#
.SYNOPSIS
Writes the winget manifests (version, installer and default-locale files) for a Classroom Manager release, ready for winget-pkgs.

.DESCRIPTION
One package per component, as the installers are one per component:
  Host          Dausuva.ClassroomHost          the Inno Setup installer of the teacher console (silent, no key needed)
  Client        Dausuva.ClassroomClient        the Inno Setup installer of the student service (silent; a silent install needs the shared key, see NOTES)
  HostPortable  Dausuva.ClassroomHost.Portable the single-file Host program, run from anywhere, no install (winget "portable")
The files go to <OutputRoot>\manifests\d\Dausuva\<Package>\<Version>\ in the folder layout winget-pkgs wants. The installer is read from
-InstallerPath (its SHA-256 is computed) or -InstallerSha256 is given with -InstallerUrl. -InstallerUrl must be the final HTTPS address of the
file on the publisher's own release page (winget-pkgs refuses redirectors and other hosts), so give the release address, not a temporary one.
Runs under Windows PowerShell 5.1 and PowerShell 7. Nothing is sent anywhere: submitting is a separate, deliberate step (see docs/windows/STORE-AND-WINGET.md).

.NOTES
A silent Client install stops without the shared key (T-226). winget downloads the installer alone, so the kit file is not beside it; give the
operator's file with --custom "/KIT=<path to classroom-kit.json>" once the installer accepts it (T-236), or put classroom-kit.json in place first.
The manifests are checked with `winget validate` when winget is installed (tests\ClassroomHost.Tests\WingetManifestTests.cs).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Host', 'Client', 'HostPortable')][string]$Component,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^https://')][string]$InstallerUrl,
    [string]$InstallerPath,
    [ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$InstallerSha256,
    [ValidateSet('x64', 'x86')][string]$Architecture = 'x64',
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\..\artifacts\winget'),
    [string]$ReleaseDate = (Get-Date -Format 'yyyy-MM-dd'),
    [string]$ReleaseNotesUrl,
    # The free core is Apache-2.0; give 'Proprietary' for a paid build. winget does not need open source. LicenseUrl must answer (a 404 fails validation), so it
    # stays on dausuva.com until the public repository exists.
    [string]$License = 'Apache-2.0',
    [string]$LicenseUrl = 'https://dausuva.com/school/'
)
$ErrorActionPreference = 'Stop'

if (-not $InstallerSha256) {
    if (-not $InstallerPath) { throw 'Give -InstallerPath (the file is hashed) or -InstallerSha256.' }
    $InstallerSha256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash
}
$InstallerSha256 = $InstallerSha256.ToUpperInvariant()

$publisher = 'Dausuva Labs'
$schema = '1.12.0'
$packages = @{
    Host         = @{ Id = 'Dausuva.ClassroomHost'; Name = 'Classroom Manager'; Folder = 'ClassroomHost'
                      Short = 'Teacher console for managing Windows classroom computers over the local network.' }
    Client       = @{ Id = 'Dausuva.ClassroomClient'; Name = 'Classroom Manager Client'; Folder = 'ClassroomClient'
                      Short = 'Student-PC service that lets the Classroom Manager teacher console manage the computer.' }
    HostPortable = @{ Id = 'Dausuva.ClassroomHost.Portable'; Name = 'Classroom Manager (portable)'; Folder = 'ClassroomHost.Portable'
                      Short = 'Teacher console of Classroom Manager as one program that runs without installing.' }
}
$package = $packages[$Component]
$id = $package.Id
$folder = Join-Path (Resolve-Path -LiteralPath (New-Item -ItemType Directory -Force -Path $OutputRoot)).Path "manifests\d\Dausuva\$($package.Folder)\$Version"
New-Item -ItemType Directory -Force -Path $folder | Out-Null

$utf8 = New-Object System.Text.UTF8Encoding($false)
function Q([string]$text) { "'" + $text.Replace("'", "''") + "'" }
function Write-Manifest([string]$suffix, [string]$text) {
    [IO.File]::WriteAllText((Join-Path $folder "$id$suffix.yaml"), ($text -replace "`r?`n", "`n"), $utf8)
}

Write-Manifest '' @"
# Created by scripts/winget/New-WingetManifests.ps1
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@

$descriptions = @{
    Host         = 'Classroom Manager Host is the teacher console. It finds and manages the computers that run Classroom Manager Client: see and control screens, share files, lock, message, run actions and keep an inventory. Keys and settings stay on the teacher computer.'
    Client       = 'Classroom Manager Client runs on student computers as a Windows service with a notification-area icon. It answers only a Host that holds the lab key and always shows the student that the computer is managed. A silent install needs the lab key (a classroom-kit.json from the operator).'
    HostPortable = 'The Host as one self-contained program: copy it anywhere and run it. Pass --data-folder or put a ClassroomHost.portable file beside it to keep its settings with the program.'
}
$notes = if ($ReleaseNotesUrl) { "ReleaseNotesUrl: $ReleaseNotesUrl`n" } else { '' }
Write-Manifest '.locale.en-US' @"
# Created by scripts/winget/New-WingetManifests.ps1
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: $publisher
PublisherUrl: https://dausuva.com/school/
PublisherSupportUrl: https://dausuva.com/school/
PrivacyUrl: https://dausuva.com/school/
Author: $publisher
PackageName: $(Q $package.Name)
PackageUrl: https://dausuva.com/school/
License: $License
LicenseUrl: $LicenseUrl
Copyright: Copyright (c) $publisher
ShortDescription: $(Q $package.Short)
Description: $(Q $descriptions[$Component])
Moniker: $($package.Folder.ToLowerInvariant())
Tags:
- classroom
- education
- school
- teacher
- lab
${notes}ManifestType: defaultLocale
ManifestVersion: $schema
"@

if ($Component -eq 'HostPortable') {
    $installer = @"
InstallerType: portable
Commands:
- classroomhost
Installers:
- Architecture: $Architecture
  InstallerUrl: $InstallerUrl
  InstallerSha256: $InstallerSha256
"@
} else {
    $arp = if ($Component -eq 'Host') { 'Classroom Manager' } else { 'Classroom Manager Client' }
    $installer = @"
InstallerType: inno
Scope: machine
InstallModes:
- interactive
- silent
- silentWithProgress
UpgradeBehavior: install
ElevationRequirement: elevationRequired
Installers:
- Architecture: $Architecture
  InstallerUrl: $InstallerUrl
  InstallerSha256: $InstallerSha256
  AppsAndFeaturesEntries:
  - DisplayName: $arp
    Publisher: $publisher
"@
}
# The Client installer has run on Windows 10 1903 (build 18362); the Host needs Windows 10 2004 like its Store package.
$minimumOs = if ($Component -eq 'Client') { '10.0.18362.0' } else { '10.0.19041.0' }
Write-Manifest '.installer' @"
# Created by scripts/winget/New-WingetManifests.ps1
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
MinimumOSVersion: $minimumOs
$installer
ReleaseDate: $ReleaseDate
ManifestType: installer
ManifestVersion: $schema
"@

Write-Host "Wrote the $Component manifests to $folder"
$folder
