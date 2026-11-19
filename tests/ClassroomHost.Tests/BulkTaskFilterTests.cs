// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using System.Text.Json.Nodes;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class BulkTaskFilterTests
{
    [Theory]
    [InlineData("lesson", true)]
    [InlineData("teacher", true)]
    [InlineData("powershell", true)]
    [InlineData("ready", true)]
    [InlineData("unrelated", false)]
    public void Filter_searches_inventory_details(string filter, bool expected)
    {
        var task = new TaskEntry
        {
            Name = @"\School\Lesson cleanup",
            Status = "Ready",
            RunAs = @"LAB\teacher",
            TaskToRun = "powershell.exe -File cleanup.ps1",
            Author = "School IT",
            LastResult = "0",
        };

        Assert.Equal(expected, BulkTasksWindow.Matches(task, filter));
    }

    [Fact]
    public void TaskEntry_accepts_older_clients_without_author()
    {
        var entry = TaskEntry.FromJson(new JsonObject
        {
            ["name"] = @"\School\Task",
            ["status"] = "Ready",
            ["nextRun"] = "Tomorrow",
            ["lastRun"] = "Yesterday",
            ["lastResult"] = "0",
            ["runAs"] = "SYSTEM",
            ["taskToRun"] = "cleanup.exe",
        });

        Assert.Equal("", entry.Author);
    }
}
