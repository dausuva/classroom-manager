// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>T-187: the key proof covers the TLS certificate, so a relay between two TLS sessions cannot forward it.</summary>
public sealed class ChannelBindingTests
{
    static readonly byte[] Key = Encoding.UTF8.GetBytes("synthetic-binding-test-key");
    static readonly byte[] Hello = Encoding.UTF8.GetBytes("synthetic-management-content");

    static byte[] Binding(string seed) => SHA256.HashData(Encoding.UTF8.GetBytes(seed));

    static async Task<(SecureChannel Server, int Index, SecureChannel Client)> Pair(byte[]? serverBinding, byte[]? clientBinding, bool require = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pair = await LoopbackStreamPair.CreateAsync();
        var accepting = SecureChannel.AcceptAsync(pair.Server, [Key], deadline.Token, serverBinding, require);
        var connecting = SecureChannel.ConnectAsync(pair.Client, Key, deadline.Token, clientBinding);
        await Task.WhenAll(accepting, connecting);
        return (accepting.Result.Channel, accepting.Result.KeyIndex, connecting.Result);
    }

    static async Task Exchange(SecureChannel from, SecureChannel to)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await from.SendAsync(Hello, deadline.Token);
        Assert.Equal(Hello, await to.ReceiveAsync(deadline.Token));
    }

    [Fact]
    public async Task Both_sides_with_the_same_binding_complete_the_bound_handshake_and_talk()
    {
        var (server, _, client) = await Pair(Binding("cert-a"), Binding("cert-a"));
        using (server) using (client)
        {
            Assert.True(server.IsBound);
            Assert.True(client.IsBound);
            await Exchange(client, server);
            await Exchange(server, client);
        }
    }

    [Fact]
    public async Task A_different_binding_on_the_two_sides_fails_at_the_server()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pair = await LoopbackStreamPair.CreateAsync();
        var accepting = SecureChannel.AcceptAsync(pair.Server, [Key], deadline.Token, Binding("cert-b"));
        var connecting = SecureChannel.ConnectAsync(pair.Client, Key, deadline.Token, Binding("cert-a"));

        await Assert.ThrowsAsync<AuthException>(() => accepting);
        pair.Server.Dispose();                                   // the server drops the connection, as it does for a wrong key
        await Assert.ThrowsAsync<AuthException>(() => connecting);
    }

    [Fact]
    public async Task A_server_with_a_binding_still_accepts_an_older_client_that_sends_the_plain_proof()
    {
        var (server, _, client) = await Pair(Binding("cert-a"), clientBinding: null);
        using (server) using (client)
        {
            Assert.False(server.IsBound);
            Assert.False(client.IsBound);
            await Exchange(client, server);
        }
    }

    [Fact]
    public async Task A_server_that_requires_the_binding_refuses_the_plain_proof()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pair = await LoopbackStreamPair.CreateAsync();
        var accepting = SecureChannel.AcceptAsync(pair.Server, [Key], deadline.Token, Binding("cert-a"), requireBinding: true);
        var connecting = SecureChannel.ConnectAsync(pair.Client, Key, deadline.Token, binding: null);

        var refused = await Assert.ThrowsAsync<AuthException>(() => accepting);
        Assert.Contains("bind", refused.Message);
        pair.Server.Dispose();
        await Assert.ThrowsAsync<AuthException>(() => connecting);
    }

    [Fact]
    public async Task A_bound_client_cannot_talk_to_a_server_that_does_not_know_the_bound_form()
    {
        // This is why the Host binds only to a Client whose protocol version says it understands the bound form.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pair = await LoopbackStreamPair.CreateAsync();
        var accepting = SecureChannel.AcceptAsync(pair.Server, [Key], deadline.Token);
        var connecting = SecureChannel.ConnectAsync(pair.Client, Key, deadline.Token, Binding("cert-a"));

        await Assert.ThrowsAsync<AuthException>(() => accepting);
        pair.Server.Dispose();
        await Assert.ThrowsAsync<AuthException>(() => connecting);
    }

    [Fact]
    public async Task A_wrong_key_is_still_refused_in_the_bound_form()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pair = await LoopbackStreamPair.CreateAsync();
        var accepting = SecureChannel.AcceptAsync(pair.Server, [Key], deadline.Token, Binding("cert-a"));
        var connecting = SecureChannel.ConnectAsync(pair.Client, Encoding.UTF8.GetBytes("another-key"), deadline.Token, Binding("cert-a"));

        await Assert.ThrowsAsync<AuthException>(() => accepting);
        pair.Server.Dispose();
        await Assert.ThrowsAsync<AuthException>(() => connecting);
    }

    [Fact]
    public void A_stream_that_is_not_tls_has_no_binding()
    {
        using var memory = new MemoryStream();
        Assert.Null(SecureChannel.BindingOf(memory));
    }

    [Fact]
    public async Task A_relay_with_a_certificate_of_its_own_cannot_forward_a_bound_proof_but_could_forward_a_plain_one()
    {
        // Host --TLS(attacker certificate)--> relay --TLS(Client certificate)--> Client, the relay copying bytes between the two.
        foreach (var bound in new[] { false, true })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ct = deadline.Token;
            using var hostLeg = await LoopbackStreamPair.CreateAsync();
            using var clientLeg = await LoopbackStreamPair.CreateAsync();
            using var attackerCert = Certificate("synthetic-attacker");
            using var clientCert = Certificate("synthetic-client");
            using var host = new SslStream(hostLeg.Client, false, (_, _, _, _) => true);
            using var relayServer = new SslStream(hostLeg.Server, false);
            using var relayClient = new SslStream(clientLeg.Client, false, (_, _, _, _) => true);
            using var client = new SslStream(clientLeg.Server, false);
            await Task.WhenAll(
                host.AuthenticateAsClientAsync(TlsProfile.Client("synthetic-client"), ct),
                relayServer.AuthenticateAsServerAsync(TlsProfile.Server(attackerCert), ct),
                relayClient.AuthenticateAsClientAsync(TlsProfile.Client("synthetic-client"), ct),
                client.AuthenticateAsServerAsync(TlsProfile.Server(clientCert), ct));

            using var pumping = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var forward = Pump(relayServer, relayClient, pumping.Token);
            var reverse = Pump(relayClient, relayServer, pumping.Token);
            try
            {
                var accepting = SecureChannel.AcceptAsync(client, [Key], ct, bound ? SecureChannel.BindingOf(client) : null);
                var connecting = SecureChannel.ConnectAsync(host, Key, ct, bound ? SecureChannel.BindingOf(host) : null);
                if (!bound)
                {
                    await Task.WhenAll(accepting, connecting);       // the relay reads everything (the existing TlsRelayTests show it)
                    (await accepting).Channel.Dispose();
                    (await connecting).Dispose();
                }
                else
                {
                    await Assert.ThrowsAsync<AuthException>(() => accepting);   // the Client sees a proof for another certificate
                    client.Dispose();
                    relayServer.Dispose();                                      // the relay's copy loop ends and the Host's side closes
                    await Assert.ThrowsAnyAsync<Exception>(() => connecting);
                }
            }
            finally
            {
                pumping.Cancel();
                await Task.WhenAll(forward, reverse);
            }
        }
    }

    static async Task Pump(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var count = await from.ReadAsync(buffer, ct);
                if (count == 0) return;
                await to.WriteAsync(buffer.AsMemory(0, count), ct);
                await to.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (AuthenticationException) { }
    }

    static X509Certificate2 Certificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
    }
}
