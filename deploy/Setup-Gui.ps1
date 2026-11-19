<#
.SYNOPSIS
  Classroom Manager setup window: install, update, modify, repair or uninstall the Client
  (student PC) or the Host (teacher PC). Elevates itself if needed.

.DESCRIPTION
  Same logic as Setup.ps1 (both use ClassroomSetup.psm1). Settings > Apps > Classroom Manager >
  Modify opens this window from the copy kept in the program's "setup" folder.

.EXAMPLE
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\Setup-Gui.ps1
  powershell.exe -ExecutionPolicy Bypass -File .\deploy\Setup-Gui.ps1 -Component Host
#>
param(
    [ValidateSet("", "Client", "Host")][string]$Component = "",
    [string]$SourceDir = "",
    # "lt" or "en"; empty follows the Windows display language. The installers pass the language they run in.
    [ValidateSet("", "en", "lt")][string]$Language = "",
    # Draws the window into this PNG file and exits: nothing is installed or changed, and no administrator
    # rights are asked for (scripts\Update-HelpScreenshots.ps1 takes the setup pictures this way).
    [string]$ScreenshotPath = ""
)

$ErrorActionPreference = "Stop"
$script:SetupFailed = $false
Import-Module (Join-Path $PSScriptRoot "ClassroomSetup.psm1") -Force
Set-CmLanguage $Language
# The text in the setup's language: T "Remove {0} from this PC?" $what
function T([string]$Text) { Get-CmText $Text $args }

