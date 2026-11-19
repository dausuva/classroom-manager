// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Maintenance;
using ClassroomManager.Modules;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// The Maintenance module's Windows Update operations through the real broker checks and a fake computer: every command is sent with
/// administrator rights only because the module declared it, and the answers are read the way the Host used to read them itself.
/// </summary>
public sealed class MaintenanceOperationsTests
{
    sealed class Computer : IModuleConnection
    {
        public List<(string Level, string Command, string Args)> Calls { get; } = [];
        public Queue<string> Answers { get; } = new();

        public Task<string> CallUserAsync(string command, string argumentsJson, CancellationToken cancellation)
        {
            Calls.Add(("user", command, argumentsJson));
            return Task.FromResult(Answers.Dequeue());
        }

        public Task<string> CallSystemAsync(string command, string argumentsJson, CancellationToken cancellation)
        {
            Calls.Add(("system", command, argumentsJson));
            return Task.FromResult(Answers.Dequeue());
        }

        public Task DownloadAsync(string remotePath, string localPath, CancellationToken cancellation) => throw new NotSupportedException();
    }

    sealed class NoSinks : IModuleSinkFolders
    {
        public string? Resolve(string sink) => null;
    }

    readonly List<string> _audit = [];

    (MaintenanceOperations Operations, ModuleComputer Computer, Computer Connection, ModuleClientSession Session) Open()
    {
        var connection = new Computer();
        var descriptor = new MaintenanceInventoryModuleEntry().Describe();
        var computer = new ModuleComputer(1, "PC-01");
        var session = new ModuleClientSession(descriptor, [new(computer, connection, "PC-01")], new NoSinks(), _audit.Add);
        return (new MaintenanceOperations(session), computer, connection, session);
    }

    [Fact]
    public async Task Every_operation_is_sent_with_administrator_rights_and_the_arguments_the_host_used_to_send()
    {
        var (operations, computer, connection, session) = Open();
        for (var i = 0; i < 6; i++) connection.Answers.Enqueue("{}");

        await operations.ScanAsync(computer, includeOptional: null, CancellationToken.None);
        await operations.ScanAsync(computer, includeOptional: true, CancellationToken.None);
        await operations.InstallAllAsync(computer, CancellationToken.None);
        await operations.StatusAsync(computer, CancellationToken.None);
        await operations.PauseAsync(computer, 7, CancellationToken.None);
        await operations.ResumeAsync(computer, CancellationToken.None);

        Assert.All(connection.Calls, call => Assert.Equal("system", call.Level));
        Assert.Equal(["windows.update.scan", "windows.update.scan", "windows.update.install", "windows.update.status", "windows.update.pause", "windows.update.resume"],
            connection.Calls.Select(call => call.Command));
        Assert.Equal("{}", connection.Calls[0].Args);
        Assert.True(JsonNode.Parse(connection.Calls[1].Args)!["includeOptional"]!.GetValue<bool>());
        Assert.True(JsonNode.Parse(connection.Calls[2].Args)!["all"]!.GetValue<bool>());
        Assert.Equal(7, JsonNode.Parse(connection.Calls[4].Args)!["days"]!.GetValue<int>());
        Assert.Equal(0, session.Refusals);
        Assert.All(_audit, line => Assert.EndsWith("(administrator)", line));
    }

