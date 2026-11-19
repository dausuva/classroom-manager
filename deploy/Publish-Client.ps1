<#
.SYNOPSIS
  Publishes the Classroom Manager client as a self-contained Release build,
  ready for Install.ps1 / Install-Client.ps1.

.DESCRIPTION
  Single-file, self-contained win-x64 publish (-Runtime win-x86 for 32-bit Windows, into publish-client-x86) (file count and .NET
  dependencies are trimmed down via Directory.Build.props and the project's
  PublishSingleFile settings - see the "Smaller publish output" note in
  README.md). Afterwards writes CHECKSUMS.sha256 for the output folder and
  drops a copy of Test-Checksums.ps1 next to it, so the folder is
  self-contained for anyone who wants to verify it.

.EXAMPLE
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\Publish-Client.ps1
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\Publish-Client.ps1 -OutputDir .\publish-client -Runtime win-x64
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\Publish-Client.ps1 -NoSingleFile
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\Publish-Client.ps1 -RuntimeMode SharedRuntime

.PARAMETER RuntimeMode
  SelfContained (default): the .NET runtime is inside the executable, so the PC needs nothing else.
  SharedRuntime: a small executable that uses the .NET Desktop Runtime installed on the PC; setup
  installs that runtime when it is missing. Goes to publish-client-shared-runtime unless -OutputDir is given.
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
if (-not $OutputDir) { $OutputDir = Join-Path $root ($(if ($sharedRuntime) { "publish-client-shared-runtime" } else { "publish-client" }) + $archSuffix) }
$proj = Join-Path $root "src\ClassroomClient\ClassroomClient.csproj"

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

Write-Host "Publishing ClassroomClient (Release, $Runtime, $(if ($sharedRuntime) { 'uses the .NET runtime on the PC' } else { 'includes .NET' })) to $OutputDir..." -ForegroundColor Cyan
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit code $LASTEXITCODE)." }

if (-not (Test-Path (Join-Path $OutputDir "ClassroomClient.exe"))) {
    throw "Publish reported success but ClassroomClient.exe was not found in $OutputDir."
}

Write-Host "Published to $OutputDir" -ForegroundColor Green

# ---- RUNTIME.json: tells setup whether this folder needs the .NET runtime on the PC ----
Import-Module (Join-Path $PSScriptRoot "ClassroomSetup.psm1") -Force
Set-CmRuntimeArchitecture $arch
$dotnet = Get-CmDotNetRuntimeInfo
[ordered]@{
    mode           = $(if ($sharedRuntime) { "shared-runtime" } else { "self-contained" })
    architecture   = $arch
    framework      = $dotnet.Framework
    majorVersion   = $dotnet.MajorVersion
    minimumVersion = "$($dotnet.MajorVersion).0.0"
    displayName    = $dotnet.DisplayName
} | ConvertTo-Json | Set-Content (Join-Path $OutputDir "RUNTIME.json") -Encoding UTF8

# ---- Checksums, so the recipient can verify the folder wasn't altered -----

if (-not $SkipChecksums) {
    & (Join-Path $PSScriptRoot "New-Checksums.ps1") -Path $OutputDir
    Copy-Item (Join-Path $PSScriptRoot "Test-Checksums.ps1") $OutputDir -Force
    Write-Host "Added CHECKSUMS.sha256 and Test-Checksums.ps1 to $OutputDir" -ForegroundColor Green
}
