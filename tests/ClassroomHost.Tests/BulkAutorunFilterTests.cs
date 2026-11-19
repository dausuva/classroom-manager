// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class BulkAutorunFilterTests
{
    [Theory]
    [InlineData("browser", true)]
    [InlineData("startup", true)]
    [InlineData("publisher app", true)]
    [InlineData("registry", true)]
    [InlineData("disabled", true)]
    [InlineData("unrelated", false)]
    public void Filter_searches_all_useful_fields(string filter, bool expected)
    {
        var entry = new AutorunEntry
        {
            Kind = "registry",
            Name = "Browser helper",
            Command = @"C:\Apps\helper.exe --startup",
            Source = @"HKCU\Run",
            MatchedProgram = "Publisher App",
            Enabled = false,
        };

        Assert.Equal(expected, BulkAutorunsWindow.Matches(entry, filter));
    }
}
