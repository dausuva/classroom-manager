// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

// T-222: explicit commit outcomes, paired key rollback and cooperating writer exclusion.
[Collection(nameof(HostBackupTests))]
public sealed class HostSettingsCommitTests : IDisposable
{
    readonly string _folder = Directory.CreateTempSubdirectory("cm-settings-commit-").FullName;
    string SettingsPath => Path.Combine(_folder, "host.json");
    string KeysPath => Path.Combine(_folder, "host.keys");
    public void Dispose() => Directory.Delete(_folder, recursive: true);

    static HostSettings Settings(string key = "fixture-old") => new()
    {
        HostId = "fixture", UploadToken = new string('a', 32), Psk = key,
        AdditionalHostKeys = [new NamedHostKey { Id = "lab", Name = "Lab", Psk = key + "-lab" }],
    };

    [Fact]
    public void Failed_json_replacement_restores_both_encrypted_key_generations_and_retry_preserves_pairing()
    {
        var settings = Settings();
        settings.Save(SettingsPath);
        settings.Psk = "fixture-middle";
        settings.Save(SettingsPath);
        var json = File.ReadAllBytes(SettingsPath);
        var jsonBackup = File.ReadAllBytes(SettingsPath + ".bak");
        var keys = File.ReadAllBytes(KeysPath);
        var keysBackup = File.ReadAllBytes(KeysPath + ".bak");
        settings.Psk = "fixture-new";
        settings.AdditionalHostKeys[0].Psk = "fixture-new-lab";
        settings.Theme = "Dark";

        using (var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Assert.Throws<HostSettingsCommitException>(() => settings.Save(SettingsPath));
            Assert.Equal(HostSettingsCommitOutcome.NotCommitted, error.Outcome);
        }

        Assert.Equal(json, File.ReadAllBytes(SettingsPath));
        Assert.Equal(jsonBackup, File.ReadAllBytes(SettingsPath + ".bak"));
        Assert.Equal(keys, File.ReadAllBytes(KeysPath));
        Assert.Equal(keysBackup, File.ReadAllBytes(KeysPath + ".bak"));
        Assert.Equal("fixture-middle", HostSettings.Load(SettingsPath).Psk);
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp-*"));

        settings.Save(SettingsPath);
        var loaded = HostSettings.Load(SettingsPath);
        Assert.Equal("fixture-new", loaded.Psk);
        Assert.Equal("fixture-new-lab", Assert.Single(loaded.AdditionalHostKeys).Psk);
        Assert.Equal("Dark", loaded.Theme);
        File.WriteAllText(SettingsPath, "{broken");
        Assert.Equal("fixture-middle", HostSettings.Load(SettingsPath).Psk);
    }

    [Fact]
    public void Failed_first_settings_write_removes_new_key_files()
    {
        // A directory at the primary's exact path refuses publication after keys were written.
        Directory.CreateDirectory(SettingsPath);
        var error = Assert.Throws<HostSettingsCommitException>(() => Settings().Save(SettingsPath));
        Assert.Equal(HostSettingsCommitOutcome.NotCommitted, error.Outcome);
        Assert.False(File.Exists(KeysPath));
        Assert.False(File.Exists(KeysPath + ".bak"));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp-*"));
    }

    [Fact]
    public void Failure_to_seed_backup_after_first_publication_reports_committed_and_keeps_roster_memory()
    {
        Directory.CreateDirectory(SettingsPath + ".bak");
        var settings = Settings();
        var live = new ClientInfo { Address = "192.0.2.10", ClientId = "fixture" };
        var roster = new HostRosterPersistence(settings, value => value.Save(SettingsPath));

        var error = Assert.Throws<HostSettingsCommitException>(() => roster.Save([live]));

        Assert.Equal(HostSettingsCommitOutcome.Committed, error.Outcome);
        Assert.Equal(live.Address, Assert.Single(settings.Computers).Address);
        Assert.Equal("fixture-old", HostSettings.Load(SettingsPath).Psk);
        Assert.Equal(live.Address, Assert.Single(HostSettings.Load(SettingsPath).Computers).Address);
    }

    [Fact]
    public void Failed_key_publication_never_starts_the_settings_write()
    {
        File.WriteAllText(SettingsPath, "previous");
        var calls = new List<string>();
        var error = Assert.Throws<HostSettingsCommitException>(() => HostSettingsCommit.Write(SettingsPath, "next", [1], (path, _) =>
        {
            calls.Add(path);
            throw new IOException();
        }));
        Assert.Equal(HostSettingsCommitOutcome.NotCommitted, error.Outcome);
        Assert.Equal([KeysPath], calls);
        Assert.Equal("previous", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Rollback_failure_reports_recovery_required_and_retains_both_errors()
    {
        File.WriteAllText(SettingsPath, "previous");
        File.WriteAllBytes(KeysPath, [1]);
        FileStream? locked = null;
        try
        {
            var error = Assert.Throws<HostSettingsCommitException>(() => HostSettingsCommit.Write(SettingsPath, "next", [2], (path, bytes) =>
            {
                if (path == KeysPath) { File.WriteAllBytes(path, bytes); return; }
                locked = new FileStream(KeysPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                throw new IOException("fixture-settings-write");
            }));
            Assert.Equal(HostSettingsCommitOutcome.RecoveryRequired, error.Outcome);
            Assert.Equal(2, Assert.IsType<AggregateException>(error.InnerException).InnerExceptions.Count);
            Assert.Equal("previous", File.ReadAllText(SettingsPath));
        }
        finally { locked?.Dispose(); }
    }

    [Fact]
    public void A_post_write_error_with_a_new_primary_is_reported_as_committed()
    {
        File.WriteAllText(SettingsPath, "previous");
        var error = Assert.Throws<HostSettingsCommitException>(() => HostSettingsCommit.Write(SettingsPath, "next", [2], (path, bytes) =>
        {
            File.WriteAllBytes(path, bytes);
            if (path == SettingsPath) throw new IOException();
        }));
        Assert.Equal(HostSettingsCommitOutcome.Committed, error.Outcome);
        Assert.Equal("next", File.ReadAllText(SettingsPath));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(KeysPath));
    }

    [Fact]
    public void An_identical_primary_can_rollback_keys_without_claiming_a_new_settings_commit()
    {
        File.WriteAllText(SettingsPath, "same");
        File.WriteAllBytes(KeysPath, [1]);
        var error = Assert.Throws<HostSettingsCommitException>(() => HostSettingsCommit.Write(SettingsPath, "same", [2], (path, bytes) =>
        {
            if (path == SettingsPath) throw new IOException();
            File.WriteAllBytes(path, bytes);
        }));
        Assert.Equal(HostSettingsCommitOutcome.NotCommitted, error.Outcome);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(KeysPath));
    }

    [Fact]
    public async Task Canonical_path_aliases_share_a_gate_and_nested_same_thread_use_is_reentrant()
    {
        using var attempted = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task contender;
        // Do not await while owning a thread-affine mutex.
        using (HostSettingsCommit.Enter(SettingsPath))
        {
            using var nested = HostSettingsCommit.Enter(Path.Combine(_folder, ".", "host.json"));
            contender = Task.Run(() =>
            {
                attempted.Set();
                using var lease = HostSettingsCommit.Enter(SettingsPath.ToUpperInvariant());
                entered.SetResult();
            });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(entered.Task.IsCompleted);
        }
        await contender.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(entered.Task.IsCompleted);
    }
}
