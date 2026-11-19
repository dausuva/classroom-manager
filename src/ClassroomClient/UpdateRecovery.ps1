param(
    [Parameter(Mandatory)][string]$Transaction,
    [Parameter(Mandatory)][string]$ActiveMarker,
    [Parameter(Mandatory)][string]$Candidate,
    [Parameter(Mandatory)][string]$PreviousExecutable,
    [Parameter(Mandatory)][string]$PreviousConfig,
    [Parameter(Mandatory)][string]$PreviousCertificate,
    [Parameter(Mandatory)][string]$Target,
    [Parameter(Mandatory)][string]$ExpectedSha256,
    [Parameter(Mandatory)][string]$PreviousSha256,
    [Parameter(Mandatory)][string]$ConfigSha256,
    [Parameter(Mandatory)][string]$CertificateSha256,
    [Parameter(Mandatory)][string]$CandidateVersion,
    [Parameter(Mandatory)][string]$PreviousVersion,
    [Parameter(Mandatory)][string]$LogFile,
    [Parameter(Mandatory)][string]$Uploaded,
    [Parameter(Mandatory)][string]$DataDirectory,
    [ValidateRange(0, 300)][int]$CandidateHealthSeconds = 60,
    [ValidateRange(0, 300)][int]$RecoveryHealthSeconds = 45
)
$ErrorActionPreference = 'Stop'
function Log([string]$Message) {
    try { Add-Content -LiteralPath $LogFile -Value "$(Get-Date -Format o) $Message" -Encoding UTF8 } catch { }
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Prune-OlderTransactions {
    $root = [IO.Path]::GetFullPath([IO.Path]::GetDirectoryName($Transaction))
    $current = [IO.Path]::GetFullPath($Transaction)
    foreach ($entry in Get-ChildItem -LiteralPath $root -Directory -ErrorAction Stop) {
        if ([IO.Path]::GetFullPath($entry.FullName).Equals($current, [StringComparison]::OrdinalIgnoreCase)) { continue }
        Remove-Item -LiteralPath $entry.FullName -Recurse -Force -ErrorAction Stop
    }
}
function Stop-Client {
    Stop-Service -Name 'ClassroomManagerClient' -Force -ErrorAction SilentlyContinue
    $limit = [DateTime]::UtcNow.AddSeconds(25)
    do {
        Get-Process -Name 'ClassroomClient' -ErrorAction SilentlyContinue | Where-Object {
            try { $_.Path -and [IO.Path]::GetFullPath($_.Path).Equals($Target, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
        } | Stop-Process -Force -ErrorAction SilentlyContinue
        $service = Get-Service -Name 'ClassroomManagerClient' -ErrorAction Stop
        $running = @(Get-Process -Name 'ClassroomClient' -ErrorAction SilentlyContinue | Where-Object {
            try { $_.Path -and [IO.Path]::GetFullPath($_.Path).Equals($Target, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
        })
        if ($service.Status -eq 'Stopped' -and $running.Count -eq 0) { return $true }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $limit)
    return $false
}
# The probe is run by the build whose acceptance is being decided: the candidate for the new Client, but the restored previous
# executable when recovering. A candidate that failed may be unable to run the probe at all (a 2026-10-31 drill pushed one that
# exits 3 for any argument), and it must not be able to veto the rollback of the build that is known to work.
function Start-Client([string]$Version, [string]$Sha256, [int]$Seconds, [string]$Verifier) {
    try { Start-Service -Name 'ClassroomManagerClient' -ErrorAction Stop } catch { Log "Service start request failed ($($_.Exception.GetType().Name))." }
    $limit = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        try {
            # Start-Process joins the list with spaces and quotes nothing: an unquoted "C:\Program Files\..." reaches the probe as several
            # arguments, which it rejects with exit 2 (the 2026-10-31 ACER update rolled back a healthy build for this reason).
            $process = Start-Process -FilePath $Verifier -ArgumentList @('--verify-update-service', "`"$Target`"", $Version, $Sha256) -WindowStyle Hidden -PassThru
            if ($process.WaitForExit(9000) -and $process.ExitCode -eq 0) { return $true }
            if (-not $process.HasExited) { $process.Kill($true) }
            Log "Authenticated service health attempt returned $($process.ExitCode)."
        } catch { Log "Authenticated service health attempt failed ($($_.Exception.GetType().Name))." }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $limit)
    return $false
}
function Restore-Previous([string]$Reason) {
    Log "New Client failed acceptance: $Reason. Restoring the previous executable and settings."
    if (-not (Stop-Client)) { Log 'ROLLBACK FAILED: the service or image did not stop.'; return $false }
    try {
        Copy-Item -LiteralPath $PreviousExecutable -Destination $Target -Force
        if ((Hash $Target) -ne $PreviousSha256) { throw 'previous executable hash mismatch' }
        if (-not (Test-Path -LiteralPath $PreviousConfig) -or (Hash $PreviousConfig) -ne $ConfigSha256) { throw 'saved configuration snapshot failed verification' }
        if (-not (Test-Path -LiteralPath $PreviousCertificate) -or (Hash $PreviousCertificate) -ne $CertificateSha256) { throw 'saved TLS certificate snapshot failed verification' }
        Copy-Item -LiteralPath $PreviousConfig -Destination (Join-Path $DataDirectory 'client.json') -Force
        Copy-Item -LiteralPath $PreviousCertificate -Destination (Join-Path $DataDirectory 'tls-cert.pfx') -Force
        & icacls.exe (Join-Path $DataDirectory 'client.json') /inheritance:r /grant:r '*S-1-5-18:(F)' '*S-1-5-32-544:(F)' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'could not restrict restored client configuration permissions' }
        & icacls.exe (Join-Path $DataDirectory 'tls-cert.pfx') /inheritance:r /grant:r '*S-1-5-18:(F)' '*S-1-5-32-544:(F)' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'could not restrict restored TLS certificate permissions' }
        if ((Hash (Join-Path $DataDirectory 'client.json')) -ne $ConfigSha256 -or
            (Hash (Join-Path $DataDirectory 'tls-cert.pfx')) -ne $CertificateSha256) { throw 'configuration restoration verification failed' }
        if (Start-Client $PreviousVersion $PreviousSha256 $RecoveryHealthSeconds $Target) {
            Log 'ROLLBACK SUCCEEDED: the previous signed Client passed the authenticated service health check.'
            Remove-Item -LiteralPath $ActiveMarker -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $Uploaded -Force -ErrorAction SilentlyContinue
            return $true
        }
        Log 'ROLLBACK FAILED: the previous Client did not pass its authenticated service health check.'
        return $false
    } catch {
        Log "ROLLBACK FAILED: recovery exception ($($_.Exception.GetType().Name))."
        try { Start-Service -Name 'ClassroomManagerClient' -ErrorAction SilentlyContinue } catch { }
        return $false
    }
}

function Invoke-ClientUpdateTransaction {
    Log 'Update transaction started.'
    try {
        if ((Hash $Candidate) -ne $ExpectedSha256 -or (Hash $Target) -ne $PreviousSha256 -or
            (Hash $PreviousExecutable) -ne $PreviousSha256 -or
            (Hash (Join-Path $DataDirectory 'client.json')) -ne $ConfigSha256 -or
            (Hash $PreviousConfig) -ne $ConfigSha256 -or
            (Hash (Join-Path $DataDirectory 'tls-cert.pfx')) -ne $CertificateSha256 -or
            (Hash $PreviousCertificate) -ne $CertificateSha256) { throw 'pre-update file integrity changed' }
        if (-not (Stop-Client)) {
            Start-Service -Name 'ClassroomManagerClient' -ErrorAction SilentlyContinue
            throw 'the service or Client image did not stop within 25 seconds; the installed files were not changed'
        }
        if ((Hash $Target) -ne $PreviousSha256) { throw 'the installed executable changed while stopping' }
        Copy-Item -LiteralPath $Candidate -Destination $Target -Force
        if ((Hash $Target) -ne $ExpectedSha256) { throw 'copied executable hash mismatch' }
        if ((Hash (Join-Path $DataDirectory 'client.json')) -ne $ConfigSha256 -or
            (Hash (Join-Path $DataDirectory 'tls-cert.pfx')) -ne $CertificateSha256) { throw 'configuration or TLS certificate changed during update' }
        if (-not (Start-Client $CandidateVersion $ExpectedSha256 $CandidateHealthSeconds $Candidate)) {
            if (-not (Restore-Previous 'the new service did not pass the bounded authenticated version check')) {
                Log 'CRITICAL: both update acceptance and rollback failed. The protected recovery files and active marker were retained.'
            }
            return 1
        }
        if ((Hash (Join-Path $DataDirectory 'client.json')) -ne $ConfigSha256 -or
            (Hash (Join-Path $DataDirectory 'tls-cert.pfx')) -ne $CertificateSha256) {
            if (-not (Restore-Previous 'service startup changed saved configuration or TLS material')) {
                Log 'CRITICAL: configuration check and rollback failed. The protected recovery files and active marker were retained.'
            }
            return 1
        }
        Log "Update accepted: version $CandidateVersion passed the authenticated local service health check."
        Remove-Item -LiteralPath $ActiveMarker -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $Uploaded -Force -ErrorAction SilentlyContinue
        try { Prune-OlderTransactions; Log 'Older completed update snapshots were pruned; the immediately previous Client backup was retained.' }
        catch { Log "Older recovery snapshots were retained because cleanup failed ($($_.Exception.GetType().Name))." }
        return 0
    } catch {
        Log "Update transaction failed before acceptance ($($_.Exception.GetType().Name))."
        try {
            if ((Hash $Target) -ne $PreviousSha256) {
                if (-not (Restore-Previous 'an update operation failed')) { Log 'CRITICAL: rollback failed; recovery material was retained.' }
            } elseif (Start-Client $PreviousVersion $PreviousSha256 $RecoveryHealthSeconds $Target) {
                Log 'Previous Client remains installed and passed authenticated service health.'
                Remove-Item -LiteralPath $ActiveMarker -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $Uploaded -Force -ErrorAction SilentlyContinue
            } else { Log 'CRITICAL: previous Client did not pass health; the active marker and recovery files were retained.' }
        } catch { Log "CRITICAL: recovery handling failed ($($_.Exception.GetType().Name)); recovery material was retained." }
        return 1
    }
}

$result = Invoke-ClientUpdateTransaction
if ($MyInvocation.InvocationName -eq '.') { return $result }
exit $result
