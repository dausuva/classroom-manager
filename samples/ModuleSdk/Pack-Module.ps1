<#
.SYNOPSIS
  Builds a module project and writes the installable package folder: the program file and a module.json that lists it with its SHA-256.

.DESCRIPTION
  The folder is named after the module id, as the Host requires. The package is NOT signed here. A Host accepts a package only when
  every .dll in it carries an Authenticode signature from a signer that Host trusts; sign the .dll first (pass -Sign with a code
  signing certificate thumbprint) or the Host lists the package as refused and says why. Nothing is installed by this script: install
  with `ClassroomCli modules install --name <folder>`.

.EXAMPLE
  powershell -File Pack-Module.ps1 -Project .\Hello\ClassroomManager.Samples.Hello.csproj -Id example.hello.sample `
      -Publisher example -Name "Hello sample" -Version 1.0.0 -Output C:\temp\packages
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Id,
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Output,
    [string]$EntryType,
    [string[]]$ClientCapabilities = @(),
    [string]$MinProtocol = '1.14',
    [string[]]$Ports = @('log', 'time'),
    [string]$ContractDll,
    [string]$Sign                         # thumbprint of a code signing certificate in the current user's store
)

$ErrorActionPreference = 'Stop'
if (-not $EntryType) { throw 'Give -EntryType, the full name of the class that implements IClassroomModule.' }

$build = Join-Path ([IO.Path]::GetTempPath()) ('module-build-' + [Guid]::NewGuid().ToString('N'))
$extra = @(); if ($ContractDll) { $extra += "-p:ContractDll=$ContractDll" }
& dotnet build $Project -c Release -o $build @extra --nologo -v:q
if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }

$assemblyName = [IO.Path]::GetFileNameWithoutExtension((Get-ChildItem -LiteralPath $build -Filter '*.dll' |
    Where-Object { $_.Name -notlike 'ClassroomManager.Modules.Contract*' } | Select-Object -First 1).Name)
$dll = Join-Path $build ($assemblyName + '.dll')

$package = Join-Path $Output $Id
if (Test-Path -LiteralPath $package) { throw "$package already exists. Give a new output folder." }
New-Item -ItemType Directory -Path $package | Out-Null
Copy-Item -LiteralPath $dll -Destination $package
$target = Join-Path $package ($assemblyName + '.dll')

if ($Sign) {
    $certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $Sign } | Select-Object -First 1
    if (-not $certificate) { throw "No certificate with thumbprint $Sign in the current user's store." }
    $result = Set-AuthenticodeSignature -FilePath $target -Certificate $certificate -HashAlgorithm SHA256
    if ($result.Status -ne 'Valid' -and $result.Status -ne 'UnknownError') { throw "Signing failed: $($result.Status) $($result.StatusMessage)" }
}

$hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
$manifest = [ordered]@{
    schema       = 1
    id           = $Id
    publisher    = $Publisher
    name         = $Name
    version      = $Version
    contract     = [ordered]@{ min = 1; max = 1 }
    os           = 'windows'
    architecture = 'x64'
    entry        = [ordered]@{ assembly = ($assemblyName + '.dll'); type = $EntryType }
    client       = [ordered]@{ minProtocol = $MinProtocol; capabilities = @($ClientCapabilities) }
    ports        = @($Ports | ForEach-Object { [ordered]@{ name = $_; version = 1 } })
    files        = @([ordered]@{ path = ($assemblyName + '.dll'); sha256 = $hash })
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $package 'module.json') -Encoding UTF8
Remove-Item -LiteralPath $build -Recurse -Force
$package
