// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class PathTextTests
{
    [Theory]
    [InlineData("\"C:\\Users\\pc\\My Documents\\Homework 3\"", @"C:\Users\pc\My Documents\Homework 3")]
    [InlineData("  \"%DOCUMENTS%\\Class work\"  ", @"%DOCUMENTS%\Class work")]
    [InlineData("\"C:\\Users\\pc\\Desktop", @"C:\Users\pc\Desktop")]
    [InlineData(@"C:\Users\pc\My Documents", @"C:\Users\pc\My Documents")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void A_pasted_path_loses_its_quotes_and_keeps_its_spaces(string? pasted, string expected) =>
        Assert.Equal(expected, PathText.Clean(pasted));
}
