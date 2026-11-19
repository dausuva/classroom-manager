<#
.SYNOPSIS
  Builds a free, unsigned, self-built Classroom Manager from this source tree (T-219).

.DESCRIPTION
  Restores, builds and publishes the Client (student PC service) and the Host (teacher app) with the .NET SDK only.
  It needs no administrator rights, no signing certificate and no changes to the Windows trust store, and it never
  signs anything: the result is an unofficial self-built version. Each output folder gets SELF-BUILT-UNOFFICIAL.txt.
  Installing the Client service on a PC afterwards is a separate step and does need administrator rights (see BUILDING.md).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Build-Free.ps1
  powershell -ExecutionPolicy Bypass -File .\Build-Free.ps1 -RuntimeMode SharedRuntime -RunTests

.PARAMETER RuntimeMode
  SelfContained (default): the .NET runtime is inside the program, so a PC needs nothing else.
  SharedRuntime: smaller programs that use the .NET Desktop Runtime installed on the PC.

.PARAMETER RunTests
  Also runs the whole test suite after the build (several minutes).
#>
param(
    [ValidateSet("SelfContained", "SharedRuntime")][string]$RuntimeMode = "SelfContained",
    [string]$OutputRoot,
    [switch]$RunTests,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Runtime = "win-x64"   # 64-bit Windows only
if (-not $OutputRoot) { $OutputRoot = Join-Path $root "free-build" }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK was not found. Install the .NET 10 SDK from https://dotnet.microsoft.com/download and run this again."
}
$sdk = (& dotnet --version).Trim()
if ([int]($sdk.Split('.')[0]) -lt 10) { throw "The .NET 10 SDK or newer is required; this PC has $sdk." }
if (-not (Test-Path (Join-Path $root "ClassroomManager.sln"))) { throw "ClassroomManager.sln was not found next to Build-Free.ps1." }

$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props") -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = "0.0.0" }
$stamp = "$version-self-built"
Write-Host "Classroom Manager $version, free self-built version (.NET SDK $sdk, $Runtime, $RuntimeMode)" -ForegroundColor Cyan

Push-Location $root
try {
    Write-Host "Building ClassroomManager.sln (Release)..." -ForegroundColor Cyan
    & dotnet build ClassroomManager.sln -c Release -p:InformationalVersion=$stamp
    if ($LASTEXITCODE -ne 0) { throw "The build failed." }

    if ($RunTests) {
        Write-Host "Running the tests..." -ForegroundColor Cyan
        & dotnet test ClassroomManager.sln -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "Tests failed. Nothing was published." }
    }

    if ($SkipPublish) { Write-Host "Build finished; publishing skipped." -ForegroundColor Green; return }

    $notice = @"
Classroom Manager $version - SELF-BUILT, UNOFFICIAL

This program was built from source on $env:COMPUTERNAME and is not signed. It is not an official release of the
publisher, it has no Authenticode signature, and an official Client will not accept it as a normal update.
Windows may warn before running it. The free features need no licence, with no limit on computers or time.
See BUILDING.md in the source folder for installing and updating a self-built version.
"@

    $outputs = @()
    foreach ($app in @(@{ Name = "Client"; Script = "Publish-Client.ps1" }, @{ Name = "Host"; Script = "Publish-Host.ps1" })) {
        $suffix = if ($RuntimeMode -eq "SharedRuntime") { "-shared-runtime" } else { "" }
        $target = Join-Path $OutputRoot ("{0}{1}" -f $app.Name.ToLower(), $suffix)
        Write-Host "Publishing the $($app.Name)..." -ForegroundColor Cyan
        & (Join-Path $root "deploy\$($app.Script)") -OutputDir $target -RuntimeMode $RuntimeMode -Runtime $Runtime -MsBuildArgs @("-p:InformationalVersion=$stamp")
        if ($LASTEXITCODE -ne 0) { throw "Publishing the $($app.Name) failed." }
        Set-Content -LiteralPath (Join-Path $target "SELF-BUILT-UNOFFICIAL.txt") -Value $notice -Encoding UTF8
        $outputs += $target
    }

    Write-Host ""
    Write-Host "Done. Unsigned self-built output:" -ForegroundColor Green
    $outputs | ForEach-Object { Write-Host "  $_" }
}
finally { Pop-Location }
