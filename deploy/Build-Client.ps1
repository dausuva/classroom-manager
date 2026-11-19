<#
.SYNOPSIS
  Builds the Classroom Manager client project.

.DESCRIPTION
  Plain `dotnet build` of ClassroomClient (and its ClassroomManager.Shared
  dependency). Use this for a quick compile/dev-loop check; use
  Publish-Client.ps1 to produce something you can actually deploy.

.EXAMPLE
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\Build-Client.ps1
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\Build-Client.ps1 -Configuration Release
#>
param(
    [ValidateSet("Debug", "Release")][string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "src\ClassroomClient\ClassroomClient.csproj"

if (-not (Test-Path $proj)) { throw "Project not found: $proj" }

Write-Host "Building ClassroomClient ($Configuration)..." -ForegroundColor Cyan
dotnet build $proj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)." }

Write-Host "Build succeeded." -ForegroundColor Green
