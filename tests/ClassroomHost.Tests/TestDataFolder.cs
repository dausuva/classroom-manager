// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Runtime.CompilerServices;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// Runs before any test in this assembly: sends everything the Host would keep in the Windows
/// user's own settings folder to a scratch folder instead.
///
/// Without it, a test that builds a Host window with empty settings and lets that window save
/// writes those empty settings over the real ones of whoever runs the tests - computers, rooms and
/// lab keys gone. That happened on 2026-10-13; this file and the test below are what stops it.
/// </summary>
static class TestDataFolder
{
    public static readonly string Real = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassroomManager");

    public static readonly string Scratch = Path.Combine(Path.GetTempPath(), "ClassroomManager.Tests", Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Redirect()
    {
        Directory.CreateDirectory(Scratch);
        HostSettings.DataFolder = Scratch;
        // The PC-wide licence folder (T-262) is guarded the same way.
        HostLicence.SharedFolder = Path.Combine(Scratch, "shared");
#if WITH_DESK
        // Classroom Desk's schools, licences, invoices and licence key (T-213) are guarded the same way.
        ClassroomManager.Desk.DeskStore.DataFolder = Path.Combine(Scratch, "desk");
#endif
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Scratch, recursive: true); } catch { /* left for the temp folder clean-up */ }
        };
    }
}

public class TestDataFolderTests
{
    [Fact]
    public void Tests_never_use_the_real_settings_folder_of_the_person_running_them()
    {
        Assert.Equal(TestDataFolder.Scratch, HostSettings.DataFolder);
        foreach (var path in new[] { HostSettings.PathOnDisk, ActivityLogFile.DefaultDirectory, CrashLog.Path, HostLicence.SharedFilePath })
            Assert.False(Path.GetFullPath(path).StartsWith(TestDataFolder.Real, StringComparison.OrdinalIgnoreCase), path);

        // A save with no path given - what a Host window does - lands in the scratch folder.
        new HostSettings().Save();
        Assert.True(File.Exists(Path.Combine(TestDataFolder.Scratch, "host.json")));
    }

#if WITH_DESK
    [Fact]
    public void Tests_never_use_the_real_data_of_Classroom_Desk()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dausuva", "ClassroomDesk");
        Assert.StartsWith(TestDataFolder.Scratch, ClassroomManager.Desk.DeskStore.DataFolder, StringComparison.OrdinalIgnoreCase);
        foreach (var path in new[] { ClassroomManager.Desk.DeskStore.DataFile, ClassroomManager.Desk.DeskStore.BackupFolder, ClassroomManager.Desk.DeskStore.ProfilesFolder })
            Assert.False(Path.GetFullPath(path).StartsWith(real, StringComparison.OrdinalIgnoreCase), path);
    }
#endif
}
