<#
.SYNOPSIS
  Publishes the Classroom Manager host (teacher app) as a self-contained
  Release build, ready for Install.ps1.

.DESCRIPTION
  Single-file, self-contained win-x64 publish (-Runtime win-x86 for 32-bit Windows, into publish-host-x86) (file count and .NET
  dependencies are trimmed down via Directory.Build.props and the project's
  PublishSingleFile settings - see the "Smaller publish output" note in
  README.md). Afterwards writes CHECKSUMS.sha256 for the output folder and
  drops a copy of Test-Checksums.ps1 next to it, so the folder is
  self-contained for anyone who wants to verify it.

.EXAMPLE
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\Publish-Host.ps1
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\Publish-Host.ps1 -OutputDir .\publish-host -Runtime win-x64
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\Publish-Host.ps1 -NoSingleFile
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\Publish-Host.ps1 -RuntimeMode SharedRuntime

.PARAMETER RuntimeMode
  SelfContained (default): the .NET runtime is inside the executable, so the PC needs nothing else.
  SharedRuntime: a small executable that uses the .NET Desktop Runtime installed on the PC; setup
  installs that runtime when it is missing. Goes to publish-host-shared-runtime unless -OutputDir is given.
#>
param(
    [string]$OutputDir,
    [ValidateSet("SelfContained", "SharedRuntime")][string]$RuntimeMode = "SelfContained",
    [ValidateSet("win-x64", "win-x86")][string]$Runtime = "win-x64",
    [switch]$NoSingleFile,
    [switch]$SkipChecksums,
    # Extra MSBuild properties, e.g. the version/build stamp scripts\build.ps1 passes in.
    [string[]]$MsBuildArgs = @()
)

$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$sharedRuntime = $RuntimeMode -eq "SharedRuntime"
$arch = $Runtime -replace '^win-', ''   # x64, or x86 for 32-bit Windows
$archSuffix = $(if ($arch -eq "x86") { "-x86" } else { "" })
if (-not $OutputDir) { $OutputDir = Join-Path $root ($(if ($sharedRuntime) { "publish-host-shared-runtime" } else { "publish-host" }) + $archSuffix) }
$proj = Join-Path $root "src\ClassroomHost\ClassroomHost.csproj"

if (-not (Test-Path $proj)) { throw "Project not found: $proj" }

if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }

$publishArgs = @(
    "publish", $proj,
    "-c", "Release",
    "-r", $Runtime,
    "--self-contained", $(if ($sharedRuntime) { "false" } else { "true" }),
    "-o", $OutputDir
)
# Compressing the single file is only possible when the runtime is inside it.
if ($sharedRuntime) { $publishArgs += "-p:EnableCompressionInSingleFile=false" }
if ($NoSingleFile) { $publishArgs += "-p:PublishSingleFile=false" }
if ($MsBuildArgs) { $publishArgs += $MsBuildArgs }

Write-Host "Publishing ClassroomHost (Release, $Runtime, $(if ($sharedRuntime) { 'uses the .NET runtime on the PC' } else { 'includes .NET' })) to $OutputDir..." -ForegroundColor Cyan
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit code $LASTEXITCODE)." }

if (-not (Test-Path (Join-Path $OutputDir "ClassroomHost.exe"))) {
    throw "Publish reported success but ClassroomHost.exe was not found in $OutputDir."
}

