// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using ClassroomManager.Reporting;
using ClassroomManager.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ClassroomHost.Tests;

public sealed class LiveReportingFactAttribute : FactAttribute
{
    public LiveReportingFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASSROOM_REPORT_TEST_ADDRESS")))
            Skip = "Opt-in installed-device reporting test; requires explicit target and settings path.";
    }
}

// T-222: explicitly opted-in read-only acceptance; normal suites do not contact real PCs.
[Collection(nameof(HostBackupTests))]
public sealed class LiveReportingModuleTests(ITestOutputHelper output)
{
    [LiveReportingFact]
    public async Task Selected_installed_client_inventory_exports_through_the_module()
    {
        var address = Environment.GetEnvironmentVariable("CLASSROOM_REPORT_TEST_ADDRESS")!;
        var settingsPath = Environment.GetEnvironmentVariable("CLASSROOM_REPORT_TEST_SETTINGS");
        var destination = Environment.GetEnvironmentVariable("CLASSROOM_REPORT_TEST_OUTPUT");
        Assert.False(string.IsNullOrWhiteSpace(settingsPath));
        Assert.False(string.IsNullOrWhiteSpace(destination));
        var before = File.ReadAllBytes(settingsPath!);
        var keysPath = Path.ChangeExtension(settingsPath!, ".keys");
        var keysBefore = File.ReadAllBytes(keysPath);
        var suspended = HostSettings.SavesSuspended;
        HostSettings.SavesSuspended = true;
        try
        {
            var settings = HostSettings.Load(settingsPath);
            var saved = Assert.Single(settings.Computers, computer => computer.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            var client = new ClientInfo { Address = saved.Address, Port = saved.Port, ClientId = saved.ClientId, Mac = saved.Mac,
                KeyId = saved.KeyId, ComputerName = saved.Name, Alias = saved.Alias };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var session = await ClientSession.OpenAsync(client, settings, deadline.Token);
            var data = await session.CallAsync(Cmd.InventoryGet, ct: deadline.Token);
            var report = InventoryReportingModule.CreateReport([new("selected", client.Name)]);
            Directory.CreateDirectory(destination!);
            var export = new InventoryReportExport(report, destination!, DateTime.Now);
            export.Include("selected", InventoryReportData.Capture(data));
            var csv = export.Write();
            Assert.Equal(1, csv.Computers);
            Assert.Equal(2, csv.HardwareRows.Count);
            Assert.Equal(csv.Programs + 1, csv.SoftwareRows.Count);
            output.WriteLine($"Read-only installed inventory: {client.Name}; hardware={csv.Computers}; programs={csv.Programs}; output={destination}");
        }
        finally
        {
            HostSettings.SavesSuspended = suspended;
            Assert.Equal(before, File.ReadAllBytes(settingsPath!));
            Assert.Equal(keysBefore, File.ReadAllBytes(keysPath));
        }
    }
}
