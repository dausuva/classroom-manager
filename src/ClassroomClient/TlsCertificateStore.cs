// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace ClassroomManager.Client;

/// <summary>
/// Loads (or creates, on first run) the self-signed certificate the service presents to hosts
/// that connect to it over TLS.
///
/// Revised Hosts independently pin this exact leaf certificate before the mutual PSK handshake.
/// Older Hosts accept any certificate and remain vulnerable to active TLS relay. The PSK still
/// determines Teacher/Admin authority; a certificate alone grants no command access. A regenerated
/// certificate needs independent re-enrollment on every pinned Host. Protect its private key and
/// Host trust metadata; local administrator compromise is outside this boundary.
/// </summary>
static class TlsCertificateStore
{
    static string PfxPath => Path.Combine(ClientConfig.DataDir, "tls-cert.pfx");

    public static X509Certificate2 LoadOrCreate()
    {
        try
        {
            if (File.Exists(PfxPath))
                return X509CertificateLoader.LoadPkcs12FromFile(PfxPath, null, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
        }
        catch (Exception ex) { Log.Write($"TLS cert: could not load the existing certificate, generating a new one: {ex.Message}"); }

        var cert = Create();
        try
        {
            Directory.CreateDirectory(ClientConfig.DataDir);
            File.WriteAllBytes(PfxPath, cert.Export(X509ContentType.Pfx));
            Lockdown(PfxPath);
        }
        catch (Exception ex) { Log.Write($"TLS cert: could not persist the new certificate (a new one will be generated at next start): {ex.Message}"); }
        return cert;
    }

    static X509Certificate2 Create()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={Environment.MachineName}.classroom-manager.local",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") /* TLS server authentication */ }, critical: false));

        using var selfSigned = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        // Re-import as exportable + persisted so it (and its private key) survive being reloaded
        // after a service restart, rather than only living in this process's memory.
        return X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
    }

    static void Lockdown(string path)
    {
        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false); // stop inheriting ProgramData's more permissive default ACL
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
            // File.SetAccessControl(string, FileSecurity) was removed in .NET Core; the replacement
            // is this FileInfo extension method (System.IO.FileSystemAclExtensions).
            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception ex) { Log.Write($"TLS cert: could not restrict file permissions on {path}: {ex.Message}"); }
    }
}
