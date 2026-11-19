// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ClassroomManager.Shared;

/// <summary>What an authenticated discovery reply tells the Host before any connection: a port, a protocol and two keyed tags.</summary>
public sealed record DiscoveryV2Reply(int TcpPort, int ProtocolMinor, byte[] IdTag, byte[] MacTag);

/// <summary>A newly installed computer announcing its certificate to the Host that holds the lab key (T-185, enrolment mode).</summary>
public sealed record DiscoveryV2Enrolment(int TcpPort, int ProtocolMinor, byte[] CertificateSha256);

/// <summary>
/// Authenticated, minimal discovery (T-185, protocol 1.18). Wire formats, all binary and fixed size:
///
///   probe, 64 bytes : "CMD2" | version 2 | nonce (16) | time (8, seconds since 1970, big endian) | tag (16) | zero padding (19)
///   reply, 51 bytes : "CMR2" | nonce (16) | tcpPort (2) | protocolMinor (1) | idTag (8) | macTag (8) | tag (12)
///   enrolment reply, 63 bytes : "CME2" | nonce (16) | tcpPort (2) | protocolMinor (1) | certificate SHA-256 (32) | tag (8)
///
/// The enrolment reply replaces the ordinary one while a Client is newly installed and no Host has connected yet. It carries the
/// SHA-256 of the Client's TLS certificate under the same discovery key, so only a holder of the lab key can have produced it; the Host
/// can approve that certificate without a person comparing digits, as it does for an enrolment file. It is shorter than the probe, and its
/// 8-byte tag is checked against a fresh nonce, so forging one within a scan takes about 2^63 tries.
///
/// Every tag is a truncated HMAC-SHA256 under the discovery key, which is derived from a lab key (HKDF, label "discovery-v2") so the lab
/// key itself is never used for broadcast. The probe is padded so that the reply can never be larger than the question it answers (a
/// forged source address then gains nothing); an invalid probe gets no answer at all; the reply carries no name, no MAC and no client id,
/// only tags of them that the Host recomputes for the computers it already knows.
/// </summary>
public static class DiscoveryV2
{
    public const int ProbeLength = 64;
    public const int ReplyLength = 51;
    public const int NonceLength = 16;
    static readonly byte[] ProbeMagic = "CMD2"u8.ToArray();
    static readonly byte[] ReplyMagic = "CMR2"u8.ToArray();
    const int ProbeTagLength = 16;
    const int IdTagLength = 8;
    const int ReplyTagLength = 12;

