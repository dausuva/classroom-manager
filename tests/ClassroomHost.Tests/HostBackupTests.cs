// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>One test here switches saving off for the whole process, so this class never runs beside another.</summary>
[CollectionDefinition(nameof(HostBackupTests), DisableParallelization = true)]
public sealed class HostBackupTestsCollection;

[Collection(nameof(HostBackupTests))]
public sealed class HostBackupTests : IDisposable
{
    readonly string _folder = Directory.CreateTempSubdirectory("cm-backups-").FullName;
    string Settings => Path.Combine(_folder, "host.json");
    string Keys => Path.Combine(_folder, "host.keys");

    public void Dispose()
    {
        HostSettings.SavesSuspended = false;
        Directory.Delete(_folder, recursive: true);
    }

    static HostSettings Lab(int computers = 3) => new()
    {
        Psk = "LAB11-LAB22-LAB33-LAB44-LAB55", DefaultKeyName = "410 Klase",
        AdditionalHostKeys = [new NamedHostKey { Name = "Home Lab", Psk = "HOME1-HOME2-HOME3-HOME4-HOME5" }],
        Computers = Enumerable.Range(1, computers).Select(n => new SavedComputer { Address = $"10.0.0.{n}", Name = $"PC-{n:00}", Room = n % 2 == 0 ? "411" : "410" }).ToList(),
        Schedules = [new ScheduledAction { Name = "Morning unlock" }],
        ExamModeDomains = ["games.example.net"], RunSchedules = true,
    };

    [Fact]
    public void A_generation_holds_settings_and_keys_and_an_unchanged_host_makes_no_second_one()
    {
        Lab().Save(Settings);
        var t = new DateTime(2026, 10, 14, 7, 30, 0, DateTimeKind.Utc);

        var folder = HostBackups.Snapshot(Settings, HostBackups.Start, t);

        Assert.NotNull(folder);
        Assert.Equal(File.ReadAllBytes(Settings), File.ReadAllBytes(Path.Combine(folder!, "host.json")));
        Assert.Equal(File.ReadAllBytes(Keys), File.ReadAllBytes(Path.Combine(folder, "host.keys")));
        Assert.Null(HostBackups.Snapshot(Settings, HostBackups.Start, t.AddHours(1)));

        var backup = Assert.Single(HostBackups.List(Settings));
        Assert.Equal((t, HostBackups.Start, 3, true), (backup.TakenUtc, backup.Reason, backup.Computers, backup.HasKeyFile));
        Assert.Equal(["410", "411"], backup.Rooms);
        Assert.Equal(["410 Klase", "Home Lab"], backup.KeyNames);
        Assert.Contains("3 computer(s) in 410, 411, keys: 410 Klase, Home Lab", backup.Label);
        // The keys themselves are in no label and in no readable file.
        Assert.DoesNotContain("LAB11", File.ReadAllText(Path.Combine(folder, "host.json")));
    }

    [Fact]
    public void Nothing_is_copied_when_there_are_no_settings_yet()
    {
        Assert.Null(HostBackups.Snapshot(Settings, HostBackups.Start));
        Assert.Empty(HostBackups.List(Settings));
    }

    [Fact]
    public void Saving_empty_settings_over_a_host_with_computers_keeps_what_was_there()
    {
        Lab().Save(Settings);

        // What happened on 2026-10-13: something saved a brand-new, empty settings object over the real one.
        new HostSettings().Save(Settings);

        var backup = Assert.Single(HostBackups.List(Settings));
        Assert.Equal(HostBackups.BeforeEmptying, backup.Reason);
        Assert.Equal(3, backup.Computers);

        HostBackups.Restore(Settings, backup);
        var back = HostSettings.Load(Settings);
        Assert.Equal(3, back.Computers.Count);
        Assert.Equal("LAB11-LAB22-LAB33-LAB44-LAB55", back.Psk);
        Assert.Equal("HOME1-HOME2-HOME3-HOME4-HOME5", Assert.Single(back.AdditionalHostKeys).Psk);
    }

