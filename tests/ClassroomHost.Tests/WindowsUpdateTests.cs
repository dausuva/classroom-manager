// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class WindowsUpdateTests
{
    [Theory]
    [InlineData("1 day", 1)]
    [InlineData("7 days", 7)]
    [InlineData("14 days", 14)]
    [InlineData("21 days", 21)]
    [InlineData("28 days", 28)]
    [InlineData("35 days", 35)]
    [InlineData("unexpected", 7)]
    public void Pause_period_choices_map_to_bounded_day_counts(string choice, int expected)
    {
        Assert.Equal(expected, WindowsUpdatePausePeriods.ParseDays(choice));
    }
}
