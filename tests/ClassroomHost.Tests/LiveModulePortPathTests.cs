// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ClassroomHost.Tests;

public sealed class LiveModulePortPathFactAttribute : FactAttribute
{
    public LiveModulePortPathFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASSROOM_MODULE_PORT_TEST_ADDRESS")))
            Skip = "Opt-in: runs the read-only operations of the Software, Browser inventory and Reporting modules through the module ports against one installed Client.";
    }
}

/// <summary>The read-only operations of three modules on a real Client, through <see cref="ModuleRuns"/>, the broker and the real session. Nothing is changed or uninstalled.</summary>
[Collection(nameof(HostBackupTests))]
public sealed class LiveModulePortPathTests(ITestOutputHelper output)
{
    [LiveModulePortPathFact]
    public async Task Software_browser_and_reporting_reads_reach_a_real_client_through_the_ports()
    {
        var address = Environment.GetEnvironmentVariable("CLASSROOM_MODULE_PORT_TEST_ADDRESS")!;
        var settingsPath = Environment.GetEnvironmentVariable("CLASSROOM_BACKUP_TEST_SETTINGS");
        Assert.False(string.IsNullOrWhiteSpace(settingsPath));
        var before = File.ReadAllBytes(settingsPath!);
        var keysPath = Path.ChangeExtension(settingsPath!, ".keys");
        var keysBefore = File.ReadAllBytes(keysPath);
        var suspended = HostSettings.SavesSuspended;
        HostSettings.SavesSuspended = true;
        try
        {
            var settings = HostSettings.Load(settingsPath);
            var saved = Assert.Single(settings.Computers, c => c.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            var client = new ClientInfo { Address = saved.Address, Port = saved.Port, ClientId = saved.ClientId,
                Mac = saved.Mac, KeyId = saved.KeyId, ComputerName = saved.Name };
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var session = await ClientSession.OpenAsync(client, settings, deadline.Token);
            var ping = await session.CallAsync(Cmd.Ping, ct: deadline.Token);
            client.Version = ping["version"]?.GetValue<string>() ?? "";
            client.User = ping["userName"]?.GetValue<string>() ?? "";
            output.WriteLine($"{client.Name}: client {client.Version}, user {client.User}");

            var (software, machine) = ModuleRuns.OpenSoftware(session, client);
            var machineWide = JsonNode.Parse(await software.ListAsync(machine, administrator: true, deadline.Token))!.AsObject();
            var userOnly = JsonNode.Parse(await software.ListAsync(machine, administrator: false, deadline.Token))!.AsObject();
            var machinePrograms = machineWide["programs"]!.AsArray().Select(p => ProgramEntry.FromJson(p!.AsObject())).ToList();
            var userPrograms = userOnly["programs"]!.AsArray().Select(p => ProgramEntry.FromJson(p!.AsObject())).ToList();
            output.WriteLine($"Programs: machine-wide {machinePrograms.Count} ({machineWide["scope"]}), signed-in user {userPrograms.Count} ({userOnly["scope"]})");
            Assert.NotEmpty(machinePrograms);

            var (browsers, browserMachine) = ModuleRuns.OpenBrowser(session, client);
            var policy = JsonNode.Parse(await browsers.ReadAsync(browserMachine, true, "browser.policy.get", deadline.Token))!.AsObject();
            var list = JsonNode.Parse(await browsers.ReadAsync(browserMachine, client.User.Length > 0 ? false : true, "browser.list", deadline.Token))!.AsObject();
            var profiles = JsonNode.Parse(await browsers.ReadAsync(browserMachine, false, "browser.profiles.list", deadline.Token))!.AsObject();
            output.WriteLine($"Browsers: policy keys {policy.Count}, list keys {list.Count}, profiles keys {profiles.Count}");

            var (reporting, reportMachine) = ModuleRuns.OpenReporting(session, client);
            var inventory = JsonNode.Parse(await reporting.InventoryAsync(reportMachine, deadline.Token))!.AsObject();
            var snapshot = InventoryReportData.Capture(inventory);
            output.WriteLine($"Inventory: {snapshot.Hardware.OS}, {snapshot.Programs.Count} programs, {snapshot.Hardware.Disks.Count} disk(s)");
            Assert.NotEmpty(snapshot.Hardware.OS);
            Assert.NotEmpty(snapshot.Programs);
        }
        finally
        {
            HostSettings.SavesSuspended = suspended;
            Assert.Equal(before, File.ReadAllBytes(settingsPath!));
            Assert.Equal(keysBefore, File.ReadAllBytes(keysPath));
        }
    }
}
