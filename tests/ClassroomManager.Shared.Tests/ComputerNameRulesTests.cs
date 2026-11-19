// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>T-171: what a Windows computer name may be, and what asking for a rename needs.</summary>
public sealed class ComputerNameRulesTests
{
    [Theory]
    [InlineData("LAB1-PC07")]
    [InlineData("a")]
    [InlineData("PC-1")]
    [InlineData("ABCDEFGHIJKLMNO")]   // 15 characters
    [InlineData("7PC")]
    public void A_name_windows_accepts_has_no_problem(string name)
    {
        Assert.Null(ComputerNameRules.Problem(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ABCDEFGHIJKLMNOP")]  // 16
    [InlineData("LAB 1")]
    [InlineData("LAB_1")]
    [InlineData("PC.01")]
    [InlineData("Pūstelnikas")]
    [InlineData("-PC")]
    [InlineData("PC-")]
    [InlineData("12345")]
    public void Anything_else_is_a_problem_with_a_reason(string? name)
    {
        Assert.False(string.IsNullOrWhiteSpace(ComputerNameRules.Problem(name)));
    }

    [Fact]
    public void Windows_ignores_the_case_of_a_name()
    {
        Assert.True(ComputerNameRules.Same("Lab1-PC", "LAB1-pc"));
        Assert.False(ComputerNameRules.Same("Lab1", "Lab2"));
    }

    [Fact]
    public void The_rename_is_new_in_1_16_an_admin_command_and_advertised()
    {
        Assert.Equal("1.16", Protocol.RequiredVersion(Cmd.SystemRename));
        Assert.Contains(Cmd.SystemRename, Protocol.Capabilities);
        Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole(Cmd.SystemRename));
        Assert.False(Cmd.IsUserOnly(Cmd.SystemRename));
        Assert.False(Protocol.SupportsCommand(Cmd.SystemRename, "1.15", [], false));
        Assert.True(Protocol.SupportsCommand(Cmd.SystemRename, "1.16", [], false));
    }
}
