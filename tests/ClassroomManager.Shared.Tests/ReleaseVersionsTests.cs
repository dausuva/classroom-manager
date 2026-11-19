// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

// T-218: a customer sees 1.2.0; an internal build keeps its build number.
public sealed class ReleaseVersionsTests
{
    [Theory]
    [InlineData("1.2.0.0", "1.2.0")]
    [InlineData("10.20.30.0", "10.20.30")]
    [InlineData("1.2.0.435", "1.2.0.435")]
    [InlineData("1.2.0.10", "1.2.0.10")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("1.2", "1.2")]
    [InlineData("1.2.0.00", "1.2.0.00")]
    [InlineData("1.2.0.0-beta", "1.2.0.0-beta")]
    [InlineData("Unknown", "Unknown")]
    [InlineData("", "")]
    public void A_zero_fourth_number_is_not_shown_and_nothing_else_changes(string shown, string expected)
    {
        Assert.Equal(expected, ReleaseVersions.Display(shown));
    }

    [Fact]
    public void Nothing_is_shown_as_nothing()
    {
        Assert.Equal("", ReleaseVersions.Display(null));
    }
}