    [Fact]
    public void Ordinary_saves_make_no_generation()
    {
        var lab = Lab();
        lab.Save(Settings);
        lab.Computers.RemoveAt(0);
        lab.Save(Settings);
        // Removing every computer while the keys stay is something a person does.
        lab.Computers.Clear();
        lab.Save(Settings);
        // And a first, empty Host has nothing to lose.
        new HostSettings().Save(Path.Combine(_folder, "fresh", "host.json"));

        Assert.Empty(HostBackups.List(Settings));
        Assert.Empty(HostBackups.List(Path.Combine(_folder, "fresh", "host.json")));
    }

    [Fact]
    public void Only_the_newest_generations_are_kept_and_restoring_keeps_the_present_too()
    {
        var lab = Lab();
        var start = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        for (int day = 0; day < HostBackups.Keep + 5; day++)
        {
            lab.QuickActionMessage = $"Day {day}";
            lab.Save(Settings);
            Assert.NotNull(HostBackups.Snapshot(Settings, HostBackups.Start, start.AddDays(day)));
        }

        var backups = HostBackups.List(Settings);
        Assert.Equal(HostBackups.Keep, backups.Count);
        Assert.Equal(start.AddDays(HostBackups.Keep + 4), backups[0].TakenUtc);
        Assert.Equal(start.AddDays(5), backups[^1].TakenUtc);

        lab.QuickActionMessage = "Today";
        lab.Save(Settings);
        HostBackups.Restore(Settings, backups[3]);

        Assert.Equal($"Day {HostBackups.Keep + 1}", HostSettings.Load(Settings).QuickActionMessage);
        var present = HostBackups.List(Settings).First(backup => backup.Reason == HostBackups.BeforeRestore);
        Assert.Contains("Today", File.ReadAllText(Path.Combine(present.Folder, "host.json")));
    }

