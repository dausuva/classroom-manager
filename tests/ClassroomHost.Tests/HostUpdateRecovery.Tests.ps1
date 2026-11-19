param([Parameter(Mandatory)][string]$RepositoryRoot, [Parameter(Mandatory)][string]$Scratch,
      [Parameter(Mandatory)][ValidateSet('success','copy-failure','restore-failure','restore-delete-failure','missing-backup','nonterminating-copy','startup-failure','startup-timeout','unsupported-source')][string]$Scenario)
$ErrorActionPreference = 'Stop'
$module = Import-Module (Join-Path $RepositoryRoot 'deploy\ClassroomSetup.psm1') -Force -PassThru
& $module {
    param($Scratch, $Scenario)
    $script:Fixture = $Scratch
    $script:Scenario = $Scenario
    $script:Registered = $false
    $script:FixtureLock = $null
    $script:Messages = [Collections.Generic.List[string]]::new()
    $installed = Join-Path $Scratch 'installed'
    $source = Join-Path $Scratch 'source'
    New-Item -ItemType Directory -Path $installed,$source | Out-Null
    Set-Content -LiteralPath (Join-Path $installed 'image.txt') -Value 'known-good'
    Set-Content -LiteralPath (Join-Path $source 'image.txt') -Value 'candidate'
    if ($Scenario -ne 'unsupported-source') { '{"hostStartupProbe":1}' | Set-Content -LiteralPath (Join-Path $source 'RUNTIME.json') }
    function script:Assert-CmAdmin { }
    function script:Get-CmHostStatus { [pscustomobject]@{ Installed=$true; Version=[version]'1.0'; InstallDir=(Join-Path $script:Fixture 'installed') } }
    function script:Resolve-CmSource { param($SourceDir,$Exe) $SourceDir }
    function script:Get-CmFileVersion { param($Path) [version]'2.0.0.0' }
    function script:Test-CmChecksums { param($Dir) }
    function script:Test-CmSourceSignature { param($Dir,$Exe) }
    function script:Confirm-CmRuntime { param($Dir) }
    function script:Stop-CmHost { }
    function script:Set-CmHostFirewall { param($Dir) }
    function script:Complete-CmHostRegistration { param($Dir) $script:Registered=$true }
    function script:Write-CmLog { param($Text) $script:Messages.Add($Text) }
    $script:Probe = $null
    function script:New-CmHostProbeProcess {
        param($Executable,$ExpectedVersion)
        if ($Executable -ne (Join-Path $script:Fixture 'installed\ClassroomHost.exe') -or $ExpectedVersion -ne '2.0.0.0') { throw 'Wrong probe target/version.' }
        if (-not (Test-Path (Join-Path $script:Fixture 'recovery'))) { throw 'Backup discarded before health.' }
        $script:Probe=[pscustomobject]@{ ExitCode=$(if($script:Scenario -eq 'startup-failure'){1}else{0}); Killed=$false; Disposed=$false }
        $script:Probe | Add-Member ScriptMethod WaitForExit { param($Milliseconds) $script:Scenario -ne 'startup-timeout' -or $this.Killed }
        $script:Probe | Add-Member ScriptMethod Kill { $this.Killed=$true }
        $script:Probe | Add-Member ScriptMethod Dispose { $this.Disposed=$true }
        $script:Probe
    }
    function script:New-CmBackup {
        param($Dir)
        $backup=Join-Path $script:Fixture 'recovery'
        if ($script:Scenario -ne 'missing-backup') { Copy-Item -LiteralPath $Dir -Destination $backup -Recurse }
        $backup
    }
    function script:Copy-CmFiles {
        param($From,$To)
        if ($script:Scenario -eq 'nonterminating-copy') { Write-Error 'synthetic copy error'; return }
        Copy-Item -LiteralPath (Join-Path $From 'image.txt') -Destination (Join-Path $To 'image.txt') -Force
        Set-Content -LiteralPath (Join-Path $To 'candidate-only.txt') -Value 'must disappear after rollback'
        if ($script:Scenario -eq 'restore-delete-failure') {
            $script:FixtureLock=[IO.File]::Open((Join-Path $To 'candidate-only.txt'), 'Open', 'ReadWrite', 'None')
            throw 'synthetic partial copy failure'
        }
        if ($script:Scenario -in @('copy-failure','restore-failure')) { throw 'synthetic partial copy failure' }
    }
    if ($Scenario -eq 'restore-failure') {
        function script:Restore-CmBackup { param($Backup,$Dir,[switch]$Strict) Write-Error 'synthetic restore error' }
    }
    $failure=$null
    try { Update-CmHost -SourceDir $source } catch { $failure=$_ }
    finally { if ($script:FixtureLock) { $script:FixtureLock.Dispose() } }
    $image=(Get-Content -LiteralPath (Join-Path $installed 'image.txt') -Raw).Trim()
    $backupExists=Test-Path -LiteralPath (Join-Path $Scratch 'recovery')
    if ($script:Probe -and -not $script:Probe.Disposed) { throw 'Probe process was not disposed.' }
    if ($Scenario -eq 'startup-timeout' -and (-not $script:Probe.Killed)) { throw 'Timed out probe was not stopped.' }
    if ($Scenario -eq 'success') {
        if ($failure -or $image -ne 'candidate' -or $backupExists -or -not $script:Registered) { throw 'Healthy copy acceptance failed.' }
    } else {
        if (-not $failure -or $script:Registered) { throw 'Failed update was reported as successful.' }
        if ($Scenario -in @('restore-failure','restore-delete-failure')) {
            if (-not $backupExists -or (Get-Content (Join-Path $Scratch 'recovery\image.txt') -Raw).Trim() -ne 'known-good') { throw 'Failed rollback lost its backup.' }
            if ($failure.Exception.Message -notmatch 'backup') { throw 'Recovery path was not reported.' }
        } elseif ($image -ne 'known-good' -or $backupExists -or (Test-Path (Join-Path $installed 'candidate-only.txt'))) { throw 'Previous image was not recovered cleanly.' }
    }
    "PASS: $Scenario"
} $Scratch $Scenario
