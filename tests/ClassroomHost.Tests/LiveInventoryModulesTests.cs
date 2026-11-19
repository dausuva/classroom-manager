// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ClassroomHost.Tests;

public sealed class InventoryModuleDeviceFactAttribute : FactAttribute
{
    public InventoryModuleDeviceFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASSROOM_MODULE_TEST_ADDRESS")))
            Skip = "Opt-in read-only inventory module device test; explicit enrolled target/settings required.";
    }
}

static class InventoryModuleDevice
{
    public static async Task ReadAsync(Func<ClientInfo, HostSettings, CancellationToken, Task> read)
    {
        var address = Environment.GetEnvironmentVariable("CLASSROOM_MODULE_TEST_ADDRESS")!;
        var path = Environment.GetEnvironmentVariable("CLASSROOM_MODULE_TEST_SETTINGS");
        Assert.False(string.IsNullOrWhiteSpace(path));
        var before = File.ReadAllBytes(path!);
        var keysPath = Path.ChangeExtension(path!, ".keys");
        var keysBefore = File.ReadAllBytes(keysPath);
        var suspended = HostSettings.SavesSuspended;
        HostSettings.SavesSuspended = true;
        try
        {
            var settings = HostSettings.Load(path);
            var saved = Assert.Single(settings.Computers, computer => computer.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            var client = new ClientInfo { Address = saved.Address, Port = saved.Port, ClientId = saved.ClientId, Mac = saved.Mac,
                KeyId = saved.KeyId, ComputerName = saved.Name, Alias = saved.Alias };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using (var session = await ClientSession.OpenAsync(client, settings, deadline.Token))
            {
                var ping = await session.CallAsync(Cmd.Ping, ct: deadline.Token);
                client.Version = ping["version"]?.GetValue<string>() ?? "";
                client.User = ping["userName"]?.GetValue<string>() ?? "";
            }
            await read(client, settings, deadline.Token);
        }
        finally
        {
            HostSettings.SavesSuspended = suspended;
            Assert.Equal(before, File.ReadAllBytes(path!));
            Assert.Equal(keysBefore, File.ReadAllBytes(keysPath));
        }
    }
}

[Collection(nameof(HostBackupTests))]
public sealed class LiveAppearanceModuleTests(ITestOutputHelper output)
{
    [InventoryModuleDeviceFact]
    public Task Screen_saver_inventory_uses_the_appearance_module() => InventoryModuleDevice.ReadAsync(async (client, settings, cancellation) =>
    {
        Assert.False(string.IsNullOrWhiteSpace(client.User));
        using var session = await ClientSession.OpenAsync(client, settings, cancellation);
        var state = ScreenSaverState.FromJson(await session.CallAtAsync(Levels.User, Cmd.ScreenSaverGet, ct: cancellation));
        var report = ScreenSaverInventory.Project([state]);
        Assert.Equal(1, report.Read);
        Assert.Equal(state.PolicyManaged ? 1 : 0, report.PolicyManaged);
        Assert.Equal(1, Assert.Single(report.Usage).Count);
        output.WriteLine($"Appearance inventory: {client.Name}; read={report.Read}; available={report.CommonNames.Count}; policyManaged={report.PolicyManaged}.");
    });
}

[Collection(nameof(HostBackupTests))]
public sealed class LiveSoftwareInventoryModuleTests(ITestOutputHelper output)
{
    [InventoryModuleDeviceFact]
    public Task Installed_programs_use_the_comparison_module() => InventoryModuleDevice.ReadAsync(async (client, settings, cancellation) =>
    {
        using var session = await ClientSession.OpenAsync(client, settings, cancellation);
        var data = await session.CallAtAsync(Levels.System, Cmd.ProgramsList, ct: cancellation);
        var programs = data["programs"]!.AsArray().Select(node => ProgramEntry.FromJson(node!.AsObject())).ToArray();
        var reports = ProgramInventoryComparison.Build([client], new Dictionary<ClientInfo, IReadOnlyList<ProgramEntry>> { [client] = programs });
        Assert.Equal(programs.Length, reports.Sum(report => report.Entries.Count));
        Assert.All(reports, report => { Assert.False(report.HasDifference); Assert.Equal("1 / 1 checked", report.Installed); });
        output.WriteLine($"Software inventory: {client.Name}; entries={programs.Length}; comparedProducts={reports.Count}.");
    });
}

[Collection(nameof(HostBackupTests))]
public sealed class LiveMaintenanceModuleTests(ITestOutputHelper output)
{
    [InventoryModuleDeviceFact]
    public Task Update_status_uses_the_maintenance_projection_without_scanning_or_changing_updates() => InventoryModuleDevice.ReadAsync(async (client, settings, cancellation) =>
    {
        Assert.True(client.SupportsCommand(Cmd.WindowsUpdateStatus));
        using var session = await ClientSession.OpenAsync(client, settings, cancellation);
        var data = await session.CallAtAsync(Levels.System, Cmd.WindowsUpdateStatus, ct: cancellation);
        Assert.NotNull(data["paused"]);
        var report = WindowsMaintenanceInventory.ProjectPause(data, false, DateTimeOffset.UtcNow);
        Assert.Equal(data["paused"]!.GetValue<bool>(), report.Paused);
        output.WriteLine($"Maintenance status: {client.Name}; paused={report.Paused}; expiryPresent={report.Until is not null}. Status only; no scan or changes.");
    });
}