# ---- Elevate --------------------------------------------------------------------
if (-not $ScreenshotPath -and -not (Test-CmAdmin)) {
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", "`"$PSCommandPath`"")
    if ($Component) { $argList += @("-Component", $Component) }
    if ($SourceDir) { $argList += @("-SourceDir", "`"$SourceDir`"") }
    if ($Language) { $argList += @("-Language", $Language) }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit
}

try {
    Add-Type -Name Win -Namespace ConsoleWindow -MemberDefinition '
        [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    '
    [ConsoleWindow.Win]::ShowWindow([ConsoleWindow.Win]::GetConsoleWindow(), 0) | Out-Null
} catch { }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$kitRoot = Split-Path $PSScriptRoot -Parent
$kit = Get-CmKit $PSScriptRoot   # classroom-kit.json (or a legacy kit-settings.json) beside these scripts; read only

# ---- Small builders ----------------------------------------------------------------
function New-Ctl($type, $parent, $x, $y, $w, $h, $text) {
    $c = New-Object "System.Windows.Forms.$type"
    $c.Location = New-Object System.Drawing.Point($x, $y)
    $c.Size = New-Object System.Drawing.Size($w, $h)
    if ($null -ne $text) { $c.Text = $text }
    $parent.Controls.Add($c)
    return $c
}

$form = New-Object System.Windows.Forms.Form
$form.Text = (T "Classroom Manager Setup")
$form.ClientSize = New-Object System.Drawing.Size(600, 769)
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedDialog"
$form.MaximizeBox = $false
$form.Font = New-Object System.Drawing.Font("Segoe UI", 9)

# -- Component + status
$grpComp = New-Ctl GroupBox $form 12 10 576 118 (T "Part")
$rbClient = New-Ctl RadioButton $grpComp 14 20 250 22 (T "Client - student PC")
$rbHost   = New-Ctl RadioButton $grpComp 280 20 290 22 (T "Host - teacher PC")
$lblState = New-Ctl Label $grpComp 14 46 470 66 ""
$btnRefresh = New-Ctl Button $grpComp 490 44 72 26 (T "Refresh")

# -- Action
$grpAct = New-Ctl GroupBox $form 12 134 576 52 (T "Action")
$rbInstall   = New-Ctl RadioButton $grpAct 14 20 88 22 (T "Install")
$rbUpdate    = New-Ctl RadioButton $grpAct 106 20 88 22 (T "Update")
$rbModify    = New-Ctl RadioButton $grpAct 198 20 88 22 (T "Modify")
$rbRepair    = New-Ctl RadioButton $grpAct 290 20 88 22 (T "Repair")
$rbUninstall = New-Ctl RadioButton $grpAct 382 20 120 22 (T "Uninstall")
$actions = @($rbInstall, $rbUpdate, $rbModify, $rbRepair, $rbUninstall)
# The logic works with the English action names; the buttons show them in the setup's language.
foreach ($pair in @(@($rbInstall, "Install"), @($rbUpdate, "Update"), @($rbModify, "Modify"), @($rbRepair, "Repair"), @($rbUninstall, "Uninstall"))) { $pair[0].Tag = $pair[1] }

# -- Source
$lblSource = New-Ctl Label $form 12 194 560 18 (T "Program files to install from (publish-client / publish-host folder):")
$txtSource = New-Ctl TextBox $form 12 214 480 24 ""
$btnSource = New-Ctl Button $form 498 212 90 26 (T "Browse...")
$chkForce  = New-Ctl CheckBox $form 12 242 400 22 (T "Allow an older or the same version (roll back / reinstall)")

# -- Client settings
$grpClient = New-Ctl GroupBox $form 12 268 576 174 (T "Client settings")
$lblKey  = New-Ctl Label $grpClient 14 22 540 18 (T "Shared key (12+ characters, same as in the teacher app):")
$txtKey  = New-Ctl TextBox $grpClient 14 42 250 24 ""
$txtKey.UseSystemPasswordChar = $true
$btnNewKey = New-Ctl Button $grpClient 270 41 94 26 (T "New key")
$chkShow = New-Ctl CheckBox $grpClient 372 44 76 22 (T "Show")
New-Ctl Label $grpClient 14 78 62 20 (T "TCP port:") | Out-Null
$txtTcp  = New-Ctl TextBox $grpClient 78 75 60 24 "5050"
New-Ctl Label $grpClient 156 78 62 20 (T "UDP port:") | Out-Null
$txtUdp  = New-Ctl TextBox $grpClient 220 75 60 24 "5051"
New-Ctl Label $grpClient 14 110 150 20 (T "Folders teacher may use:") | Out-Null
$txtRoots = New-Ctl TextBox $grpClient 166 107 395 24 "C:\Users"

# -- Host settings
$grpHost = New-Ctl GroupBox $form 12 268 576 174 (T "Host settings")
New-Ctl Label $grpHost 14 22 540 18 (T "Install folder:") | Out-Null
$txtHostDir = New-Ctl TextBox $grpHost 14 42 450 24 "$env:ProgramFiles\ClassroomHost"
$btnHostDir = New-Ctl Button $grpHost 472 40 90 26 (T "Browse...")
$lblHostKey = New-Ctl Label $grpHost 14 72 548 18 (T "Shared key for the teacher app:")
$txtHostKey = New-Ctl TextBox $grpHost 14 92 330 24 ""
$btnNewHostKey = New-Ctl Button $grpHost 352 90 94 26 (T "New key")
$chkDesktop = New-Ctl CheckBox $grpHost 14 122 190 22 (T "Desktop shortcut")
$chkStartup = New-Ctl CheckBox $grpHost 210 122 350 22 (T "Start the teacher app when Windows starts")
$chkPath    = New-Ctl CheckBox $grpHost 14 146 548 22 (T "Add to PATH: run ClassroomCli from any command prompt")

# -- Options
$grpOpt = New-Ctl GroupBox $form 12 448 576 76 (T "Options")
$chkNet  = New-Ctl CheckBox $grpOpt 14 20 550 22 (T "Set this PC's network to Private if Windows marks it Public (trusted school networks only)")
$chkData = New-Ctl CheckBox $grpOpt 14 44 550 22 ""
$script:kitKey = Get-CmKitKey $PSScriptRoot

# -- Run
$btnRun   = New-Ctl Button $form 12 532 140 34 (T "Install")
$btnRun.BackColor = [System.Drawing.Color]::FromArgb(0, 120, 215)
$btnRun.ForeColor = [System.Drawing.Color]::White
$btnClose = New-Ctl Button $form 160 532 90 34 (T "Close")
$lblHint  = New-Ctl Label $form 262 534 326 34 ""
$lblHint.ForeColor = [System.Drawing.Color]::DimGray

$txtLog = New-Ctl TextBox $form 12 574 576 160 ""
$txtLog.Multiline = $true; $txtLog.ReadOnly = $true; $txtLog.ScrollBars = "Vertical"
$txtLog.Font = New-Object System.Drawing.Font("Consolas", 8.5)
$lblStatus = New-Ctl Label $form 12 740 576 20 (T "Ready.")

function Append-Log([string]$text) {
    $txtLog.AppendText("$text`r`n")
    [System.Windows.Forms.Application]::DoEvents()
}
Set-CmLogger { param([string]$m) Append-Log $m }
Set-CmRuntimeDownloadPrompt {
    param([string]$Question)
    return ([System.Windows.Forms.MessageBox]::Show($Question, (T "Classroom Manager Setup"),
        [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question) -eq [System.Windows.Forms.DialogResult]::Yes)
}

function Get-Status { if ($rbClient.Checked) { Get-CmClientStatus } else { Get-CmHostStatus } }

function Get-DefaultSource {
    $name = $(if ($rbClient.Checked) { "publish-client" } else { "publish-host" })
    $exe  = $(if ($rbClient.Checked) { "ClassroomClient.exe" } else { "ClassroomHost.exe" })
    $p = Join-Path $kitRoot $name
    if (Test-Path (Join-Path $p $exe)) { return $p }
    # A kit can also carry only the small build that uses the .NET Desktop Runtime on the PC.
    $shared = Join-Path $kitRoot "$name-shared-runtime"
    if (Test-Path (Join-Path $shared $exe)) { return $shared }
    return ""
}

function Selected-Action { foreach ($r in $actions) { if ($r.Checked) { return [string]$r.Tag } }; return "" }

function Refresh-State {
    $s = Get-Status
    $lblState.Text = $(if ($rbClient.Checked) { Format-CmClientStatus $s } else { Format-CmHostStatus $s })
    $rbInstall.Enabled   = -not $s.Installed
    $rbUpdate.Enabled    = $s.Installed
    $rbModify.Enabled    = $s.Installed
    $rbUninstall.Enabled = $s.Installed
    $rbRepair.Enabled    = $true
    if (-not (Selected-Action) -or -not ($actions | Where-Object { $_.Checked -and $_.Enabled })) {
        if ($s.Installed) { $rbUpdate.Checked = $true } else { $rbInstall.Checked = $true }
    }
    if ($rbClient.Checked) {
        $txtTcp.Text = "$($s.TcpPort)"; $txtUdp.Text = "$($s.UdpPort)"
        $txtRoots.Text = ($s.AllowedRoots -join ", ")
        if (-not $s.Installed -and $kit) {
            if ($kit.PSObject.Properties["tcpPort"]) { $txtTcp.Text = "$($kit.tcpPort)" }
            if ($kit.PSObject.Properties["udpPort"]) { $txtUdp.Text = "$($kit.udpPort)" }
            if ($kit.PSObject.Properties["allowedRoots"]) { $txtRoots.Text = (@($kit.allowedRoots) -join ", ") }
        }
    } else {
        $txtHostDir.Text = $s.InstallDir
        $chkDesktop.Checked = $(if ($s.Installed) { $s.DesktopShortcut } else { $true })
        $chkStartup.Checked = $s.StartWithWindows
        $chkPath.Checked = $(if ($s.Installed) { $s.OnPath } else { $true })
    }
    $chkNet.Checked = $false
    $chkNet.Text = $(if (Get-CmPublicNetworks) { (T "Set this PC's network to Private (it is Public now, which blocks Classroom Manager)") }
                     else { (T "Set this PC's network to Private if Windows marks it Public (none are Public now)") })
    Update-Layout
}

$script:keyDefaultsFor = ""
function Set-KeyDefaults([string]$a, [bool]$client) {
    # Fill the key boxes once per component/action: the key of classroom-kit.json, else (teacher app) the key already
    # saved for this Windows user, else a new random key. Nothing is saved on the kit (T-226).
    $tag = "$client/$a"
    if ($script:keyDefaultsFor -eq $tag) { return }
    $script:keyDefaultsFor = $tag
    $script:kitKey = Get-CmKitKey $PSScriptRoot
    if ($client) {
        if ($a -eq "Install") {
            if ($script:kitKey) { $txtKey.Text = $script:kitKey; $lblKey.Text = (T "Shared key (from classroom-kit.json):"); $chkShow.Checked = $false }
            else { $txtKey.Text = New-CmKey; $lblKey.Text = (T "Shared key (new random key - write it down, every PC needs the same key):"); $chkShow.Checked = $true }
        } else { $txtKey.Text = "" }
    } else {
        $existing = Get-CmHostKey
        if ($existing) { $txtHostKey.Text = $existing; $lblHostKey.Text = (T "Shared key for the teacher app (already saved for Windows user {0}):" $env:USERNAME) }
        elseif ($script:kitKey) { $txtHostKey.Text = $script:kitKey; $lblHostKey.Text = (T "Shared key for the teacher app (from classroom-kit.json; saved for Windows user {0}):" $env:USERNAME) }
        else { $txtHostKey.Text = New-CmKey; $lblHostKey.Text = (T "Shared key for the teacher app (new random key; saved for Windows user {0}):" $env:USERNAME) }
    }
}

function Update-Layout {
    $a = Selected-Action
    $client = $rbClient.Checked
    $grpClient.Visible = $client
    $grpHost.Visible = -not $client

    $needsSource = $a -in @("Install", "Update", "Repair")
    $txtSource.Enabled = $needsSource; $btnSource.Enabled = $needsSource
    $chkForce.Visible = ($a -eq "Update")
    if ($needsSource -and -not $txtSource.Text) { $txtSource.Text = Get-DefaultSource }
    $lblSource.Text = $(if ($a -eq "Repair") { (T "Program files to reinstall from (optional - leave empty to only fix settings):") }
                        else { (T "Program files to install from (publish-client / publish-host folder):") })

    $editClient = $client -and $a -in @("Install", "Modify")
    foreach ($c in @($txtKey, $chkShow, $txtTcp, $txtUdp, $txtRoots)) { $c.Enabled = $editClient }
    $lblKey.Text = $(if ($a -eq "Modify") { (T "New shared key (leave empty to keep the current key):") }
                     else { (T "Shared key (12+ characters, same as in the teacher app):") })
    Set-KeyDefaults $a $client

    $txtHostKey.Enabled = (-not $client) -and $a -in @("Install", "Modify")
    $btnNewHostKey.Enabled = $txtHostKey.Enabled
    $btnNewKey.Enabled = $editClient
    $txtHostDir.Enabled = (-not $client) -and $a -eq "Install"
    $btnHostDir.Enabled = $txtHostDir.Enabled
    $chkDesktop.Enabled = (-not $client) -and $a -in @("Install", "Modify")
    $chkStartup.Enabled = $chkDesktop.Enabled
    $chkPath.Enabled = $chkDesktop.Enabled

    $chkNet.Enabled = $a -in @("Install", "Repair")
    $chkData.Visible = ($a -eq "Uninstall")
    if ($client) { $chkData.Text = (T "Also delete settings, logs and certificate (untick to keep them for a reinstall)") }
    else { $chkData.Text = (T "Also delete every teacher's saved settings (shared key, computer list)") }

    $btnRun.Text = (T $a)
    $lblHint.Text = switch ($a) {
        "Install"   { T "Installs and registers it in Settings > Apps." }
        "Update"    { T "Replaces the program, keeps settings, rolls back if it fails." }
        "Modify"    { $(if ($client) { T "Changes key, ports or folders and restarts the service." } else { T "Changes shortcuts, startup and PATH." }) }
        "Repair"    { T "Re-applies permissions, firewall, service and shortcuts." }
        "Uninstall" { T "Removes the program from this PC." }
        default     { "" }
    }
}

$rbClient.Add_CheckedChanged({ if ($rbClient.Checked) { $txtSource.Text = ""; $chkData.Checked = $true; $script:keyDefaultsFor = ""; Refresh-State } })
$rbHost.Add_CheckedChanged({ if ($rbHost.Checked) { $txtSource.Text = ""; $chkData.Checked = $false; $script:keyDefaultsFor = ""; Refresh-State } })
foreach ($r in $actions) { $r.Add_CheckedChanged({ Update-Layout }) }
$btnRefresh.Add_Click({ $script:keyDefaultsFor = ""; Refresh-State })
$btnNewKey.Add_Click({ $txtKey.Text = New-CmKey; $chkShow.Checked = $true })
$btnNewHostKey.Add_Click({ $txtHostKey.Text = New-CmKey })
$chkShow.Add_CheckedChanged({ $txtKey.UseSystemPasswordChar = -not $chkShow.Checked })
$btnClose.Add_Click({ $form.Close() })

$pickFolder = {
    param($box, $title)
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = $title
    if ($box.Text -and (Test-Path $box.Text)) { $dlg.SelectedPath = $box.Text }
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $box.Text = $dlg.SelectedPath }
}
$btnSource.Add_Click({ & $pickFolder $txtSource (T "Select the publish-client or publish-host folder") })
$btnHostDir.Add_Click({ & $pickFolder $txtHostDir (T "Select the install folder") })

function Set-HostKeyConfirmed([string]$key) {
    $existing = Get-CmHostKey
    if ($existing -and $existing -ne $key) {
        $answer = [System.Windows.Forms.MessageBox]::Show(
            (T "The teacher app for {0} already has a different shared key.`r`n`r`nReplace it? Student PCs set up with the old key will then show 'Wrong shared key' - to change the key on student PCs, use Rotate key... in the teacher app instead." $env:USERNAME),
            (T "Replace the teacher app's key?"), [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Warning)
        if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { Write-CmLog "Kept the teacher app's existing key."; return }
    }
    if ($existing -ne $key) {
        if (Get-Process ClassroomHost -ErrorAction SilentlyContinue) { throw (T "Close the teacher app first, then run this again.") }
        Set-CmHostKey $key
    }
}

function Parse-Port([string]$text, [string]$name) {
    $n = 0
    if (-not [int]::TryParse($text.Trim(), [ref]$n) -or $n -lt 1 -or $n -gt 65535) { throw (T "{0} must be a number from 1 to 65535." $name) }
    return $n
}

$btnRun.Add_Click({
    $a = Selected-Action
    $client = $rbClient.Checked
    $what = $(if ($client) { T "Classroom Manager Client" } else { T "the Classroom Manager teacher app" })
    if ($a -eq "Uninstall") {
        $ok = [System.Windows.Forms.MessageBox]::Show((T "Remove {0} from this PC?" $what), (T "Classroom Manager Setup"),
            [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question)
        if ($ok -ne [System.Windows.Forms.DialogResult]::Yes) { return }
    }

    $txtLog.Clear()
    $form.Enabled = $false
    $form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
    $lblStatus.Text = (T "{0} in progress..." (T $a))
    try {
        $src = $txtSource.Text.Trim()
        $roots = @($txtRoots.Text.Split(",") | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        switch ("$(if ($client) { 'Client' } else { 'Host' })/$a") {
            "Client/Install"   {
                $problem = Test-CmKey $txtKey.Text
                if ($problem) { throw $problem }
                Install-CmClient -SourceDir $src -Key $txtKey.Text -TcpPort (Parse-Port $txtTcp.Text (T "TCP port")) `
                    -UdpPort (Parse-Port $txtUdp.Text (T "UDP port")) -AllowedRoots $roots -SetNetworkPrivate:$chkNet.Checked
            }
            "Client/Update"    { Update-CmClient -SourceDir $src -Force:$chkForce.Checked }
            "Client/Modify"    {
                Set-CmClientConfig -Key $txtKey.Text -TcpPort (Parse-Port $txtTcp.Text (T "TCP port")) `
                    -UdpPort (Parse-Port $txtUdp.Text (T "UDP port")) -AllowedRoots $roots
            }
            "Client/Repair"    { Repair-CmClient -SourceDir $src -SetNetworkPrivate:$chkNet.Checked }
            "Client/Uninstall" { Uninstall-CmClient -KeepData:(-not $chkData.Checked) }
            "Host/Install"     {
                $hostKey = $txtHostKey.Text.Trim()
                $problem = Test-CmKey $hostKey
                if ($problem) { throw $problem }
                Install-CmHost -SourceDir $src -InstallDir $txtHostDir.Text.Trim() -DesktopShortcut $chkDesktop.Checked `
                    -StartWithWindows $chkStartup.Checked -AddToPath $chkPath.Checked -SetNetworkPrivate:$chkNet.Checked
                Set-HostKeyConfirmed $hostKey
            }
            "Host/Update"      { Update-CmHost -SourceDir $src -Force:$chkForce.Checked }
            "Host/Modify"      {
                Set-CmHostOptions -DesktopShortcut $(if ($chkDesktop.Checked) { "On" } else { "Off" }) `
                    -StartWithWindows $(if ($chkStartup.Checked) { "On" } else { "Off" }) `
                    -AddToPath $(if ($chkPath.Checked) { "On" } else { "Off" })
                $hostKey = $txtHostKey.Text.Trim()
                if ($hostKey -and $hostKey -ne (Get-CmHostKey)) {
                    $problem = Test-CmKey $hostKey
                    if ($problem) { throw $problem }
                    Set-HostKeyConfirmed $hostKey
                }
            }
            "Host/Repair"      { Repair-CmHost -SourceDir $src -SetNetworkPrivate:$chkNet.Checked }
            "Host/Uninstall"   { Uninstall-CmHost -RemoveSettings:$chkData.Checked }
        }
        $script:keyDefaultsFor = ""
        $lblStatus.Text = (T "{0} finished." (T $a))
        [System.Windows.Forms.MessageBox]::Show((T "{0} finished." (T $a)), (T "Classroom Manager Setup"),
            [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
    }
    catch {
        $script:SetupFailed = $true
        Append-Log "ERROR: $($_.Exception.Message)"
        $lblStatus.Text = (T "{0} failed - see the log above." (T $a))
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, (T "{0} failed" (T $a)),
            [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    }
    finally {
        $form.Enabled = $true
        $form.Cursor = [System.Windows.Forms.Cursors]::Default
        Refresh-State
    }
})

if ($Component -eq "Host") { $rbHost.Checked = $true } else { $rbClient.Checked = $true }
if ($SourceDir) { $txtSource.Text = $SourceDir }
Refresh-State
if ($ScreenshotPath) {
    $form.ShowInTaskbar = $false
    $form.StartPosition = "Manual"
    $form.Location = New-Object System.Drawing.Point(-20000, -20000)
    $form.Show()
    [System.Windows.Forms.Application]::DoEvents()
    $bitmap = New-Object System.Drawing.Bitmap($form.Width, $form.Height)
    $form.DrawToBitmap($bitmap, (New-Object System.Drawing.Rectangle(0, 0, $form.Width, $form.Height)))
    $bitmap.Save($ScreenshotPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $form.Close()
    exit 0
}
[System.Windows.Forms.Application]::Run($form)
if ($script:SetupFailed) { exit 1 }
exit 0
