<#
.SYNOPSIS
Writes a software bill of materials (CycloneDX 1.5, JSON) listing every NuGet package the programs are built from, with the version and SHA-512 hash recorded in the committed packages.lock.json files.

.DESCRIPTION
The release workflow attaches the file to every release (sbom.cdx.json) so that anyone can see what is inside a download without building it. The list comes from the lock files under src\, the same ones CI restores
against in locked mode, so it is the real build input and not a guess. Projects of this repository itself are listed as one component, "Classroom Manager". Test projects are not part of a release and are left out.
-Exclude names source folders whose lock files are not part of what is released (the private-only tools); in the public repository they do not exist.
Runs under PowerShell 7 and Windows PowerShell 5.1; needs nothing installed.
#>
[CmdletBinding()]
param(
    [string]$Output,
    [string[]]$Exclude = @('ClassroomDesk', 'ClassroomCleanup', 'ClassroomTrustMigration', 'ClassroomHealth', 'ClassroomClientHealth', 'MatrixGeometryScreensaver'),
    [string]$Timestamp = ([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path $here -Parent
if (-not $Output) { $Output = Join-Path $root 'artifacts\sbom.cdx.json' }
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

$lockFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -Filter packages.lock.json -File |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } |
    Where-Object { $path = $_.FullName; -not ($Exclude | Where-Object { $path -match "[\\/]$([regex]::Escape($_))[\\/]" }) })
if ($lockFiles.Count -eq 0) { throw "No packages.lock.json under $root\src. Restore first (dotnet restore) or check that lock files are committed." }

$packages = @{}
foreach ($file in $lockFiles) {
    $lock = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    foreach ($framework in $lock.dependencies.PSObject.Properties) {
        foreach ($package in $framework.Value.PSObject.Properties) {
            $entry = $package.Value
            if ($entry.type -eq 'Project' -or -not $entry.resolved) { continue }   # this repository's own projects
            $key = "$($package.Name)@$($entry.resolved)"
            if (-not $packages.ContainsKey($key)) { $packages[$key] = [pscustomobject]@{ Name = $package.Name; Version = $entry.resolved; Hash = $entry.contentHash } }
        }
    }
}

$components = foreach ($key in ($packages.Keys | Sort-Object)) {
    $package = $packages[$key]
    $component = [ordered]@{
        type = 'library'
        'bom-ref' = "pkg:nuget/$($package.Name)@$($package.Version)"
        name = $package.Name
        version = $package.Version
        purl = "pkg:nuget/$($package.Name)@$($package.Version)"
        scope = 'required'
    }
    if ($package.Hash) {
        $hex = [BitConverter]::ToString([Convert]::FromBase64String($package.Hash)).Replace('-', '').ToLowerInvariant()
        $component.hashes = @([ordered]@{ alg = 'SHA-512'; content = $hex })
    }
    $component
}

# The serial number comes from the content, so the same lock files give the same document apart from the timestamp.
$sha = [System.Security.Cryptography.SHA256]::Create()
try { $digest = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes((($packages.Keys | Sort-Object) -join "`n") + "|$version")) } finally { $sha.Dispose() }
$serial = ([Guid]::new([byte[]]$digest[0..15])).ToString()

$bom = [ordered]@{
    bomFormat = 'CycloneDX'
    specVersion = '1.5'
    serialNumber = "urn:uuid:$serial"
    version = 1
    metadata = [ordered]@{
        timestamp = $Timestamp
        tools = @([ordered]@{ vendor = 'Dausuva Labs'; name = 'scripts/Export-Sbom.ps1' })
        component = [ordered]@{
            type = 'application'
            'bom-ref' = 'classroom-manager'
            name = 'Classroom Manager'
            version = $version
            supplier = [ordered]@{ name = 'Dausuva Labs'; url = @('https://dausuva.com/school/') }
            licenses = @([ordered]@{ license = [ordered]@{ id = 'Apache-2.0' } })
            purl = "pkg:github/dausuva/classroom-manager@v$version"
        }
    }
    components = @($components)
    dependencies = @([ordered]@{ ref = 'classroom-manager'; dependsOn = @($components | ForEach-Object { $_['bom-ref'] }) })
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Output) | Out-Null
[IO.File]::WriteAllText($Output, (($bom | ConvertTo-Json -Depth 10) + "`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "SBOM with $($components.Count) packages written to $Output"
