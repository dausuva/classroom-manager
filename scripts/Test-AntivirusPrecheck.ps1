<#
.SYNOPSIS
Scans every release file with Microsoft Defender on this computer before it goes to winget, the Microsoft Store or the public release page, and writes a report. Nothing is uploaded anywhere.

.DESCRIPTION
winget and the Store scan submissions (Defender and other products) and refuse a potentially unwanted application. A false alarm found there costs days; found here it costs a minute.
For each file under -Path (a file or a folder, recursively) the script: records its SHA-256, size and signature state; runs `MpCmdRun -Scan -ScanType 3 -DisableRemediation` (Defender reports and does not remove or quarantine anything); and notes the verdict.
Before scanning it checks that Defender is on and that its signatures are not older than -MaxSignatureAgeDays (-UpdateSignatures fetches them first). A folder that Defender is told to skip would give a clean answer without scanning:
the exclusions list needs administrator rights to read, so -SelfTest instead writes the harmless EICAR test string into each folder, requires Defender to report it, and deletes it. Without -SelfTest the report says the folders were not proved.
Exit code: 0 = every file scanned clean and the checks held; 1 = a detection; 2 = the check is incomplete (Defender off, signatures old, a self-test not detected, a file that could not be scanned), unless -AllowIncomplete.
The report (Markdown and JSON, by default under artifacts\av-precheck) is NOT written into the folder of the release files.
It proves only what Microsoft Defender on THIS PC says today. It does not prove the other products a store runs; the report lists the manual check by hash (no upload) for that.
Runs under PowerShell 7 and Windows PowerShell 5.1.
#>
[CmdletBinding()]
param(
    [string[]]$Path,
    [string]$ReportFolder,
    [switch]$SelfTest,
    [switch]$UpdateSignatures,
    [double]$MaxSignatureAgeDays = 3,
    [switch]$AllowIncomplete
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path $here -Parent
Import-Module (Join-Path $here 'AntivirusPrecheck.psm1') -Force
if (-not $Path) { $Path = @(Join-Path $root 'artifacts\public\release') }
if (-not $ReportFolder) { $ReportFolder = Join-Path $root 'artifacts\av-precheck' }

$problems = New-Object System.Collections.Generic.List[string]   # make the check incomplete
$files = @()
foreach ($item in $Path) {
    if (-not (Test-Path -LiteralPath $item)) { throw "Not found: $item" }
    $files += @(Get-Item -LiteralPath $item | ForEach-Object { if ($_.PSIsContainer) { Get-ChildItem -LiteralPath $_.FullName -Recurse -File } else { $_ } })
}
$files = @($files | Where-Object { $_.Name -ne 'SHA256SUMS.txt' -and $_.Extension -ne '.sha256' } | Sort-Object FullName -Unique)
if ($files.Count -eq 0) { throw "No files to scan under: $($Path -join ', ')." }
$reportFull = [IO.Path]::GetFullPath($ReportFolder).TrimEnd('\') + '\'
foreach ($item in $Path) {
    $scanned = [IO.Path]::GetFullPath($item).TrimEnd('\') + '\'
    if ($reportFull.StartsWith($scanned, [StringComparison]::OrdinalIgnoreCase)) { throw 'The report folder must not be inside the folder being scanned: the report would end up among the release files.' }
}
$reportRoot = (Resolve-Path (New-Item -ItemType Directory -Force -Path $ReportFolder)).Path

# ---- Defender itself ----
$mpCmdRun = Find-MpCmdRun
if (-not $mpCmdRun) { throw 'Microsoft Defender (MpCmdRun.exe) was not found on this computer.' }
$status = $null
try { $status = Get-MpComputerStatus } catch { $problems.Add("Defender's status could not be read: $($_.Exception.Message)") }
if ($UpdateSignatures) { try { Update-MpSignature; $status = Get-MpComputerStatus } catch { $problems.Add("Signatures could not be updated: $($_.Exception.Message)") } }
$engine = ''; $signatures = ''; $signatureAge = $null
if ($status) {
    $engine = [string]$status.AMEngineVersion; $signatures = [string]$status.AntivirusSignatureVersion
    $signatureAge = Get-SignatureAgeDays -LastUpdated $status.AntivirusSignatureLastUpdated
    if (-not ($status.AMServiceEnabled -and $status.AntivirusEnabled)) { $problems.Add('Microsoft Defender antivirus is turned off (another antivirus product may be in charge); this scan does not count.') }
    if ($signatureAge -gt $MaxSignatureAgeDays) { $problems.Add("Defender's signatures are $signatureAge days old (limit $MaxSignatureAgeDays). Run with -UpdateSignatures.") }
}

function Invoke-DefenderScan([string]$File) {
    $saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $text = (& $mpCmdRun -Scan -ScanType 3 -File $File -DisableRemediation 2>&1 | ForEach-Object { "$_" }) -join "`n"; $code = $LASTEXITCODE } finally { $ErrorActionPreference = $saved }
    ConvertFrom-MpCmdRunOutput -Text $text -ExitCode $code
}

# ---- does Defender really look in these folders? ----
$folders = @($files | ForEach-Object { $_.DirectoryName } | Sort-Object -Unique)
$selfTestResult = @{}
foreach ($folder in $folders) {
    if (-not $SelfTest) { $selfTestResult[$folder] = 'not tried'; continue }
    $probe = Join-Path $folder ('av-selftest-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.com')
    try {
        [IO.File]::WriteAllText($probe, (Get-EicarTestText))
        $verdict = if (Test-Path -LiteralPath $probe) { Invoke-DefenderScan $probe } else { [pscustomobject]@{ Threats = @('removed by real-time protection') } }
        if ($verdict.Threats.Count -gt 0) { $selfTestResult[$folder] = 'detected' } else { $selfTestResult[$folder] = 'NOT detected'; $problems.Add("The EICAR test file in $folder was not reported: Defender may be told to skip this folder, so a clean result there would mean nothing.") }
    } finally { Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue }
}

# ---- the files ----
$results = foreach ($file in $files) {
    Write-Host "Scanning $($file.Name)..." -NoNewline
    # Real-time protection may refuse even to open a file it considers a virus: that is a detection, not a failure of this check.
    $sha = ''; $signature = 'n/a'; $blocked = $null
    try {
        $hasher = [System.Security.Cryptography.SHA256]::Create()
        try { $stream = [IO.File]::OpenRead($file.FullName); try { $sha = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() } finally { $stream.Dispose() } } finally { $hasher.Dispose() }
        if ($file.Extension -in '.exe', '.dll', '.msix', '.msi') { $signature = (Get-AuthenticodeSignature -LiteralPath $file.FullName).Status.ToString() }
    } catch { $blocked = $_.Exception.Message }
    if ($blocked) {
        $reason = if ($blocked -match 'virus|unwanted') { 'blocked by real-time protection: the file contains a virus or potentially unwanted software' } else { "could not be read: $blocked" }
        Write-Host " $reason" -ForegroundColor Red
        if ($blocked -notmatch 'virus|unwanted') { $problems.Add("$($file.Name): $reason") }
        [pscustomobject]@{ File = $file.Name; Folder = $file.DirectoryName; Sha256 = $sha; Bytes = $file.Length; Signature = $signature; Verdict = $reason; Clean = $false; Completed = ($blocked -match 'virus|unwanted') }
        continue
    }
    if (-not (Test-Path -LiteralPath $file.FullName)) { Write-Host ' REMOVED' -ForegroundColor Red; [pscustomobject]@{ File = $file.Name; Folder = $file.DirectoryName; Sha256 = $sha; Bytes = $file.Length; Signature = $signature; Verdict = 'file removed during the check'; Clean = $false; Completed = $true }; continue }
    $verdict = Invoke-DefenderScan $file.FullName
    Write-Host (' ' + $verdict.Summary) -ForegroundColor $(if ($verdict.Clean) { 'Green' } else { 'Red' })
    if (-not $verdict.Completed) { $problems.Add("$($file.Name): $($verdict.Summary)") }
    [pscustomobject]@{ File = $file.Name; Folder = $file.DirectoryName; Sha256 = $sha; Bytes = $file.Length; Signature = $signature; Verdict = $verdict.Summary; Clean = $verdict.Clean; Completed = $verdict.Completed }
}
$results = @($results)
$detections = @($results | Where-Object { -not $_.Clean -and $_.Completed })

# ---- report ----
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$outcome = if ($detections.Count -gt 0) { 'DETECTION' } elseif ($problems.Count -gt 0) { 'INCOMPLETE' } else { 'CLEAN' }
$lines = @("# Antivirus pre-check: $outcome", '',
    "Made $((Get-Date).ToString('yyyy-MM-dd HH:mm')) on $env:COMPUTERNAME with Microsoft Defender engine $engine, signatures $signatures (age $signatureAge days). Nothing was uploaded.", '',
    '| File | Verdict | Signature | SHA-256 |', '|---|---|---|---|')
foreach ($r in $results) { $lines += "| ``$($r.File)`` | $($r.Verdict) | $($r.Signature) | ``$($r.Sha256)`` |" }
$lines += ''
$lines += '## Did Defender really look in these folders?'
foreach ($folder in $folders) { $lines += "- $folder : EICAR self-test $($selfTestResult[$folder])" }
if (-not $SelfTest) { $lines += '- Not proved: run with -SelfTest. A folder in Defender''s exclusion list would give a clean answer without a scan.' }
if ($problems.Count -gt 0) { $lines += ''; $lines += '## Why the check is incomplete'; foreach ($p in $problems) { $lines += "- $p" } }
$lines += ''
$lines += '## What this does not prove'
$lines += '- It is one product (Microsoft Defender) on one computer, today. winget and the Store run Defender and other products on their side, with newer signatures.'
$lines += '- A manual look by hash, which uploads nothing: open `https://www.virustotal.com/gui/file/<SHA-256>` for each file. A file never seen before shows "not found"; that says nothing against it. Do not upload a file there before the release is public if it holds anything private.'
$lines += '- A false alarm in a store is answered at https://www.microsoft.com/en-us/wdsi/filesubmission (submit the file as a developer for Microsoft Defender); do it before the submission, not after a refusal.'
$markdown = Join-Path $reportRoot "av-precheck-$stamp.md"
[IO.File]::WriteAllText($markdown, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
[IO.File]::WriteAllText((Join-Path $reportRoot "av-precheck-$stamp.json"), ([pscustomobject]@{ outcome = $outcome; engine = $engine; signatures = $signatures; signatureAgeDays = $signatureAge; problems = @($problems); files = $results } | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "$outcome. Report: $markdown" -ForegroundColor $(if ($outcome -eq 'CLEAN') { 'Green' } else { 'Red' })
if ($detections.Count -gt 0) { exit 1 }
if ($problems.Count -gt 0 -and -not $AllowIncomplete) { exit 2 }
exit 0
