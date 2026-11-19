// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.IO;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>The weekly update check and the monthly backup of student work as schedules (T-214).</summary>
public sealed class StudentBackupTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "cm-backup-" + Guid.NewGuid().ToString("N"));
    string Record => Path.Combine(_folder, "student-backups.json");

    public StudentBackupTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    static DateTime At(string text) => DateTime.ParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    [Fact]
    public void A_computer_never_backed_up_starts_from_everything_and_then_from_its_last_backup()
    {
        Assert.Equal(StudentBackup.Never, StudentBackup.LastBackupUtc("pc1", Record));
        var started = new DateTime(2026, 11, 2, 11, 0, 0, DateTimeKind.Utc);
        StudentBackup.RecordBackup("pc1", started, Record);
        Assert.Equal(started, StudentBackup.LastBackupUtc("PC1", Record));
        Assert.Equal(DateTimeKind.Utc, StudentBackup.LastBackupUtc("pc1", Record).Kind);
        Assert.Equal(StudentBackup.Never, StudentBackup.LastBackupUtc("pc2", Record));
    }

    [Fact]
    public void A_damaged_record_means_a_full_backup_not_a_failure()
    {
        File.WriteAllText(Record, "not json");
        Assert.Equal(StudentBackup.Never, StudentBackup.LastBackupUtc("pc1", Record));
    }

    [Fact]
    public void A_backup_takes_every_file_of_the_computers_watched_folders_since_its_last_backup()
    {
        var settings = new RecentFilesSettings();
        var client = new ClientInfo { ClientId = "C1", Address = "10.0.0.1" };
        RecentFilesPlan.SetWatchedFolders(settings, client, "%DOCUMENTS%\nD:\\Projects");
        var since = new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);

        var query = StudentBackup.Query(settings, client, since);
        Assert.Equal(["%DOCUMENTS%", @"D:\Projects"], query.Folders);
        Assert.True(query.AllFiles);
        Assert.False(query.IncludeFolders);
        Assert.Equal(since, query.SinceUtc);
        Assert.Equal(RecentFiles.MaxItemsLimit, query.MaxItems);
        Assert.Null(query.WithinMinutes);
    }

    [Fact]
    public void Backups_go_to_a_backups_folder_beside_the_recent_files_copies() =>
        Assert.Equal(@"E:\Copies\Backups", StudentBackup.Destination(new RecentFilesSettings { Destination = @"E:\Copies" }));

    [Theory]
    [InlineData("2026-11-02 12:00", "2026-11-02 12:00")]   // the first Monday of November, at noon
    [InlineData("2026-11-20 09:00", "2026-11-02 12:00")]   // later in November: still that run
    [InlineData("2026-12-07 13:00", "2026-12-07 12:00")]   // the first Monday of December
    [InlineData("2026-12-07 11:00", "2026-11-02 12:00")]   // that morning, before noon: November's run
    [InlineData("2026-11-09 12:30", "2026-11-02 12:00")]   // the second Monday does not count
    public void A_monthly_schedule_runs_on_the_first_chosen_weekday_of_the_month(string now, string expected) =>
        Assert.Equal(At(expected), ScheduleRuns.LatestOccurrence(new ScheduledAction { Time = "12:00", Days = ["Mon"], Monthly = true }, At(now)));

    [Fact]
    public void The_update_check_and_the_backup_always_catch_up_and_are_offered_as_samples_switched_off()
    {
        Assert.True(ScheduleRuns.CatchesUp(new ScheduledAction { Action = "updatescan" }));
        Assert.True(ScheduleRuns.CatchesUp(new ScheduledAction { Action = "backup" }));
        var check = ScheduleRuns.SampleUpdateCheck();
        var backup = ScheduleRuns.SampleBackup();
        Assert.False(check.Enabled || backup.Enabled);
        Assert.Equal(["Mon"], check.Days);
        Assert.Equal(["Mon"], backup.Days);
        Assert.False(check.Monthly);
        Assert.True(backup.Monthly);
        Assert.True(check.AllComputers && backup.AllComputers);
    }

    [Fact]
    public void Every_install_gets_the_three_samples_once()
    {
        var path = Path.Combine(_folder, "host.json");
        var fresh = HostSettings.Load(path);
        Assert.Equal(["timesync", "updatescan", "backup"], fresh.Schedules.Select(schedule => schedule.Action));
        Assert.Equal(3, fresh.ScheduleDefaultsVersion);

        File.WriteAllText(path, """{ "scheduleDefaultsVersion": 2, "schedules": [ { "name": "Clocks", "action": "timesync" } ] }""");
        Assert.Equal(["timesync", "updatescan", "backup"], HostSettings.Load(path).Schedules.Select(schedule => schedule.Action));
        File.WriteAllText(path, """{ "scheduleDefaultsVersion": 3, "schedules": [] }""");
        Assert.Empty(HostSettings.Load(path).Schedules);
    }

    [Fact]
    public void Both_run_from_the_command_line_and_from_a_schedule()
    {
        Assert.Equal(("updatescan", "410"), (CliParser.Parse(["updatescan", "--room", "410"]).Command, CliParser.Parse(["updatescan", "--room", "410"]).Room));
        Assert.Equal("backup", CliParser.Parse(["backup", "--all"]).Command);
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["backup"]));
        var options = CliParser.Parse(["schedule", "run", "--name", "x"]);
        Assert.Equal("updatescan", CliSchedules.ToCommand(ScheduleRuns.SampleUpdateCheck(), options).Command);
        Assert.Equal("backup", CliSchedules.ToCommand(ScheduleRuns.SampleBackup(), options).Command);
        Assert.Contains("updatescan", CliParser.Usage);
    }

    [Fact]
    public void Computers_can_be_put_to_sleep_from_the_command_line_with_yes()
    {
        var sleep = CliParser.Parse(["power", "sleep", "--computer", "192.168.1.205", "--yes"]);
        Assert.Equal(("power", "sleep"), (sleep.Command, sleep.Action));
        Assert.Contains("sleep", CliParser.Usage);
    }
}
