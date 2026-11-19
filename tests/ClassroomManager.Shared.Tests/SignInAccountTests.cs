// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class SignInAccountTests
{
    [Theory]
    [InlineData("student", "", "student", true, "student")]
    [InlineData("  student  ", "", "student", true, "student")]
    [InlineData(@".\student", "", "student", true, "student")]
    [InlineData(@"SCHOOL\anna.b", "SCHOOL", "anna.b", false, @"SCHOOL\anna.b")]
    [InlineData(@"LAB-PC-07\Exam User", "LAB-PC-07", "Exam User", false, @"LAB-PC-07\Exam User")]
    [InlineData("anna@school.example", "school.example", "anna@school.example", false, "anna@school.example")]
    public void Accounts_are_split_the_way_Windows_expects(string text, string domain, string user, bool local, string display)
    {
        var account = SignInAccount.Parse(text);

        Assert.NotNull(account);
        Assert.Equal(domain, account.Domain);
        Assert.Equal(user, account.User);
        Assert.Equal(local, account.IsLocal);
        Assert.Equal(display, account.Display);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\student")]
    [InlineData(@"SCHOOL\")]
    [InlineData(@"SCHOOL\a\b")]
    [InlineData("a@b@c")]
    [InlineData("@school.example")]
    [InlineData("anna@")]
    [InlineData("stu/dent")]
    [InlineData("stu\"dent")]
    [InlineData("stu:dent")]
    [InlineData("student*")]
    [InlineData("stu\tdent")]
    public void Malformed_accounts_are_rejected(string? text)
    {
        Assert.Null(SignInAccount.Parse(text));
    }

    [Fact]
    public void Remote_sign_in_is_a_protocol_1_8_system_capability()
    {
        Assert.Equal("1.8", Protocol.RequiredVersion(Cmd.UserSignIn));
        // It must reach the elevated service; with nobody signed in there is no user helper at all.
        Assert.False(Cmd.IsUserOnly(Cmd.UserSignIn));
        Assert.Contains(Cmd.UserSignIn, Protocol.Capabilities);
        Assert.False(Protocol.SupportsCommand(Cmd.UserSignIn, "1.7", [], false));
    }
}
