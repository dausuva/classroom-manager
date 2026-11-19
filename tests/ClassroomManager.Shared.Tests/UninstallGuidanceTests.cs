// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class UninstallGuidanceTests
{
    [Fact]
    public void Msi_is_a_trusted_silent_path()
    {
        var result = UninstallGuidance.Analyze(true, "", "MsiExec.exe /I{123}");

        Assert.True(result.IsSilent);
        Assert.Contains("Windows Installer", result.Source);
        Assert.Empty(result.SuggestedArgs);
    }

    [Fact]
    public void Registry_quiet_command_is_preferred_over_installer_guessing()
    {
        var result = UninstallGuidance.Analyze(false, @"C:\App\unins000.exe /S", @"C:\App\unins000.exe");

        Assert.True(result.IsSilent);
        Assert.Equal("Registry quiet command", result.Source);
        Assert.Empty(result.SuggestedArgs);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Example\\unins000.exe\"")]
    [InlineData("C:\\Example\\unins42.exe /uninstall")]
    public void Inno_uninstaller_name_produces_a_reviewable_candidate(string command)
    {
        var result = UninstallGuidance.Analyze(false, "", command);

        Assert.False(result.IsSilent);
        Assert.Equal("Inno Setup candidate", result.Source);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", result.SuggestedArgs);
    }

    [Fact]
    public void Unknown_executable_does_not_guess_switches()
    {
        var result = UninstallGuidance.Analyze(false, "", @"C:\App\uninstall.exe");

        Assert.False(result.IsSilent);
        Assert.Equal("Not advertised", result.Source);
        Assert.Empty(result.SuggestedArgs);
    }
}
