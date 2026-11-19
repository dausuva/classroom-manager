// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ClassroomManager.Client;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>T-185 enrolment mode on the Client: a newly installed computer announces its certificate until a Host connects.</summary>
public sealed class EnrolmentModeTests : IDisposable
{
    const string LabKey = "synthetic-lab-key-0001";
    readonly string _folder = Path.Combine(Path.GetTempPath(), "classroom-enrol-" + Guid.NewGuid().ToString("N"));
    string StatePath => Path.Combine(_folder, "enrolment.json");
    static readonly byte[] Certificate = SHA256.HashData("this computer's certificate"u8.ToArray());

    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    // ---- the state ----------------------------------------------------------------------------------

    [Fact]
    public void A_fresh_install_is_open_for_the_window_and_then_closes_by_itself()
    {
        var clock = new FakeClock();
        var state = new EnrolmentState(StatePath, clock);

        Assert.True(state.IsOpen(7));
        clock.Advance(TimeSpan.FromDays(6.9));
        Assert.True(state.IsOpen(7));
        clock.Advance(TimeSpan.FromDays(0.2));
        Assert.False(state.IsOpen(7));
        Assert.True(File.Exists(StatePath));
    }

    [Fact]
    public void Zero_days_means_never_and_the_first_host_that_connects_closes_it_for_good()
    {
        var clock = new FakeClock();
        var state = new EnrolmentState(StatePath, clock);
        Assert.False(state.IsOpen(0));

        state.MarkEnrolled();
        state.MarkEnrolled();                                   // every session calls it

        Assert.False(state.IsOpen(7));
        Assert.Equal(clock.Now, state.Enrolled);
        var again = new EnrolmentState(StatePath, clock);       // a restart of the service
        Assert.False(again.IsOpen(7));
        Assert.Equal(clock.Now, again.Enrolled);
    }

    [Fact]
    public void A_restart_keeps_the_original_start_so_the_window_does_not_begin_again()
    {
        var clock = new FakeClock();
        _ = new EnrolmentState(StatePath, clock);
        clock.Advance(TimeSpan.FromDays(8));

        var restarted = new EnrolmentState(StatePath, clock);

        Assert.False(restarted.IsOpen(7));
    }

    [Fact]
    public void Deleting_the_file_opens_the_window_again_and_a_damaged_file_does_too()
    {
        var clock = new FakeClock();
        new EnrolmentState(StatePath, clock).MarkEnrolled();
        File.Delete(StatePath);
        Assert.True(new EnrolmentState(StatePath, clock).IsOpen(7));

        File.WriteAllText(StatePath, "not json {{{");
        Assert.True(new EnrolmentState(StatePath, clock).IsOpen(7));
    }

    // ---- the answer to a probe ---------------------------------------------------------------------

    static (DiscoveryResponder Responder, byte[] Key) Responder(EnrolmentState? state, int days = 7, byte[]? certificate = null)
    {
        var config = new ClientConfig { UdpPort = 5051, TcpPort = 5050, ClientId = "id-17", EnrolmentWindowDays = days };
        var responder = new DiscoveryResponder(config, new KeyStore(LabKey, []), enrolment: state, certificate: () => certificate ?? Certificate);
        return (responder, DiscoveryV2.DeriveKey(Encoding.UTF8.GetBytes(LabKey)));
    }