    /// <summary>The discovery key of a lab key: the lab key is never used for broadcast itself.</summary>
    public static byte[] DeriveKey(byte[] labKey) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, labKey, 32, salt: null, info: "discovery-v2"u8.ToArray());

    public static bool LooksLikeProbe(ReadOnlySpan<byte> packet) => packet.Length == ProbeLength && packet[..4].SequenceEqual(ProbeMagic);
    public static bool LooksLikeReply(ReadOnlySpan<byte> packet) => packet.Length == ReplyLength && packet[..4].SequenceEqual(ReplyMagic);

    public const int EnrolmentReplyLength = 63;
    static readonly byte[] EnrolmentMagic = "CME2"u8.ToArray();
    const int EnrolmentTagLength = 8;

    public static bool LooksLikeEnrolmentReply(ReadOnlySpan<byte> packet) =>
        packet.Length == EnrolmentReplyLength && packet[..4].SequenceEqual(EnrolmentMagic);

    public static byte[] CreateEnrolmentReply(byte[] discoveryKey, byte[] nonce, int tcpPort, int protocolMinor, byte[] certificateSha256)
    {
        if (nonce.Length != NonceLength) throw new ArgumentException("The nonce must be 16 bytes.", nameof(nonce));
        if (certificateSha256.Length != 32) throw new ArgumentException("A certificate fingerprint is 32 bytes.", nameof(certificateSha256));
        var packet = new byte[EnrolmentReplyLength];
        EnrolmentMagic.CopyTo(packet, 0);
        nonce.CopyTo(packet, 4);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), (ushort)tcpPort);
        packet[22] = (byte)Math.Clamp(protocolMinor, 0, 255);
        certificateSha256.CopyTo(packet, 23);
        Tag(discoveryKey, "enrol", packet.AsSpan(0, 55), EnrolmentTagLength).CopyTo(packet, 55);
        return packet;
    }

    /// <summary>True when the packet is an enrolment reply under this discovery key to the probe with this nonce.</summary>
    public static bool TryReadEnrolmentReply(byte[] packet, byte[] discoveryKey, byte[] nonce, out DiscoveryV2Enrolment enrolment)
    {
        enrolment = new DiscoveryV2Enrolment(0, 0, []);
        if (!LooksLikeEnrolmentReply(packet) || !packet.AsSpan(4, NonceLength).SequenceEqual(nonce)) return false;
        if (!CryptographicOperations.FixedTimeEquals(Tag(discoveryKey, "enrol", packet.AsSpan(0, 55), EnrolmentTagLength), packet.AsSpan(55, EnrolmentTagLength))) return false;
        enrolment = new DiscoveryV2Enrolment(BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(20, 2)), packet[22], packet.AsSpan(23, 32).ToArray());
        return true;
    }

    public static byte[] CreateProbe(byte[] discoveryKey, byte[] nonce, DateTimeOffset now)
    {
        if (nonce.Length != NonceLength) throw new ArgumentException("The nonce must be 16 bytes.", nameof(nonce));
        var packet = new byte[ProbeLength];
        ProbeMagic.CopyTo(packet, 0);
        packet[4] = 2;
        nonce.CopyTo(packet, 5);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(21, 8), now.ToUnixTimeSeconds());
        Tag(discoveryKey, "probe", packet.AsSpan(0, 29), ProbeTagLength).CopyTo(packet, 29);
        return packet;
    }

    /// <summary>True when the packet is a probe made with this discovery key and not older or newer than <paramref name="window"/>.</summary>
    public static bool TryReadProbe(byte[] packet, byte[] discoveryKey, DateTimeOffset now, TimeSpan window, out byte[] nonce)
    {
        nonce = [];
        if (!LooksLikeProbe(packet) || packet[4] != 2) return false;
        var tagOk = CryptographicOperations.FixedTimeEquals(Tag(discoveryKey, "probe", packet.AsSpan(0, 29), ProbeTagLength), packet.AsSpan(29, ProbeTagLength));
        var seconds = BinaryPrimitives.ReadInt64BigEndian(packet.AsSpan(21, 8));
        var difference = Math.Abs(now.ToUnixTimeSeconds() - seconds);
        if (!tagOk || difference > window.TotalSeconds) return false;
        nonce = packet.AsSpan(5, NonceLength).ToArray();
        return true;
    }

    public static byte[] CreateReply(byte[] discoveryKey, byte[] nonce, int tcpPort, int protocolMinor, string clientId, string mac)
    {
        var packet = new byte[ReplyLength];
        ReplyMagic.CopyTo(packet, 0);
        nonce.CopyTo(packet, 4);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), (ushort)tcpPort);
        packet[22] = (byte)Math.Clamp(protocolMinor, 0, 255);
        IdentityTag(discoveryKey, "id", nonce, clientId).CopyTo(packet, 23);
        IdentityTag(discoveryKey, "mac", nonce, NormalizeMac(mac)).CopyTo(packet, 31);
        Tag(discoveryKey, "reply", packet.AsSpan(0, 39), ReplyTagLength).CopyTo(packet, 39);
        return packet;
    }

    /// <summary>True when the packet is a reply under this discovery key to the probe with this nonce.</summary>
    public static bool TryReadReply(byte[] packet, byte[] discoveryKey, byte[] nonce, out DiscoveryV2Reply reply)
    {
        reply = new DiscoveryV2Reply(0, 0, [], []);
        if (!LooksLikeReply(packet) || !packet.AsSpan(4, NonceLength).SequenceEqual(nonce)) return false;
        if (!CryptographicOperations.FixedTimeEquals(Tag(discoveryKey, "reply", packet.AsSpan(0, 39), ReplyTagLength), packet.AsSpan(39, ReplyTagLength))) return false;
        reply = new DiscoveryV2Reply(BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(20, 2)), packet[22],
            packet.AsSpan(23, IdTagLength).ToArray(), packet.AsSpan(31, IdTagLength).ToArray());
        return true;
    }

    /// <summary>Whether a reply belongs to the computer with this client id and card address (the Host knows both for a saved computer).</summary>
    public static bool Matches(DiscoveryV2Reply reply, byte[] discoveryKey, byte[] nonce, string clientId, string mac)
    {
        var idOk = clientId.Length > 0 && CryptographicOperations.FixedTimeEquals(IdentityTag(discoveryKey, "id", nonce, clientId), reply.IdTag);
        var macOk = NormalizeMac(mac).Length > 0 && CryptographicOperations.FixedTimeEquals(IdentityTag(discoveryKey, "mac", nonce, NormalizeMac(mac)), reply.MacTag);
        return idOk || macOk;
    }

    static string NormalizeMac(string mac) => new string((mac ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    static byte[] IdentityTag(byte[] key, string label, byte[] nonce, string value) =>
        Tag(key, label, [.. nonce, .. Encoding.UTF8.GetBytes(value)], IdTagLength);

    static byte[] Tag(byte[] key, string label, ReadOnlySpan<byte> data, int length)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(Encoding.UTF8.GetBytes(label));
        hmac.AppendData([0]);
        hmac.AppendData(data);
        return hmac.GetHashAndReset().AsSpan(0, length).ToArray();
    }
}

/// <summary>Remembers the nonces of probes already answered, so a recorded probe cannot be replayed within its time window.</summary>
public sealed class ProbeReplayCache
{
    readonly TimeSpan _memory;
    readonly int _capacity;
    readonly TimeProvider _time;
    readonly object _lock = new();
    readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    public ProbeReplayCache(TimeSpan? memory = null, int capacity = 4096, TimeProvider? time = null)
    {
        _memory = memory ?? TimeSpan.FromMinutes(2);
        _capacity = capacity;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>True the first time a nonce is offered inside the memory; false for a repeat, or when the cache is full of recent nonces.</summary>
    public bool TryRemember(byte[] nonce)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_seen.Count >= _capacity)
                foreach (var key in _seen.Where(pair => now - pair.Value > _memory).Select(pair => pair.Key).ToList()) _seen.Remove(key);
            var id = Convert.ToHexString(nonce);
            if (_seen.TryGetValue(id, out var when) && now - when <= _memory) return false;
            if (_seen.Count >= _capacity) return false;
            _seen[id] = now;
            return true;
        }
    }
}
