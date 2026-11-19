// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// DR-2 core: the recovery engine finishes or undoes an operation after the process ends at any step. The files are in memory, and a
/// "crash" is an exception the engine does not catch, followed by a new engine (the next process) over the same files.
/// </summary>
public sealed class HostRecoveryEngineTests
{
    sealed class Crash : Exception;

    /// <summary>In-memory files that can end the process after a given number of writes, deletes and record changes.</summary>
    sealed class MemoryFiles : IHostRecoveryFiles
    {
        public readonly Dictionary<RecoveryRole, byte[]> Roles = [];
        public byte[]? Record;
        public int Operations;
        public int CrashAt = -1;
        public bool FailRecordDelete;
        public RecoveryRole? FailWriteOf;
        public bool FailWritingCommittedRecord;

        void Step()
        {
            if (Operations++ == CrashAt) throw new Crash();
        }

        public byte[]? Read(RecoveryRole role) => Roles.TryGetValue(role, out var bytes) ? bytes.ToArray() : null;

        public void Write(RecoveryRole role, byte[] bytes)
        {
            Step();
            if (FailWriteOf == role) throw new IOException("disk full");
            Roles[role] = bytes.ToArray();
        }

        public void Delete(RecoveryRole role) { Step(); Roles.Remove(role); }
        public byte[]? ReadRecord() => Record?.ToArray();

        public void WriteRecord(byte[] bytes)
        {
            Step();
            if (FailWritingCommittedRecord && Record is not null) throw new IOException("record write failed");
            Record = bytes.ToArray();
        }

        public void DeleteRecord()
        {
            Step();
            if (FailRecordDelete) throw new IOException("record delete failed");
            Record = null;
        }
    }

    static readonly HostRecoveryCodec Codec = new(new PlainProtection());

    sealed class PlainProtection : IHostRecoveryProtection
    {
        public byte[] Protect(byte[] plaintext) => [.. "P:"u8, .. plaintext];
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.Length >= 2 && ciphertext[0] == (byte)'P' ? ciphertext[2..] : throw new InvalidDataException();
    }

    static byte[] B(string text) => Encoding.UTF8.GetBytes(text);

    static Dictionary<RecoveryRole, byte[]?> Next(string generation) => new()
    {
        [RecoveryRole.Settings] = B("settings " + generation),
        [RecoveryRole.SettingsBackup] = B("settings backup " + generation),
        [RecoveryRole.Keys] = B("keys " + generation),
        [RecoveryRole.KeysBackup] = null,   // absent after: the delete case
    };

    static MemoryFiles Existing()
    {
        var files = new MemoryFiles();
        files.Roles[RecoveryRole.Settings] = B("settings old");
        files.Roles[RecoveryRole.SettingsBackup] = B("settings backup old");
        files.Roles[RecoveryRole.Keys] = B("keys old");
        files.Roles[RecoveryRole.KeysBackup] = B("keys backup old");
        return files;
    }

    static string Generation(MemoryFiles files) =>
        string.Join("|", Enum.GetValues<RecoveryRole>().Select(role => files.Roles.TryGetValue(role, out var bytes) ? Encoding.UTF8.GetString(bytes) : "(absent)"));

    static string Old => Generation(Existing());

    static string New
    {
        get
        {
            var files = new MemoryFiles();
            Assert.Equal(RecoveryCommit.Committed, new HostRecoveryEngine(files, Codec).Commit(RecoveryOperation.Save, Next("new")));
            return Generation(files);
        }
    }

    static HostRecoveryEngine Engine(MemoryFiles files) => new(files, Codec);

    [Fact]
    public void A_commit_replaces_the_four_files_including_an_absence_and_leaves_no_record()
    {
        var files = Existing();

        var result = Engine(files).Commit(RecoveryOperation.Save, Next("new"));

        Assert.Equal(RecoveryCommit.Committed, result);
        Assert.Equal("settings new|settings backup new|keys new|(absent)", Generation(files));
        Assert.Null(files.Record);
    }

