// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class TlsProfileTests
{
    [Fact]
    public void The_profile_names_tls_12_and_13_and_nothing_older()
    {
#pragma warning disable SYSLIB0039, CS0618 // the old protocols are named here to prove they are absent
        var retired = SslProtocols.Ssl2 | SslProtocols.Ssl3 | SslProtocols.Tls | SslProtocols.Tls11;
#pragma warning restore SYSLIB0039, CS0618
        Assert.Equal(SslProtocols.None, TlsProfile.Protocols & retired);
        Assert.True(TlsProfile.Protocols.HasFlag(SslProtocols.Tls12));
        Assert.True(TlsProfile.Protocols.HasFlag(SslProtocols.Tls13));
        Assert.NotEqual(SslProtocols.None, TlsProfile.Protocols);       // "let the system choose" is not the profile
    }

    [Fact]
    public async Task A_client_and_a_server_using_the_profile_complete_a_handshake_and_negotiate_tls_12_or_13()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));   // a hang guard; RSA key generation inside it is slow on a busy machine
        using var pair = await LoopbackStreamPair.CreateAsync();
        using var certificate = Certificate("synthetic-profile");
        using var client = new SslStream(pair.Client, false, (_, _, _, _) => true);
        using var server = new SslStream(pair.Server, false);

        await Task.WhenAll(client.AuthenticateAsClientAsync(TlsProfile.Client("synthetic-profile"), deadline.Token),
            server.AuthenticateAsServerAsync(TlsProfile.Server(certificate), deadline.Token));

        Assert.True(client.SslProtocol is SslProtocols.Tls12 or SslProtocols.Tls13, client.SslProtocol.ToString());
        Assert.Equal(client.SslProtocol, server.SslProtocol);
    }

    [Fact]
    public async Task A_peer_that_offers_only_a_retired_protocol_is_refused()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));   // a hang guard; RSA key generation inside it is slow on a busy machine
        using var pair = await LoopbackStreamPair.CreateAsync();
        using var certificate = Certificate("synthetic-profile");
        using var client = new SslStream(pair.Client, false, (_, _, _, _) => true);
        using var server = new SslStream(pair.Server, false);

#pragma warning disable SYSLIB0039, CS0618
        var oldOnly = new SslClientAuthenticationOptions { TargetHost = "synthetic-profile", EnabledSslProtocols = SslProtocols.Tls11 };
#pragma warning restore SYSLIB0039, CS0618
        var serving = server.AuthenticateAsServerAsync(TlsProfile.Server(certificate), deadline.Token);

        // Either this operating system cannot even offer TLS 1.1 or the server refuses it: both are a refusal.
        await Assert.ThrowsAnyAsync<Exception>(() => client.AuthenticateAsClientAsync(oldOnly, deadline.Token));
        client.Dispose();
        try { await serving; } catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or NotSupportedException) { }
    }

    static X509Certificate2 Certificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
    }
}
