// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-164: the Host reads the program's name from a Client that sends it and shows nothing from one that does not.</summary>
public sealed class ProcessEntryTests
{
    static JsonObject Item(string? description)
    {
        var item = new JsonObject
        {
            ["pid"] = 4242, ["name"] = "msedge", ["sessionId"] = 1, ["title"] = "Docs", ["path"] = @"C:\x\msedge.exe",
            ["version"] = "141.0", ["memoryMb"] = 210.5,
        };
        if (description is not null) item["description"] = description;
        return item;
    }

    [Fact]
    public void The_description_is_read_from_a_client_that_sends_it()
    {
        Assert.Equal("Microsoft Edge", ProcessEntry.FromJson(Item("Microsoft Edge")).Description);
    }

    [Fact]
    public void An_older_client_that_does_not_send_it_gives_an_empty_one_not_an_error()
    {
        var entry = ProcessEntry.FromJson(Item(null));

        Assert.Equal("", entry.Description);
        Assert.Equal("msedge", entry.Name);
    }

    [Fact]
    public void The_description_is_something_the_filter_finds()
    {
        var entry = ProcessEntry.FromJson(Item("Microsoft Edge"));

        Assert.True(FilterMatch.Matches("microsoft edge", entry.Name, entry.Description, entry.Title, entry.Owner, entry.Kind, entry.Path, entry.Version));
        Assert.False(FilterMatch.Matches("firefox", entry.Name, entry.Description, entry.Title, entry.Owner, entry.Kind, entry.Path, entry.Version));
    }
}
