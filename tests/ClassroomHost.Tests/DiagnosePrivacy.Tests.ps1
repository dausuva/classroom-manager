param([Parameter(Mandatory = $true)][string]$RepositoryRoot,
      [Parameter(Mandatory = $true)][ValidateSet('Nested', 'Malformed', 'InvalidTypes', 'IncludeLogs', 'Legacy', 'RootArray', 'Empty')][string]$Scenario)
$ErrorActionPreference = 'Stop'

# Run the real packaged script, isolated from real settings and diagnostic providers.
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('cm-diagnose-test-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $env:ProgramData = $scratch
    $env:APPDATA = $scratch
    $env:ProgramFiles = $scratch
    $env:COMPUTERNAME = 'Synthetic-PC'
    $env:USERNAME = 'Synthetic-User'
    $settings = New-Item -ItemType Directory -Path (Join-Path $scratch 'ClassroomManager')
    $secret = 'SYNTHETIC-DO-NOT-EXPORT-790f4b'
    $json = @{
        tcpPort = 5050; udpPort = 5051; useTls = $true; PSK = $secret
        accessKeys = @(@{ name = $secret; role = 'Admin'; psk = $secret }, @{ previousKey = $secret })
        allowedRoots = @($secret); allowedUpdateSignerCertificateSha256 = @($secret)
        computers = @(@{ password = $secret }); unknown = @{ nested = @(@{ password = $secret }) }
    } | ConvertTo-Json -Depth 10
    if ($Scenario -eq 'Malformed') { $json = '{"psk":"' + $secret + '", broken ' + $secret }
    if ($Scenario -eq 'RootArray') { $json = '[' + $json + ']' }
    if ($Scenario -eq 'Empty') { $json = '' }
    if ($Scenario -eq 'Legacy') { $json = '{"psk":"' + $secret + '","tcpPort":5050,"udpPort":5051,"useTls":false}' }
    if ($Scenario -eq 'InvalidTypes') {
        $json = @{ tcpPort = $secret; udpPort = @{ password = $secret }; useTls = $secret; psk = @{ nested = $secret }; accessKeys = $secret } | ConvertTo-Json
    }
    foreach ($file in 'client.json', 'host.json') { Set-Content -LiteralPath (Join-Path $settings $file) -Value $json }
    foreach ($file in 'client.log', 'update.log') { Set-Content -LiteralPath (Join-Path $settings $file) -Value $secret }
    Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'deploy/tools/Diagnose.ps1') -Destination $scratch
    function Get-CimInstance { }
    function Get-NetConnectionProfile { }
    function Get-NetIPAddress { }
    function Get-Service { }
    function Get-NetTCPConnection { }
    function Get-Process { }
    function Get-ItemProperty { }
    function Get-NetFirewallRule { }
    function Select-String { return $false }

    $options = @{ IncludeLogs = ($Scenario -eq 'IncludeLogs') }
    $console = (& (Join-Path $scratch 'Diagnose.ps1') @options *>&1 | Out-String -Width 32767)
    $reports = @(Get-ChildItem -LiteralPath $scratch -Filter 'Diagnostics-*.txt')
    if ($reports.Count -ne 1) { throw 'Expected exactly one saved report.' }
    $saved = Get-Content -LiteralPath $reports[0].FullName -Raw
    foreach ($output in @($console, $saved)) {
        if ($Scenario -eq 'IncludeLogs') {
            if (-not $output.Contains($secret) -or -not $output.Contains('INCLUDED - may contain credentials')) { throw 'Explicit raw log export must carry a warning.' }
        } elseif ($output.Contains($secret)) { throw 'A synthetic secret escaped into a report.' }
        if ($Scenario -eq 'Nested' -and (-not $output.Contains('"tcpPort":5050') -or -not $output.Contains('"accessKeysCount":2'))) { throw 'Useful allowlisted diagnostics were lost.' }
        if ($Scenario -in @('Malformed', 'RootArray', 'Empty') -and -not $output.Contains('(unavailable: invalid configuration)')) { throw 'Expected a safe parsing failure.' }
        if ($Scenario -eq 'Legacy' -and -not $output.Contains('"primaryKeyConfigured":true')) { throw 'Expected legacy key presence without its value.' }
        if ($Scenario -eq 'InvalidTypes' -and -not $output.Contains('(missing or invalid)')) { throw 'Expected safe invalid-field status.' }
    }
    foreach ($line in (Get-Content -LiteralPath $reports[0].FullName)) {
        if (-not $console.Contains($line)) { throw 'Console and saved report differ.' }
    }
    Write-Output "PASS: $Scenario"
} finally {
    # Only remove the fresh, explicitly named test directory, never a user data folder.
    if ($scratch -and (Split-Path $scratch -Leaf).StartsWith('cm-diagnose-test-')) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
