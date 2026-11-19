// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClassroomManager.Client;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>
/// T-185 enrolment mode from end to end, over real TLS: a newly installed Client announces its certificate, the Host reads it, looks at
/// the certificate the Client really presents, approves it and connects, with nobody typing a fingerprint.
/// </summary>
public sealed class EnrolmentEndToEndTests : IDisposable
{
    const string LabKey = "synthetic-lab-key-0001";
    readonly string _folder = Path.Combine(Path.GetTempPath(), "classroom-enrolE2E-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    static X509Certificate2 MakeCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=new-client", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
    }

    [Fact]
    public async Task A_new_client_is_found_approved_and_connected_to_without_anyone_typing_a_fingerprint_and_then_stops_announcing()
    {
        using var certificate = MakeCertificate();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var state = new EnrolmentState(Path.Combine(_folder, "enrolment.json"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var config = new ClientConfig { UseTls = true, TcpPort = port, ClientId = "id-new" };
        var keys = new KeyStore(LabKey, []);
        var server = new CommandServer(config, keys, (_, _, _, _) => Task.FromResult(Response.Success()),
            new PreAuthGate(new ConnectionLimits { FailuresBeforeBackoff = 100 }), tlsCertificate: certificate, enrolment: state);
        var responder = new DiscoveryResponder(config, keys, enrolment: state, certificate: () => server.CertificateSha256);
        var handled = new List<Task>();
        var accepting = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) { var tcp = await listener.AcceptTcpClientAsync(stop.Token); lock (handled) handled.Add(server.HandleAsync(tcp, stop.Token)); } }
            catch (OperationCanceledException) { }
        });

        // The Host: a lab key and nothing about this computer.
        var host = new HostSettings { UseTls = true, Psk = LabKey };
        var probes = Discovery.MakeProbes(host, DateTimeOffset.UtcNow);
        var reply = responder.AnswerProbe(probes[0].Packet, DateTimeOffset.UtcNow);
        var announced = Discovery.ReadEnrolment(reply!, probes.Select(probe => probe.Sent).ToList());
        Assert.NotNull(announced);
        var offer = new EnrolmentOffer(IPAddress.Loopback, announced!.TcpPort, announced.ProtocolMinor, Convert.ToHexString(announced.CertificateSha256));

        // Before: this Host does not trust it, so it cannot connect.
        var info = new ClientInfo { Address = "127.0.0.1", Port = port, Version = Protocol.Version };
        await Assert.ThrowsAnyAsync<Exception>(() => ClientSession.OpenAsync(info, host, stop.Token));

        var done = await AutoEnrol.ApplyAsync(host, AutoEnrol.Plan(host, [offer]), (o, ct) => AutoEnrol.PresentedAsync(host, o, ct), addComputers: true, stop.Token);

        Assert.Equal(AutoEnrolAction.Enrol, Assert.Single(done).Action);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(certificate.RawData)), host.TlsPins("127.0.0.1", port, "").Single());
        using (var session = await ClientSession.OpenAsync(info, host, stop.Token))
        {
            Assert.True(session.IsBound);
            await session.CallAsync(Cmd.Ping, ct: stop.Token);
        }

        // After: no Host needs to find it any more, so the next signed probe gets the ordinary tagged answer.
        for (var wait = 0; wait < 40 && state.IsOpen(7); wait++) await Task.Delay(50);
        var later = responder.AnswerProbe(Discovery.MakeProbes(host, DateTimeOffset.UtcNow)[0].Packet, DateTimeOffset.UtcNow);
        Assert.True(DiscoveryV2.LooksLikeReply(later!));

        stop.Cancel();
        await accepting;
        listener.Stop();
        Task[] all; lock (handled) all = [.. handled];
        await Task.WhenAll(all);
    }

    [Fact]
    public async Task A_computer_that_announces_one_certificate_and_presents_another_is_not_enrolled()
    {
        using var certificate = MakeCertificate();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new CommandServer(new ClientConfig { UseTls = true }, new KeyStore(LabKey, []), (_, _, _, _) => Task.FromResult(Response.Success()), tlsCertificate: certificate);
        var handled = new List<Task>();
        var accepting = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) { var tcp = await listener.AcceptTcpClientAsync(stop.Token); lock (handled) handled.Add(server.HandleAsync(tcp, stop.Token)); } }
            catch (OperationCanceledException) { }
        });
        var host = new HostSettings { UseTls = true, Psk = LabKey };
        var someoneElse = Convert.ToHexString(SHA256.HashData("another certificate"u8.ToArray()));
        var offer = new EnrolmentOffer(IPAddress.Loopback, port, 18, someoneElse);   // signed by the lab key, but not what this address presents

        var done = await AutoEnrol.ApplyAsync(host, AutoEnrol.Plan(host, [offer]), (o, ct) => AutoEnrol.PresentedAsync(host, o, ct), addComputers: true, stop.Token);

        Assert.Equal(AutoEnrolAction.Refused, Assert.Single(done).Action);
        Assert.Empty(host.TrustedTlsEndpoints);
        stop.Cancel();
        await accepting;
        listener.Stop();
        Task[] all; lock (handled) all = [.. handled];
        await Task.WhenAll(all);
    }
}
