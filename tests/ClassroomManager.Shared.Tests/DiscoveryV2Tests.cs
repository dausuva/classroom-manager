// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class DiscoveryV2Tests
{
    static readonly byte[] LabKey = Encoding.UTF8.GetBytes("synthetic-lab-key-0001");
    static readonly byte[] Other = Encoding.UTF8.GetBytes("synthetic-lab-key-0002");
    static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    const string ClientId = "6532665a4a5e45f4bad18a2d66b44aaa";
    const string Mac = "C46516BDAFE1";

    sealed class FakeClock : TimeProvider
    {
        DateTimeOffset _now = Now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    static byte[] NewNonce() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(DiscoveryV2.NonceLength);

    [Fact]
    public void The_discovery_key_comes_from_the_lab_key_but_is_not_the_lab_key_and_differs_per_lab()
    {
        var key = DiscoveryV2.DeriveKey(LabKey);

        Assert.Equal(32, key.Length);
        Assert.NotEqual(LabKey, key);
        Assert.Equal(key, DiscoveryV2.DeriveKey(LabKey));
        Assert.NotEqual(key, DiscoveryV2.DeriveKey(Other));
    }

    [Fact]
    public void A_probe_is_64_bytes_and_verifies_with_the_same_key_inside_the_window()
    {
        var key = DiscoveryV2.DeriveKey(LabKey);
        var nonce = NewNonce();
        var probe = DiscoveryV2.CreateProbe(key, nonce, Now);

        Assert.Equal(DiscoveryV2.ProbeLength, probe.Length);
        Assert.True(DiscoveryV2.LooksLikeProbe(probe));
        Assert.True(DiscoveryV2.TryReadProbe(probe, key, Now.AddSeconds(30), Window, out var read));
        Assert.Equal(nonce, read);
        Assert.True(DiscoveryV2.TryReadProbe(probe, key, Now.AddSeconds(-30), Window, out _));
    }

    [Fact]
    public void A_probe_is_refused_for_another_key_an_old_time_a_future_time_or_any_changed_byte()
    {
        var key = DiscoveryV2.DeriveKey(LabKey);
        var probe = DiscoveryV2.CreateProbe(key, NewNonce(), Now);

        Assert.False(DiscoveryV2.TryReadProbe(probe, DiscoveryV2.DeriveKey(Other), Now, Window, out _));
        Assert.False(DiscoveryV2.TryReadProbe(probe, key, Now.AddHours(1), Window, out _));      // an hour-old probe
        Assert.False(DiscoveryV2.TryReadProbe(probe, key, Now.AddHours(-1), Window, out _));     // a probe from the future
        for (var i = 0; i < 29 + 16; i++)                                                       // every byte before the padding is covered
        {
            var changed = (byte[])probe.Clone();
            changed[i] ^= 0x01;
            Assert.False(DiscoveryV2.TryReadProbe(changed, key, Now, Window, out _), $"byte {i}");
        }
        Assert.False(DiscoveryV2.TryReadProbe(probe[..63], key, Now, Window, out _));
        Assert.False(DiscoveryV2.TryReadProbe(Encoding.UTF8.GetBytes(Protocol.DiscoverMagic), key, Now, Window, out _));   // the old probe
        Assert.False(DiscoveryV2.TryReadProbe(new byte[64], key, Now, Window, out _));
    }

    [Fact]
    public void The_reply_is_never_larger_than_the_probe_and_holds_no_name_mac_or_client_id()
    {
        var key = DiscoveryV2.DeriveKey(LabKey);
        var nonce = NewNonce();
        var probe = DiscoveryV2.CreateProbe(key, nonce, Now);
        var reply = DiscoveryV2.CreateReply(key, nonce, 5050, 17, ClientId, Mac);

        Assert.Equal(DiscoveryV2.ReplyLength, reply.Length);
        Assert.True(reply.Length <= probe.Length, "a reply larger than the question is an amplifier");
        var text = Encoding.UTF8.GetString(reply) + Convert.ToHexString(reply);
        Assert.DoesNotContain(ClientId, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Mac, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("410-17", text);
    }

    [Fact]
    public void The_host_reads_the_reply_to_its_own_probe_and_recognises_a_computer_it_knows_by_client_id_or_card()
    {
        var key = DiscoveryV2.DeriveKey(LabKey);
        var nonce = NewNonce();
        var packet = DiscoveryV2.CreateReply(key, nonce, 5050, 17, ClientId, Mac);

        Assert.True(DiscoveryV2.TryReadReply(packet, key, nonce, out var reply));
        Assert.Equal((5050, 17), (reply.TcpPort, reply.ProtocolMinor));
        Assert.True(DiscoveryV2.Matches(reply, key, nonce, ClientId, ""));
        Assert.True(DiscoveryV2.Matches(reply, key, nonce, "", "c4-65-16-bd-af-e1"));        // the card in any common writing
        Assert.True(DiscoveryV2.Matches(reply, key, nonce, "someone-else", Mac));
        Assert.False(DiscoveryV2.Matches(reply, key, nonce, "someone-else", "AABBCCDDEEFF"));
        Assert.False(DiscoveryV2.Matches(reply, key, nonce, "", ""));                        // nothing known: no match
    }

    [Fact]
    public void A_reply_for_another_probe_another_key_or_with_a_changed_byte_is_not_accepted()
    {
        var key = DiscoveryV2.DeriveKey(LabKey);
        var nonce = NewNonce();
        var packet = DiscoveryV2.CreateReply(key, nonce, 5050, 17, ClientId, Mac);

        Assert.False(DiscoveryV2.TryReadReply(packet, key, NewNonce(), out _));
        Assert.False(DiscoveryV2.TryReadReply(packet, DiscoveryV2.DeriveKey(Other), nonce, out _));
        for (var i = 4; i < DiscoveryV2.ReplyLength; i++)
        {
            var changed = (byte[])packet.Clone();
            changed[i] ^= 0x01;
            Assert.False(DiscoveryV2.TryReadReply(changed, key, nonce, out _), $"byte {i}");
        }
    }

    [Fact]
    public void A_nonce_is_accepted_once_inside_its_memory_and_again_after_it_has_passed()
    {
        var clock = new FakeClock();
        var cache = new ProbeReplayCache(TimeSpan.FromMinutes(2), 100, clock);
        var nonce = NewNonce();

        Assert.True(cache.TryRemember(nonce));
        Assert.False(cache.TryRemember(nonce));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(cache.TryRemember(nonce));
        clock.Advance(TimeSpan.FromMinutes(1.5));
        Assert.True(cache.TryRemember(nonce));
    }

    [Fact]
    public void The_replay_cache_cannot_grow_past_its_capacity_and_frees_old_entries()
    {
        var clock = new FakeClock();
        var cache = new ProbeReplayCache(TimeSpan.FromMinutes(2), 10, clock);

        var accepted = Enumerable.Range(0, 50).Count(_ => cache.TryRemember(NewNonce()));
        Assert.Equal(10, accepted);

        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(cache.TryRemember(NewNonce()));
    }

    // ---- enrolment mode ----------------------------------------------------------------------------

    static readonly byte[] Certificate = System.Security.Cryptography.SHA256.HashData("a certificate"u8.ToArray());

    [Fact]
    public void An_enrolment_reply_is_63_bytes_shorter_than_the_probe_and_carries_the_certificate_only_for_the_key_holder()
    {
        var key = DiscoveryV2.DeriveKey(LabKey);
        var nonce = NewNonce();
        var packet = DiscoveryV2.CreateEnrolmentReply(key, nonce, 5050, 18, Certificate);

        Assert.Equal(DiscoveryV2.EnrolmentReplyLength, packet.Length);
        Assert.True(packet.Length <= DiscoveryV2.ProbeLength, "a reply larger than the question is an amplifier");
        Assert.True(DiscoveryV2.LooksLikeEnrolmentReply(packet));
        Assert.False(DiscoveryV2.LooksLikeReply(packet));
        Assert.True(DiscoveryV2.TryReadEnrolmentReply(packet, key, nonce, out var offer));
        Assert.Equal((5050, 18), (offer.TcpPort, offer.ProtocolMinor));
        Assert.Equal(Certificate, offer.CertificateSha256);
    }

    [Fact]
    public void An_enrolment_reply_is_refused_for_another_key_another_probe_or_any_changed_byte()
    {
        var key = DiscoveryV2.DeriveKey(LabKey);
        var nonce = NewNonce();
        var packet = DiscoveryV2.CreateEnrolmentReply(key, nonce, 5050, 18, Certificate);

        Assert.False(DiscoveryV2.TryReadEnrolmentReply(packet, DiscoveryV2.DeriveKey(Other), nonce, out _));
        Assert.False(DiscoveryV2.TryReadEnrolmentReply(packet, key, NewNonce(), out _));
        for (var i = 4; i < DiscoveryV2.EnrolmentReplyLength; i++)     // the certificate, the port and the tag: every byte is covered
        {
            var changed = (byte[])packet.Clone();
            changed[i] ^= 0x01;
            Assert.False(DiscoveryV2.TryReadEnrolmentReply(changed, key, nonce, out _), $"byte {i}");
        }
        Assert.False(DiscoveryV2.TryReadEnrolmentReply(DiscoveryV2.CreateReply(key, nonce, 5050, 18, ClientId, Mac), key, nonce, out _));   // the ordinary reply is not an enrolment
    }

    [Fact]
    public void An_enrolment_reply_needs_a_real_nonce_and_fingerprint()
    {
        var key = DiscoveryV2.DeriveKey(LabKey);

        Assert.Throws<ArgumentException>(() => DiscoveryV2.CreateEnrolmentReply(key, new byte[8], 5050, 18, Certificate));
        Assert.Throws<ArgumentException>(() => DiscoveryV2.CreateEnrolmentReply(key, NewNonce(), 5050, 18, new byte[20]));
    }
}
