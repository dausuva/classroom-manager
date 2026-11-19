<#
.SYNOPSIS
  Builds the Classroom Manager host (teacher app) project.

.DESCRIPTION
  Plain `dotnet build` of ClassroomHost (and its ClassroomManager.Shared
  dependency). Use this for a quick compile/dev-loop check; use
  Publish-Host.ps1 to produce something you can actually deploy.

.EXAMPLE
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\Build-Host.ps1
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\Build-Host.ps1 -Configuration Release
#>
param(
    [ValidateSet("Debug", "Release")][string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "src\ClassroomHost\ClassroomHost.csproj"

if (-not (Test-Path $proj)) { throw "Project not found: $proj" }

Write-Host "Building ClassroomHost ($Configuration)..." -ForegroundColor Cyan
dotnet build $proj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)." }

Write-Host "Build succeeded." -ForegroundColor Green