    [Fact]
    public async Task A_system_command_may_be_sent_as_the_lesser_user_level_but_an_undeclared_one_cannot_be_sent_at_all()
    {
        var (_, computer, connection, session) = Open();
        connection.Answers.Enqueue("{}");

        await session.CallAsync(computer, "windows.update.scan", "{}");
        Assert.Equal("user", Assert.Single(connection.Calls).Level);
        connection.Calls.Clear();

        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsSystemAsync(computer, "cmd.run", """{"command":"calc"}"""));
        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsSystemAsync(computer, "power.action", """{"action":"shutdown"}"""));
        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsSystemAsync(computer, "fs.delete", """{"path":"C:\\"}"""));

        Assert.Empty(connection.Calls);
        Assert.True(session.Stopped);
    }

    [Fact]
    public async Task A_module_that_declares_a_command_as_user_level_cannot_send_it_with_administrator_rights()
    {
        var connection = new Computer();
        var descriptor = new ModuleDescriptor("dausuva.demo.user", "Demo", 1, ["fs.recent"]) { Ports = [new(ModulePorts.ClientCall, 1)] };
        var computer = new ModuleComputer(1, "PC-01");
        var session = new ModuleClientSession(descriptor, [new(computer, connection, "x")], new NoSinks(), _audit.Add);

        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsSystemAsync(computer, "fs.recent", "{}"));

        Assert.Empty(connection.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(61 * 60)]
    public async Task A_timeout_outside_the_allowed_range_is_refused(int seconds)
    {
        var (_, computer, connection, session) = Open();

        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsSystemAsync(computer, "windows.update.scan", "{}", timeout: TimeSpan.FromSeconds(seconds)));

        Assert.Empty(connection.Calls);
    }

    [Fact]
    public async Task A_long_operation_may_ask_for_more_than_a_minute()
    {
        var (_, computer, connection, session) = Open();
        connection.Answers.Enqueue("{}");

        await session.CallAsSystemAsync(computer, "windows.update.install", "{}", timeout: TimeSpan.FromMinutes(60));

        Assert.Single(connection.Calls);
    }

    [Fact]
    public void A_scan_answer_is_read_into_the_same_projection_the_host_showed_before()
    {
        var answer = """{"updates":[{"title":"Security update","kb":["5030211","KB5031"],"size":10485760,"optional":false,"requiresRestart":true},{"kb":[],"feature":true,"size":0},{"title":"Driver","optional":true}]}""";

        var report = MaintenanceOperations.ReadScan(answer, "Unnamed update");

        Assert.Equal(3, report.Updates.Count);
        Assert.Equal(["KB5030211", "KB5031"], report.Updates[0].Kb);
        Assert.Equal(10d, report.Updates[0].SizeMb);
        Assert.Equal("Unnamed update", report.Updates[1].Title);
        Assert.Equal((1, 1, 1), (report.Features, report.Optional, report.RequiringRestart));
        Assert.Equal(report, WindowsMaintenanceInventory.ProjectScan((JsonObject)JsonNode.Parse(answer)!) with { }, new ReportComparer());
        Assert.Equal(["Security update", "Driver"], MaintenanceOperations.ReadTitles(answer));
    }

    sealed class ReportComparer : IEqualityComparer<UpdateScanReport>
    {
        public bool Equals(UpdateScanReport? x, UpdateScanReport? y) =>
            x is not null && y is not null && x.Features == y.Features && x.Optional == y.Optional && x.RequiringRestart == y.RequiringRestart &&
            x.Updates.Select(u => (u.Title, string.Join(",", u.Kb), u.SizeMb)).SequenceEqual(y.Updates.Select(u => (u.Title, string.Join(",", u.Kb), u.SizeMb)));

        public int GetHashCode(UpdateScanReport obj) => obj.Updates.Count;
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"updates":"none"}""")]
    [InlineData("""{"updates":[1]}""")]
    [InlineData("[]")]
    public void An_answer_that_is_not_a_list_of_updates_is_a_json_error(string answer)
    {
        Assert.Throws<JsonException>(() => MaintenanceOperations.ReadScan(answer, "Unnamed update"));
        Assert.Throws<JsonException>(() => WindowsMaintenanceInventory.ProjectScan(JsonNode.Parse(answer) as JsonObject ?? throw new JsonException()));
    }

    [Fact]
    public void A_pause_answer_is_read_like_the_host_read_it()
    {
        var now = new DateTimeOffset(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);

        var paused = MaintenanceOperations.ReadPause("""{"paused":true,"untilUtc":"2026-11-07T08:00:00Z"}""", false, now);
        Assert.True(paused.Paused);
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 8, 0, 0, TimeSpan.Zero), paused.Until);

        Assert.False(MaintenanceOperations.ReadPause("""{"paused":false}""", true, now).Paused);
        // An older Client says nothing about "paused": a pause that was just asked for and runs into the future counts as paused.
        Assert.True(MaintenanceOperations.ReadPause("""{"untilUtc":"2026-11-07T08:00:00Z"}""", true, now).Paused);
        Assert.False(MaintenanceOperations.ReadPause("""{"untilUtc":"2026-11-07T08:00:00Z"}""", false, now).Paused);
        Assert.Equal(MaintenanceOperations.ReadPause("""{"paused":true}""", false, now),
            WindowsMaintenanceInventory.ProjectPause((JsonObject)JsonNode.Parse("""{"paused":true}""")!, false, now));
    }

    [Fact]
    public void The_module_declares_what_it_sends_and_that_all_of_it_is_administrator_level()
    {
        var descriptor = new MaintenanceInventoryModuleEntry().Describe();

        Assert.Equal(MaintenanceOperations.Commands, descriptor.ClientCapabilities);
        Assert.Equal(MaintenanceOperations.Commands, descriptor.SystemCommands);
        Assert.Equal([ModulePorts.ClientCall], descriptor.Ports.Select(port => port.Name));
        Assert.Empty(ModuleDescriptorRules.Problems(descriptor));
        Assert.Contains("This module acts with administrator rights on the computers you select.", BuiltInModules.PortSentences(descriptor));
    }

    [Fact]
    public void A_manifest_may_list_system_commands_only_from_its_declared_capabilities()
    {
        Assert.Empty(ModuleManifest.SystemCommandProblems(["a.b"], ["a.b", "c.d"]));
        Assert.Empty(ModuleManifest.SystemCommandProblems(null, null));
        Assert.NotEmpty(ModuleManifest.SystemCommandProblems(["x.y"], ["a.b"]));
        Assert.NotEmpty(ModuleManifest.SystemCommandProblems(["a.b", "a.b"], ["a.b"]));
        Assert.NotEmpty(ModuleManifest.SystemCommandProblems(["a.b"], null));
    }
}
