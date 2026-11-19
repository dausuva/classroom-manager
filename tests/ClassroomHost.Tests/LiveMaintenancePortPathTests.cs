// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Maintenance;
using ClassroomManager.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ClassroomHost.Tests;

public sealed class LiveMaintenancePortPathFactAttribute : FactAttribute
{
    public LiveMaintenancePortPathFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASSROOM_MAINTENANCE_PORT_TEST_ADDRESS")))
            Skip = "Opt-in: runs the Maintenance module's Windows Update operations through the module ports against one installed test Client.";
    }
}

/// <summary>
/// The Maintenance module on a real Client through <see cref="ModuleRuns"/> and the broker: status, a scan, and (only when updates are not
/// paused to begin with) a one-day pause that is removed again. Nothing is installed. Use a test computer.
/// </summary>
[Collection(nameof(HostBackupTests))]
public sealed class LiveMaintenancePortPathTests(ITestOutputHelper output)
{
    [LiveMaintenancePortPathFact]
    public async Task Maintenance_operations_reach_a_real_client_with_administrator_rights_through_the_ports()
    {
        var address = Environment.GetEnvironmentVariable("CLASSROOM_MAINTENANCE_PORT_TEST_ADDRESS")!;
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
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            using var session = await ClientSession.OpenAsync(client, settings, deadline.Token);
            var ping = await session.CallAsync(Cmd.Ping, ct: deadline.Token);
            client.Version = ping["version"]?.GetValue<string>() ?? "";
            output.WriteLine($"{client.Name}: client {client.Version}");

            var (operations, machine) = ModuleRuns.OpenMaintenance(session, client);
            var status = await operations.StatusAsync(machine, deadline.Token);
            var initial = MaintenanceOperations.ReadPause(status, false, DateTimeOffset.UtcNow);
            output.WriteLine($"Status: paused={initial.Paused}");

            var scan = await operations.ScanAsync(machine, includeOptional: null, deadline.Token);
            var report = MaintenanceOperations.ReadScan(scan, "Unnamed update");
            output.WriteLine($"Scan: {report.Updates.Count} update(s), {report.Features} feature, {report.Optional} optional, {report.RequiringRestart} need a restart");
            // The module and the Host's own reader agree on the same real answer.
            Assert.Equal(report.Updates.Count, WindowsMaintenanceInventory.ProjectScan((JsonObject)JsonNode.Parse(scan)!).Updates.Count);

            if (!initial.Paused)
            {
                try
                {
                    var paused = MaintenanceOperations.ReadPause(await operations.PauseAsync(machine, 1, deadline.Token), true, DateTimeOffset.UtcNow);
                    output.WriteLine($"Pause: paused={paused.Paused} until {paused.Until}");
                    Assert.True(paused.Paused);
                }
                finally
                {
                    var resumed = MaintenanceOperations.ReadPause(await operations.ResumeAsync(machine, CancellationToken.None), false, DateTimeOffset.UtcNow);
                    output.WriteLine($"Resume: paused={resumed.Paused}");
                    Assert.False(resumed.Paused);
                }
            }
        }
        finally
        {
            HostSettings.SavesSuspended = suspended;
            Assert.Equal(before, File.ReadAllBytes(settingsPath!));
            Assert.Equal(keysBefore, File.ReadAllBytes(keysPath));
        }
    }
}