    [Fact]
    public void While_open_a_valid_probe_gets_the_certificate_and_nothing_that_names_the_computer()
    {
        var (responder, key) = Responder(new EnrolmentState(StatePath));
        var nonce = RandomNumberGenerator.GetBytes(16);

        var reply = responder.AnswerProbe(DiscoveryV2.CreateProbe(key, nonce, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);

        Assert.NotNull(reply);
        Assert.True(DiscoveryV2.TryReadEnrolmentReply(reply!, key, nonce, out var offer));
        Assert.Equal(Certificate, offer.CertificateSha256);
        Assert.Equal(5050, offer.TcpPort);
        Assert.True(reply!.Length <= DiscoveryV2.ProbeLength);
        Assert.DoesNotContain("id-17", Encoding.UTF8.GetString(reply));
    }

    [Fact]
    public void Once_a_host_has_connected_the_answer_is_the_ordinary_tagged_one()
    {
        var state = new EnrolmentState(StatePath);
        state.MarkEnrolled();
        var (responder, key) = Responder(state);
        var nonce = RandomNumberGenerator.GetBytes(16);

        var reply = responder.AnswerProbe(DiscoveryV2.CreateProbe(key, nonce, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow)!;

        Assert.True(DiscoveryV2.LooksLikeReply(reply));
        Assert.False(DiscoveryV2.LooksLikeEnrolmentReply(reply));
    }

    [Fact]
    public void Switched_off_or_without_a_certificate_the_answer_is_the_ordinary_one_and_a_wrong_key_still_gets_nothing()
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        var (off, key) = Responder(new EnrolmentState(StatePath), days: 0);
        Assert.True(DiscoveryV2.LooksLikeReply(off.AnswerProbe(DiscoveryV2.CreateProbe(key, nonce, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow)!));

        var noTls = new DiscoveryResponder(new ClientConfig { ClientId = "id-17" }, new KeyStore(LabKey, []), enrolment: new EnrolmentState(StatePath), certificate: () => null);
        Assert.True(DiscoveryV2.LooksLikeReply(noTls.AnswerProbe(DiscoveryV2.CreateProbe(key, RandomNumberGenerator.GetBytes(16), DateTimeOffset.UtcNow), DateTimeOffset.UtcNow)!));

        var (open, _) = Responder(new EnrolmentState(StatePath));
        var stranger = DiscoveryV2.DeriveKey(Encoding.UTF8.GetBytes("another-lab-key-9999"));
        Assert.Null(open.AnswerProbe(DiscoveryV2.CreateProbe(stranger, RandomNumberGenerator.GetBytes(16), DateTimeOffset.UtcNow), DateTimeOffset.UtcNow));
    }

    // ---- the server closes the window ---------------------------------------------------------------

    [Fact]
    public async Task The_first_good_handshake_closes_the_window_and_a_wrong_key_does_not()
    {
        var state = new EnrolmentState(StatePath);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new CommandServer(new ClientConfig { UseTls = false }, new KeyStore(LabKey, []), (_, _, _, _) => Task.FromResult(Response.Success()),
            new PreAuthGate(new ConnectionLimits { FailuresBeforeBackoff = 100 }), enrolment: state);
        var handled = new List<Task>();
        var accepting = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) { var tcp = await listener.AcceptTcpClientAsync(stop.Token); lock (handled) handled.Add(server.HandleAsync(tcp, stop.Token)); } }
            catch (OperationCanceledException) { }
        });
        var info = new ClientInfo { Address = "127.0.0.1", Port = port };
        var host = new HostSettings { UseTls = false };

        await Assert.ThrowsAnyAsync<Exception>(() => ClientSession.OpenWithKeyAsync(info, host, "a-wrong-key-entirely", stop.Token));
        // Wait until the server has finished with that connection, so "still open" is checked after it could have closed, not before.
        Task[] first;
        lock (handled) first = [.. handled];
        await Task.WhenAll(first).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(state.IsOpen(7));

        using (await ClientSession.OpenWithKeyAsync(info, host, LabKey, stop.Token)) { }
        for (var wait = 0; wait < 40 && state.IsOpen(7); wait++) await Task.Delay(50);   // the server finishes its side a moment after the Host has its answer
        Assert.False(state.IsOpen(7));
        Assert.NotNull(state.Enrolled);

        stop.Cancel();
        await accepting;
        listener.Stop();
        Task[] all; lock (handled) all = [.. handled];
        await Task.WhenAll(all);
    }

    [Fact]
    public void The_certificate_a_server_announces_is_the_one_it_presents()
    {
        using var rsa = RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=announce", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            made.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null, System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.DefaultKeySet);

        var server = new CommandServer(new ClientConfig { UseTls = true }, new KeyStore(LabKey, []), (_, _, _, _) => Task.FromResult(Response.Success()), tlsCertificate: certificate);

        Assert.Equal(SHA256.HashData(certificate.RawData), server.CertificateSha256);
        Assert.Null(new CommandServer(new ClientConfig { UseTls = false }, new KeyStore(LabKey, []), (_, _, _, _) => Task.FromResult(Response.Success())).CertificateSha256);
    }
}
