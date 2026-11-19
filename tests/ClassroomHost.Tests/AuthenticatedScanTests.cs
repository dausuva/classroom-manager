// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-185 on the Host side: which keys it probes with, and how it recognises a computer from tags alone.</summary>
public sealed class AuthenticatedScanTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    const string ClientId = "6532665a4a5e45f4bad18a2d66b44aaa";

    static HostSettings Settings() => new()
    {
        Psk = "synthetic-lab-key-0001",
        AdditionalHostKeys = [new NamedHostKey { Name = "Room 12", Psk = "synthetic-lab-key-0002" }, new NamedHostKey { Name = "Same again", Psk = "synthetic-lab-key-0001" }],
        Computers = [new SavedComputer { Name = "410-17", Address = "10.42.4.68", ClientId = ClientId, Mac = "C46516BDAFE1" },
                     new SavedComputer { Name = "410-18", Address = "10.42.4.69", ClientId = "other-id", Mac = "AABBCCDDEEFF" }],
    };

    [Fact]
    public void One_probe_is_made_per_different_lab_key_and_each_is_64_bytes_with_its_own_nonce()
    {
        var probes = Discovery.MakeProbes(Settings(), Now);

        Assert.Equal(2, probes.Count);                                       // the repeated key is probed once
        Assert.All(probes, probe => Assert.Equal(DiscoveryV2.ProbeLength, probe.Packet.Length));
        Assert.NotEqual(probes[0].Sent.Nonce, probes[1].Sent.Nonce);
        Assert.All(probes, probe => Assert.True(DiscoveryV2.TryReadProbe(probe.Packet, probe.Sent.DiscoveryKey, Now, TimeSpan.FromSeconds(60), out _)));
    }

    [Fact]
    public void A_reply_to_one_of_the_probes_is_turned_into_the_saved_computer_it_belongs_to()
    {
        var settings = Settings();
        var probes = Discovery.MakeProbes(settings, Now);
        var sent = probes.Select(probe => probe.Sent).ToList();
        var reply = DiscoveryV2.CreateReply(sent[1].DiscoveryKey, sent[1].Nonce, 5050, 18, ClientId, "C46516BDAFE1");   // made under the second lab key

        var known = Discovery.ReadAuthenticated(reply, sent, settings.Computers);

        Assert.NotNull(known);
        Assert.Equal(("410-17", ClientId, "C46516BDAFE1", 5050, "1.18"), (known!.MachineName, known.ClientId, known.Mac, known.TcpPort, known.Version));
    }

    [Fact]
    public void A_computer_the_host_does_not_know_a_reply_to_another_probe_and_junk_are_all_ignored()
    {
        var settings = Settings();
        var sent = Discovery.MakeProbes(settings, Now).Select(probe => probe.Sent).ToList();

        var stranger = DiscoveryV2.CreateReply(sent[0].DiscoveryKey, sent[0].Nonce, 5050, 18, "never-seen", "112233445566");
        Assert.Null(Discovery.ReadAuthenticated(stranger, sent, settings.Computers));

        var wrongNonce = DiscoveryV2.CreateReply(sent[0].DiscoveryKey, new byte[16], 5050, 18, ClientId, "C46516BDAFE1");
        Assert.Null(Discovery.ReadAuthenticated(wrongNonce, sent, settings.Computers));

        var otherLab = DiscoveryV2.CreateReply(DiscoveryV2.DeriveKey(Encoding.UTF8.GetBytes("not-our-key")), sent[0].Nonce, 5050, 18, ClientId, "C46516BDAFE1");
        Assert.Null(Discovery.ReadAuthenticated(otherLab, sent, settings.Computers));

        Assert.Null(Discovery.ReadAuthenticated(new byte[DiscoveryV2.ReplyLength], sent, settings.Computers));
    }

    [Fact]
    public void A_host_with_no_key_makes_no_probe()
    {
        Assert.Empty(Discovery.MakeProbes(new HostSettings { Psk = "" }, Now));
    }
}
