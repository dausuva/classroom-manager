// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ProgramLaunchTests
{
    [Theory]
    [InlineData("C:\\Program Files\\Mozilla Firefox\\firefox.exe,0", "C:\\Program Files\\Mozilla Firefox\\firefox.exe")]
    [InlineData("\"C:\\Program Files\\VideoLAN\\VLC\\vlc.exe\"", "C:\\Program Files\\VideoLAN\\VLC\\vlc.exe")]
    [InlineData("C:\\Program Files\\Notepad++\\notepad++.exe", "C:\\Program Files\\Notepad++\\notepad++.exe")]
    [InlineData("C:\\Program Files (x86)\\Tool\\unins000.exe", "")]
    [InlineData("C:\\Windows\\Installer\\{1234}\\setup.exe,0", "")]
    [InlineData("C:\\Program Files\\App\\Uninstall.exe", "")]
    [InlineData("C:\\Program Files\\App\\app.ico", "")]
    [InlineData("", "")]
    public void The_program_comes_from_the_icon_path_unless_that_is_an_installer(string displayIcon, string expected)
    {
        Assert.Equal(expected, ProgramLaunch.Executable(new ProgramEntry { DisplayIcon = displayIcon }));
    }

    [Fact]
    public void Without_an_executable_the_dialog_starts_in_the_install_folder()
    {
        Assert.Equal("C:\\Tools\\Paint\\", ProgramLaunch.Suggestion([
            new ProgramEntry { DisplayIcon = "C:\\Tools\\Paint\\uninstall.exe", InstallLocation = "C:\\Tools\\Paint" },
        ]));
        Assert.Equal("C:\\Apps\\b.exe", ProgramLaunch.Suggestion([
            new ProgramEntry { InstallLocation = "C:\\Apps\\" },
            new ProgramEntry { DisplayIcon = "C:\\Apps\\b.exe" },
        ]));
        Assert.Equal("", ProgramLaunch.Suggestion([new ProgramEntry()]));
    }

    [Theory]
    [InlineData("firefox.exe", "firefox")]
    [InlineData(" Teams.EXE ", "Teams")]
    [InlineData("notepad", "notepad")]
    public void A_process_name_is_searched_without_its_extension(string process, string expected)
    {
        Assert.Equal(expected, ProgramLaunch.SearchName(process));
    }
}
