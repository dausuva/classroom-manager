// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Self-update: the host uploads a new ClassroomClient.exe into the staging folder (the same
/// chunked fs.write upload used for installers), then calls self.update with its path and
/// SHA-256. This handler verifies the file, then launches a small detached PowerShell script
/// that stops the service, replaces the exe, and starts the service back up - the running
/// process cannot overwrite its own locked binary, so that last step has to happen outside it.
/// </summary>
sealed class UpdateHandler
{
    const string ServiceName = "ClassroomManagerClient";

    public async Task<Response> ApplyAsync(JsonObject a, CancellationToken ct)
    {
        // Only the elevated service may replace the installed binary and restart the service.
        if (AgentContext.IsUserAgent) return Response.Fail("self.update must run at the system level.");
        if (PackagedClient.SelfUpdateRefusal(PackageIdentity.IsPackaged) is { } packaged) return Response.Fail(packaged);

        if (!UpdateFileIntegrity.TryReadHash(a["sha256"], out var expectedSha256))
            return Response.Fail(ErrorCatalog.UpdateFailedChecks, UpdateFileIntegrity.InvalidHash);
        var path = a.Str("path");
        bool force = a.Bool("force") ?? false;

        // The file must already be inside the staging folder (uploaded via fs.write) - never an
        // arbitrary path, elevated or not.
        var staging = Path.GetFullPath(ClientConfig.StagingDir).TrimEnd('\\');
        var full = Path.GetFullPath(path);
        if (!full.Equals(staging, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(staging + "\\", StringComparison.OrdinalIgnoreCase))
            return Response.Fail("The update file must be uploaded into the staging folder first.");
        if (!File.Exists(full)) return Response.Fail($"File not found: {full}");

        var integrityError = await UpdateFileIntegrity.VerifyAsync(full, expectedSha256, ct);
        if (integrityError is not null) return Response.Fail(ErrorCatalog.UpdateFailedChecks, integrityError);

        // Sanity check: it should at least look like a Windows executable, not a half-uploaded
        // or wrong file.
        using (var fs = File.OpenRead(full))
        {
            var head = new byte[2];
            if (fs.Read(head, 0, 2) != 2 || head[0] != (byte)'M' || head[1] != (byte)'Z')
                return Response.Fail(ErrorCatalog.UpdateFailedChecks, "That doesn't look like a Windows executable (missing MZ header).");
        }

        // Built for another processor: it would not start here (x64 on 32-bit Windows) or would see the wrong registry (x86 on 64-bit).
        // Say so plainly, before the slower checks and whatever --force says.
        if (PeArchitecture.RefuseUpdate(PeArchitecture.Current, PeArchitecture.Read(full)) is { } wrongArchitecture)
            return Response.Fail(ErrorCatalog.UpdateWrongArchitecture, wrongArchitecture);

        var config = ClientConfig.Load();
        var allowedSigners = config?.AllowedUpdateSignerCertificateSha256 ?? new List<string>();
        if (allowedSigners.Count == 0)
            return Response.Fail(ErrorCatalog.UpdateFailedChecks, "This Client takes no updates from the Host: it was installed from a version that is not signed (a self-built or unsigned version), so it has no approved publisher. Install the new version on this computer with its installer, or reinstall it from a signed release.");
        var (signatureError, candidateSigner) = await CheckSignatureAsync(full, allowedSigners, ct);
        if (signatureError is not null) return Response.Fail(ErrorCatalog.UpdateFailedChecks, signatureError);

        var targetExe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ClassroomClient.exe");

        var newVer = SafeFileVersion(full);
        var curVer = SafeFileVersion(targetExe);
        if (newVer is null || curVer is null)
            return Response.Fail(ErrorCatalog.UpdateFailedChecks, "The current and uploaded Client must both have readable file versions before a recoverable update can run.");

        // A customer release carries its identity inside its signed file (T-218); the order of two of them is decided by that identity, bound to the
        // digest and signer computed here. Programs with no identity on either side keep the file-version rule below.
        var candidateClaim = UpdateOrder.ReadClaim(full);
        var installedClaim = UpdateOrder.ReadClaim(targetExe);
        var useFileVersions = true;
        if (candidateClaim is not null || installedClaim is not null)
        {
            var installedSigner = installedClaim is null ? "" : (await CheckSignatureAsync(targetExe, null, ct)).Signer;
            var installedSha = installedClaim is null ? "" : await UpdateFileIntegrity.Sha256Async(targetExe, ct);
            var order = UpdateOrder.Evaluate(new(installedSha, installedSigner, curVer, installedClaim), new(expectedSha256, candidateSigner, newVer, candidateClaim), force);
            if (!order.Allowed) return Response.Fail(order.Error!, order.Message);
            useFileVersions = order.UseFileVersions;
        }
        if (useFileVersions && !force && newVer <= curVer)
            return Response.Fail(ErrorCatalog.UpdateNotNewer, $"Uploaded build (v{newVer}) is not newer than the running client (v{curVer}). " +
                                  "Pass force to push it anyway.");

        // A build that cannot start here must never replace the running one: this service is the only
        // remote way back in. The usual cause is a build that uses the .NET runtime installed on the
        // PC when that runtime is missing or too old.
        var probe = await ProbeAsync(full, ct);
        if (probe.Failed)
            return Response.Fail(ErrorCatalog.UpdateCannotStart, $"The uploaded build cannot start on this computer ({probe.Detail}). " +
                                  "It was not installed. If this build uses the .NET runtime on the PC, install that runtime first, or push the build that includes .NET.");
        if (probe.TimedOut && !force)
            return Response.Fail(ErrorCatalog.UpdateCannotStart, "The uploaded build did not answer the start-up check, so it was not installed. " +
                                  "Builds older than this check do not answer it; pass force to install one anyway.");

        UpdateRecoveryTransaction.Snapshot transaction;
        try
        {
            transaction = await UpdateRecoveryTransaction.CreateAsync(full, targetExe, expectedSha256,
                curVer.ToString(), newVer.ToString(), allowedSigners, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                                   System.Security.SecurityException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            Log.Write($"self.update: could not prepare protected recovery ({ex.GetType().Name}).");
            return Response.Fail(ErrorCatalog.UpdateFailedChecks, $"A protected recovery copy could not be prepared; the installed Client was not stopped. {ex.Message}");
        }

        var scriptArguments = new[]
        {
            "-Transaction", transaction.Directory, "-ActiveMarker", transaction.ActiveMarker,
            "-Candidate", transaction.Candidate, "-PreviousExecutable", transaction.PreviousExecutable,
            "-PreviousConfig", transaction.PreviousConfig, "-PreviousCertificate", transaction.PreviousCertificate,
            "-Target", transaction.Target, "-Uploaded", full,
            "-ExpectedSha256", transaction.CandidateSha256, "-PreviousSha256", transaction.PreviousSha256,
            "-ConfigSha256", transaction.ConfigSha256, "-CertificateSha256", transaction.CertificateSha256,
            "-CandidateVersion", transaction.CandidateVersion, "-PreviousVersion", transaction.PreviousVersion,
            "-LogFile", transaction.Log, "-DataDirectory", ClientConfig.DataDir,
            "-CandidateHealthSeconds", "60", "-RecoveryHealthSeconds", "45",
        };
        var commandLine = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File {QuoteArgument(transaction.Script)} " +
                          string.Join(" ", scriptArguments.Select(QuoteArgument));
        var psi = new ProcessStartInfo("powershell.exe", commandLine)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = transaction.Directory,
        };

        try
        {
            if (Process.Start(psi) is null)
            {
                try { File.Delete(transaction.ActiveMarker); } catch { }
                return Response.Fail("The updater process could not be started; the installed Client was not stopped.");
            }
        }
        catch (Exception ex)
        {
            try { File.Delete(transaction.ActiveMarker); } catch { }
            Log.Write($"self.update: could not start updater ({ex.GetType().Name}).");
            return Response.Fail($"Could not start the updater ({ex.GetType().Name}); the installed Client was not stopped.");
        }

        Log.Write($"self.update: protected transaction {Path.GetFileName(transaction.Directory)} started for version {newVer}.");
        return Response.Success(new JsonObject
        {
            ["applying"] = true,
            ["message"] = "Update started; the service will restart in a few seconds.",
        });
    }

    static string QuoteArgument(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    internal sealed record ProbeResult(bool Failed, bool TimedOut, string Detail);

    /// <summary>
    /// Starts the uploaded executable with <see cref="Program.ProbeSwitch"/>, which makes a working
    /// build exit at once with code 0. Its signature has already been verified, so running it is no
    /// more than the update itself would do.
    /// </summary>
    internal static async Task<ProbeResult> ProbeAsync(string exe, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, Program.ProbeSwitch) { UseShellExecute = false, CreateNoWindow = true };
            // Without this a missing runtime shows a message box nobody can see in the service session.
            psi.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
            using var process = Process.Start(psi);
            if (process is null) return new ProbeResult(true, false, "it could not be started");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));   // a build that includes .NET unpacks itself on first run
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new ProbeResult(false, true, "no answer");
            }
            return process.ExitCode == 0
                ? new ProbeResult(false, false, "")
                : new ProbeResult(true, false, $"exit code 0x{process.ExitCode:X8}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new ProbeResult(true, false, ex.Message); }
    }

    static Version? SafeFileVersion(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return v is not null && Version.TryParse(v, out var parsed) ? parsed : null;
        }
        catch { return null; }
    }

    internal static async Task<string?> VerifyAuthenticodeAsync(string path, IEnumerable<string> allowedFingerprints, CancellationToken ct) =>
        (await CheckSignatureAsync(path, allowedFingerprints, ct)).Error;

    /// <summary>The signature check, also returning the SHA-256 of the signer's certificate. With no approved list it checks only that the signature is valid.</summary>
    internal static async Task<(string? Error, string Signer)> CheckSignatureAsync(string path, IEnumerable<string>? allowedFingerprints, CancellationToken ct)
    {
        // Get-AuthenticodeSignature uses WinVerifyTrust. The path is passed through the
        // environment rather than interpolated into executable PowerShell source.
        const string command = "$s=Get-AuthenticodeSignature -LiteralPath $env:CM_UPDATE_FILE;" +
            "$h='';if($s.SignerCertificate){$x=[Security.Cryptography.SHA256]::Create();try{$h=([BitConverter]::ToString($x.ComputeHash($s.SignerCertificate.RawData))-replace '-','')}finally{$x.Dispose()}};" +
            "[Console]::Out.Write(([string]$s.Status)+'|'+$h)";
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);
        psi.Environment["CM_UPDATE_FILE"] = path;
        using var process = Process.Start(psi);
        if (process is null) return ("Windows signature verification could not be started.", "");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var result = (await stdout).Trim().Split('|', 2);
        var error = (await stderr).Trim();
        if (process.ExitCode != 0 || result.Length != 2)
            return ($"Windows signature verification failed to run: {error}", "");
        if (!result[0].Equals("Valid", StringComparison.OrdinalIgnoreCase))
            return ($"The update's Authenticode signature is not valid or trusted (Windows status: {result[0]}).", "");
        var actual = NormalizeFingerprint(result[1]);
        if (allowedFingerprints is null) return (null, actual.ToLowerInvariant());
        var allowed = allowedFingerprints.Select(NormalizeFingerprint).Where(x => x.Length == 64).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!allowed.Contains(actual))
            return ($"This build is signed by a publisher this computer does not accept (certificate SHA-256 {actual}). A Client takes updates only from the publisher it was installed from. To move it to another publisher's builds, reinstall it with that publisher's installer; an intentional certificate rotation also goes through administrator setup.", actual.ToLowerInvariant());
        return (null, actual.ToLowerInvariant());
    }

    static string NormalizeFingerprint(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var c in value)
            if (Uri.IsHexDigit(c)) result.Append(char.ToUpperInvariant(c));
        return result.ToString();
    }

}