    [Fact]
    public void Termination_at_every_step_leaves_the_complete_old_pair_or_the_complete_new_pair_never_a_mixture()
    {
        var total = CountOperations();
        // Prepared record, four files, Committed record, removal of the record.
        Assert.Equal(7, total);
        for (var crashAt = 0; crashAt < total; crashAt++)
        {
            var files = Existing();
            files.CrashAt = crashAt;
            Assert.Throws<Crash>(() => Engine(files).Commit(RecoveryOperation.Save, Next("new")));
            files.CrashAt = -1;

            var resolution = Engine(files).Resolve();

            Assert.True(resolution is RecoveryResolution.RolledBack or RecoveryResolution.RolledForward or RecoveryResolution.Clean, $"step {crashAt}: {resolution}");
            Assert.True(Generation(files) == Old || Generation(files) == New, $"step {crashAt}: {Generation(files)}");
            Assert.Null(files.Record);
            // The commit point: once the Committed record was published, the result is the new pair.
            if (resolution == RecoveryResolution.RolledForward) Assert.Equal(New, Generation(files));
            if (resolution == RecoveryResolution.RolledBack) Assert.Equal(Old, Generation(files));
            // Running recovery again changes nothing.
            Assert.Equal(RecoveryResolution.Clean, Engine(files).Resolve());
        }
    }

    [Fact]
    public void Termination_during_recovery_itself_is_recovered_again_to_the_same_end()
    {
        var total = CountOperations();
        for (var first = 1; first < total; first++)
        {
            var interrupted = Existing();
            interrupted.CrashAt = first;
            Assert.Throws<Crash>(() => Engine(interrupted).Commit(RecoveryOperation.Save, Next("new")));
            interrupted.CrashAt = -1;
            var expected = Resolved(interrupted);

            for (var second = 0; second < 8; second++)
            {
                var files = Clone(interrupted);
                files.Operations = 0;
                files.CrashAt = second;
                try { Engine(files).Resolve(); } catch (Crash) { }
                files.CrashAt = -1;

                Engine(files).Resolve();

                Assert.Equal(expected, Generation(files));
                Assert.Null(files.Record);
            }
        }
    }

    [Fact]
    public void A_failed_write_before_the_commit_point_puts_the_old_files_back_and_says_not_committed()
    {
        var files = Existing();
        files.FailWriteOf = RecoveryRole.Keys;

        var result = Engine(files).Commit(RecoveryOperation.Save, Next("new"));

        Assert.Equal(RecoveryCommit.NotCommitted, result);
        Assert.Equal(Old, Generation(files));
        Assert.Null(files.Record);
    }

    [Fact]
    public void A_failure_writing_the_committed_record_rolls_back()
    {
        var files = Existing();
        files.FailWritingCommittedRecord = true;

        var result = Engine(files).Commit(RecoveryOperation.Save, Next("new"));

        Assert.Equal(RecoveryCommit.NotCommitted, result);
        Assert.Equal(Old, Generation(files));
        Assert.Null(files.Record);
    }

    [Fact]
    public void A_record_that_cannot_be_removed_after_the_commit_reports_the_commit_and_the_next_resolve_finishes_it()
    {
        var files = Existing();
        files.FailRecordDelete = true;

        var result = Engine(files).Commit(RecoveryOperation.Save, Next("new"));

        Assert.Equal(RecoveryCommit.CommittedCleanupPending, result);
        Assert.Equal(New, Generation(files));
        Assert.NotNull(files.Record);

        files.FailRecordDelete = false;
        Assert.Equal(RecoveryResolution.RolledForward, Engine(files).Resolve());
        Assert.Null(files.Record);
        Assert.Equal(New, Generation(files));
    }

    [Fact]
    public void A_new_write_first_finishes_an_earlier_interrupted_operation()
    {
        var files = Existing();
        files.CrashAt = 6;   // after the committed record, before it is removed
        Assert.Throws<Crash>(() => Engine(files).Commit(RecoveryOperation.Save, Next("first")));
        files.CrashAt = -1;

        var result = Engine(files).Commit(RecoveryOperation.Save, Next("second"));

        Assert.Equal(RecoveryCommit.Committed, result);
        Assert.Equal("settings second|settings backup second|keys second|(absent)", Generation(files));
    }

    [Fact]
    public void A_file_that_neither_side_of_the_operation_explains_is_kept_and_recovery_is_required()
    {
        var files = Existing();
        files.CrashAt = 3;
        Assert.Throws<Crash>(() => Engine(files).Commit(RecoveryOperation.Save, Next("new")));
        files.CrashAt = -1;
        files.Roles[RecoveryRole.Settings] = B("edited by someone else");
        var snapshot = Generation(files);
        var record = files.Record!.ToArray();

        Assert.Equal(RecoveryResolution.RecoveryRequired, Engine(files).Resolve());
        Assert.Equal(RecoveryCommit.RecoveryRequired, Engine(files).Commit(RecoveryOperation.Save, Next("later")));

        Assert.Equal(snapshot, Generation(files));
        Assert.Equal(record, files.Record);
    }

