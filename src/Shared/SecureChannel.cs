// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClassroomManager.Shared;

public sealed class AuthException : Exception
{
    public AuthException(string message) : base(message) { }
}

/// <summary>
/// Length-prefixed, HMAC-authenticated message channel over a stream.
///
/// Handshake (mutual pre-shared-key authentication, nothing secret crosses the wire):
///   server -> client : nonceS (32 bytes)
///   client -> server : nonceC (32) + HMAC(psk, "H" | nonceS | nonceC)
///   server -> client : HMAC(psk, "S" | nonceC | nonceS)
/// Session key = HMAC(psk, "K" | nonceS | nonceC).
/// Every frame: [len:4 BE][body][HMAC(sessionKey, dir | seq:8 BE | body)]
/// so frames cannot be forged, modified, replayed or reflected.
///
/// Bound handshake (protocol 1.17, T-187), used inside TLS: the same three messages, but the proofs and the session key also cover
/// the SHA-256 of the server's TLS certificate ("binding") and use the tags B, T and Q instead of H, S and K. A relay that
/// holds two TLS sessions with different certificates cannot forward a proof from one to the other, because the binding differs.
/// A server accepts either form unless it is told to require the bound one; a client sends the bound form only to a server it knows
/// understands it, since an older server would see a wrong proof and drop the connection.
///
/// NOTE: this authenticates and integrity-protects traffic but does not encrypt it (TLS does that).
/// </summary>
public sealed class SecureChannel : IDisposable
{
    const int MacLen = 32;
    const int NonceLen = 32;

    readonly Stream _stream;
    readonly byte[] _key;
    readonly byte _sendDir;
    readonly byte _recvDir;
    ulong _sendSeq;
    ulong _recvSeq;

    /// <summary>True when the proofs covered the TLS certificate (the bound form).</summary>
    public bool IsBound { get; private init; }

    SecureChannel(Stream stream, byte[] key, byte sendDir, byte recvDir, bool bound = false)
    {
        _stream = stream;
        _key = key;
        _sendDir = sendDir;
        _recvDir = recvDir;
        IsBound = bound;
    }

    /// <summary>The binding for a TLS stream: the SHA-256 of the server certificate, or null when the stream is not TLS.</summary>
    public static byte[]? BindingOf(Stream stream) => stream switch
    {
        System.Net.Security.SslStream { IsServer: true, LocalCertificate: { } local } => SHA256.HashData(local.GetRawCertData()),
        System.Net.Security.SslStream { IsServer: false, RemoteCertificate: { } remote } => SHA256.HashData(remote.GetRawCertData()),
        _ => null,
    };

    // ---- handshake ------------------------------------------------------

    /// <summary>Server side (the classroom client app).</summary>
    public static async Task<SecureChannel> AcceptAsync(Stream stream, byte[] psk, CancellationToken ct) =>
        (await AcceptAsync(stream, [psk], ct)).Channel;

