param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference='Stop'
$version=[Diagnostics.FileVersionInfo]::GetVersionInfo($Executable).FileVersion
$profile=Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'ClassroomManager'
$before=@{}
if(Test-Path -LiteralPath $profile) {
    Get-ChildItem -LiteralPath $profile -File -Recurse | ForEach-Object { $before[$_.FullName]=(Get-FileHash -LiteralPath $_.FullName).Hash }
}
foreach($case in @(@{Version=$version;Exit=0},@{Version='0.0.0.0';Exit=2},@{Version='invalid';Exit=2})) {
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$Executable
    $start.Arguments='--startup-probe '+$case.Version
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.WindowStyle='Hidden'
    $process=[Diagnostics.Process]::Start($start)
    try {
        if(-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Actual startup probe timed out.' }
        if($process.ExitCode -ne $case.Exit) { throw "Actual startup probe exit $($process.ExitCode), expected $($case.Exit)." }
    } finally { $process.Dispose() }
}
$after=@{}
if(Test-Path -LiteralPath $profile) {
    Get-ChildItem -LiteralPath $profile -File -Recurse | ForEach-Object { $after[$_.FullName]=(Get-FileHash -LiteralPath $_.FullName).Hash }
}
if($before.Count -ne $after.Count) { throw 'Startup probe changed profile file count.' }
foreach($path in $before.Keys) { if($before[$path] -ne $after[$path]) { throw 'Startup probe changed profile bytes.' } }
'PASS: actual Host startup, version rejection and unchanged profile'
