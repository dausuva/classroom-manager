// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.BrowserInventory;
using ClassroomManager.Modules;
using ClassroomManager.Reporting;
using ClassroomManager.SoftwareInventory;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// The Software, Browser inventory and Reporting modules' operations through the real broker checks and a fake computer: each module can
/// send only the commands it declared, with administrator rights only for the ones it declared for that, and nothing that changes a browser,
/// a file or a power state.
/// </summary>
public sealed class ModuleOperationsTests
{
    sealed class Computer : IModuleConnection
    {
        public List<(string Level, string Command, string Args)> Calls { get; } = [];

        public Task<string> CallUserAsync(string command, string argumentsJson, CancellationToken cancellation)
        {
            Calls.Add(("user", command, argumentsJson));
            return Task.FromResult("{}");
        }

        public Task<string> CallSystemAsync(string command, string argumentsJson, CancellationToken cancellation)
        {
            Calls.Add(("system", command, argumentsJson));
            return Task.FromResult("{}");
        }

        public Task DownloadAsync(string remotePath, string localPath, CancellationToken cancellation) => throw new NotSupportedException();
    }

    sealed class NoSinks : IModuleSinkFolders
    {
        public string? Resolve(string sink) => null;
    }

    static (ModuleClientSession Session, ModuleComputer Computer, Computer Connection) Open(ModuleDescriptor descriptor)
    {
        var connection = new Computer();
        var computer = new ModuleComputer(1, "PC-01");
        return (new ModuleClientSession(descriptor, [new(computer, connection, "PC-01")], new NoSinks(), _ => { }), computer, connection);
    }

    [Fact]
    public async Task Software_lists_and_uninstalls_at_the_level_the_teacher_chose_and_passes_the_clients_own_words_unchanged()
    {
        var (session, computer, connection) = Open(new SoftwareInventoryModuleEntry().Describe());
        var software = new SoftwareOperations(session);

        await software.ListAsync(computer, administrator: true, CancellationToken.None);
        await software.ListAsync(computer, administrator: false, CancellationToken.None);
        await software.UninstallAsync(computer, administrator: true, @"HKLM\Uninstall\{GUID}", "64", "/S /norestart", CancellationToken.None);
        await software.UninstallAsync(computer, administrator: false, "key", "32", "", CancellationToken.None);

        Assert.Equal([("system", "programs.list"), ("user", "programs.list"), ("system", "programs.uninstall"), ("user", "programs.uninstall")],
            connection.Calls.Select(call => (call.Level, call.Command)));
        Assert.Equal("{}", connection.Calls[0].Args);
        var uninstall = JsonNode.Parse(connection.Calls[2].Args)!;
        Assert.Equal(@"HKLM\Uninstall\{GUID}", uninstall["key"]!.GetValue<string>());
        Assert.Equal("64", uninstall["view"]!.GetValue<string>());
        Assert.Equal("/S /norestart", uninstall["args"]!.GetValue<string>());
        Assert.Equal(0, session.Refusals);
    }

    [Theory]
    [InlineData("cmd.run")]
    [InlineData("install.run")]
    [InlineData("fs.delete")]
    [InlineData("proc.kill")]
    [InlineData("registry.import")]
    public async Task Software_cannot_send_anything_it_did_not_declare_at_either_level(string command)
    {
        var (session, computer, connection) = Open(new SoftwareInventoryModuleEntry().Describe());

        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsSystemAsync(computer, command, "{}"));
        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsync(computer, command, "{}"));

        Assert.Empty(connection.Calls);
    }

    [Fact]
    public async Task Browser_inventory_reads_only_and_only_the_machine_wide_reads_may_use_administrator_rights()
    {
        var (session, computer, connection) = Open(new BrowserInventoryModuleEntry().Describe());
        var browsers = new BrowserOperations(session);

        await browsers.ReadAsync(computer, administrator: true, BrowserOperations.Policy, CancellationToken.None);
        await browsers.ReadAsync(computer, administrator: false, BrowserOperations.Browsers, CancellationToken.None);
        await browsers.ReadAsync(computer, administrator: true, BrowserOperations.Browsers, CancellationToken.None);
        await browsers.ReadAsync(computer, administrator: false, BrowserOperations.Profiles, CancellationToken.None);

        Assert.Equal(4, connection.Calls.Count);
        // The profiles are the signed-in student's own; administrator rights for them are not declared and are refused.
        await Assert.ThrowsAsync<ModulePortException>(() => browsers.ReadAsync(computer, administrator: true, BrowserOperations.Profiles, CancellationToken.None));
        Assert.Equal(4, connection.Calls.Count);
    }

    [Theory]
    [InlineData("browser.data.clear")]
    [InlineData("browser.profiles.remove")]
    [InlineData("browser.policy.set")]
    [InlineData("browser.default.set")]
    public async Task Browser_inventory_cannot_change_a_browser_even_though_the_name_is_close(string command)
    {
        var (session, computer, connection) = Open(new BrowserInventoryModuleEntry().Describe());

        await Assert.ThrowsAsync<ModulePortException>(() => new BrowserOperations(session).ReadAsync(computer, administrator: false, command, CancellationToken.None));
        await Assert.ThrowsAsync<ModulePortException>(() => new BrowserOperations(session).ReadAsync(computer, administrator: true, command, CancellationToken.None));

        Assert.Empty(connection.Calls);
    }

    [Fact]
    public async Task Reporting_takes_one_snapshot_with_administrator_rights_and_nothing_else()
    {
        var (session, computer, connection) = Open(new InventoryReportingModuleEntry().Describe());

        await new ReportingOperations(session).InventoryAsync(computer, CancellationToken.None);

        var call = Assert.Single(connection.Calls);
        Assert.Equal(("system", "inventory.get"), (call.Level, call.Command));
        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsSystemAsync(computer, "programs.list", "{}"));
        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsSystemAsync(computer, "fs.read", "{}"));
    }

    [Fact]
    public void Each_converted_module_declares_exactly_the_commands_its_operations_send()
    {
        Assert.Equal(SoftwareOperations.Commands, new SoftwareInventoryModuleEntry().Describe().ClientCapabilities);
        Assert.Equal(BrowserOperations.Commands, new BrowserInventoryModuleEntry().Describe().ClientCapabilities);
        Assert.Equal(BrowserOperations.SystemCommands, new BrowserInventoryModuleEntry().Describe().SystemCommands);
        Assert.Equal(ReportingOperations.Commands, new InventoryReportingModuleEntry().Describe().ClientCapabilities);
        Assert.All(new IClassroomModule[] { new SoftwareInventoryModuleEntry(), new BrowserInventoryModuleEntry(), new InventoryReportingModuleEntry() },
            module => Assert.Empty(ModuleDescriptorRules.Problems(module.Describe())));
    }
}
