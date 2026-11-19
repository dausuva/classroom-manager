<#
.SYNOPSIS
Builds the free, open-source release in two stages so a signing service can sign between them (used by .github/workflows/release.yml; runs on any PC with the .NET 10 SDK).

.DESCRIPTION
The installers contain the programs, so the programs must be signed before the installers are made, and the installers signed after:
  -Stage Binaries   publishes the Host and the Client (self-contained, win-x64, one file each) into <Output>\binaries\{host,client}; nothing is signed.
                    The folder <Output>\binaries is what the signing service receives (.signpath/artifact-configurations/binaries.xml).
  -Stage Installers takes the programs back from -SignedBinaries (the same layout, now signed), writes the checksum files, compiles the two Inno Setup
                    installers and copies the single-file programs beside them under release names, with SHA256SUMS.txt.
                    <Output>\installers is what the second signing request receives (.signpath/artifact-configurations/installers.xml).
Nothing here needs a certificate; signing is not this script's job. A build made without the signing service is unsigned and says so in SELF-BUILT-UNOFFICIAL.txt
beside the programs of Build-Free.ps1; this script is for the release workflow and is not the way to build for yourself (use Build-Free.ps1).
Output names: Dausuva-Classroom-Host-Setup-<v>-win-x64.exe, Dausuva-Classroom-Client-Setup-<v>-win-x64.exe, Dausuva-Classroom-Host-<v>-win-x64.exe (portable),
Dausuva-Classroom-Client-<v>-win-x64.exe, SHA256SUMS.txt.
Runs under PowerShell 7 and Windows PowerShell 5.1.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Binaries', 'Installers')][string]$Stage,
    [string]$Output,
    [string]$SignedBinaries,
    [string]$InnoCompilerPath,
    [string]$Publisher = 'Dausuva Labs',
    [string]$ProductName = 'Classroom Manager',
    [string]$ArtifactPrefix = 'Dausuva-Classroom'
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path $here -Parent
if (-not $Output) { $Output = Join-Path $root 'artifacts\public' }
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$Output = (Resolve-Path -LiteralPath $Output).Path
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3; found '$version'." }
$fileVersion = "$version.0"
$apps = @(
    @{ Name = 'Host'; Folder = 'host'; Exe = 'ClassroomHost.exe'; Publish = 'Publish-Host.ps1' },
    @{ Name = 'Client'; Folder = 'client'; Exe = 'ClassroomClient.exe'; Publish = 'Publish-Client.ps1' }
)

if ($Stage -eq 'Binaries') {
    $binaries = Join-Path $Output 'binaries'
    if (Test-Path -LiteralPath $binaries) { Remove-Item -LiteralPath $binaries -Recurse -Force }
    foreach ($app in $apps) {
        $target = Join-Path $binaries $app.Folder
        Write-Host "Publishing the $($app.Name) $version..." -ForegroundColor Cyan
        & (Join-Path $root "deploy\$($app.Publish)") -OutputDir $target -RuntimeMode SelfContained -Runtime win-x64 -SkipChecksums -MsBuildArgs @("-p:InformationalVersion=$version", "-p:ClassroomFreeLimit=true")
        if ($LASTEXITCODE -ne 0) { throw "Publishing the $($app.Name) failed." }
        if (-not (Test-Path -LiteralPath (Join-Path $target $app.Exe))) { throw "$($app.Exe) is missing from $target." }
    }
    Write-Host "Unsigned programs are in $binaries; send that folder to be signed." -ForegroundColor Green
    return
}

# ---- Installers ----
if (-not $SignedBinaries) { throw '-Stage Installers needs -SignedBinaries (the folder the signing service returned).' }
$SignedBinaries = (Resolve-Path -LiteralPath $SignedBinaries).Path
$installers = Join-Path $Output 'installers'
if (Test-Path -LiteralPath $installers) { Remove-Item -LiteralPath $installers -Recurse -Force }
New-Item -ItemType Directory -Force -Path $installers | Out-Null

if (-not $InnoCompilerPath) {
    $iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if (-not $iscc) {
        foreach ($candidate in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) { if (Test-Path -LiteralPath $candidate) { $InnoCompilerPath = $candidate; break } }
    } else { $InnoCompilerPath = $iscc.Source }
}
if (-not $InnoCompilerPath) { throw 'Inno Setup 6 (ISCC.exe) was not found; install it (choco install innosetup) or pass -InnoCompilerPath.' }

$setupScripts = Join-Path $Output 'setup-scripts'
if (Test-Path -LiteralPath $setupScripts) { Remove-Item -LiteralPath $setupScripts -Recurse -Force }
New-Item -ItemType Directory -Force -Path $setupScripts | Out-Null
foreach ($name in 'ClassroomSetup.psm1', 'Setup.ps1', 'Setup-Bundled.ps1', 'Setup-Gui.ps1', 'SetupText.lt.psd1') { Copy-Item -LiteralPath (Join-Path $root "deploy\$name") -Destination $setupScripts }

foreach ($app in $apps) {
    $source = Join-Path $SignedBinaries $app.Folder
    $exe = Join-Path $source $app.Exe
    if (-not (Test-Path -LiteralPath $exe)) { throw "$exe is missing: -SignedBinaries must have the layout of the Binaries stage." }
    if ((Get-AuthenticodeSignature -LiteralPath $exe).Status -eq 'NotSigned') { Write-Warning "$($app.Exe) is not signed; the installer will contain an unsigned program." }
    Copy-Item -LiteralPath (Join-Path $root 'deploy\Test-Checksums.ps1') -Destination $source -Force
    & (Join-Path $root 'deploy\New-Checksums.ps1') -Path $source
    Write-Host "Compiling the $($app.Name) installer..." -ForegroundColor Cyan
    & $InnoCompilerPath "/DAppVersion=$version" "/DFileVersion=$fileVersion" "/DSourceDir=$source" "/DPublisher=$Publisher" "/DProductName=$ProductName" `
        "/DArtifactPrefix=$ArtifactPrefix" "/DDeployDir=$setupScripts" "/DOutputDir=$installers" (Join-Path $root "packaging\windows\Classroom$($app.Name).iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno compilation failed for the $($app.Name)." }
    Copy-Item -LiteralPath $exe -Destination (Join-Path $installers "$ArtifactPrefix-$($app.Name)-$version-win-x64.exe")
}

$lines = Get-ChildItem -LiteralPath $installers -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $hash = ([BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($_.FullName)))).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose() }
    "$hash  $($_.Name)"
}
[IO.File]::WriteAllText((Join-Path $installers 'SHA256SUMS.txt'), (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Installers are in $installers; send that folder to be signed, then publish it." -ForegroundColor Green