    [Fact]
    public void A_configuration_file_carries_everything_and_opens_only_with_its_password()
    {
        var text = HostConfigurationFile.Write(Lab(), "correct horse battery", "1.2.0.300");

        Assert.True(HostConfigurationFile.IsConfiguration(text));
        Assert.False(HostConfigurationFile.IsConfiguration(TeacherSettingsFile.Write(new TeacherSettings { Key = "x" }, null)));
        foreach (var secretOrName in new[] { "LAB11", "HOME1", "PC-01", "410 Klase", "games.example.net" })
            Assert.DoesNotContain(secretOrName, text);

        var (settings, info) = HostConfigurationFile.Read(text, "correct horse battery");
        Assert.Equal("LAB11-LAB22-LAB33-LAB44-LAB55", settings.Psk);
        Assert.Equal("HOME1-HOME2-HOME3-HOME4-HOME5", Assert.Single(settings.AdditionalHostKeys).Psk);
        Assert.Equal(3, settings.Computers.Count);
        Assert.Equal(["games.example.net"], settings.ExamModeDomains);
        Assert.Equal((3, 1, "1.2.0.300"), (info.Computers, info.Schedules, info.HostVersion));
        Assert.Equal(["410 Klase", "Home Lab"], info.KeyNames);
        Assert.Contains("Computers: 3 in 410, 411", info.Describe());
        Assert.DoesNotContain("LAB11", info.Describe());

        Assert.Contains("password", Assert.Throws<InvalidDataException>(() => HostConfigurationFile.Read(text, "wrong password")).Message);
        Assert.Throws<InvalidDataException>(() => HostConfigurationFile.Read("{\"format\":\"something\"}", "correct horse battery"));
        Assert.Throws<InvalidDataException>(() => HostConfigurationFile.Read("not json", "correct horse battery"));
        // The file holds the keys to every computer, so a short password is refused.
        Assert.Throws<ArgumentException>(() => HostConfigurationFile.Write(Lab(), "short", "1.2.0.300"));
        Assert.False(HostConfigurationFile.PasswordIsAcceptable("1234567", out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Importing_replaces_the_settings_and_keeps_the_old_ones_as_a_generation()
    {
        var mine = new HostSettings { Psk = "MINE1-MINE2-MINE3-MINE4-MINE5", Computers = [new SavedComputer { Address = "192.168.1.5", Name = "OLD-PC", Room = "Old" }] };
        mine.Save(Settings);
        var (imported, _) = HostConfigurationFile.Read(HostConfigurationFile.Write(Lab(), "correct horse battery", "1.2.0.300"), "correct horse battery");
        var originalId = imported.HostId;

        HostConfigurationFile.Install(Settings, imported, asSecondHost: false);

        var now = HostSettings.Load(Settings);
        Assert.Equal(3, now.Computers.Count);
        Assert.Equal("LAB11-LAB22-LAB33-LAB44-LAB55", now.Psk);
        Assert.True(now.RunSchedules);
        Assert.Equal(originalId, imported.HostId);
        var before = Assert.Single(HostBackups.List(Settings));
        Assert.Equal((HostBackups.BeforeImport, 1), (before.Reason, before.Computers));
    }

    [Fact]
    public void A_second_host_gets_its_own_identity_and_runs_no_schedules()
    {
        var source = Lab();
        source.HostId = "aaaaaaaa";
        source.UploadToken = new string('b', 32);
        source.ExamModeEnabled = true;
        source.ExamModeComputers = ["10.0.0.1"];
        var (imported, _) = HostConfigurationFile.Read(HostConfigurationFile.Write(source, "correct horse battery", "1.2.0.300"), "correct horse battery");
        Assert.Equal(("aaaaaaaa", new string('b', 32)), (imported.HostId, imported.UploadToken));

        HostConfigurationFile.Install(Settings, imported, asSecondHost: true);

        var second = HostSettings.Load(Settings);
        Assert.NotEqual("aaaaaaaa", second.HostId);
        Assert.NotEqual(new string('b', 32), second.UploadToken);
        Assert.Equal(32, second.UploadToken.Length);
        Assert.False(second.RunSchedules);
        Assert.False(second.ExamModeEnabled);
        Assert.Empty(second.ExamModeComputers);
        Assert.Equal(3, second.Computers.Count);
    }

    [Fact]
    public void A_program_that_only_reads_the_settings_never_writes_them()
    {
        // Settings without a Host id (an old or hand-made file): loading them normally would add one and save.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Settings, "{\"computers\":[{\"address\":\"10.0.0.1\",\"name\":\"PC-01\"}],\"hostId\":\"\"}");
        var before = File.ReadAllBytes(Settings);

        HostSettings.SavesSuspended = true;      // what Classroom Health does when it starts
        var loaded = HostSettings.Load(Settings);

        Assert.Equal("PC-01", Assert.Single(loaded.Computers).Name);
        Assert.Equal(before, File.ReadAllBytes(Settings));
        Assert.Equal([Path.GetFileName(Settings)], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void Once_the_files_were_replaced_the_settings_still_in_memory_are_not_written_back()
    {
        Lab().Save(Settings);
        var written = File.GetLastWriteTimeUtc(Settings);
        var content = File.ReadAllText(Settings);

        HostSettings.SavesSuspended = true;
        new HostSettings { QuickActionMessage = "stale" }.Save(Settings);

        Assert.Equal(content, File.ReadAllText(Settings));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Settings));
    }

    [Fact]
    public void Writing_over_settings_another_program_changed_keeps_a_copy_and_names_the_writer()
    {
        var mine = Lab(2);
        mine.Save(Settings);
        mine.Save(Settings);
        Assert.Empty(HostBackups.List(Settings));

        // Another program (the window of a second Host, an import) saves its own settings meanwhile.
        var theirs = Lab(5);
        File.WriteAllText(Settings, System.Text.Json.JsonSerializer.Serialize(theirs, ClassroomManager.Shared.Protocol.Json));
        File.SetLastWriteTimeUtc(Settings, DateTime.UtcNow.AddSeconds(5));

        mine.Save(Settings);

        var copy = Assert.Single(HostBackups.List(Settings));
        Assert.Equal((HostBackups.BeforeOverwrite, 5), (copy.Reason, copy.Computers));
        var journal = File.ReadAllText(Path.Combine(_folder, "settings-writes.log"));
        Assert.Contains("5 computer(s) before", journal);
        Assert.Contains("now: 2 computer(s)", journal);
    }
}
