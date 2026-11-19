// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace ClassroomHost.Tests;

/// <summary>
/// The default lab certificate's public half (lab-signing-public/) is kept out of git on purpose, so a CI checkout does not have it. Tests that need "a lab certificate in a checkout"
/// use the real folder when it is there and otherwise a freshly made self-signed code-signing certificate in a scratch folder. Only the public half is ever written.
/// </summary>
static class LabCertificateFixture
{
    public sealed record Fixture(string Root, string Sha256, string CerPath);

    public static Fixture Prepare(string scratch)
    {
        var real = StringExtractor.RepositoryRoot();
        var realJson = Path.Combine(real, "lab-signing-public", "CERTIFICATE.json");
        var realCer = Path.Combine(real, "lab-signing-public", "Dausuva-Classroom-Lab-Code-Signing.cer");
        if (File.Exists(realJson) && File.Exists(realCer))
            return new Fixture(real, JsonNode.Parse(File.ReadAllText(realJson))!["CertificateSha256"]!.GetValue<string>(), realCer);

        var root = Path.Combine(scratch, "synthetic-checkout");
        var folder = Path.Combine(root, "lab-signing-public");
        Directory.CreateDirectory(folder);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Dausuva Classroom Lab Code Signing", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.3")], critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
        var cer = Path.Combine(folder, "Dausuva-Classroom-Lab-Code-Signing.cer");
        File.WriteAllBytes(cer, certificate.Export(X509ContentType.Cert));
        var sha256 = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));
        File.WriteAllText(Path.Combine(folder, "CERTIFICATE.json"), new JsonObject
        {
            ["Subject"] = certificate.Subject, ["Issuer"] = certificate.Issuer, ["StoreSelectorSha1"] = certificate.Thumbprint,
            ["CertificateSha256"] = sha256, ["NotAfter"] = certificate.NotAfter.ToString("o"),
        }.ToJsonString());
        return new Fixture(root, sha256, cer);
    }
}
