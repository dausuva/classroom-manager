// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using ClassroomManager.Client;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

public sealed class UpdateServiceHealthTests
{
    const string Machine = "UPDATE-PC";
    const string Client = "a1b2c3d4";
    const string Version = "1.2.3.4";

    static Response Ping(string machine = Machine, string client = Client, string version = Version, bool ok = true) => new()
    {
        Ok = ok,
        Data = new JsonObject { ["machineName"] = machine, ["clientId"] = client, ["appVersion"] = version },
    };

    [Fact]
    public void Exact_authenticated_machine_identity_and_expected_version_pass()
    {
        Assert.True(UpdateServiceHealth.Accepts(Ping(), Machine, Client, Version));
    }

    [Theory]
    [InlineData("OTHER-PC", Client, Version, true)]
    [InlineData(Machine, "other-client", Version, true)]
    [InlineData(Machine, Client, "1.2.3.3", true)]
    [InlineData(Machine, Client, Version, false)]
    public void Failed_or_mismatched_service_health_never_accepts(string machine, string client, string version, bool ok)
    {
        Assert.False(UpdateServiceHealth.Accepts(Ping(machine, client, version, ok), Machine, Client, Version));
    }

    [Fact]
    public async Task Health_switch_rejects_malformed_request_without_touching_files()
    {
        Assert.Equal(2, await UpdateServiceHealth.RunAsync([]));
        Assert.Equal(2, await UpdateServiceHealth.RunAsync(["not-a-path", "bad-version", "bad-hash"]));
    }

    [Fact]
    public void TLS_health_probe_requires_the_persisted_certificate_fingerprint()
    {
        using var first = Certificate("first");
        using var second = Certificate("second");
        var firstHash = SHA256.HashData(first.RawData);

        Assert.True(UpdateServiceHealth.MatchesCertificate(first, firstHash));
        Assert.False(UpdateServiceHealth.MatchesCertificate(second, firstHash));
        Assert.False(UpdateServiceHealth.MatchesCertificate(null, firstHash));
        Assert.False(UpdateServiceHealth.MatchesCertificate(first, firstHash[..^1]));
    }

    static X509Certificate2 Certificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    }
}
