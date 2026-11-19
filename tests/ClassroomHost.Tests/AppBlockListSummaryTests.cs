// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class AppBlockListSummaryTests
{
    [Fact]
    public void Summary_unions_lists_and_detects_divergence_and_unavailable_targets()
    {
        IReadOnlyList<string>[] lists =
        [
            ["browser.exe", "game.exe"],
            ["BROWSER.exe", "chat.exe"],
        ];

        var summary = AppBlockListSummary.Create(lists, 3);

        Assert.Equal(["browser.exe", "chat.exe", "game.exe"], summary.Union);
        Assert.Equal(2, summary.DistinctListCount);
        Assert.Equal(2, summary.AvailableCount);
        Assert.Equal(1, summary.UnavailableCount);
    }
}
