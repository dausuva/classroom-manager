// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using Microsoft.Win32;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>Install on this computer: when it is offered, and that it installs and removes only what it made. Scratch folders and a scratch registry key; nothing of the real user's Start menu or Apps list.</summary>
public sealed class HostSelfInstallTests : IDisposable
{
    readonly string _root = Path.Combine(TestDataFolder.Scratch, "self-install-" + Guid.NewGuid().ToString("N"));
    readonly string _registryKey = @"Software\ClassroomManagerTests\self-install-" + Guid.NewGuid().ToString("N");

    public HostSelfInstallTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
        Registry.CurrentUser.DeleteSubKeyTree(_registryKey, throwOnMissingSubKey: false);
    }

    SelfInstallPaths Paths => new(Path.Combine(_root, "Programs", "Classroom Manager"), Path.Combine(_root, "StartMenu"), _registryKey);

    string ProgramIn(string folderName, params string[] alsoBeside)
    {
        var folder = Path.Combine(_root, folderName);
        Directory.CreateDirectory(folder);
        foreach (var name in alsoBeside) File.WriteAllText(Path.Combine(folder, name), "x");
        var program = Path.Combine(folder, HostSelfInstall.ExeName);
        File.WriteAllText(program, "program");
        return program;
    }

    string[] ProgramFiles => [Path.Combine(_root, "Program Files")];

    [Fact]
    public void A_single_file_program_in_an_ordinary_folder_is_offered_the_install()
        => Assert.Equal(SelfInstallState.Offered, HostSelfInstall.Detect(ProgramIn("Downloads"), false, Paths, ProgramFiles));

    [Fact]
    public void A_packaged_program_is_never_offered_the_install_because_Windows_installs_it()
        => Assert.Equal(SelfInstallState.Packaged, HostSelfInstall.Detect(ProgramIn("Downloads"), true, Paths, ProgramFiles));

    [Fact]
    public void A_program_that_runs_from_the_install_folder_or_from_Program_Files_is_already_installed()
    {
        Assert.Equal(SelfInstallState.AlreadyInstalled, HostSelfInstall.Detect(ProgramIn(Path.Combine("Programs", "Classroom Manager")), false, Paths, ProgramFiles));
        Assert.Equal(SelfInstallState.AlreadyInstalled, HostSelfInstall.Detect(ProgramIn(Path.Combine("Program Files", "ClassroomHost")), false, Paths, ProgramFiles));
    }

    [Fact]
    public void A_folder_whose_name_only_starts_like_the_install_folder_is_not_taken_for_it()
        => Assert.Equal(SelfInstallState.Offered, HostSelfInstall.Detect(ProgramIn(Path.Combine("Programs", "Classroom Manager old")), false, Paths, ProgramFiles));

    [Fact]
    public void A_portable_marker_a_winget_folder_and_a_program_with_other_files_beside_it_are_not_offered()
    {
        Assert.Equal(SelfInstallState.PortableMarker, HostSelfInstall.Detect(ProgramIn("Portable", HostDataLocation.PortableMarker), false, Paths, ProgramFiles));
        Assert.Equal(SelfInstallState.ManagedByWinget, HostSelfInstall.Detect(ProgramIn(Path.Combine("Microsoft", "WinGet", "Packages", "Dausuva")), false, Paths, ProgramFiles));
        Assert.Equal(SelfInstallState.MoreThanOneFile, HostSelfInstall.Detect(ProgramIn("Build", "ClassroomHost.dll"), false, Paths, ProgramFiles));
    }

    [Fact]
    public void Install_copies_the_program_and_adds_a_shortcut_and_an_Apps_entry_and_Uninstall_removes_the_shortcut_and_the_entry()
    {
        var paths = Paths;
        var source = ProgramIn("Downloads");
        HostSelfInstall.Install(source, "1.2.0", paths);

        Assert.Equal("program", File.ReadAllText(paths.ProgramPath));
        Assert.True(File.Exists(paths.ShortcutPath));
        using (var key = Registry.CurrentUser.OpenSubKey(_registryKey))
        {
            Assert.NotNull(key);
            Assert.Equal("1.2.0", key!.GetValue("DisplayVersion"));
            Assert.Equal($"\"{paths.ProgramPath}\" {HostSelfInstall.UninstallSwitch}", key.GetValue("UninstallString"));
        }

        HostSelfInstall.Install(source, "1.2.1", paths);   // again: replaces, does not fail
        HostSelfInstall.Uninstall(paths);

        Assert.False(File.Exists(paths.ShortcutPath));
        Assert.Null(Registry.CurrentUser.OpenSubKey(_registryKey));
        Assert.True(File.Exists(source), "The downloaded program is not the Host's to delete.");
    }

    [Fact]
    public void After_the_install_the_same_program_is_no_longer_offered_from_the_install_folder()
    {
        var paths = Paths;
        HostSelfInstall.Install(ProgramIn("Downloads"), "1.2.0", paths);
        Assert.Equal(SelfInstallState.AlreadyInstalled, HostSelfInstall.Detect(paths.ProgramPath, false, paths, ProgramFiles));
    }
}
