// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class BulkFileComparisonTests
{
    [Fact]
    public void Build_compares_presence_and_distinct_sizes_across_computers()
    {
        var first = new ClientInfo { Name = "PC-1", Address = "10.0.0.1" };
        var second = new ClientInfo { Name = "PC-2", Address = "10.0.0.2" };
        var rows = BulkFileComparison.Build([first, second],
        [
            new BulkFilePresence(first, "lesson.txt", false, 10, "2026-10-09 10:00"),
            new BulkFilePresence(second, "lesson.txt", false, 20, "2026-10-09 10:01"),
            new BulkFilePresence(first, "Shared", true, 0, "2026-10-09 09:00"),
        ]);

        var folder = Assert.Single(rows, row => row.Name == "Shared");
        Assert.Equal("1 / 2", folder.Present);
        Assert.True(folder.IsDir);
        var file = Assert.Single(rows, row => row.Name == "lesson.txt");
        Assert.Equal("2 / 2", file.Present);
        Assert.Equal("10 B / 20 B", file.Sizes);
    }
}
