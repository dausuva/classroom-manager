# Compile and execute the real Inno wrappers with harmless synthetic child scripts, never a real install.
param([Parameter(Mandatory)][string]$RepositoryRoot, [Parameter(Mandatory)][string]$Scratch)
$ErrorActionPreference = 'Stop'
$compiler = (Get-Command ISCC.exe -ErrorAction Stop).Source
New-Item -ItemType Directory -Path $Scratch -Force | Out-Null
$utf8Bom = New-Object System.Text.UTF8Encoding $true
foreach ($component in 'Host','Client') {
    $folder = Join-Path $Scratch $component
    $app = Join-Path $folder 'app'
    $deploy = Join-Path $folder 'deploy'
    $out = Join-Path $folder 'out'
    New-Item -ItemType Directory -Path $app,$deploy,$out -Force | Out-Null
    Set-Content (Join-Path $app 'fixture.txt') 'Synthetic payload only'
    foreach ($name in 'ClassroomSetup.psm1','Setup.ps1','SetupText.lt.psd1') { Set-Content (Join-Path $deploy $name) '# Synthetic fixture' }
    Set-Content (Join-Path $deploy 'Setup-Gui.ps1') 'exit 92'
    foreach ($file in 'ComponentSetup.isinc','Lithuanian.isl') { Copy-Item (Join-Path $RepositoryRoot "packaging\windows\$file") $folder }
    $source = [IO.File]::ReadAllText((Join-Path $RepositoryRoot "packaging\windows\Classroom$component.iss"))
    # Only this temporary fixture drops elevation and icons/license paths. It cannot install product code.
    $source = $source.Replace('PrivilegesRequired=admin','PrivilegesRequired=lowest')
    $source = [regex]::Replace($source, '(?m)^(SetupIconFile|LicenseFile)=[^\r\n]+\r?\n', '')
    $iss = Join-Path $folder 'fixture.iss'
    [IO.File]::WriteAllText($iss,$source,$utf8Bom)
    foreach ($result in 0,23,3010) {
        $record = Join-Path $folder "called-$result.txt"
        $child = "param(`$Component,`$SourceDir,`$Language)`r`n[IO.File]::WriteAllText('$($record.Replace("'","''"))',`"`$Component|`$Language`")`r`nexit $result"
        [IO.File]::WriteAllText((Join-Path $deploy 'Setup-Bundled.ps1'),$child,$utf8Bom)
        & $compiler '/Q' '/DAppVersion=1.2.0' "/DSourceDir=$app" "/DDeployDir=$deploy" "/DOutputDir=$out" $iss | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Synthetic Inno compilation failed.' }
        $exe = @(Get-ChildItem $out -Filter '*.exe')[0].FullName
        $process = Start-Process -FilePath $exe -ArgumentList '/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LANG=en' -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne $result) { throw "$component expected exit $result, got $($process.ExitCode)." }
        if (-not (Test-Path $record) -or [IO.File]::ReadAllText($record) -ne "$component|en") { throw 'Silent wrapper did not invoke the expected synthetic child.' }
    }
}
Write-Output 'PASS: real Host/Client Inno wrappers invoke unattended children and preserve success/failure/restart exit codes.'