    [Fact]
    public void A_tampered_or_unreadable_record_requires_recovery_and_nothing_is_changed()
    {
        var files = Existing();
        files.CrashAt = 3;
        Assert.Throws<Crash>(() => Engine(files).Commit(RecoveryOperation.Save, Next("new")));
        files.CrashAt = -1;
        files.Record![files.Record.Length / 2] ^= 1;
        var snapshot = Generation(files);

        Assert.Equal(RecoveryResolution.RecoveryRequired, Engine(files).Resolve());
        Assert.Equal(snapshot, Generation(files));
        Assert.NotNull(files.Record);
    }

    [Fact]
    public void No_pair_at_all_can_become_a_pair_and_back_with_exact_absence()
    {
        var files = new MemoryFiles();
        Assert.Equal(RecoveryCommit.Committed, Engine(files).Commit(RecoveryOperation.Import, Next("first")));
        Assert.Equal(RecoveryCommit.Committed, Engine(files).Commit(RecoveryOperation.Restore, new Dictionary<RecoveryRole, byte[]?>()));
        Assert.Empty(files.Roles);
    }

    // ---- helpers

    static int CountOperations()
    {
        var files = Existing();
        Assert.Equal(RecoveryCommit.Committed, Engine(files).Commit(RecoveryOperation.Save, Next("new")));
        return files.Operations;
    }

    static string Resolved(MemoryFiles interrupted)
    {
        var copy = Clone(interrupted);
        Engine(copy).Resolve();
        return Generation(copy);
    }

    static MemoryFiles Clone(MemoryFiles files)
    {
        var copy = new MemoryFiles { Record = files.Record?.ToArray() };
        foreach (var (role, bytes) in files.Roles) copy.Roles[role] = bytes.ToArray();
        return copy;
    }
}

/// <summary>The disk adapter: where each role lives, and that it replaces and deletes exactly.</summary>
public sealed class HostRecoveryDiskFilesTests : IDisposable
{
    readonly string _folder = Path.Combine(TestDataFolder.Scratch, "recovery-disk-" + Guid.NewGuid().ToString("N"));

    public HostRecoveryDiskFilesTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    [Fact]
    public void Roles_are_the_settings_the_keys_and_their_backups_and_the_record_sits_beside_them()
    {
        var files = new HostRecoveryDiskFiles(Path.Combine(_folder, "host.json"));

        Assert.Equal(Path.Combine(_folder, "host.json"), files.PathFor(RecoveryRole.Settings));
        Assert.Equal(Path.Combine(_folder, "host.json.bak"), files.PathFor(RecoveryRole.SettingsBackup));
        Assert.Equal(Path.Combine(_folder, "host.keys"), files.PathFor(RecoveryRole.Keys));
        Assert.Equal(Path.Combine(_folder, "host.keys.bak"), files.PathFor(RecoveryRole.KeysBackup));
        Assert.Equal(Path.Combine(_folder, "host.recovery"), files.RecordPath);
    }

    [Fact]
    public void A_real_commit_replaces_the_files_exactly_without_rotating_the_backups_and_survives_a_second_run()
    {
        var path = Path.Combine(_folder, "host.json");
        var files = new HostRecoveryDiskFiles(path);
        var engine = new HostRecoveryEngine(files, new HostRecoveryCodec());
        File.WriteAllText(path, "old");
        File.WriteAllText(path + ".bak", "older");
        File.WriteAllBytes(HostSecretStore.PathFor(path), [1, 2, 3]);

        var result = engine.Commit(RecoveryOperation.Save, new Dictionary<RecoveryRole, byte[]?>
        {
            [RecoveryRole.Settings] = "new"u8.ToArray(),
            [RecoveryRole.SettingsBackup] = "old"u8.ToArray(),
            [RecoveryRole.Keys] = [4, 5, 6],
        });

        Assert.Equal(RecoveryCommit.Committed, result);
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal("old", File.ReadAllText(path + ".bak"));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(HostSecretStore.PathFor(path)));
        Assert.False(File.Exists(HostSecretStore.PathFor(path) + ".bak"));
        Assert.False(File.Exists(files.RecordPath));
        Assert.Equal(RecoveryResolution.Clean, engine.Resolve());
    }

    [Fact]
    public void A_record_bigger_than_the_codec_allows_is_not_read_whole()
    {
        var files = new HostRecoveryDiskFiles(Path.Combine(_folder, "host.json"));
        File.WriteAllBytes(files.RecordPath, new byte[HostRecoveryCodec.MaxRecordBytes + 1]);

        Assert.Throws<IOException>(() => files.ReadRecord());
        Assert.Equal(RecoveryResolution.RecoveryRequired, new HostRecoveryEngine(files, new HostRecoveryCodec()).Resolve());
    }
}