    /// <summary>
    /// Server side when several keys are accepted. The messages on the wire are the same as for one
    /// key, so the peer needs no change: the server works out which of its keys produced the peer's
    /// proof and reports that key's position, which is how the client knows who is calling.
    /// </summary>
    public static async Task<(SecureChannel Channel, int KeyIndex)> AcceptAsync(
        Stream stream, IReadOnlyList<byte[]> keys, CancellationToken ct, byte[]? binding = null, bool requireBinding = false)
    {
        if (keys.Count == 0) throw new ArgumentException("At least one key is required.", nameof(keys));
        var nonceS = RandomNumberGenerator.GetBytes(NonceLen);
        await stream.WriteAsync(nonceS, ct);
        await stream.FlushAsync(ct);

        var buf = new byte[NonceLen + MacLen];
        await ReadExactAsync(stream, buf, ct);
        var nonceC = buf.AsSpan(0, NonceLen).ToArray();
        var mac = buf.AsSpan(NonceLen, MacLen).ToArray();

        // Every key is checked in both forms every time, so the time taken does not reveal which one matched.
        var plainProof = Concat(Tag('H'), nonceS, nonceC);
        var boundProof = binding is null ? plainProof : Concat(Tag('B'), nonceS, nonceC, binding);
        var matched = -1;
        var bound = false;
        for (var i = 0; i < keys.Count; i++)
        {
            var plainOk = CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(keys[i], plainProof), mac);
            var boundOk = binding is not null && CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(keys[i], boundProof), mac);
            if ((plainOk || boundOk) && matched < 0) { matched = i; bound = boundOk; }
        }
        if (matched < 0) throw new AuthException("Bad pre-shared key from peer.");
        if (requireBinding && !bound) throw new AuthException("The peer did not bind its proof to the TLS session, which this computer requires.");
        var psk = keys[matched];

        var reply = HMACSHA256.HashData(psk, bound ? Concat(Tag('T'), nonceC, nonceS, binding!) : Concat(Tag('S'), nonceC, nonceS));
        await stream.WriteAsync(reply, ct);
        await stream.FlushAsync(ct);

        var key = HMACSHA256.HashData(psk, bound ? Concat(Tag('Q'), nonceS, nonceC, binding!) : Concat(Tag('K'), nonceS, nonceC));
        return (new SecureChannel(stream, key, sendDir: 2, recvDir: 1, bound), matched);
    }

    /// <summary>Client side (the teacher's host app).</summary>
    public static async Task<SecureChannel> ConnectAsync(Stream stream, byte[] psk, CancellationToken ct, byte[]? binding = null)
    {
        var nonceS = new byte[NonceLen];
        await ReadExactAsync(stream, nonceS, ct);
        var nonceC = RandomNumberGenerator.GetBytes(NonceLen);

        var mac = HMACSHA256.HashData(psk, binding is null ? Concat(Tag('H'), nonceS, nonceC) : Concat(Tag('B'), nonceS, nonceC, binding));
        await stream.WriteAsync(Concat(nonceC, mac), ct);
        await stream.FlushAsync(ct);

        var serverMac = new byte[MacLen];
        try
        {
            await ReadExactAsync(stream, serverMac, ct);
        }
        catch (EndOfStreamException)
        {
            // The client drops the connection when our key is wrong.
            throw new AuthException("Authentication failed (wrong shared key?).");
        }

        var expected = HMACSHA256.HashData(psk, binding is null ? Concat(Tag('S'), nonceC, nonceS) : Concat(Tag('T'), nonceC, nonceS, binding));
        if (!CryptographicOperations.FixedTimeEquals(expected, serverMac))
            throw new AuthException("Server failed to prove it knows the shared key.");

        var key = HMACSHA256.HashData(psk, binding is null ? Concat(Tag('K'), nonceS, nonceC) : Concat(Tag('Q'), nonceS, nonceC, binding));
        return new SecureChannel(stream, key, sendDir: 1, recvDir: 2, bound: binding is not null);
    }

    // ---- framing --------------------------------------------------------

    public async Task SendAsync(ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        if (body.Length > Protocol.MaxFrameBytes) throw new InvalidOperationException("Frame too large.");
        var mac = Mac(_sendDir, _sendSeq++, body.Span);
        var length = 4 + body.Length + mac.Length;
        var frame = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            // Same wire bytes, one transport/TLS write instead of three separately framed writes.
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), body.Length);
            body.CopyTo(frame.AsMemory(4));
            mac.CopyTo(frame, 4 + body.Length);
            await _stream.WriteAsync(frame.AsMemory(0, length), ct);
            await _stream.FlushAsync(ct);
        }
        finally { ArrayPool<byte>.Shared.Return(frame, clearArray: true); }
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken ct = default)
    {
        var header = new byte[4];
        await ReadExactAsync(_stream, header, ct);
        int len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len < 0 || len > Protocol.MaxFrameBytes) throw new InvalidDataException("Bad frame length.");

        var body = new byte[len];
        await ReadExactAsync(_stream, body, ct);
        var mac = new byte[MacLen];
        await ReadExactAsync(_stream, mac, ct);

        var expected = Mac(_recvDir, _recvSeq++, body);
        if (!CryptographicOperations.FixedTimeEquals(expected, mac))
            throw new AuthException("Message authentication failed.");
        return body;
    }

    public Task SendJsonAsync<T>(T value, CancellationToken ct = default) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json), ct);

    public async Task<T> ReceiveJsonAsync<T>(CancellationToken ct = default)
    {
        var body = await ReceiveAsync(ct);
        return JsonSerializer.Deserialize<T>(body, Protocol.Json)
               ?? throw new InvalidDataException("Empty message.");
    }

    // ---- helpers --------------------------------------------------------

    byte[] Mac(byte dir, ulong seq, ReadOnlySpan<byte> body)
    {
        Span<byte> prefix = stackalloc byte[9];
        prefix[0] = dir;
        BinaryPrimitives.WriteUInt64BigEndian(prefix[1..], seq);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _key);
        hmac.AppendData(prefix);
        hmac.AppendData(body);
        return hmac.GetHashAndReset();
    }

    static byte[] Tag(char c) => new[] { (byte)c };

    static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var result = new byte[total];
        int pos = 0;
        foreach (var p in parts) { p.CopyTo(result, pos); pos += p.Length; }
        return result;
    }

    static async Task ReadExactAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await s.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    public void Dispose() => _stream.Dispose();
}
