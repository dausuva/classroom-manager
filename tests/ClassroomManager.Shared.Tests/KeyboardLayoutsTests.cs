// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class KeyboardLayoutsTests
{
    [Theory]
    [InlineData("0427:00010427", true)]
    [InlineData("0409:00000409", true)]
    [InlineData("0411:{03B5835F-F03C-411B-9CE2-AA23E1171E36}{A76C93D9-5523-4E90-AAFA-4DB112F9AC76}", true)]
    [InlineData("0427", false)]
    [InlineData("0427:0001042", false)]
    [InlineData("lt-LT", false)]
    [InlineData("0427:00010427'; Remove-Item C:\\ -Recurse; '", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_Windows_input_method_names_are_accepted(string? tip, bool accepted)
    {
        Assert.Equal(accepted, KeyboardLayouts.IsTip(tip));
    }

    [Fact]
    public void The_language_and_layout_parts_are_read_from_the_name()
    {
        Assert.Equal("lt-LT", KeyboardLayouts.LanguageOf("0427:00010427"));
        Assert.Equal("en-US", KeyboardLayouts.LanguageOf("0409:00020409"));
        Assert.Equal("00010427", KeyboardLayouts.LayoutId("0427:00010427"));
        Assert.Equal("", KeyboardLayouts.LayoutId("0411:{03B5835F-F03C-411B-9CE2-AA23E1171E36}{A76C93D9-5523-4E90-AAFA-4DB112F9AC76}"));
        Assert.Equal("040C:0000040C", KeyboardLayouts.Normalize(" 040c:0000040c "));
    }

    [Fact]
    public void Keyboard_commands_need_protocol_1_11_run_as_the_user_and_need_an_Admin_key()
    {
        foreach (var command in new[] { Cmd.KeyboardList, Cmd.KeyboardSet })
        {
            Assert.Equal("1.11", Protocol.RequiredVersion(command));
            Assert.Contains(command, Protocol.Capabilities);
            Assert.True(Cmd.IsUserOnly(command));
            Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole(command));
        }
        Assert.All(KeyboardLayouts.Common, entry => Assert.True(KeyboardLayouts.IsTip(entry.Tip), entry.Tip));
    }
}
