// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-173: the update list PC information reads is kept, with its age, for the Windows Update window.</summary>
public sealed class WindowsUpdateSnapshotCacheTests
{
    static ClientInfo Client(string id) => new() { ClientId = id, Name = "PC-" + id, Address = "10.0.0.9" };

    static JsonArray Updates() => new()
    {
        new JsonObject
        {
            ["id"] = "u1", ["title"] = "Security update", ["kb"] = new JsonArray("5001"), ["size"] = 1048576L,
            ["downloaded"] = true, ["requiresRestart"] = false,
        },
    };

    [Fact]
    public void A_saved_list_comes_back_with_its_time_and_entries()
    {
        var client = Client("cache-1");
        var at = DateTime.Now.AddMinutes(-20);
        WindowsUpdateSnapshotCache.Set(client, true, Updates(), at);

        Assert.True(WindowsUpdateSnapshotCache.TryGet(client, out var saved));
        Assert.Equal(at, saved.At);
        Assert.True(saved.IncludedOptional);
        var entry = WindowsUpdateEntry.FromJson(saved.Updates[0]!);
        Assert.Equal("Security update", entry.Title);
        Assert.Equal("KB5001", entry.Kb);
    }

    [Fact]
    public void A_computer_with_nothing_saved_has_no_list()
    {
        Assert.False(WindowsUpdateSnapshotCache.TryGet(Client("never-scanned"), out _));
    }

    [Fact]
    public void A_list_older_than_the_limit_is_not_used()
    {
        var client = Client("cache-old");
        WindowsUpdateSnapshotCache.Set(client, false, Updates(), DateTime.Now - WindowsUpdateSnapshotCache.MaxAge - TimeSpan.FromMinutes(1));

        Assert.False(WindowsUpdateSnapshotCache.TryGet(client, out _));
    }

    [Fact]
    public void Each_computer_keeps_its_own_list()
    {
        WindowsUpdateSnapshotCache.Set(Client("cache-a"), false, Updates());

        Assert.False(WindowsUpdateSnapshotCache.TryGet(Client("cache-b"), out _));
    }

    [Theory]
    [InlineData(0, "List read less than a minute ago (2 update(s)).")]
    [InlineData(12, "List read 12 minute(s) ago (2 update(s)).")]
    [InlineData(190, "List read 3 hour(s) ago (2 update(s)).")]
    public void The_age_is_told_in_the_largest_fitting_unit(int minutes, string expected)
    {
        var now = DateTime.Now;
        Assert.StartsWith(expected, WindowsUpdateSnapshotCache.SavedText(now.AddMinutes(-minutes), now, 2));
    }
}
