// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ActivityLogFileTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "classroom-log-test-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Unavailable_secret_provider_cannot_write_raw_text_or_interrupt_the_host()
    {
        var log = new ActivityLogFile(_folder, () => throw new InvalidOperationException("fixture"));
        var at = new DateTime(2026, 10, 30);
        log.Write(at, "synthetic-secret-provider-failure");
        Assert.False(File.Exists(Path.Combine(_folder, ActivityLogFile.FileName(at))));
    }

    [Fact]
    public void Known_current_and_changed_credentials_are_removed_before_persistence()
    {
        var secrets = new List<string> { "synthetic-first-secret-39ef" };
        var log = new ActivityLogFile(_folder, () => secrets);
        var at = new DateTime(2026, 10, 30, 12, 0, 0);
        log.Write(at, "010002 " + secrets[0]);
        secrets.Add("synthetic-second-secret-519d");
        log.Write(at, secrets[1]);
        var text = File.ReadAllText(Path.Combine(_folder, ActivityLogFile.FileName(at)));
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, text));
        Assert.Contains("010002", text);
    }

    [Fact]
    public void Every_line_goes_into_the_file_of_its_day_with_the_full_date_and_time()
    {
        var log = new ActivityLogFile(_folder);

        log.Write(new DateTime(2026, 10, 13, 16, 5, 9), "Lock: 20 computer(s), all at once...");
        log.Write(new DateTime(2026, 10, 13, 16, 5, 10), "  OK    410-1");
        log.Write(new DateTime(2026, 10, 14, 8, 0, 0), "Scanning...\nsecond line");

        Assert.Equal(["host-2026-10-13.log", "host-2026-10-14.log"],
            Directory.GetFiles(_folder).Select(Path.GetFileName).OrderBy(name => name));
        Assert.Equal(["2026-10-13 16:05:09  Lock: 20 computer(s), all at once...", "2026-10-13 16:05:10    OK    410-1"],
            File.ReadAllLines(Path.Combine(_folder, "host-2026-10-13.log")));
        // One entry stays one line, so the file can be searched and counted line by line.
        Assert.Equal(["2026-10-14 08:00:00  Scanning... second line"], File.ReadAllLines(Path.Combine(_folder, "host-2026-10-14.log")));
    }

    [Fact]
    public void Files_past_the_retention_period_are_removed_and_nothing_else_is_touched()
    {
        var log = new ActivityLogFile(_folder);
        var today = new DateTime(2026, 10, 13);
        log.Write(today.AddDays(-400), "old");
        log.Write(today.AddDays(-ActivityLogFile.KeepDays), "last day that is kept");
        log.Write(today, "today");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not a log file");
        File.WriteAllText(Path.Combine(_folder, "host-export.log"), "not a daily file");

        Assert.Equal(1, log.RemoveOld(today));

        Assert.Equal(4, Directory.GetFiles(_folder).Length);
        Assert.False(File.Exists(Path.Combine(_folder, ActivityLogFile.FileName(today.AddDays(-400)))));
    }

    [Fact]
    public void A_folder_that_cannot_be_written_does_not_stop_the_Host()
    {
        var blocked = Path.Combine(_folder, "file-in-the-way");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(blocked, "");

        var log = new ActivityLogFile(blocked);   // a file where the folder should be
        log.Write(DateTime.Now, "ignored");

        Assert.Equal(0, log.RemoveOld(DateTime.Now));
    }
}