# ---- Command line (ClassroomCli) ---------------------------------------------
# The build that includes .NET runs scripted commands inside ClassroomHost.exe, started through the
# ClassroomCli.cmd launcher the project publishes beside it; a second executable would carry a
# second full copy of the runtime. The shared-runtime build is small enough to have a real
# ClassroomCli.exe instead, which then replaces the launcher. Scripts call "ClassroomCli" without an
# extension and get whichever one the folder has.
# The public source has no ClassroomHostCli project (the real ClassroomCli.exe is part of the sellable build only), so a shared-runtime build made
# from it keeps the ClassroomCli.cmd launcher, as the build that includes .NET does.
$sharedCli = $sharedRuntime -and (Test-Path -LiteralPath (Join-Path $root "src\ClassroomHostCli\ClassroomHostCli.csproj"))
if ($sharedCli) {
    $cliProj = Join-Path $root "src\ClassroomHostCli\ClassroomHostCli.csproj"
    $cliStage = Join-Path ([IO.Path]::GetTempPath()) ("classroom-cli-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    $cliArgs = @("publish", $cliProj, "-c", "Release", "-r", $Runtime, "--self-contained", "false", "-o", $cliStage)
    if ($NoSingleFile) { $cliArgs += "-p:PublishSingleFile=false" }
    if ($MsBuildArgs) { $cliArgs += $MsBuildArgs }
    try {
        dotnet @cliArgs
        if ($LASTEXITCODE -ne 0) { throw "Publishing ClassroomCli failed (exit code $LASTEXITCODE)." }
        if ($NoSingleFile) {
            # Loose-file layout: ClassroomCli.exe loads the Host's assemblies from the same folder.
            Copy-Item (Join-Path $cliStage "ClassroomCli*") $OutputDir -Force
        } else {
            Copy-Item (Join-Path $cliStage "ClassroomCli.exe") $OutputDir -Force
        }
    } finally { Remove-Item $cliStage -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item (Join-Path $OutputDir "ClassroomCli.cmd") -Force -ErrorAction SilentlyContinue
}
$cliName = $(if ($sharedCli) { "ClassroomCli.exe" } else { "ClassroomCli.cmd" })
if (-not (Test-Path (Join-Path $OutputDir $cliName))) { throw "Publish reported success but $cliName was not found in $OutputDir." }
# Run it once, by the extension-less name scripts use, so a build whose command line is broken never ships.
$cliHelp = @(& $env:ComSpec /c "`"$(Join-Path $OutputDir 'ClassroomCli')`" help")
if ($LASTEXITCODE -ne 0 -or -not ($cliHelp -match 'Usage:')) {
    throw "ClassroomCli help failed (exit code $LASTEXITCODE); the published Host's command line does not work."
}
Write-Host "Command line verified ($cliName)." -ForegroundColor Green

# ---- RUNTIME.json: tells setup whether this folder needs the .NET runtime on the PC ----
$setupModule = Import-Module (Join-Path $PSScriptRoot "ClassroomSetup.psm1") -Force -PassThru
& $setupModule {
    param($Directory)
    Test-CmHostStartup $Directory (Get-CmFileVersion (Join-Path $Directory 'ClassroomHost.exe'))
} $OutputDir
Write-Host "Host expected-version startup verified." -ForegroundColor Green
Set-CmRuntimeArchitecture $arch
$dotnet = Get-CmDotNetRuntimeInfo
[ordered]@{
    mode           = $(if ($sharedRuntime) { "shared-runtime" } else { "self-contained" })
    architecture   = $arch
    framework      = $dotnet.Framework
    majorVersion   = $dotnet.MajorVersion
    minimumVersion = "$($dotnet.MajorVersion).0.0"
    displayName    = $dotnet.DisplayName
    hostStartupProbe = 1
} | ConvertTo-Json | Set-Content (Join-Path $OutputDir "RUNTIME.json") -Encoding UTF8

Write-Host "Published to $OutputDir" -ForegroundColor Green

# ---- Checksums, so the recipient can verify the folder wasn't altered -----

if (-not $SkipChecksums) {
    & (Join-Path $PSScriptRoot "New-Checksums.ps1") -Path $OutputDir
    Copy-Item (Join-Path $PSScriptRoot "Test-Checksums.ps1") $OutputDir -Force
    Write-Host "Added CHECKSUMS.sha256 and Test-Checksums.ps1 to $OutputDir" -ForegroundColor Green
}
