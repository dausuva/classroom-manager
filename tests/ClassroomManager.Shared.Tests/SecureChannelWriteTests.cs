// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class SecureChannelWriteTests
{
    sealed class Tap(Stream inner) : Stream
    {
        public bool Record;
        public List<byte[]> Writes = [];
        public int Flushes;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) { if (Record) Flushes++; return inner.FlushAsync(ct); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Record) { Writes.Add(buffer.ToArray()); return ValueTask.CompletedTask; }
            return inner.WriteAsync(buffer, ct);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1024)]
    [InlineData(Protocol.ChunkSize)]
    public async Task One_write_and_flush_contains_a_complete_legacy_compatible_authenticated_frame(int bytes)
    {
        using var pair = await LoopbackStreamPair.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var key = Encoding.UTF8.GetBytes("synthetic-coalesced-frame-key");
        var tap = new Tap(pair.Client);
        var accept = SecureChannel.AcceptAsync(pair.Server, key, deadline.Token);
        using var sender = await SecureChannel.ConnectAsync(tap, key, deadline.Token);
        using var receiver = await accept;
        tap.Record = true;
        var payload = Enumerable.Range(0, bytes).Select(index => (byte)(index % 251)).ToArray();
        await sender.SendAsync(payload, deadline.Token);
        var frame = Assert.Single(tap.Writes);
        Assert.Equal(1, tap.Flushes);
        Assert.Equal(bytes, BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(0, 4)));
        Assert.Equal(4 + bytes + 32, frame.Length);
        Assert.Equal(payload, frame[4..(4 + bytes)]);
        // Simulate the old writer's separate header/body/MAC writes; both versions use the same receiver.
        var reading = receiver.ReceiveAsync(deadline.Token);
        await pair.Client.WriteAsync(frame.AsMemory(0, 4), deadline.Token);
        await pair.Client.WriteAsync(frame.AsMemory(4, bytes), deadline.Token);
        await pair.Client.WriteAsync(frame.AsMemory(4 + bytes), deadline.Token);
        Assert.Equal(payload, await reading);
    }
}
