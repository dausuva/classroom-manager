// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>
/// T-192: what the handshake and the frame reader do with input nobody should send. A fixed seed and a fixed number of cases make this an
/// ordinary test; a longer run is a matter of raising <see cref="Cases"/>. The rule under test: bad input ends in one of a few named
/// exceptions, never in success, never in a crash of another kind, never in a hang.
/// </summary>
public sealed class SecureChannelNegativeTests
{
    const int Cases = 400;
    static readonly byte[] Psk = Encoding.UTF8.GetBytes("synthetic-fuzz-test-key");
    static CancellationToken Timeout(int seconds = 15) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    /// <summary>A stream that reads from fixed bytes, then ends, and throws away what is written.</summary>
    sealed class ScriptedStream(byte[] input) : Stream
    {
        readonly MemoryStream _in = new(input);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    /// <summary>Remembers every byte written through it.</summary>
    sealed class RecordingStream(Stream inner) : Stream
    {
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { Written.Write(buffer, offset, count); inner.Write(buffer, offset, count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Written.Write(buffer.Span);
            await inner.WriteAsync(buffer, ct);
        }
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    }

    static bool IsNamedFailure(Exception ex) => ex is AuthException or InvalidDataException or EndOfStreamException or IOException;

    static async Task<(SecureChannel Server, SecureChannel Client)> Established(LoopbackStreamPair pair, Stream? clientWire = null, Stream? serverWire = null)
    {
        var ct = Timeout();
        var accepting = SecureChannel.AcceptAsync(serverWire ?? pair.Server, Psk, ct);
        var connecting = SecureChannel.ConnectAsync(clientWire ?? pair.Client, Psk, ct);
        await Task.WhenAll(accepting, connecting);
        return (accepting.Result, connecting.Result);
    }

    // ---- handshake ----------------------------------------------------------------------------

    [Fact]
    public async Task Random_bytes_in_place_of_the_clients_handshake_never_authenticate_and_end_in_a_named_failure()
    {
        var random = new Random(20261023);
        for (var i = 0; i < Cases; i++)
        {
            var input = new byte[random.Next(0, 97)];
            random.NextBytes(input);
            var failure = await Record.ExceptionAsync(() => SecureChannel.AcceptAsync(new ScriptedStream(input), Psk, Timeout()));
            Assert.NotNull(failure);
            Assert.True(failure is AuthException or EndOfStreamException, $"case {i}, {input.Length} bytes: {failure}");
        }
    }

    [Fact]
    public async Task A_client_that_gets_random_bytes_in_place_of_the_servers_replies_never_trusts_them()
    {
        var random = new Random(20261024);
        for (var i = 0; i < Cases; i++)
        {
            var input = new byte[random.Next(0, 97)];
            random.NextBytes(input);
            var failure = await Record.ExceptionAsync(() => SecureChannel.ConnectAsync(new ScriptedStream(input), Psk, Timeout()));
            Assert.NotNull(failure);
            Assert.True(failure is AuthException or EndOfStreamException, $"case {i}, {input.Length} bytes: {failure}");
        }
    }

    // ---- frames after a good handshake ----------------------------------------------------------

    [Fact]
    public async Task Random_bytes_where_a_frame_should_be_end_in_a_named_failure()
    {
        var random = new Random(20261025);
        for (var i = 0; i < 150; i++)
        {
            using var pair = await LoopbackStreamPair.CreateAsync();
            var (server, client) = await Established(pair);
            using (server) using (client)
            {
                var junk = new byte[random.Next(0, 81)];
                random.NextBytes(junk);
                await pair.Client.WriteAsync(junk);
                pair.Client.Socket.Shutdown(System.Net.Sockets.SocketShutdown.Send);       // the peer ends: the reader must not wait for ever

                var failure = await Record.ExceptionAsync(() => server.ReceiveAsync(Timeout()));
                Assert.NotNull(failure);
                Assert.True(IsNamedFailure(failure!), $"case {i}, {junk.Length} bytes: {failure}");
            }
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(Protocol.MaxFrameBytes + 1)]
    [InlineData(int.MaxValue)]
    public async Task A_frame_length_outside_the_limit_is_refused_before_anything_is_allocated_for_it(int length)
    {
        using var pair = await LoopbackStreamPair.CreateAsync();
        var (server, client) = await Established(pair);
        using (server) using (client)
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, length);
            await pair.Client.WriteAsync(header);

            await Assert.ThrowsAsync<InvalidDataException>(() => server.ReceiveAsync(Timeout()));
        }
    }

    [Fact]
    public async Task A_truncated_header_or_body_ends_the_read_with_end_of_stream_not_a_hang()
    {
        foreach (var bytes in new[] { new byte[] { 0, 0 }, new byte[] { 0, 0, 0, 10, 1, 2, 3 } })
        {
            using var pair = await LoopbackStreamPair.CreateAsync();
            var (server, client) = await Established(pair);
            using (server) using (client)
            {
                await pair.Client.WriteAsync(bytes);
                pair.Client.Socket.Shutdown(System.Net.Sockets.SocketShutdown.Send);
                await Assert.ThrowsAsync<EndOfStreamException>(() => server.ReceiveAsync(Timeout()));
            }
        }
    }

