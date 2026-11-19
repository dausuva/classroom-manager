// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// client.uninstall: the client removes itself. A running service cannot delete its own program or stop
/// itself and carry on, so it hands the work to a separate PowerShell process, as self.update does, and
/// answers the Host before that starts. The process runs the setup scripts the install put in the
/// program's "setup" folder (Setup.ps1 -Action Uninstall, the same as Settings > Apps), from a copy in the
/// Windows temporary folder so the program folder can go, and deletes that copy when it is done. What is
/// left is one log file, C:\Windows\Temp\ClassroomClient-uninstall.log.
/// </summary>
sealed class UninstallHandler
{
    public const string KeepData = "keepData";
    public const string RemoveLabTrust = "removeLabTrust";

    public Response Start(JsonObject a)
    {
        if (AgentContext.IsUserAgent) return Response.Fail("The client can only be removed at the system level.");
        if (PackagedClient.UninstallRefusal(PackageIdentity.IsPackaged) is { } packaged) return Response.Fail(packaged);
        var setup = Path.Combine(AppContext.BaseDirectory, "setup");
        foreach (var file in new[] { "Setup.ps1", "ClassroomSetup.psm1" })
            if (!File.Exists(Path.Combine(setup, file)))
                return Response.Fail($"The setup scripts are not in {setup}, so the client cannot remove itself. Remove it in Settings > Apps on the computer.");

        var temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        var work = Path.Combine(temp, "ClassroomClientUninstall-" + Guid.NewGuid().ToString("N")[..8]);
        var log = Path.Combine(temp, "ClassroomClient-uninstall.log");
        try
        {
            Directory.CreateDirectory(work);
            foreach (var file in Directory.EnumerateFiles(setup)) File.Copy(file, Path.Combine(work, Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(work, "remove-client.ps1"), Script);
        }
        catch (Exception ex) { return Response.Fail("Could not prepare the removal: " + ex.Message); }

        bool keep = a.Bool(KeepData) == true;
        // The lab trust goes only when asked, and only the exact lab certificate this client is signed with.
        var (labTrust, labTrustNote) = a.Bool(RemoveLabTrust) == true ? LabCertificate() : ("", "");
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{Path.Combine(work, "remove-client.ps1")}\" " +
            $"-Work \"{work}\" -LogFile \"{log}\"{(keep ? " -KeepData" : "")}{(labTrust.Length > 0 ? $" -LabTrustSha256 {labTrust}" : "")}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = temp,
        };
        try { Process.Start(psi); }
        catch (Exception ex) { return Response.Fail("Could not start the removal: " + ex.Message); }

        Log.Write($"client.uninstall: removal started{(keep ? ", settings kept for a reinstall" : ", nothing kept")}" +
                  $"{(labTrust.Length > 0 ? $", lab trust {labTrust} to be removed" : labTrustNote.Length > 0 ? $"; {labTrustNote}" : "")}; log {log}.");
        return Response.Success(new JsonObject
        {
            ["removing"] = true,
            [KeepData] = keep,
            ["log"] = log,
            ["labTrust"] = labTrust,
            ["labTrustNote"] = labTrustNote,
        });
    }

    /// <summary>
    /// The SHA-256 fingerprint of the lab certificate this client's program is signed with, the trust the lab
    /// trust installer added to the computer. Only a self-signed Dausuva certificate counts: one from a real
    /// certificate authority is never removed. Otherwise "" and the reason.
    /// </summary>
    static (string Sha256, string Note) LabCertificate()
    {
        try
        {
            var exe = Environment.ProcessPath ?? "";
#pragma warning disable SYSLIB0057 // reads the signer of a signed file; the loader that replaces it does not do that
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(exe));
#pragma warning restore SYSLIB0057
            if (!Regex.IsMatch(signer.Subject, @"^CN=Dausuva(?: |,|$)") || signer.Subject != signer.Issuer)
                return ("", $"the client is signed by {signer.Subject}, not a lab certificate, so no trust was removed");
            return (Convert.ToHexString(SHA256.HashData(signer.RawData)), "");
        }
        catch (CryptographicException) { return ("", "the client is not signed, so there is no lab trust to remove"); }
    }

    /// <summary>
    /// Waits a moment so the answer reaches the Host, runs the uninstall from this copy, then deletes the copy.
    /// A script PowerShell has read is not held open, so it can delete its own folder. Updating a client
    /// replaces only its program, so the setup scripts can be older than it: only options every Setup.ps1
    /// knows are passed, and each user's folder is removed here as well as by newer scripts.
    /// </summary>
    const string Script = """
        param([string]$Work, [string]$LogFile, [switch]$KeepData, [string]$LabTrustSha256 = "")
        Start-Sleep -Seconds 3
        $env:CM_SETUP_TEMP = "1"
        $setup = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $Work "Setup.ps1"),
                   "-Component", "Client", "-Action", "Uninstall", "-Quiet", "-LogFile", $LogFile)
        if ($KeepData) { $setup += "-KeepData" }
        & powershell.exe @setup
        if (-not $KeepData) {
            foreach ($profile in (Get-ChildItem (Join-Path $env:SystemDrive "Users") -Directory -ErrorAction SilentlyContinue)) {
                Remove-Item (Join-Path $profile.FullName "AppData\Local\ClassroomManager") -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        # Last, once nothing signed with it has to run any more: exactly this certificate, as Remove-LabTrust.ps1 does.
        if ($LabTrustSha256) {
            $removed = @()
            foreach ($storeName in "Root", "TrustedPublisher") {
                $store = New-Object Security.Cryptography.X509Certificates.X509Store($storeName, "LocalMachine")
                try {
                    $store.Open("ReadWrite")
                    foreach ($cert in @($store.Certificates)) {
                        $sha = [Security.Cryptography.SHA256]::Create()
                        try { $fingerprint = [BitConverter]::ToString($sha.ComputeHash($cert.RawData)) -replace "-", "" } finally { $sha.Dispose() }
                        if ($fingerprint -eq $LabTrustSha256) { $store.Remove($cert); $removed += "LocalMachine\$storeName" }
                    }
                } finally { $store.Close() }
            }
            $trustLog = Join-Path $env:ProgramData "Dausuva\CertificateTrust"
            New-Item -ItemType Directory -Path $trustLog -Force | Out-Null
            "$(Get-Date -Format o) REMOVE SHA256=$LabTrustSha256 STORES=$($removed -join ',') BY=client.uninstall" |
                Add-Content (Join-Path $trustLog "changes.log") -Encoding UTF8
            "Lab trust $LabTrustSha256 removed from: $($removed -join ', ')" | Add-Content $LogFile -Encoding UTF8
        }
        Set-Location $env:SystemRoot
        Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
        """;
}
