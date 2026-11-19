<# Unattended entry point for the signed Host/Client Inno wrappers. No secrets on the command line. #>
param(
    [Parameter(Mandatory)][ValidateSet('Client','Host')][string]$Component,
    [Parameter(Mandatory)][string]$SourceDir,
    [ValidateSet('','en','lt')][string]$Language = ''
)
$ErrorActionPreference = 'Stop'
try {
    Import-Module (Join-Path $PSScriptRoot 'ClassroomSetup.psm1') -Force
    $status = if ($Component -eq 'Client') { Get-CmClientStatus } else { Get-CmHostStatus }
    $source = Get-Item -LiteralPath (Join-Path $SourceDir "Classroom$Component.exe") -ErrorAction Stop
    $version = [version]$source.VersionInfo.FileVersion
    if (-not $version) { throw 'The bundled executable has no file version.' }
    # Update keeps keys/settings and retains the existing downgrade refusal. The same release again repairs; two customer releases of one public
    # version (same file version) are ordered by release number inside Update, which refuses an older one.
    $action = Get-CmBundledAction -Installed ([bool]$status.Installed) -InstalledVersion $status.Version -SourceVersion $version `
        -InstalledClaim (Get-CmReleaseClaim (Join-Path $status.InstallDir "Classroom$Component.exe")) -SourceClaim (Get-CmReleaseClaim $source.FullName)
    if ($Component -eq 'Client' -and $action -eq 'Install') {
        $key = Get-CmKitKey $PSScriptRoot
        if (-not $key -or (Test-CmKey $key)) {
            throw 'A fresh silent Client install requires a valid key in classroom-kit.json beside the installer. No key was generated or changed.'
        }
    }
    $setup = @{ Component=$Component; Action=$action; SourceDir=$SourceDir; Quiet=$true; RuntimeDownload='Never'; Language=$Language }
    & (Join-Path $PSScriptRoot 'Setup.ps1') @setup
    exit $LASTEXITCODE
} catch {
    Write-Error -Message 'Bundled unattended setup failed. Check the setup log and configuration; existing keys are not replaced by this entry point.' -ErrorAction Continue
    exit 1
}