    [Fact]
    public async Task An_empty_frame_with_a_zero_tag_is_refused()
    {
        using var pair = await LoopbackStreamPair.CreateAsync();
        var (server, client) = await Established(pair);
        using (server) using (client)
        {
            await pair.Client.WriteAsync(new byte[4 + 0 + 32]);          // length 0, no body, 32 zero bytes of tag
            await Assert.ThrowsAsync<AuthException>(() => server.ReceiveAsync(Timeout()));
        }
    }

    [Fact]
    public async Task A_frame_sent_by_the_server_and_reflected_back_to_it_is_refused()
    {
        using var pair = await LoopbackStreamPair.CreateAsync();
        var recording = new RecordingStream(pair.Server);
        var (server, client) = await Established(pair, serverWire: recording);
        using (server) using (client)
        {
            var handshakeBytes = (int)recording.Written.Length;
            await server.SendAsync(Encoding.UTF8.GetBytes("from the server"), Timeout());
            Assert.Equal("from the server", Encoding.UTF8.GetString(await client.ReceiveAsync(Timeout())));

            var frame = recording.Written.ToArray()[handshakeBytes..];
            await pair.Client.WriteAsync(frame);                          // an attacker repeats it towards the server

            await Assert.ThrowsAsync<AuthException>(() => server.ReceiveAsync(Timeout()));
        }
    }

    [Fact]
    public async Task A_frame_from_one_session_is_refused_in_another_session_with_the_same_key()
    {
        using var pairA = await LoopbackStreamPair.CreateAsync();
        using var pairB = await LoopbackStreamPair.CreateAsync();
        var recordingA = new RecordingStream(pairA.Client);
        var (serverA, clientA) = await Established(pairA, clientWire: recordingA);
        var (serverB, clientB) = await Established(pairB);
        using (serverA) using (clientA) using (serverB) using (clientB)
        {
            var handshakeBytes = (int)recordingA.Written.Length;
            await clientA.SendAsync(Encoding.UTF8.GetBytes("session A"), Timeout());
            Assert.Equal("session A", Encoding.UTF8.GetString(await serverA.ReceiveAsync(Timeout())));

            var frame = recordingA.Written.ToArray()[handshakeBytes..];
            await pairB.Client.WriteAsync(frame);                         // same direction, same sequence number, same key: still not valid

            await Assert.ThrowsAsync<AuthException>(() => serverB.ReceiveAsync(Timeout()));
        }
    }

    [Fact]
    public async Task A_peer_that_stops_sending_is_released_by_the_cancellation_token()
    {
        using var pair = await LoopbackStreamPair.CreateAsync();
        var (server, client) = await Established(pair);
        using (server) using (client)
        {
            using var short_ = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.ReceiveAsync(short_.Token));
        }
    }

    // ---- what an authenticated peer can still get wrong -----------------------------------------

    [Fact]
    public async Task An_authenticated_peer_that_sends_junk_instead_of_json_gets_a_json_error_not_a_crash()
    {
        using var pair = await LoopbackStreamPair.CreateAsync();
        var (server, client) = await Established(pair);
        using (server) using (client)
        {
            await client.SendAsync(Encoding.UTF8.GetBytes("{ this is not json"), Timeout());
            await Assert.ThrowsAsync<JsonException>(() => server.ReceiveJsonAsync<Request>(Timeout()));

            await client.SendAsync(Encoding.UTF8.GetBytes("null"), Timeout());
            await Assert.ThrowsAsync<InvalidDataException>(() => server.ReceiveJsonAsync<Request>(Timeout()));

            await client.SendAsync(new byte[] { 0xFF, 0xFE, 0x00, 0x80 }, Timeout());
            await Assert.ThrowsAnyAsync<JsonException>(() => server.ReceiveJsonAsync<Request>(Timeout()));
        }
    }

    [Fact]
    public async Task Random_bytes_as_the_body_of_an_authenticated_frame_never_crash_the_json_reader()
    {
        var random = new Random(20261026);
        using var pair = await LoopbackStreamPair.CreateAsync();
        var (server, client) = await Established(pair);
        using (server) using (client)
        {
            for (var i = 0; i < Cases; i++)
            {
                var body = new byte[random.Next(0, 200)];
                random.NextBytes(body);
                await client.SendAsync(body, Timeout());
                var failure = await Record.ExceptionAsync(() => server.ReceiveJsonAsync<Request>(Timeout()));
                Assert.True(failure is null or JsonException or InvalidDataException, $"case {i}: {failure}");
            }
        }
    }
}
