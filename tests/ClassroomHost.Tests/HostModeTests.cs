// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class HostModeTests
{
    [Fact]
    public void Teacher_defaults_hide_complex_remote_tools_and_keep_classroom_actions()
    {
        var defaults = TeacherCommandCatalog.Defaults();

        Assert.Contains("classroom.lock", defaults);
        Assert.Contains("restrictions.exam", defaults);
        Assert.Contains("restrictions.exam-settings", defaults);
        Assert.Contains("settings.interface", defaults);
        Assert.Contains("classroom.transfer-history", defaults);
        Assert.DoesNotContain(TeacherCommandCatalog.All, command => command.Group == "Quick actions");
        Assert.DoesNotContain("remote.terminal", defaults);
        Assert.DoesNotContain("remote.windows-update", defaults);
        Assert.DoesNotContain("remote.install", defaults);
        Assert.DoesNotContain("remote.staged-uploads", defaults);
    }

    [Fact]
    public void Normalize_removes_unknown_and_duplicate_command_ids()
    {
        var values = TeacherCommandCatalog.Normalize(["remote.terminal", "unknown", "REMOTE.TERMINAL", "classroom.lock"]);

        Assert.Equal(["remote.terminal", "classroom.lock"], values);
    }

    [Fact]
    public void Command_groups_follow_task_oriented_navigation()
    {
        var groups = TeacherCommandCatalog.All.Select(command => command.Group).Distinct().ToList();

        Assert.Contains("Classroom", groups);
        Assert.Contains("Files", groups);
        Assert.Contains("Software", groups);
        Assert.Contains("Restrictions", groups);
        Assert.Contains("Reports", groups);
        Assert.DoesNotContain("Files and classroom", groups);
        Assert.DoesNotContain("Restrictions and reporting", groups);
        Assert.DoesNotContain("Software and remote tools", groups);
    }

    /// <summary>T-079: Power... is shown to teachers by default, also in settings saved before, but only once.</summary>
    [Fact]
    public void Power_reaches_existing_settings_once_and_stays_hidden_when_the_teacher_hides_it()
    {
        Assert.Contains("remote.power", TeacherCommandCatalog.Defaults());
        var folder = System.IO.Directory.CreateTempSubdirectory("cm-teacher-defaults-").FullName;
        try
        {
            var path = System.IO.Path.Combine(folder, "host.json");
            System.IO.File.WriteAllText(path, """{"teacherVisibleCommands":["computers.scan"]}""");
            var old = HostSettings.Load(path);
            Assert.Equal(["computers.scan", "remote.power", "classroom.appearance-settings"], old.TeacherVisibleCommands);

            old.TeacherVisibleCommands.Remove("remote.power");
            old.Save(path);
            Assert.DoesNotContain("remote.power", HostSettings.Load(path).TeacherVisibleCommands);
        }
        finally { System.IO.Directory.Delete(folder, recursive: true); }
    }
}
