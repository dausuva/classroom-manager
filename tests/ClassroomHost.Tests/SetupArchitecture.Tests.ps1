param([Parameter(Mandatory)][string]$RepositoryRoot, [Parameter(Mandatory)][string]$Scenario)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $RepositoryRoot 'deploy\ClassroomSetup.psm1') -Force

function Fail($message) { Write-Output "FAIL: $Scenario - $message"; exit 1 }
function Expect($actual, $expected, $what) { if ($actual -ne $expected) { Fail "$what was '$actual', expected '$expected'" } }
function Expect-Throws($block, $pattern, $what) {
    try { & $block } catch { if ($_.Exception.Message -match $pattern) { return } Fail "$what threw '$($_.Exception.Message)', expected a message matching '$pattern'" }
    Fail "$what did not throw"
}
function New-Source($architecture) {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("cm-arch-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir | Out-Null
    if ($architecture) { [ordered]@{ mode = 'self-contained'; architecture = $architecture } | ConvertTo-Json | Set-Content (Join-Path $dir 'RUNTIME.json') -Encoding UTF8 }
    return $dir
}
function With-OsArchitecture($architecture, $block) {
    $saved = @{ A = $env:PROCESSOR_ARCHITECTURE; W = $env:PROCESSOR_ARCHITEW6432 }
    try {
        if ($architecture -eq 'x86') { $env:PROCESSOR_ARCHITECTURE = 'x86'; $env:PROCESSOR_ARCHITEW6432 = $null }
        else { $env:PROCESSOR_ARCHITECTURE = 'AMD64'; $env:PROCESSOR_ARCHITEW6432 = $null }
        & $block
    } finally { $env:PROCESSOR_ARCHITECTURE = $saved.A; $env:PROCESSOR_ARCHITEW6432 = $saved.W }
}

switch ($Scenario) {
    'Names' {
        Set-CmRuntimeArchitecture 'x86'
        $x86 = Get-CmDotNetRuntimeInfo
        Expect $x86.Architecture 'x86' 'architecture'
        Expect $x86.InstallerPattern 'windowsdesktop-runtime-*-win-x86.exe' 'x86 installer pattern'
        Expect $x86.DownloadUrl 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x86.exe' 'x86 download'
        Expect $x86.DisplayName '.NET 10 Desktop Runtime (x86)' 'x86 name'
        Set-CmRuntimeArchitecture 'X64'
        $x64 = Get-CmDotNetRuntimeInfo
        Expect $x64.InstallerPattern 'windowsdesktop-runtime-*-win-x64.exe' 'x64 installer pattern'
        Expect $x64.DisplayName '.NET 10 Desktop Runtime (x64)' 'x64 name'
        Expect-Throws { Set-CmRuntimeArchitecture 'arm' } 'Unknown architecture' 'an unknown architecture'
    }
    'SourceArchitecture' {
        Expect (Get-CmSourceArchitecture (New-Source $null)) 'x64' 'a folder with no RUNTIME.json'
        Expect (Get-CmSourceArchitecture (New-Source 'x86')) 'x86' 'an x86 folder'
        $old = New-Source $null
        '{ "mode": "shared-runtime", "majorVersion": 10 }' | Set-Content (Join-Path $old 'RUNTIME.json')
        Expect (Get-CmSourceArchitecture $old) 'x64' 'a RUNTIME.json from before the x86 build'
    }
    'WrongWindows' {
        With-OsArchitecture 'x86' {
            Expect-Throws { Assert-CmArchitecture (New-Source 'x64') } '64-bit \(x64\) build.*32-bit Windows.*win-x86' 'x64 build on 32-bit Windows'
            Expect-Throws { Assert-CmArchitecture (New-Source $null) } '64-bit \(x64\) build' 'a build with no architecture on 32-bit Windows'
            Assert-CmArchitecture (New-Source 'x86')
        }
        With-OsArchitecture 'x64' {
            Expect-Throws { Assert-CmArchitecture (New-Source 'x86') } '32-bit \(x86\) build.*64-bit Windows.*win-x64' 'x86 build on 64-bit Windows'
            Assert-CmArchitecture (New-Source 'x64')
            Assert-CmArchitecture (New-Source $null)
        }
    }
    'RuntimeFolder' {
        Set-CmRuntimeArchitecture 'x64'
        Expect (Get-CmDotNetRoot) (Join-Path $env:ProgramFiles 'dotnet') 'x64 runtime folder'
        Set-CmRuntimeArchitecture 'x86'
        $expected = $(if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'dotnet' } else { Join-Path $env:ProgramFiles 'dotnet' })
        Expect (Get-CmDotNetRoot) $expected 'x86 runtime folder'
        Set-CmRuntimeArchitecture 'x64'
    }
    default { Fail 'unknown scenario' }
}
Write-Output "PASS: $Scenario"
