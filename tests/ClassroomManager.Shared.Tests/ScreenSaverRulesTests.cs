// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class ScreenSaverRulesTests
{
    [Theory]
    [InlineData("MatrixGeometry.scr")]
    [InlineData("Matrix Geometry (v2).scr")]
    [InlineData("  bubbles.SCR  ")]
    [InlineData("school_logo-2026.scr")]
    public void Plain_scr_file_names_are_accepted(string name)
    {
        Assert.Equal(name.Trim(), ScreenSaverRules.SafeFileName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".scr")]
    [InlineData("saver.exe")]
    [InlineData("saver.scr.exe")]
    [InlineData("..\\saver.scr")]
    [InlineData("C:\\Windows\\System32\\saver.scr")]
    [InlineData("folder/saver.scr")]
    [InlineData("saver.scr:stream")]
    [InlineData("..scr")]
    [InlineData(".hidden.scr")]
    [InlineData("trailing .scr")]
    [InlineData("CON.scr")]
    [InlineData("lpt1.scr")]
    [InlineData("com1.backup.scr")]
    [InlineData("sav\u00E9r.scr")]
    public void Paths_other_extensions_and_device_names_are_rejected(string? name)
    {
        Assert.Equal("", ScreenSaverRules.SafeFileName(name));
    }

    [Fact]
    public void Overlong_names_are_rejected()
    {
        Assert.Equal("", ScreenSaverRules.SafeFileName(new string('a', 77) + ".scr"));
        Assert.NotEqual("", ScreenSaverRules.SafeFileName(new string('a', 76) + ".scr"));
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(59, 60)]
    [InlineData(600, 600)]
    [InlineData(int.MaxValue, 9999 * 60)]
    public void Wait_time_is_kept_within_what_Windows_offers(int requested, int applied)
    {
        Assert.Equal(applied, ScreenSaverRules.ClampTimeoutSeconds(requested));
    }
}
