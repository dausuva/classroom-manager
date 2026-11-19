# Helpers of Test-AntivirusPrecheck.ps1 (T-240): finding Microsoft Defender's command-line scanner and reading what it says.
# Runs under PowerShell 7 and Windows PowerShell 5.1.

function Find-MpCmdRun {
    <# The scanner program of the newest Defender platform, or $null. #>
    $candidates = @(
        (Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform\CurrentVersion\MpCmdRun.exe'),
        (Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'))
    $platform = Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform'
    if (Test-Path $platform) {
        $candidates = @(Get-ChildItem -Path $platform -Directory -ErrorAction SilentlyContinue | Sort-Object { try { [version]($_.Name -replace '-.*$', '') } catch { [version]'0.0' } } -Descending |
            ForEach-Object { Join-Path $_.FullName 'MpCmdRun.exe' }) + $candidates
    }
    foreach ($candidate in $candidates) { if (Test-Path -LiteralPath $candidate) { return $candidate } }
    return $null
}

function ConvertFrom-MpCmdRunOutput {
    <# MpCmdRun -Scan prints "... found no threats." or "... found N threats." and, for each, "Threat : <name>". Exit code 0 = clean, 2 = threats or a failed scan. #>
    param([string]$Text, [int]$ExitCode)
    $threats = @([regex]::Matches($Text, '(?m)^\s*Threat\s*:\s*(.+?)\s*$') | ForEach-Object { $_.Groups[1].Value })
    $reported = [regex]::Match($Text, 'found (no|\d+) threats?')
    $completed = $Text -match 'Scan finished'
    $clean = $ExitCode -eq 0 -and $completed -and $reported.Success -and $reported.Groups[1].Value -eq 'no' -and $threats.Count -eq 0
    [pscustomobject]@{
        Clean = $clean
        Threats = $threats
        Completed = $completed -and $reported.Success
        Summary = if ($clean) { 'no threats found' } elseif ($threats.Count -gt 0) { 'threat: ' + ($threats -join ', ') } elseif (-not $completed) { "the scan did not finish (exit code $ExitCode)" } else { "exit code $ExitCode" }
    }
}

function Get-EicarTestText {
    <# The industry-standard harmless test string every scanner reports as a virus. Written in two pieces so that this file is not itself reported. #>
    'X5O!P%@AP[4\PZX54(P^)7CC)7}$' + 'EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*'
}

function Get-SignatureAgeDays {
    param([datetime]$LastUpdated, [datetime]$Now = (Get-Date))
    [math]::Round(($Now - $LastUpdated).TotalDays, 1)
}

Export-ModuleMember -Function Find-MpCmdRun, ConvertFrom-MpCmdRunOutput, Get-EicarTestText, Get-SignatureAgeDays
