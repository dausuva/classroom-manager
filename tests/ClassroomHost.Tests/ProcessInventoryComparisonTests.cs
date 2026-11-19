// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using System.Text.Json.Nodes;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ProcessInventoryComparisonTests
{
    [Fact]
    public void ProcessEntry_accepts_older_clients_without_version_or_start_time()
    {
        var entry = ProcessEntry.FromJson(new JsonObject
        {
            ["pid"] = 42,
            ["name"] = "legacy",
            ["sessionId"] = 1,
            ["title"] = "",
            ["memoryMb"] = 12.5,
        });

        Assert.Equal("", entry.Version);
        Assert.Equal(0, entry.StartUtcTicks);
        Assert.False(ProcessInventoryComparison.SafeToBatchEnd(entry));
    }

    [Fact]
    public void Build_compares_versions_instances_windows_and_coverage()
    {
        var first = Client("PC-1", "10.0.0.1");
        var second = Client("PC-2", "10.0.0.2");
        var third = Client("PC-3", "10.0.0.3");
        var inventories = new Dictionary<ClientInfo, IReadOnlyList<ProcessEntry>>
        {
            [first] = [Process(10, "browser", "1.0", "Page one")],
            [second] = [Process(20, "Browser", "2.0", "Page two"), Process(21, "browser", "2.0", "")],
            [third] = [],
        };

        var browser = Assert.Single(ProcessInventoryComparison.Build([first, second, third], inventories));

        Assert.Equal("1.0, 2.0", browser.Versions);
        Assert.Equal("2 / 3 checked", browser.Running);
        Assert.Equal(3, browser.Instances);
        Assert.Equal("PC-3", browser.MissingComputers);
        Assert.True(browser.HasVersionDifference);
        Assert.True(browser.HasVisibleWindow);
        Assert.True(browser.HasDifference);
        Assert.Contains("PC-1: 1×, v1.0", browser.ComputerDetails);
        Assert.Contains("PC-2: 2×, v2.0", browser.ComputerDetails);
        Assert.Contains("Instance counts differ", browser.Comparison);
    }

    [Fact]
    public void Build_does_not_treat_an_unavailable_computer_as_not_running()
    {
        var online = Client("Online", "10.0.0.1");
        var offline = Client("Offline", "10.0.0.2");
        var inventories = new Dictionary<ClientInfo, IReadOnlyList<ProcessEntry>>
        {
            [online] = [Process(10, "browser", "1.0", "")],
        };

        var browser = Assert.Single(ProcessInventoryComparison.Build([online, offline], inventories));

        Assert.False(browser.IsMissingFromSome);
        Assert.Equal("—", browser.MissingComputers);
        Assert.Contains("1 unavailable", browser.Comparison);
    }

    [Theory]
    [InlineData("User", "browser", 10, 100, true)]
    [InlineData("System", "browser", 10, 100, false)]
    [InlineData("Unknown", "browser", 10, 100, true)]
    [InlineData("User", "ClassroomClient", 10, 100, false)]
    [InlineData("User", "browser", 4, 100, false)]
    [InlineData("User", "browser", 10, 0, false)]
    public void Batch_end_only_accepts_verified_non_system_processes(
        string role, string name, int pid, long startUtcTicks, bool expected)
    {
        var process = Process(pid, name, "1.0", "", role, startUtcTicks);

        Assert.Equal(expected, ProcessInventoryComparison.SafeToBatchEnd(process));
    }

    static ClientInfo Client(string name, string address) => new() { Name = name, Address = address };

    static ProcessEntry Process(
        int pid, string name, string version, string title, string role = "User", long startUtcTicks = 100) => new()
    {
        Pid = pid,
        Name = name,
        Version = version,
        Title = title,
        Path = $@"C:\Apps\{name}.exe",
        MemoryMb = 25,
        Owner = "LAB\\student",
        Role = role,
        Kind = title.Length > 0 ? "User / windowed app" : "User / background",
        StartUtcTicks = startUtcTicks,
    };
}
