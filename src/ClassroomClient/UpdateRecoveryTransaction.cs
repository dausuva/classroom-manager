// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>Creates a protected, durable snapshot before the update helper can stop the Client service.</summary>
static class UpdateRecoveryTransaction
{
    internal sealed record Snapshot(string Directory, string Candidate, string PreviousExecutable, string PreviousConfig,
        string PreviousCertificate, string Script, string Log, string ActiveMarker, string Target,
        string CandidateSha256, string PreviousSha256, string ConfigSha256, string CertificateSha256,
        string PreviousVersion, string CandidateVersion);

    static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static async Task<Snapshot> CreateAsync(string uploaded, string target, string expectedSha256,
        string previousVersion, string candidateVersion, IEnumerable<string> allowedSigners, CancellationToken ct)
    {
        var recoveryRoot = Path.Combine(ClientConfig.DataDir, "UpdateRecovery");
        EnsurePrivateDirectory(recoveryRoot);

        var active = Path.Combine(recoveryRoot, "active.json");
        var transactionId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(recoveryRoot, transactionId);
        Directory.CreateDirectory(directory);
        RestrictDirectory(directory);

        var snapshot = new Snapshot(directory,
            Path.Combine(directory, "candidate.exe"),
            Path.Combine(directory, "previous.exe"),
            Path.Combine(directory, "client.json"),
            Path.Combine(directory, "tls-cert.pfx"),
            Path.Combine(directory, "apply-update.ps1"),
            Path.Combine(directory, "update.log"),
            active,
            Path.GetFullPath(target),
            expectedSha256,
            HashFile(target),
            HashFile(ClientConfig.ConfigPath),
            HashFile(Path.Combine(ClientConfig.DataDir, "tls-cert.pfx")),
            previousVersion,
            candidateVersion);

        // Reserve before doing any work that another concurrent request could duplicate. A stale marker
        // after a crash is intentional: an administrator must inspect the retained transaction first.
        try
        {
            using var marker = new FileStream(active, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(marker, new { Transaction = transactionId, CreatedUtc = DateTime.UtcNow }, cancellationToken: ct);
            await marker.FlushAsync(ct);
        }
        catch (IOException) when (File.Exists(active))
        {
            throw new InvalidOperationException("A previous Client update needs recovery review before another update can run.");
        }

        try
        {
            File.Copy(target, snapshot.PreviousExecutable);
            File.Copy(ClientConfig.ConfigPath, snapshot.PreviousConfig);
            File.Copy(Path.Combine(ClientConfig.DataDir, "tls-cert.pfx"), snapshot.PreviousCertificate);
            File.Copy(uploaded, snapshot.Candidate);

            if (!HashFile(target).Equals(snapshot.PreviousSha256, StringComparison.OrdinalIgnoreCase) ||
                !HashFile(snapshot.PreviousExecutable).Equals(snapshot.PreviousSha256, StringComparison.OrdinalIgnoreCase) ||
                !HashFile(snapshot.PreviousConfig).Equals(snapshot.ConfigSha256, StringComparison.OrdinalIgnoreCase) ||
                !HashFile(snapshot.PreviousCertificate).Equals(snapshot.CertificateSha256, StringComparison.OrdinalIgnoreCase) ||
                !HashFile(snapshot.Candidate).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Update snapshot verification failed; the running Client was not stopped.");

            var signatureError = await UpdateHandler.VerifyAuthenticodeAsync(snapshot.Candidate, allowedSigners, ct);
            if (signatureError is not null) throw new InvalidOperationException($"The protected update copy failed signature verification: {signatureError}");
            var probe = await UpdateHandler.ProbeAsync(snapshot.Candidate, ct);
            if (probe.Failed || probe.TimedOut)
                throw new InvalidOperationException("The protected update copy failed its start-up check; the running Client was not stopped.");

            await using (var script = typeof(UpdateRecoveryTransaction).Assembly.GetManifestResourceStream("ClassroomClient.UpdateRecovery.ps1")
                ?? throw new InvalidOperationException("The protected update recovery script is missing from the Client executable."))
            using (var destination = new FileStream(snapshot.Script, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await script.CopyToAsync(destination, ct);
            return snapshot;
        }
        catch
        {
            try { File.Delete(active); } catch { }
            throw;
        }
    }

    static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    static void EnsurePrivateDirectory(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Client update recovery folder is a reparse point; refusing to use it.");
        Directory.CreateDirectory(path);
        RestrictDirectory(path);
    }

    static void RestrictDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

}
