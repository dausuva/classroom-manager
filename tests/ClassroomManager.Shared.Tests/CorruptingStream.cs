// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Shared.Tests;

/// <summary>
/// Wraps a stream and, when armed, flips a bit in the very next write before it reaches the
/// underlying stream - used to simulate a frame being tampered with in transit so tests can
/// confirm SecureChannel's per-frame MAC actually catches it.
/// </summary>
sealed class CorruptingStream : Stream
{
    readonly Stream _inner;

    /// <summary>Set true to corrupt exactly the next write call; auto-resets after firing once.</summary>
    public bool CorruptNextWrite;

    public CorruptingStream(Stream inner) => _inner = inner;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // SecureChannel writes the four-byte header separately to avoid copying the whole payload.
        // Leave that header intact, then corrupt the following body write so frame authentication
        // (rather than length validation) remains what this test exercises.
        if (CorruptNextWrite && buffer.Length > 4)
        {
            CorruptNextWrite = false;
            var corrupted = buffer.ToArray();
            corrupted[^1] ^= 0xFF; // flip the frame's trailing byte (part of its MAC)
            return _inner.WriteAsync(corrupted, cancellationToken);
        }
        return _inner.WriteAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
