// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClassroomManager.Host;

// DR-1 only: no production reader/writer activates this format yet.
internal enum RecoveryRole : byte { Settings, SettingsBackup, Keys, KeysBackup }
internal enum RecoveryPhase : byte { Prepared, Committed }
internal enum RecoveryOperation : byte { Save, Patch, Import, Restore, Repair }

internal sealed class RecoveryImage
{
    readonly byte[]? _bytes;
    public bool Present => _bytes is not null;
    public ReadOnlySpan<byte> Bytes => _bytes;
    public RecoveryImage(byte[]? bytes)
    {
        if (bytes?.Length > HostRecoveryCodec.MaxFileBytes) throw new HostRecoveryFormatException();
        _bytes = bytes?.ToArray();
    }
}

internal sealed record RecoveryChange(RecoveryRole Role, RecoveryImage Before, RecoveryImage After);

internal sealed class HostRecoveryRecord(Guid transaction, RecoveryPhase phase, RecoveryOperation operation,
    IEnumerable<RecoveryChange> changes)
{
    public Guid Transaction { get; } = transaction;
    public RecoveryPhase Phase { get; } = phase;
    public RecoveryOperation Operation { get; } = operation;
    public IReadOnlyList<RecoveryChange> Changes { get; } = Array.AsReadOnly(changes.ToArray());
}

internal sealed class HostRecoveryFormatException() : IOException("Host settings recovery data could not be verified. Preserve the files for recovery.")
{
    public HostSettingsCommitOutcome Outcome => HostSettingsCommitOutcome.RecoveryRequired;
}

internal interface IHostRecoveryProtection
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

internal sealed class HostRecoveryProtection : IHostRecoveryProtection
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClassroomManager.Host.Recovery.v1");
    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>Bounded, path-free, authenticated recovery envelope. File publication and ACLs belong to DR-2/DR-3.</summary>
internal sealed class HostRecoveryCodec(IHostRecoveryProtection? protection = null)
{
    // Current real settings are ~26 KiB and protected keys ~0.5 KiB. Allow >80x
    // growth per image, while bounding eight images and DPAPI overhead explicitly.
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxPlainBytes = 8 * MaxFileBytes + 1024;
    public const int MaxRecordBytes = MaxPlainBytes + 65536;
    const int Magic = 0x31524d43; // CMR1, little endian
    readonly IHostRecoveryProtection _protection = protection ?? new HostRecoveryProtection();

    public byte[] Encode(HostRecoveryRecord record)
    {
        byte[]? plain = null;
        try
        {
            Validate(record);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(Magic);
            writer.Write(1);
            writer.Write(record.Transaction.ToByteArray());
            writer.Write((byte)record.Phase);
            writer.Write((byte)record.Operation);
            writer.Write(Generation(record, before: true));
            writer.Write(Generation(record, before: false));
            writer.Write(record.Changes.Count);
            foreach (var change in record.Changes)
            {
                writer.Write((byte)change.Role);
                WriteImage(writer, change.Before);
                WriteImage(writer, change.After);
            }
            writer.Flush();
            plain = stream.ToArray();
            var result = _protection.Protect(plain);
            if (result.Length == 0 || result.Length > MaxRecordBytes) throw new HostRecoveryFormatException();
            return result;
        }
        catch { throw new HostRecoveryFormatException(); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public HostRecoveryRecord Decode(byte[] ciphertext)
    {
        byte[]? plain = null;
        try
        {
            if (ciphertext.Length == 0 || ciphertext.Length > MaxRecordBytes) throw new HostRecoveryFormatException();
            plain = _protection.Unprotect(ciphertext);
            if (plain.Length > MaxPlainBytes) throw new HostRecoveryFormatException();
            using var stream = new MemoryStream(plain, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != 1) throw new HostRecoveryFormatException();
            var transaction = new Guid(ReadExact(reader, 16));
            var phase = (RecoveryPhase)reader.ReadByte();
            var operation = (RecoveryOperation)reader.ReadByte();
            var source = ReadExact(reader, 32);
            var target = ReadExact(reader, 32);
            if (reader.ReadInt32() != 4) throw new HostRecoveryFormatException();
            var changes = new List<RecoveryChange>();
            for (var role = 0; role < 4; role++)
            {
                if (reader.ReadByte() != role) throw new HostRecoveryFormatException();
                changes.Add(new((RecoveryRole)role, ReadImage(reader), ReadImage(reader)));
            }
            if (stream.Position != stream.Length) throw new HostRecoveryFormatException();
            var record = new HostRecoveryRecord(transaction, phase, operation, changes);
            Validate(record);
            if (!CryptographicOperations.FixedTimeEquals(source, Generation(record, true)) ||
                !CryptographicOperations.FixedTimeEquals(target, Generation(record, false))) throw new HostRecoveryFormatException();
            return record;
        }
        catch { throw new HostRecoveryFormatException(); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    static void Validate(HostRecoveryRecord record)
    {
        if (record.Transaction == Guid.Empty || !Enum.IsDefined(record.Phase) || !Enum.IsDefined(record.Operation) ||
            record.Changes.Count != 4) throw new HostRecoveryFormatException();
        for (var i = 0; i < 4; i++)
            if ((int)record.Changes[i].Role != i) throw new HostRecoveryFormatException();
    }

    static void WriteImage(BinaryWriter writer, RecoveryImage image)
    {
        writer.Write(image.Present ? image.Bytes.Length : -1);
        writer.Write(SHA256.HashData(image.Bytes));
        writer.Write(image.Bytes);
    }

    static RecoveryImage ReadImage(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < -1 || length > MaxFileBytes || length > reader.BaseStream.Length - reader.BaseStream.Position - 32)
            throw new HostRecoveryFormatException();
        var hash = ReadExact(reader, 32);
        var bytes = length == -1 ? null : ReadExact(reader, length);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(bytes ?? []))) throw new HostRecoveryFormatException();
            return new(bytes);
        }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    static byte[] Generation(HostRecoveryRecord record, bool before)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var change in record.Changes)
        {
            var image = before ? change.Before : change.After;
            hash.AppendData([(byte)change.Role, image.Present ? (byte)1 : (byte)0]);
            hash.AppendData(SHA256.HashData(image.Bytes));
        }
        return hash.GetHashAndReset();
    }

    static byte[] ReadExact(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new HostRecoveryFormatException();
        return bytes;
    }
}
