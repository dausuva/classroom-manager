// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

// T-222: roster snapshot ownership, save failures and the existing atomic file boundary.
[Collection(nameof(HostBackupTests))]
public sealed class HostRosterPersistenceTests
{
    static ClientInfo Computer() => new()
    {
        Address = "192.0.2.10", ComputerName = "PC-10", Alias = "Teacher label", ClientId = "identity",
        Mac = "aa:bb:cc:dd:ee:ff", Port = 4100, Room = "Room 1", KeyId = "fingerprint",
        Status = "Online", IsSelected = true, BusyCount = 2,
    };

    [Fact]
    public void Successful_save_keeps_all_persistent_fields_in_a_detached_snapshot()
    {
        var settings = new HostSettings();
        var live = Computer();
        var calls = 0;
        new HostRosterPersistence(settings, value =>
        {
            Assert.Same(settings, value);
            Assert.Equal("Teacher label", Assert.Single(value.Computers).Alias);
            calls++;
        }).Save([live]);

        var saved = Assert.Single(settings.Computers);
        Assert.Equal(("192.0.2.10", "PC-10", "Teacher label", "identity", "AABBCCDDEEFF", 4100, "Room 1", "fingerprint"),
            (saved.Address, saved.Name, saved.Alias, saved.ClientId, saved.Mac, saved.Port, saved.Room, saved.KeyId));
        live.Address = "192.0.2.20";
        live.Alias = "Changed";
        Assert.Equal("192.0.2.10", saved.Address);
        Assert.Equal("Teacher label", saved.Alias);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Failure_restores_the_previous_snapshot_and_leaves_live_edits_available_for_retry()
    {
        var settings = new HostSettings { Computers = [new SavedComputer { Address = "previous" }] };
        var previous = settings.Computers;
        var live = Computer();
        var fail = true;
        var writer = new HostRosterPersistence(settings, _ => { if (fail) throw new IOException(); });

        Assert.Throws<IOException>(() => writer.Save([live]));
        Assert.Same(previous, settings.Computers);
        Assert.Equal("192.0.2.10", live.Address);
        fail = false;
        writer.Save([live]);
        Assert.NotSame(previous, settings.Computers);
        Assert.Equal(live.Address, Assert.Single(settings.Computers).Address);
    }

    [Fact]
    public void Enumeration_failure_does_not_publish_a_partial_roster_or_start_a_save()
    {
        var settings = new HostSettings();
        var previous = settings.Computers;
        var saved = false;
        IEnumerable<ClientInfo> Broken()
        {
            yield return Computer();
            throw new InvalidOperationException();
        }

        Assert.Throws<InvalidOperationException>(() =>
            new HostRosterPersistence(settings, _ => saved = true).Save(Broken()));
        Assert.Same(previous, settings.Computers);
        Assert.False(saved);
    }

    [Fact]
    public void Suspended_saves_do_not_enumerate_or_replace_imported_state()
    {
        var settings = new HostSettings();
        var previous = settings.Computers;
        IEnumerable<ClientInfo> Unexpected()
        {
            yield return Computer();
            throw new InvalidOperationException();
        }
        HostSettings.SavesSuspended = true;
        try
        {
            new HostRosterPersistence(settings, _ => Assert.Fail("Saving was suspended")).Save(Unexpected());
            Assert.Same(previous, settings.Computers);
        }
        finally { HostSettings.SavesSuspended = false; }
    }

    [Fact]
    public void Empty_roster_is_forwarded_to_the_existing_store_without_filtering()
    {
        var settings = new HostSettings { Computers = [new SavedComputer { Address = "previous" }] };
        var calls = 0;
        new HostRosterPersistence(settings, value => { Assert.Empty(value.Computers); calls++; }).Save([]);
        Assert.Empty(settings.Computers);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Real_store_round_trip_preserves_identity_and_a_locked_primary_survives_failed_replacement()
    {
        var folder = Directory.CreateTempSubdirectory("cm-roster-").FullName;
        try
        {
            var path = Path.Combine(folder, "host.json");
            var settings = new HostSettings { HostId = "fixture", UploadToken = new string('a', 32) };
            var writer = new HostRosterPersistence(settings, value => value.Save(path));
            var live = Computer();
            writer.Save([live]);
            var before = File.ReadAllBytes(path);
            var previous = settings.Computers;
            live.Address = "192.0.2.20";
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.ThrowsAny<IOException>(() => writer.Save([live]));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Same(previous, settings.Computers);
            Assert.Empty(Directory.GetFiles(folder, "*.tmp-*"));

            writer.Save([live]);
            var loaded = HostSettings.Load(path);
            var saved = Assert.Single(loaded.Computers);
            Assert.Equal(("192.0.2.20", "PC-10", "Teacher label", "identity", "AABBCCDDEEFF", 4100, "Room 1", "fingerprint"),
                (saved.Address, saved.Name, saved.Alias, saved.ClientId, saved.Mac, saved.Port, saved.Room, saved.KeyId));
            Assert.Equal("fixture", loaded.HostId);
            Assert.Equal(new string('a', 32), loaded.UploadToken);
            Assert.Equal(before, File.ReadAllBytes(path + ".bak"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
