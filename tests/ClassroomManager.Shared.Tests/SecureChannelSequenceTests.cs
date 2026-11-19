// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>
/// Inside an authenticated session every frame's MAC covers its sequence number, so a frame recorded on the wire cannot be played again
/// or moved. Found by mutation testing (Stryker, 2026-10-24): taking the sequence number out of the MAC changed no test's result.
/// </summary>
public sealed class SecureChannelSequenceTests
{
    static readonly byte[] Psk = Encoding.UTF8.GetBytes("synthetic-sequence-test-key");

    /// <summary>
    /// The client's end of the wire. During the handshake it passes everything through; afterwards it only records what the client sends,
    /// so the test decides which frames reach the server and in which order.
    /// </summary>
    sealed class Tap(Stream inner) : Stream
    {
        public bool Forward { get; set; } = true;
        public MemoryStream Recorded { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { if (Forward) inner.Flush(); }
        public override Task FlushAsync(CancellationToken ct) => Forward ? inner.FlushAsync(ct) : Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Forward) await inner.WriteAsync(buffer, ct);
            else Recorded.Write(buffer.Span);
        }
    }

    /// <summary>Splits recorded bytes into whole frames: 4-byte length, body, 32-byte MAC.</summary>
    static List<byte[]> Frames(byte[] wire)
    {
        var frames = new List<byte[]>();
        for (var at = 0; at < wire.Length;)
        {
            var size = 4 + BinaryPrimitives.ReadInt32BigEndian(wire.AsSpan(at)) + 32;
            frames.Add(wire[at..(at + size)]);
            at += size;
        }
        return frames;
    }

    static async Task<(SecureChannel Server, Tap Tap, List<byte[]> Frames, LoopbackStreamPair Pair)> RecordAsync(params string[] bodies)
    {
        var pair = await LoopbackStreamPair.CreateAsync();
        var tap = new Tap(pair.Client);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var accepting = SecureChannel.AcceptAsync(pair.Server, Psk, deadline.Token);
        var client = await SecureChannel.ConnectAsync(tap, Psk, deadline.Token);
        var server = await accepting;

        tap.Forward = false;
        foreach (var body in bodies) await client.SendAsync(Encoding.UTF8.GetBytes(body));
        return (server, tap, Frames(tap.Recorded.ToArray()), pair);
    }

    static async Task DeliverAsync(LoopbackStreamPair pair, byte[] frame)
    {
        await pair.Client.WriteAsync(frame);
        await pair.Client.FlushAsync();
    }

    static async Task<string> ReceiveAsync(SecureChannel server)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return Encoding.UTF8.GetString(await server.ReceiveAsync(deadline.Token));
    }

    [Fact]
    public async Task Frames_delivered_in_the_order_they_were_sent_are_accepted()
    {
        var (server, _, frames, pair) = await RecordAsync("first", "second");
        using (pair)
        {
            await DeliverAsync(pair, frames[0]);
            await DeliverAsync(pair, frames[1]);

            Assert.Equal("first", await ReceiveAsync(server));
            Assert.Equal("second", await ReceiveAsync(server));
        }
    }

    [Fact]
    public async Task A_frame_played_again_is_refused_even_when_its_body_is_the_same_as_the_next_one()
    {
        // Two sends of the same command differ only in their sequence number: replaying the first in place of the second must fail.
        var (server, _, frames, pair) = await RecordAsync("lock.set", "lock.set");
        using (pair)
        {
            await DeliverAsync(pair, frames[0]);
            Assert.Equal("lock.set", await ReceiveAsync(server));

            await DeliverAsync(pair, frames[0]);
            await Assert.ThrowsAsync<AuthException>(() => ReceiveAsync(server));
        }
    }

    [Fact]
    public async Task Frames_swapped_on_the_wire_are_refused()
    {
        var (server, _, frames, pair) = await RecordAsync("first", "second");
        using (pair)
        {
            await DeliverAsync(pair, frames[1]);
            await Assert.ThrowsAsync<AuthException>(() => ReceiveAsync(server));
        }
    }

    [Fact]
    public async Task A_frame_sent_by_the_server_cannot_be_reflected_back_to_it()
    {
        // The direction byte is in the MAC as well: the server's own message, sent back, is not a message from the client.
        var pair = await LoopbackStreamPair.CreateAsync();
        using (pair)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var accepting = SecureChannel.AcceptAsync(pair.Server, Psk, deadline.Token);
            var client = await SecureChannel.ConnectAsync(pair.Client, Psk, deadline.Token);
            var server = await accepting;

            await server.SendAsync(Encoding.UTF8.GetBytes("from the server"));
            var raw = new byte[4 + "from the server".Length + 32];
            var read = 0;
            while (read < raw.Length) read += await pair.Client.ReadAsync(raw.AsMemory(read), deadline.Token);

            await DeliverAsync(pair, raw);
            await Assert.ThrowsAsync<AuthException>(() => ReceiveAsync(server));
            GC.KeepAlive(client);
        }
    }

    [Fact]
    public async Task The_largest_allowed_frame_goes_through_and_one_byte_more_is_refused_before_anything_is_sent()
    {
        var pair = await LoopbackStreamPair.CreateAsync();
        using (pair)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var accepting = SecureChannel.AcceptAsync(pair.Server, Psk, deadline.Token);
            var client = await SecureChannel.ConnectAsync(pair.Client, Psk, deadline.Token);
            var server = await accepting;

            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new byte[Protocol.MaxFrameBytes + 1]));

            var largest = new byte[Protocol.MaxFrameBytes];
            largest[^1] = 7;
            var sending = client.SendAsync(largest, deadline.Token);
            var received = await server.ReceiveAsync(deadline.Token);
            await sending;
            Assert.Equal(Protocol.MaxFrameBytes, received.Length);
            Assert.Equal(7, received[^1]);
        }
    }
}
