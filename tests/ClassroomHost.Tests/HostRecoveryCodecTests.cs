// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class HostRecoveryCodecTests
{
    const string Secret = "synthetic-legacy-secret-2864a879";
    static HostRecoveryRecord Record(RecoveryPhase phase = RecoveryPhase.Prepared) => new(Guid.NewGuid(), phase,
        RecoveryOperation.Import, Enum.GetValues<RecoveryRole>().Select(role => new RecoveryChange(role,
            new(role == RecoveryRole.Keys ? null : Encoding.UTF8.GetBytes("before " + Secret)),
            new(role == RecoveryRole.SettingsBackup ? [] : Encoding.UTF8.GetBytes("after " + Secret)))));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Current_user_DPAPI_round_trip_preserves_all_four_roles_and_exact_presence(int phaseValue)
    {
        var phase = (RecoveryPhase)phaseValue;
        var codec = new HostRecoveryCodec();
        var expected = Record(phase);
        var envelope = codec.Encode(expected);
        Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(envelope));
        var actual = codec.Decode(envelope);
        Assert.Equal(expected.Transaction, actual.Transaction);
        Assert.Equal(phase, actual.Phase);
        Assert.Equal(RecoveryOperation.Import, actual.Operation);
        foreach (var (left, right) in expected.Changes.Zip(actual.Changes))
        {
            Assert.Equal(left.Role, right.Role);
            Assert.Equal(left.Before.Present, right.Before.Present);
            Assert.Equal(left.Before.Bytes.ToArray(), right.Before.Bytes.ToArray());
            Assert.Equal(left.After.Present, right.After.Present);
            Assert.Equal(left.After.Bytes.ToArray(), right.After.Bytes.ToArray());
        }
    }

    [Fact]
    public void Tampering_or_wrong_protection_purpose_has_a_secret_free_recovery_required_outcome()
    {
        var codec = new HostRecoveryCodec();
        var envelope = codec.Encode(Record());
        envelope[envelope.Length / 2] ^= 1;
        AssertSafe(() => codec.Decode(envelope));
        var otherPurpose = ProtectedData.Protect(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes("wrong-purpose"), DataProtectionScope.CurrentUser);
        AssertSafe(() => codec.Decode(otherPurpose));
        AssertSafe(() => new HostRecoveryCodec(new FailingProtection()).Encode(Record()));
        AssertSafe(() => new HostRecoveryCodec(new FailingProtection()).Decode([1]));
    }

    [Theory]
    [InlineData(0)] // magic
    [InlineData(4)] // version
    [InlineData(24)] // phase
    [InlineData(25)] // operation
    [InlineData(26)] // source generation
    [InlineData(58)] // target generation
    [InlineData(90)] // role count
    [InlineData(94)] // role: paths/unknown roles cannot be represented
    [InlineData(95)] // image length
    [InlineData(99)] // image hash
    [InlineData(131)] // image contents
    public void Invalid_metadata_lengths_generations_and_images_are_rejected(int offset)
    {
        var codec = new HostRecoveryCodec(new CopyProtection());
        var envelope = codec.Encode(Record());
        envelope[offset] ^= 0x7f;
        AssertSafe(() => codec.Decode(envelope));
    }

    [Fact]
    public void Truncated_trailing_oversized_and_unbounded_length_records_are_refused()
    {
        var codec = new HostRecoveryCodec(new CopyProtection());
        var envelope = codec.Encode(Record());
        foreach (var length in new[] { 0, 7, 25, 94, envelope.Length - 1 })
            AssertSafe(() => codec.Decode(envelope[..length]));
        AssertSafe(() => codec.Decode([.. envelope, 0]));
        BitConverter.GetBytes(int.MaxValue).CopyTo(envelope, 95);
        AssertSafe(() => codec.Decode(envelope));
        var protection = new CountingProtection();
        AssertSafe(() => new HostRecoveryCodec(protection).Decode(new byte[HostRecoveryCodec.MaxRecordBytes + 1]));
        Assert.Equal(0, protection.Calls);
        AssertSafe(() => new RecoveryImage(new byte[HostRecoveryCodec.MaxFileBytes + 1]));
    }

    [Fact]
    public void Empty_transaction_unknown_phase_operation_and_duplicate_or_missing_roles_cannot_be_encoded()
    {
        var record = Record();
        var codec = new HostRecoveryCodec();
        foreach (var invalid in new[]
        {
            new HostRecoveryRecord(Guid.Empty, record.Phase, record.Operation, record.Changes),
            new HostRecoveryRecord(record.Transaction, (RecoveryPhase)99, record.Operation, record.Changes),
            new HostRecoveryRecord(record.Transaction, record.Phase, (RecoveryOperation)99, record.Changes),
            new HostRecoveryRecord(record.Transaction, record.Phase, record.Operation, record.Changes.Take(3)),
            new HostRecoveryRecord(record.Transaction, record.Phase, record.Operation, Enumerable.Repeat(record.Changes[0], 4))
        }) AssertSafe(() => codec.Encode(invalid));
    }

    [Fact]
    public void Image_owns_a_copy_and_no_role_supplies_a_filesystem_path()
    {
        var bytes = Encoding.UTF8.GetBytes(Secret);
        var image = new RecoveryImage(bytes);
        bytes[0] = 0;
        Assert.Equal(Secret, Encoding.UTF8.GetString(image.Bytes));
        Assert.Equal(new[] { "Settings", "SettingsBackup", "Keys", "KeysBackup" }, Enum.GetNames<RecoveryRole>());
    }

    static void AssertSafe(Action action)
    {
        var error = Assert.Throws<HostRecoveryFormatException>(action);
        Assert.Equal(HostSettingsCommitOutcome.RecoveryRequired, error.Outcome);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.Null(error.InnerException);
    }

    sealed class CopyProtection : IHostRecoveryProtection
    {
        public byte[] Protect(byte[] bytes) => bytes.ToArray();
        public byte[] Unprotect(byte[] bytes) => bytes.ToArray();
    }
    sealed class FailingProtection : IHostRecoveryProtection
    {
        public byte[] Protect(byte[] _) => throw new CryptographicException(Secret);
        public byte[] Unprotect(byte[] _) => throw new CryptographicException(Secret);
    }
    sealed class CountingProtection : IHostRecoveryProtection
    {
        public int Calls;
        public byte[] Protect(byte[] _) { Calls++; return []; }
        public byte[] Unprotect(byte[] _) { Calls++; return []; }
    }
}
