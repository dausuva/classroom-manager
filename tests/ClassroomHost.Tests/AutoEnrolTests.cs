// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-185 enrolment mode on the Host: a computer that announces itself is added only when every safeguard holds.</summary>
public sealed class AutoEnrolTests
{
    static readonly string Pin = Convert.ToHexString(SHA256.HashData("computer 17"u8.ToArray()));
    static readonly string OtherPin = Convert.ToHexString(SHA256.HashData("someone else"u8.ToArray()));

    static EnrolmentOffer Offer(string address = "10.42.4.68", string? pin = null, int port = 5050) => new(IPAddress.Parse(address), port, 18, pin ?? Pin);

    static Func<EnrolmentOffer, CancellationToken, Task<string>> Presents(string pin) => (_, _) => Task.FromResult(pin);

    [Fact]
    public async Task A_new_address_whose_real_certificate_is_the_announced_one_is_approved_and_added()
    {
        var settings = new HostSettings();
        var plan = AutoEnrol.Plan(settings, [Offer()]);

        var done = await AutoEnrol.ApplyAsync(settings, plan, Presents(Pin), addComputers: true);

        Assert.Equal(AutoEnrolAction.Enrol, Assert.Single(done).Action);
        Assert.Equal([Pin], settings.TlsPins("10.42.4.68", 5050, ""));
        var computer = Assert.Single(settings.Computers);
        Assert.Equal(("10.42.4.68", 0), (computer.Address, computer.Port));
    }

    [Fact]
    public async Task A_computer_whose_real_certificate_differs_from_the_announced_one_is_refused_and_nothing_is_written()
    {
        var settings = new HostSettings();

        var done = await AutoEnrol.ApplyAsync(settings, AutoEnrol.Plan(settings, [Offer()]), Presents(OtherPin), addComputers: true);

        Assert.Equal(AutoEnrolAction.Refused, Assert.Single(done).Action);
        Assert.Contains("not the one it announced", done[0].Text);
        Assert.Empty(settings.TrustedTlsEndpoints);
        Assert.Empty(settings.Computers);
    }

    [Fact]
    public async Task A_computer_that_does_not_answer_the_look_at_its_certificate_is_refused()
    {
        var settings = new HostSettings();

        var done = await AutoEnrol.ApplyAsync(settings, AutoEnrol.Plan(settings, [Offer()]), Presents(""), addComputers: true);

        Assert.Equal(AutoEnrolAction.Refused, done[0].Action);
        Assert.Empty(settings.TrustedTlsEndpoints);
    }

    [Fact]
    public async Task An_address_that_has_an_approval_is_never_changed_whatever_it_announces()
    {
        var settings = new HostSettings();
        settings.EnrollTrust("10.42.4.68", 5050, "C46516BDAFE1", [OtherPin]);

        var plan = AutoEnrol.Plan(settings, [Offer()]);
        var done = await AutoEnrol.ApplyAsync(settings, plan, Presents(Pin), addComputers: true);

        Assert.Equal(AutoEnrolAction.DifferentApproval, Assert.Single(done).Action);
        Assert.Contains("trust-check", done[0].Text);
        Assert.Equal([OtherPin], settings.TrustedTlsEndpoints.Single().CertificateSha256);
        Assert.Empty(settings.Computers);
    }

    [Fact]
    public void An_address_already_approved_with_the_announced_certificate_needs_nothing()
    {
        var settings = new HostSettings();
        settings.EnrollTrust("10.42.4.68", 5050, "C46516BDAFE1", [Pin.ToLowerInvariant()]);

        Assert.Equal(AutoEnrolAction.AlreadyApproved, Assert.Single(AutoEnrol.Plan(settings, [Offer()])).Action);
    }

    [Fact]
    public async Task A_computer_that_is_already_saved_is_not_added_twice()
    {
        var settings = new HostSettings { Computers = [new SavedComputer { Address = "10.42.4.68", Name = "410-17", ClientId = "id-17" }] };

        await AutoEnrol.ApplyAsync(settings, AutoEnrol.Plan(settings, [Offer()]), Presents(Pin), addComputers: true);

        Assert.Equal("410-17", Assert.Single(settings.Computers).Name);
        Assert.Equal([Pin], settings.TlsPins("10.42.4.68", 5050, ""));
    }

    [Fact]
    public async Task Two_offers_from_one_address_count_once_and_a_different_port_is_kept()
    {
        var settings = new HostSettings();

        var plan = AutoEnrol.Plan(settings, [Offer(), Offer(), Offer("10.42.4.69", port: 6000)]);
        await AutoEnrol.ApplyAsync(settings, plan, Presents(Pin), addComputers: true);

        Assert.Equal(2, plan.Count);
        Assert.Equal(6000, settings.Computers.Single(computer => computer.Address == "10.42.4.69").Port);
    }

    [Fact]
    public async Task More_than_the_limit_in_one_scan_is_not_enrolled_by_itself()
    {
        var settings = new HostSettings();
        var offers = Enumerable.Range(1, AutoEnrol.MaxPerScan + 5).Select(i => Offer("10.0.0." + i)).ToList();

        var done = await AutoEnrol.ApplyAsync(settings, AutoEnrol.Plan(settings, offers), Presents(Pin), addComputers: true);

        Assert.Equal(AutoEnrol.MaxPerScan, done.Count(decision => decision.Action == AutoEnrolAction.Enrol));
        Assert.Equal(5, done.Count(decision => decision.Action == AutoEnrolAction.Refused));
        Assert.Equal(AutoEnrol.MaxPerScan, settings.Computers.Count);
    }

    [Fact]
    public void Enrolment_is_on_by_default_and_a_reply_is_read_only_against_the_probes_sent_with_the_hosts_keys()
    {
        var settings = new HostSettings { Psk = "synthetic-lab-key-0001" };
        Assert.True(settings.AutoEnrolNewComputers);

        var probes = Discovery.MakeProbes(settings, DateTimeOffset.UtcNow);
        var sent = probes.Select(probe => probe.Sent).ToList();
        var certificate = SHA256.HashData("computer 17"u8.ToArray());
        var genuine = DiscoveryV2.CreateEnrolmentReply(sent[0].DiscoveryKey, sent[0].Nonce, 5050, 18, certificate);
        var forged = DiscoveryV2.CreateEnrolmentReply(DiscoveryV2.DeriveKey(Encoding.UTF8.GetBytes("not-our-key")), sent[0].Nonce, 5050, 18, certificate);
        var stale = DiscoveryV2.CreateEnrolmentReply(sent[0].DiscoveryKey, new byte[16], 5050, 18, certificate);

        Assert.Equal(certificate, Discovery.ReadEnrolment(genuine, sent)!.CertificateSha256);
        Assert.Null(Discovery.ReadEnrolment(forged, sent));
        Assert.Null(Discovery.ReadEnrolment(stale, sent));
        Assert.Null(Discovery.ReadEnrolment(new byte[DiscoveryV2.EnrolmentReplyLength], sent));
    }
}
