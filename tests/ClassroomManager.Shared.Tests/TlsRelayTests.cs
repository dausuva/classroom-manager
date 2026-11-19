// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>T-111: isolated reproduction, never real endpoints, credentials or certificate stores.</summary>
public sealed class TlsRelayTests
{
    static readonly byte[] Key = Encoding.UTF8.GetBytes("synthetic-relay-test-key");
    static readonly byte[] Content = Encoding.UTF8.GetBytes("synthetic-management-content");

    [Theory]
    [InlineData(SslProtocols.Tls12)]
    [InlineData(SslProtocols.Tls13)]
    public async Task Accept_any_certificate_and_unbound_PSK_allow_two_TLS_legs_to_observe_content(SslProtocols protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));   // a hang guard; RSA key generation inside it is slow on a busy machine
        var ct = deadline.Token;
        using var hostLeg = await LoopbackStreamPair.CreateAsync();
        using var clientLeg = await LoopbackStreamPair.CreateAsync();
        using var attackerCert = Certificate("synthetic-attacker");
        using var clientCert = Certificate("synthetic-client");
        using var host = new SslStream(hostLeg.Client, false, (_, _, _, _) => true);
        using var attackerServer = new SslStream(hostLeg.Server, false);
        using var attackerClient = new SslStream(clientLeg.Client, false, (_, _, _, _) => true);
        using var client = new SslStream(clientLeg.Server, false);
        await Task.WhenAll(
            host.AuthenticateAsClientAsync(ClientOptions(protocol), ct),
            attackerServer.AuthenticateAsServerAsync(ServerOptions(attackerCert, protocol), ct),
            attackerClient.AuthenticateAsClientAsync(ClientOptions(protocol), ct),
            client.AuthenticateAsServerAsync(ServerOptions(clientCert, protocol), ct));
        Assert.NotEqual(attackerCert.Thumbprint, clientCert.Thumbprint);
        var observed = new ConcurrentQueue<byte[]>();
        using var pumping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var forward = Pump(attackerServer, attackerClient, observed, pumping.Token);
        var reverse = Pump(attackerClient, attackerServer, new(), pumping.Token);
        try
        {
            var accept = SecureChannel.AcceptAsync(client, Key, ct);
            var connect = SecureChannel.ConnectAsync(host, Key, ct);
            await Task.WhenAll(accept, connect);
            using var receiving = accept.Result;
            using var sending = connect.Result;
            await sending.SendAsync(Content, ct);
            Assert.Equal(Content, await receiving.ReceiveAsync(ct));
            // The relay copies in the background; give it a moment to have copied the last piece before looking (it failed about
            // once in fifty runs when the check came first), and say what was seen if the content is really not there.
            var expected = Encoding.UTF8.GetString(Content);
            for (var wait = 0; wait < 30 && !Encoding.UTF8.GetString(observed.SelectMany(x => x).ToArray()).Contains(expected); wait++)
                await Task.Delay(100, ct);
            Assert.True(Encoding.UTF8.GetString(observed.SelectMany(x => x).ToArray()).Contains(expected),
                "chunks seen by the relay: " + string.Join("|", observed.Select(x => x.Length + ":" + Convert.ToHexString(x))));
        }
        finally
        {
            pumping.Cancel();
            await Task.WhenAll(forward, reverse);
        }
    }

    [Fact]
    public async Task Independent_certificate_pin_refuses_the_attacker_before_PSK_or_management_content()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));   // a hang guard; RSA key generation inside it is slow on a busy machine
        using var pair = await LoopbackStreamPair.CreateAsync();
        using var expected = Certificate("synthetic-enrolled-client");
        using var attacker = Certificate("synthetic-attacker");
        var approved = SHA256.HashData(expected.RawData);
        using var host = new SslStream(pair.Client, false, TlsEndpointTrust.Validator([Convert.ToHexString(approved)]));
        using var server = new SslStream(pair.Server, false);
        var serving = server.AuthenticateAsServerAsync(ServerOptions(attacker, SslProtocols.Tls12), deadline.Token);
        await Assert.ThrowsAsync<AuthenticationException>(() => host.AuthenticateAsClientAsync(ClientOptions(SslProtocols.Tls12), deadline.Token));
        host.Dispose();
        // Schannel may complete the server handshake before seeing the peer's fatal alert.
        try { await serving; } catch (AuthenticationException) { } catch (IOException) { }
    }

    [Fact]
    public async Task Independently_pinned_self_signed_certificate_allows_direct_PSK_traffic()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));   // a hang guard; RSA key generation inside it is slow on a busy machine
        using var pair = await LoopbackStreamPair.CreateAsync();
        using var certificate = Certificate("synthetic-enrolled-client");
        var approved = SHA256.HashData(certificate.RawData);
        using var host = new SslStream(pair.Client, false, TlsEndpointTrust.Validator([Convert.ToHexString(approved)]));
        using var server = new SslStream(pair.Server, false);
        await Task.WhenAll(host.AuthenticateAsClientAsync(ClientOptions(SslProtocols.Tls12), deadline.Token),
            server.AuthenticateAsServerAsync(ServerOptions(certificate, SslProtocols.Tls12), deadline.Token));
        var accepting = SecureChannel.AcceptAsync(server, Key, deadline.Token);
        using var channel = await SecureChannel.ConnectAsync(host, Key, deadline.Token);
        using var peer = await accepting;
        await channel.SendAsync(Content, deadline.Token);
        Assert.Equal(Content, await peer.ReceiveAsync(deadline.Token));
    }

    static SslClientAuthenticationOptions ClientOptions(SslProtocols protocol) => new()
    { TargetHost = "synthetic-client", EnabledSslProtocols = protocol };

    static SslServerAuthenticationOptions ServerOptions(X509Certificate2 certificate, SslProtocols protocol) => new()
    { ServerCertificate = certificate, EnabledSslProtocols = protocol };

    static X509Certificate2 Certificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // Schannel cannot use EphemeralKeySet for TLS credentials. DefaultKeySet creates
        // a temporary key container deleted on Dispose; no certificate store enrollment.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
    }

    static async Task Pump(Stream from, Stream to, ConcurrentQueue<byte[]> capture, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var count = await from.ReadAsync(buffer, ct);
                if (count == 0) return;
                capture.Enqueue(buffer[..count]);
                await to.WriteAsync(buffer.AsMemory(0, count), ct);
                await to.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { /* Endpoint disposal closes the forwarding leg. */ }
    }
}
