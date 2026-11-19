// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>The file names a screen saver may have (docs/QUALITY.md: the mutation test found the Windows device-name list and the length edge unwatched).</summary>
public sealed class ScreenSaverNameTests
{
    [Theory]
    [InlineData("CON")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("COM5")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("LPT5")]
    [InlineData("LPT9")]
    [InlineData("con")]
    [InlineData("Lpt3")]
    public void A_Windows_device_name_is_refused_as_a_screen_saver_name_in_any_case(string device)
    {
        Assert.Equal("", ScreenSaverRules.SafeFileName(device + ".scr"));
        Assert.Equal("", ScreenSaverRules.SafeFileName(device + ".part2.scr"));   // the device name is what comes before the first dot
    }

    [Theory]
    [InlineData("Matrix.scr", "Matrix.scr")]
    [InlineData("  Matrix.scr  ", "Matrix.scr")]
    [InlineData("Matrix.SCR", "Matrix.SCR")]
    [InlineData("My saver (2).scr", "My saver (2).scr")]
    [InlineData("a-b_c.d.scr", "a-b_c.d.scr")]
    [InlineData("COM10.scr", "COM10.scr")]
    [InlineData("CONSOLE.scr", "CONSOLE.scr")]
    [InlineData("LPT0.scr", "LPT0.scr")]
    public void A_plain_name_is_kept_trimmed(string typed, string expected) => Assert.Equal(expected, ScreenSaverRules.SafeFileName(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Matrix")]
    [InlineData("Matrix.exe")]
    [InlineData(".scr")]
    [InlineData(".hidden.scr")]
    [InlineData("name..scr")]
    [InlineData("name .scr")]
    [InlineData(@"sub\Matrix.scr")]
    [InlineData("sub/Matrix.scr")]
    [InlineData("..\\Matrix.scr")]
    [InlineData("Mätrix.scr")]
    [InlineData("a:b.scr")]
    public void Anything_else_is_refused(string? typed) => Assert.Equal("", ScreenSaverRules.SafeFileName(typed));

    [Fact]
    public void A_name_may_be_eighty_characters_with_the_extension_and_not_more()
    {
        var ok = new string('a', 80 - ScreenSaverRules.Extension.Length) + ScreenSaverRules.Extension;
        Assert.Equal(80, ok.Length);
        Assert.Equal(ok, ScreenSaverRules.SafeFileName(ok));
        Assert.Equal("", ScreenSaverRules.SafeFileName("a" + ok));
    }
}
