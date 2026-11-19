// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public class ActivityLogRetentionTests
{
    [Fact]
    public void Retention_is_one_of_five_periods_and_anything_else_means_the_longest()
    {
        Assert.Equal([7, 14, 30, 180, 365], ActivityLogFile.RetentionChoices);
        Assert.All(ActivityLogFile.RetentionChoices, days =>
        {
            Assert.Equal(days, ActivityLogFile.NormalizeKeepDays(days));
            Assert.Equal(days, ActivityLogFile.ParseRetention(ActivityLogFile.RetentionLabel(days)));
        });
        Assert.Equal(365, ActivityLogFile.NormalizeKeepDays(0));
        Assert.Equal(365, ActivityLogFile.NormalizeKeepDays(90));
        Assert.Equal(365, ActivityLogFile.ParseRetention("for ever"));

        var settings = new HostSettings();
        Assert.True(settings.ActivityLogEnabled);
        Assert.Equal(365, settings.ActivityLogKeepDays);
    }

    [Fact]
    public void A_shorter_period_removes_the_files_older_than_it()
    {
        var folder = Directory.CreateTempSubdirectory("cm-log-").FullName;
        try
        {
            var log = new ActivityLogFile(folder);
            var today = new DateTime(2026, 10, 14, 9, 0, 0);
            foreach (var age in new[] { 0, 7, 8, 40 }) log.Write(today.AddDays(-age), $"line from {age} day(s) ago");

            Assert.Equal(2, log.RemoveOld(today, 7));
            Assert.Equal(2, Directory.GetFiles(folder).Length);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Admin_mode_warns_only_when_every_answer_was_a_teacher_key()
    {
        Assert.Contains("No Admin key is set", KeyRoleSummary.AdminModeWarning(["Teacher", "Teacher", ""]));
        Assert.Equal("", KeyRoleSummary.AdminModeWarning(["Teacher", "Admin"]));
        Assert.Equal("", KeyRoleSummary.AdminModeWarning(["", ""]));      // nothing has answered yet
        Assert.Equal("", KeyRoleSummary.AdminModeWarning([]));
    }
}
